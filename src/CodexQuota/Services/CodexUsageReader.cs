using CodexQuota.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexQuota.Services
{
    public sealed class CodexUsageReader : IDisposable
    {
        public const int ShortWindowMinutes = 300;
        public const int WeekWindowMinutes = 10080;
        private const int MaxFilesToScan = 160;
        private const int TailBytesPerFile = 2 * 1024 * 1024;

        private readonly string _sessionsPath;
        private readonly JavaScriptSerializer _json;
        private readonly object _refreshGate = new object();
        private FileSystemWatcher _watcher;
        private Timer _debounceTimer;
        private bool _disposed;

        public event EventHandler<UsageSnapshot> SnapshotChanged;

        public CodexUsageReader(string sessionsPath)
        {
            _sessionsPath = sessionsPath;
            _json = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 128
            };
        }

        public static string GetDefaultSessionsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "sessions");
        }

        public UsageSnapshot ReadLatest()
        {
            if (!Directory.Exists(_sessionsPath))
            {
                return new UsageSnapshot { StatusMessage = "未找到 Codex 会话目录" };
            }

            try
            {
                var candidates = Directory.EnumerateFiles(_sessionsPath, "rollout-*.jsonl", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(MaxFilesToScan)
                    .ToList();

                UsageWindowSnapshot shortWindow = null;
                UsageWindowSnapshot weekWindow = null;

                foreach (var file in candidates)
                {
                    foreach (var parsed in ReadSnapshotsFromTail(file.FullName))
                    {
                        if (parsed.WindowMinutes == ShortWindowMinutes &&
                            (shortWindow == null || parsed.ObservedAt > shortWindow.ObservedAt))
                        {
                            shortWindow = parsed;
                        }
                        else if (parsed.WindowMinutes == WeekWindowMinutes &&
                                 (weekWindow == null || parsed.ObservedAt > weekWindow.ObservedAt))
                        {
                            weekWindow = parsed;
                        }
                    }

                    if (shortWindow != null && weekWindow != null &&
                        file.LastWriteTimeUtc < shortWindow.ObservedAt.UtcDateTime &&
                        file.LastWriteTimeUtc < weekWindow.ObservedAt.UtcDateTime)
                    {
                        break;
                    }
                }

                return new UsageSnapshot
                {
                    ShortWindow = shortWindow,
                    WeekWindow = weekWindow,
                    StatusMessage = shortWindow == null && weekWindow == null
                        ? "暂未在本地日志中找到额度信息"
                        : "已从 Codex 本地日志同步"
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new UsageSnapshot { StatusMessage = "没有权限读取 Codex 会话目录" };
            }
            catch (IOException)
            {
                return new UsageSnapshot { StatusMessage = "Codex 日志正在写入，请稍后重试" };
            }
            catch (Exception)
            {
                return new UsageSnapshot { StatusMessage = "读取本地额度时发生错误" };
            }
        }

        public void StartWatching()
        {
            if (!Directory.Exists(_sessionsPath) || _watcher != null)
            {
                return;
            }

            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_sessionsPath, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 32768,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
        }

        public void RequestRefresh()
        {
            if (_disposed) return;
            if (_debounceTimer == null)
            {
                RaiseSnapshot(ReadLatest());
                return;
            }
            _debounceTimer.Change(250, Timeout.Infinite);
        }

        public IList<UsageWindowSnapshot> ParseLine(string line, string sourceFile)
        {
            var results = new List<UsageWindowSnapshot>();
            if (string.IsNullOrWhiteSpace(line) ||
                line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0)
            {
                return results;
            }

            try
            {
                var root = _json.DeserializeObject(line) as IDictionary<string, object>;
                if (root == null || GetString(root, "type") != "event_msg") return results;

                var payload = GetDictionary(root, "payload");
                if (payload == null || GetString(payload, "type") != "token_count") return results;

                var rateLimits = GetDictionary(payload, "rate_limits");
                if (rateLimits == null) return results;

                DateTimeOffset observedAt;
                if (!DateTimeOffset.TryParse(GetString(root, "timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out observedAt))
                {
                    observedAt = DateTimeOffset.UtcNow;
                }

                AddWindow(rateLimits, "primary", observedAt, sourceFile, results);
                AddWindow(rateLimits, "secondary", observedAt, sourceFile, results);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }

            return results;
        }

        private IEnumerable<UsageWindowSnapshot> ReadSnapshotsFromTail(string path)
        {
            var results = new List<UsageWindowSnapshot>();
            byte[] bytes;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0L, stream.Length - TailBytesPerFile);
                stream.Position = start;
                bytes = new byte[stream.Length - start];
                int total = 0;
                while (total < bytes.Length)
                {
                    int read = stream.Read(bytes, total, bytes.Length - total);
                    if (read == 0) break;
                    total += read;
                }
            }

            string text = Encoding.UTF8.GetString(bytes);
            string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int firstCompleteLine = bytes.Length >= TailBytesPerFile ? 1 : 0;
            for (int i = Math.Max(0, firstCompleteLine); i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                foreach (var parsed in ParseLine(line, path))
                {
                    results.Add(parsed);
                }
            }
            return results;
        }

        private static void AddWindow(IDictionary<string, object> rateLimits, string key,
            DateTimeOffset observedAt, string sourceFile, IList<UsageWindowSnapshot> results)
        {
            var window = GetDictionary(rateLimits, key);
            if (window == null) return;

            int minutes;
            double used;
            long resetUnix;
            if (!TryGetInt(window, "window_minutes", out minutes) ||
                !TryGetDouble(window, "used_percent", out used) ||
                !TryGetLong(window, "resets_at", out resetUnix))
            {
                return;
            }

            if (minutes != ShortWindowMinutes && minutes != WeekWindowMinutes) return;

            results.Add(new UsageWindowSnapshot
            {
                WindowMinutes = minutes,
                UsedPercent = used,
                ResetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix),
                ObservedAt = observedAt,
                SourceFile = sourceFile
            });
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name != null && e.Name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                RequestRefresh();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            OnFileChanged(sender, e);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            RequestRefresh();
        }

        private void OnDebounceElapsed(object state)
        {
            if (_disposed || !Monitor.TryEnter(_refreshGate)) return;
            try { RaiseSnapshot(ReadLatest()); }
            finally { Monitor.Exit(_refreshGate); }
        }

        private void RaiseSnapshot(UsageSnapshot snapshot)
        {
            var handler = SnapshotChanged;
            if (handler != null) handler(this, snapshot);
        }

        private static IDictionary<string, object> GetDictionary(IDictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }

        private static string GetString(IDictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
        }

        private static bool TryGetInt(IDictionary<string, object> source, string key, out int value)
        {
            object raw;
            if (source.TryGetValue(key, out raw) && raw != null)
                return int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            value = 0;
            return false;
        }

        private static bool TryGetLong(IDictionary<string, object> source, string key, out long value)
        {
            object raw;
            if (source.TryGetValue(key, out raw) && raw != null)
                return long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            value = 0L;
            return false;
        }

        private static bool TryGetDouble(IDictionary<string, object> source, string key, out double value)
        {
            object raw;
            if (source.TryGetValue(key, out raw) && raw != null)
                return double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
            value = 0d;
            return false;
        }

        public void Dispose()
        {
            _disposed = true;
            if (_watcher != null) _watcher.Dispose();
            if (_debounceTimer != null) _debounceTimer.Dispose();
            _watcher = null;
            _debounceTimer = null;
        }
    }
}
