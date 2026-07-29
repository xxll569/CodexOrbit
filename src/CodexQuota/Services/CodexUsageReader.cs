using CodexQuota.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexQuota.Services
{
    public sealed class CodexUsageReader : IDisposable
    {
        public const int ShortWindowMinutes = 300;
        public const int WeekWindowMinutes = 10080;
        public const string StaleDataStatusMessage = "同步暂时失败，正在显示上次成功数据";

        private const int PollIntervalMs = 30000;
        private const int DefaultResponseTimeoutMs = 8000;
        private const int DefaultOverallTimeoutMs = 25000;
        private const int MaxRateLimitAttempts = 3;
        private const long MaxDiagnosticLogBytes = 2 * 1024 * 1024;
        private const string DirectUsageEndpoint =
            "https://chatgpt.com/backend-api/wham/usage";
        private static readonly Regex AnsiEscapePattern =
            new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private readonly string _sessionsPath;
        private readonly JavaScriptSerializer _json;
        private readonly object _refreshGate = new object();
        private readonly object _snapshotGate = new object();
        private readonly object _logGate = new object();
        private readonly Func<ProcessStartInfo> _startInfoFactory;
        private readonly int _responseTimeoutMs;
        private readonly int _overallTimeoutMs;
        private readonly string _diagnosticLogPath;
        private readonly string _authPath;
        private readonly bool _directUsageEnabled;
        private UsageSnapshot _lastSuccessfulSnapshot;
        private string _lastProxyDescription;
        private FileSystemWatcher _watcher;
        private Timer _debounceTimer;
        private Timer _safetyTimer;
        private volatile bool _disposed;

        public event EventHandler<UsageSnapshot> SnapshotChanged;

        public CodexUsageReader(string sessionsPath)
            : this(
                sessionsPath,
                CreateCodexStartInfo,
                DefaultResponseTimeoutMs,
                DefaultOverallTimeoutMs,
                GetDiagnosticLogPath())
        {
            _authPath = GetDefaultAuthPath();
            _directUsageEnabled = true;
        }

        internal CodexUsageReader(
            string sessionsPath,
            Func<ProcessStartInfo> startInfoFactory,
            int processTimeoutMs)
            : this(
                sessionsPath,
                startInfoFactory,
                processTimeoutMs,
                processTimeoutMs,
                null)
        {
        }

        internal CodexUsageReader(
            string sessionsPath,
            Func<ProcessStartInfo> startInfoFactory,
            int processTimeoutMs,
            string diagnosticLogPath)
            : this(
                sessionsPath,
                startInfoFactory,
                processTimeoutMs,
                processTimeoutMs,
                diagnosticLogPath)
        {
        }

        internal CodexUsageReader(
            string sessionsPath,
            Func<ProcessStartInfo> startInfoFactory,
            int responseTimeoutMs,
            int overallTimeoutMs,
            string diagnosticLogPath)
        {
            if (startInfoFactory == null) throw new ArgumentNullException("startInfoFactory");
            if (responseTimeoutMs <= 0) throw new ArgumentOutOfRangeException("responseTimeoutMs");
            if (overallTimeoutMs < responseTimeoutMs)
                throw new ArgumentOutOfRangeException("overallTimeoutMs");

            _sessionsPath = sessionsPath;
            _startInfoFactory = startInfoFactory;
            _responseTimeoutMs = responseTimeoutMs;
            _overallTimeoutMs = overallTimeoutMs;
            _diagnosticLogPath = diagnosticLogPath;
            _json = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 128
            };
        }

        public static string GetDefaultSessionsPath()
        {
            return Path.Combine(GetDefaultCodexHomePath(), "sessions");
        }

        public static string GetDiagnosticLogPath()
        {
            string localAppData =
                Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
            }

            return Path.Combine(
                localAppData,
                "CodexOrbit",
                "logs",
                "CodexOrbit.log");
        }

        public static string GetDefaultAuthPath()
        {
            return Path.Combine(GetDefaultCodexHomePath(), "auth.json");
        }

        private static string GetDefaultCodexHomePath()
        {
            string configuredHome =
                Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(configuredHome))
                return configuredHome;

            string userProfile =
                Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                userProfile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(userProfile, ".codex");
        }

        public UsageSnapshot ReadLatest()
        {
            if (!_directUsageEnabled)
                return ReadLatestViaAppServer();

            DirectUsageResult directResult = ReadLatestDirect();
            if (directResult.Snapshot != null &&
                directResult.Snapshot.HasAnyData)
            {
                RememberSuccessfulSnapshot(directResult.Snapshot);
                return directResult.Snapshot;
            }

            WriteDiagnosticLog(
                "INFO",
                "直接读取不可用，尝试 Codex app-server 回退",
                "reason=" + SanitizeLogValue(directResult.FailureMessage));

            UsageSnapshot fallback = ReadLatestViaAppServer();
            if (fallback != null && fallback.HasAnyData)
                return fallback;

            return CreateFailureSnapshot(
                string.IsNullOrWhiteSpace(directResult.FailureMessage)
                    ? (fallback == null ? "读取 Codex 额度失败" : fallback.StatusMessage)
                    : directResult.FailureMessage);
        }

        private DirectUsageResult ReadLatestDirect()
        {
            var elapsed = Stopwatch.StartNew();
            DirectAuthSnapshot auth;
            try
            {
                auth = ReadDirectAuth(_authPath);
            }
            catch (DirectUsageException ex)
            {
                WriteDiagnosticLog(
                    "WARN",
                    "无法读取 Codex 共享登录状态",
                    "reason=" + SanitizeLogValue(ex.Message) +
                    "; elapsedMs=" + elapsed.ElapsedMilliseconds);
                return new DirectUsageResult(null, ex.Message);
            }

            DateTime overallDeadline =
                DateTime.UtcNow.AddMilliseconds(_overallTimeoutMs);
            string proxyDescription = "not-configured";
            string lastFailure = null;

            for (int attempt = 1; attempt <= MaxRateLimitAttempts; attempt++)
            {
                try
                {
                    int remainingMs = (int)Math.Floor(
                        (overallDeadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remainingMs <= 0) throw new TimeoutException();

                    int timeoutMs = Math.Min(_responseTimeoutMs, remainingMs);
                    DirectProxySettings proxy = ResolveDirectProxy(
                        new Uri(DirectUsageEndpoint));
                    proxyDescription = proxy.Description;
                    LogProxyConfigurationIfChanged(proxyDescription);

                    string json = SendDirectUsageRequest(
                        auth,
                        proxy.Proxy,
                        timeoutMs);
                    UsageSnapshot snapshot = ParseDirectRateLimitsResponse(json);
                    if (!snapshot.HasAnyData)
                    {
                        lastFailure = snapshot.StatusMessage;
                        WriteDiagnosticLog(
                            "WARN",
                            "直接额度响应中没有支持的窗口",
                            "attempt=" + attempt +
                            "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                            "; proxy=" + proxyDescription +
                            "; status=" + SanitizeLogValue(snapshot.StatusMessage));
                        break;
                    }

                    if (attempt > 1)
                    {
                        WriteDiagnosticLog(
                            "INFO",
                            "直接额度接口重试后恢复",
                            "attempt=" + attempt +
                            "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                            "; proxy=" + proxyDescription);
                    }

                    return new DirectUsageResult(snapshot, null);
                }
                catch (WebException ex)
                {
                    int? statusCode = GetHttpStatusCode(ex);
                    bool authFailure =
                        statusCode == (int)HttpStatusCode.Unauthorized ||
                        statusCode == (int)HttpStatusCode.Forbidden;
                    bool transient = !authFailure &&
                        (statusCode == null ||
                         statusCode == (int)HttpStatusCode.RequestTimeout ||
                         statusCode == 429 ||
                         (statusCode.HasValue &&
                          statusCode.Value >= 500));

                    lastFailure = authFailure
                        ? "Codex 登录状态已失效，请打开 Codex 桌面端重新登录"
                        : "Codex 用量接口暂时不可用";

                    WriteDiagnosticLog(
                        transient ? "WARN" : "ERROR",
                        "直接额度接口请求失败",
                        "attempt=" + attempt + "/" + MaxRateLimitAttempts +
                        "; statusCode=" +
                        (statusCode.HasValue
                            ? statusCode.Value.ToString(CultureInfo.InvariantCulture)
                            : "<empty>") +
                        "; webStatus=" + ex.Status +
                        "; message=" + SanitizeLogValue(ex.Message) +
                        "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                        "; proxy=" + proxyDescription);

                    if (!transient || attempt == MaxRateLimitAttempts)
                        break;

                    WaitBeforeRetry(overallDeadline, attempt);
                }
                catch (TimeoutException)
                {
                    lastFailure = "读取 Codex 用量接口超时，请稍后重试";
                    WriteDiagnosticLog(
                        "WARN",
                        lastFailure,
                        "attempt=" + attempt + "/" + MaxRateLimitAttempts +
                        "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                        "; proxy=" + proxyDescription);

                    if (attempt == MaxRateLimitAttempts)
                        break;

                    WaitBeforeRetry(overallDeadline, attempt);
                }
                catch (Exception ex)
                {
                    lastFailure = "读取 Codex 用量接口失败";
                    WriteDiagnosticLog(
                        "ERROR",
                        lastFailure,
                        "attempt=" + attempt + "/" + MaxRateLimitAttempts +
                        "; exceptionType=" + ex.GetType().FullName +
                        "; message=" + SanitizeLogValue(ex.Message) +
                        "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                        "; proxy=" + proxyDescription);
                    break;
                }
            }

            return new DirectUsageResult(
                null,
                string.IsNullOrWhiteSpace(lastFailure)
                    ? "读取 Codex 用量接口失败"
                    : lastFailure);
        }

        private DirectAuthSnapshot ReadDirectAuth(string authPath)
        {
            if (string.IsNullOrWhiteSpace(authPath) || !File.Exists(authPath))
                throw new DirectUsageException(
                    "未找到 Codex 登录状态，请先登录 Codex 桌面端");

            string json;
            try
            {
                using (var stream = new FileStream(
                    authPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    true))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                throw new DirectUsageException(
                    "无法读取 Codex 登录状态: " + ex.Message);
            }

            IDictionary<string, object> root = DeserializeDictionary(json);
            IDictionary<string, object> tokens = GetDictionary(root, "tokens");
            string authMode = GetString(root, "auth_mode");
            string accessToken = GetString(tokens, "access_token");
            string accountId = GetString(tokens, "account_id");

            if (!string.Equals(
                    authMode,
                    "chatgpt",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(accessToken))
            {
                throw new DirectUsageException(
                    "当前 Codex 登录方式不支持读取 ChatGPT 用量");
            }

            return new DirectAuthSnapshot(accessToken, accountId);
        }

        private string SendDirectUsageRequest(
            DirectAuthSnapshot auth,
            IWebProxy proxy,
            int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |=
                SecurityProtocolType.Tls12;

            var request = (HttpWebRequest)WebRequest.Create(DirectUsageEndpoint);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "CodexOrbit/1.2.3";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = false;
            request.Proxy = proxy;
            request.Headers[HttpRequestHeader.Authorization] =
                "Bearer " + auth.AccessToken;
            request.Headers["OpenAI-Beta"] = "codex-1";
            request.Headers["originator"] = "Codex Desktop";
            if (!string.IsNullOrWhiteSpace(auth.AccountId))
                request.Headers["ChatGPT-Account-Id"] = auth.AccountId;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        private static int? GetHttpStatusCode(WebException exception)
        {
            var response = exception.Response as HttpWebResponse;
            return response == null ? (int?)null : (int)response.StatusCode;
        }

        private static DirectProxySettings ResolveDirectProxy(Uri destination)
        {
            string environmentProxy =
                Environment.GetEnvironmentVariable("HTTPS_PROXY");
            if (string.IsNullOrWhiteSpace(environmentProxy))
                environmentProxy =
                    Environment.GetEnvironmentVariable("https_proxy");

            Uri environmentProxyUri;
            if (!string.IsNullOrWhiteSpace(environmentProxy) &&
                Uri.TryCreate(
                    environmentProxy,
                    UriKind.Absolute,
                    out environmentProxyUri))
            {
                return new DirectProxySettings(
                    new WebProxy(environmentProxyUri),
                    "source=environment; https=" +
                    DescribeProxy(environmentProxy));
            }

            try
            {
                IWebProxy systemProxy = WebRequest.GetSystemWebProxy();
                if (systemProxy != null && !systemProxy.IsBypassed(destination))
                {
                    Uri proxyUri = systemProxy.GetProxy(destination);
                    if (proxyUri != null && proxyUri != destination)
                    {
                        systemProxy.Credentials =
                            CredentialCache.DefaultCredentials;
                        return new DirectProxySettings(
                            systemProxy,
                            "source=windows-system; https=" +
                            DescribeProxy(proxyUri.AbsoluteUri));
                    }
                }
            }
            catch
            {
            }

            return new DirectProxySettings(
                null,
                "source=direct; https=direct");
        }

        private UsageSnapshot ReadLatestViaAppServer()
        {
            var standardError = new StringBuilder();
            var elapsed = Stopwatch.StartNew();
            string commandDescription = null;
            string proxyDescription = "not-configured";

            try
            {
                using (var proc = new Process())
                {
                    proc.StartInfo = _startInfoFactory();
                    ConfigureRedirectedProcess(proc.StartInfo);
                    proxyDescription = ApplySystemProxy(
                        proc.StartInfo,
                        ResolveSystemProxy);
                    LogProxyConfigurationIfChanged(proxyDescription);
                    commandDescription = proc.StartInfo.FileName + " " + proc.StartInfo.Arguments;
                    proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                    {
                        if (string.IsNullOrEmpty(args.Data)) return;
                        lock (standardError)
                        {
                            standardError.AppendLine(args.Data);
                        }
                    };

                    bool started = false;
                    try
                    {
                        proc.Start();
                        started = true;
                        proc.BeginErrorReadLine();

                        DateTime overallDeadline =
                            DateTime.UtcNow.AddMilliseconds(_overallTimeoutMs);

                        WriteMessage(proc,
                            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-orbit\",\"version\":\"1.2.3\"}}}");
                        ReadResponseForId(
                            proc,
                            1,
                            GetResponseDeadline(overallDeadline));

                        WriteMessage(proc,
                            "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");

                        IDictionary<string, object> response = null;
                        for (int attempt = 1; attempt <= MaxRateLimitAttempts; attempt++)
                        {
                            int requestId = attempt + 1;
                            WriteMessage(
                                proc,
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{{\"jsonrpc\":\"2.0\",\"id\":{0},\"method\":\"account/rateLimits/read\",\"params\":null}}",
                                    requestId));

                            try
                            {
                                response = ReadResponseForId(
                                    proc,
                                    requestId,
                                    GetResponseDeadline(overallDeadline));
                                if (attempt > 1)
                                {
                                    WriteDiagnosticLog(
                                        "INFO",
                                        "额度接口重试后恢复",
                                        "attempt=" + attempt +
                                        "; elapsedMs=" + elapsed.ElapsedMilliseconds +
                                        "; proxy=" + proxyDescription);
                                }
                                break;
                            }
                            catch (CodexProtocolException ex)
                            {
                                ex.Attempt = attempt;
                                WriteDiagnosticLog(
                                    "WARN",
                                    "额度接口请求失败",
                                    BuildProtocolDiagnostic(
                                        ex,
                                        attempt,
                                        commandDescription,
                                        GetErrorText(standardError),
                                        elapsed.ElapsedMilliseconds,
                                        proxyDescription));

                                if (!ex.IsTransient || attempt == MaxRateLimitAttempts)
                                    throw;

                                WaitBeforeRetry(overallDeadline, attempt);
                            }
                        }

                        UsageSnapshot snapshot = ParseRateLimitsResponse(response);
                        if (snapshot.HasAnyData)
                        {
                            RememberSuccessfulSnapshot(snapshot);
                            return snapshot;
                        }

                        WriteDiagnosticLog(
                            "WARN",
                            "额度响应中没有支持的窗口",
                            "status=" + SanitizeLogValue(snapshot.StatusMessage));
                        return CreateFailureSnapshot(snapshot.StatusMessage);
                    }
                    finally
                    {
                        if (started)
                        {
                            CloseInput(proc);
                            StopProcess(proc);
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                const string message = "读取 Codex 额度超时，请稍后重试";
                WriteDiagnosticLog(
                    "ERROR",
                    message,
                    BuildFailureDiagnostic(
                        commandDescription,
                        GetErrorText(standardError),
                        elapsed.ElapsedMilliseconds,
                        proxyDescription));
                return CreateFailureSnapshot(message);
            }
            catch (Win32Exception ex)
            {
                string message = "无法启动 Codex CLI: " + ex.Message;
                WriteDiagnosticLog(
                    "ERROR",
                    "无法启动 Codex CLI",
                    BuildFailureDiagnostic(
                        commandDescription,
                        GetErrorText(standardError),
                        elapsed.ElapsedMilliseconds,
                        proxyDescription) +
                    "; exception=" + SanitizeLogValue(ex.ToString()));
                return CreateFailureSnapshot(message);
            }
            catch (CodexProtocolException ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.ProtocolMessage)
                    ? "Codex 服务返回额度错误，详情见诊断日志"
                    : "Codex 服务返回错误: " + ex.ProtocolMessage;
                WriteDiagnosticLog(
                    "ERROR",
                    "额度接口连续失败",
                    BuildProtocolDiagnostic(
                        ex,
                        ex.Attempt > 0 ? ex.Attempt : MaxRateLimitAttempts,
                        commandDescription,
                        GetErrorText(standardError),
                        elapsed.ElapsedMilliseconds,
                        proxyDescription));
                return CreateFailureSnapshot(message);
            }
            catch (InvalidOperationException ex)
            {
                string error = GetErrorText(standardError);
                string message = string.IsNullOrWhiteSpace(error)
                    ? ex.Message
                    : ex.Message + "；Codex 错误: " + error;
                WriteDiagnosticLog(
                    "ERROR",
                    "Codex 进程通信失败",
                    BuildFailureDiagnostic(
                        commandDescription,
                        error,
                        elapsed.ElapsedMilliseconds,
                        proxyDescription) +
                    "; exception=" + SanitizeLogValue(ex.ToString()));
                return CreateFailureSnapshot(message);
            }
            catch (Exception ex)
            {
                string error = GetErrorText(standardError);
                string message = string.IsNullOrWhiteSpace(error)
                    ? "读取失败: " + ex.Message
                    : "读取失败: " + ex.Message + "；Codex 错误: " + error;
                WriteDiagnosticLog(
                    "ERROR",
                    "读取额度时发生未处理异常",
                    BuildFailureDiagnostic(
                        commandDescription,
                        error,
                        elapsed.ElapsedMilliseconds,
                        proxyDescription) +
                    "; exception=" + SanitizeLogValue(ex.ToString()));
                return CreateFailureSnapshot(message);
            }
        }

        public void StartWatching()
        {
            if (_safetyTimer != null) return;

            _safetyTimer = new Timer(_ => RequestRefresh(), null, PollIntervalMs, PollIntervalMs);
            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

            if (Directory.Exists(_sessionsPath))
            {
                try
                {
                    _watcher = new FileSystemWatcher(_sessionsPath, "*.jsonl")
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        InternalBufferSize = 32768,
                        EnableRaisingEvents = false
                    };
                    _watcher.Changed += OnFileChanged;
                    _watcher.Created += OnFileChanged;
                    _watcher.Renamed += OnFileRenamed;
                    _watcher.Error += OnWatcherError;
                    _watcher.EnableRaisingEvents = true;
                }
                catch
                {
                    _watcher = null;
                }
            }
        }

        public void RequestRefresh()
        {
            if (_disposed) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!Monitor.TryEnter(_refreshGate)) return;
                try
                {
                    if (!_disposed) RaiseSnapshot(ReadLatest());
                }
                finally
                {
                    Monitor.Exit(_refreshGate);
                }
            });
        }

        public void Dispose()
        {
            _disposed = true;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }
            if (_safetyTimer != null)
            {
                _safetyTimer.Dispose();
                _safetyTimer = null;
            }
        }

        internal UsageSnapshot ParseRateLimitsResponse(string jsonLine)
        {
            IDictionary<string, object> root = DeserializeDictionary(jsonLine);
            if (root == null)
                return new UsageSnapshot { StatusMessage = "解析响应失败" };

            return ParseRateLimitsResponse(root);
        }

        internal UsageSnapshot ParseDirectRateLimitsResponse(string json)
        {
            IDictionary<string, object> root = DeserializeDictionary(json);
            if (root == null)
                return new UsageSnapshot { StatusMessage = "解析直接额度响应失败" };

            IDictionary<string, object> rateLimit =
                GetDictionary(root, "rate_limit");
            if (rateLimit == null)
                return new UsageSnapshot { StatusMessage = "直接响应中未找到额度数据" };

            DateTimeOffset now = DateTimeOffset.UtcNow;
            UsageWindowSnapshot shortWindow = null;
            UsageWindowSnapshot weekWindow = null;

            ClassifyWindow(
                ParseDirectWindow(rateLimit, "primary_window", now),
                ref shortWindow,
                ref weekWindow);
            ClassifyWindow(
                ParseDirectWindow(rateLimit, "secondary_window", now),
                ref shortWindow,
                ref weekWindow);

            return new UsageSnapshot
            {
                ShortWindow = shortWindow,
                WeekWindow = weekWindow,
                StatusMessage = shortWindow == null && weekWindow == null
                    ? "直接响应中没有支持的 5 小时或周额度窗口"
                    : "已从 Codex 账号同步"
            };
        }

        private UsageSnapshot ParseRateLimitsResponse(IDictionary<string, object> root)
        {
            IDictionary<string, object> result = GetDictionary(root, "result");
            if (result == null)
                return new UsageSnapshot { StatusMessage = "额度响应格式异常" };

            IDictionary<string, object> rateLimits = GetDictionary(result, "rateLimits");
            if (rateLimits == null)
                return new UsageSnapshot { StatusMessage = "未找到额度数据" };

            DateTimeOffset now = DateTimeOffset.UtcNow;
            UsageWindowSnapshot shortWindow = null;
            UsageWindowSnapshot weekWindow = null;

            ClassifyWindow(ParseWindow(rateLimits, "primary", now), ref shortWindow, ref weekWindow);
            ClassifyWindow(ParseWindow(rateLimits, "secondary", now), ref shortWindow, ref weekWindow);

            return new UsageSnapshot
            {
                ShortWindow = shortWindow,
                WeekWindow = weekWindow,
                StatusMessage = shortWindow == null && weekWindow == null
                    ? "未找到支持的 5 小时或周额度窗口"
                    : "已从 Codex 服务同步"
            };
        }

        private IDictionary<string, object> ReadResponseForId(
            Process proc,
            int expectedId,
            DateTime deadline)
        {
            while (true)
            {
                int remainingMs = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds);
                if (remainingMs <= 0) throw new TimeoutException();

                Task<string> readTask = proc.StandardOutput.ReadLineAsync();
                if (!readTask.Wait(remainingMs)) throw new TimeoutException();

                string line = readTask.Result;
                if (line == null)
                    throw new InvalidOperationException("Codex 服务在返回额度前已退出");

                IDictionary<string, object> message = DeserializeDictionary(line);
                if (message == null) continue;

                int responseId;
                if (!TryGetInt(message, "id", out responseId) || responseId != expectedId)
                    continue;

                IDictionary<string, object> error = GetDictionary(message, "error");
                if (error != null)
                {
                    string code = GetString(error, "code");
                    string detail = GetString(error, "message");
                    string data = SerializeDictionaryValue(error, "data");
                    throw new CodexProtocolException(
                        code,
                        detail,
                        data,
                        IsTransientProtocolError(detail));
                }

                return message;
            }
        }

        private void RememberSuccessfulSnapshot(UsageSnapshot snapshot)
        {
            lock (_snapshotGate)
            {
                _lastSuccessfulSnapshot = new UsageSnapshot
                {
                    ShortWindow = snapshot.ShortWindow,
                    WeekWindow = snapshot.WeekWindow,
                    StatusMessage = snapshot.StatusMessage
                };
            }
        }

        private UsageSnapshot CreateFailureSnapshot(string failureMessage)
        {
            lock (_snapshotGate)
            {
                if (_lastSuccessfulSnapshot == null || !_lastSuccessfulSnapshot.HasAnyData)
                    return new UsageSnapshot { StatusMessage = failureMessage };

                return new UsageSnapshot
                {
                    ShortWindow = _lastSuccessfulSnapshot.ShortWindow,
                    WeekWindow = _lastSuccessfulSnapshot.WeekWindow,
                    StatusMessage = StaleDataStatusMessage
                };
            }
        }

        private static bool IsTransientProtocolError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            string normalized = message.ToLowerInvariant();
            if (normalized.Contains("authentication") ||
                normalized.Contains("unauthorized") ||
                normalized.Contains("login required"))
            {
                return false;
            }

            return normalized.Contains("failed to fetch codex rate limits") ||
                   normalized.Contains("temporarily unavailable") ||
                   normalized.Contains("timed out") ||
                   normalized.Contains("timeout") ||
                   normalized.Contains("connection") ||
                   normalized.Contains("network");
        }

        private DateTime GetResponseDeadline(DateTime overallDeadline)
        {
            DateTime responseDeadline =
                DateTime.UtcNow.AddMilliseconds(_responseTimeoutMs);
            return responseDeadline < overallDeadline
                ? responseDeadline
                : overallDeadline;
        }

        private static void WaitBeforeRetry(DateTime deadline, int failedAttempt)
        {
            int delayMs = failedAttempt == 1 ? 500 : 1500;
            int remainingMs = (int)Math.Floor((deadline - DateTime.UtcNow).TotalMilliseconds);
            if (remainingMs <= delayMs) throw new TimeoutException();
            Thread.Sleep(delayMs);
        }

        private string SerializeDictionaryValue(
            IDictionary<string, object> source,
            string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
                return null;

            try
            {
                return _json.Serialize(value);
            }
            catch
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private string BuildProtocolDiagnostic(
            CodexProtocolException exception,
            int attempt,
            string command,
            string standardError,
            long elapsedMs,
            string proxyDescription)
        {
            return "attempt=" + attempt + "/" + MaxRateLimitAttempts +
                   "; code=" + SanitizeLogValue(exception.Code) +
                   "; message=" + SanitizeLogValue(exception.ProtocolMessage) +
                   "; data=" + SanitizeLogValue(exception.ProtocolData) +
                   "; " + BuildFailureDiagnostic(
                       command,
                       standardError,
                       elapsedMs,
                       proxyDescription);
        }

        private string BuildFailureDiagnostic(
            string command,
            string standardError,
            long elapsedMs,
            string proxyDescription)
        {
            bool networkAvailable;
            try
            {
                networkAvailable = NetworkInterface.GetIsNetworkAvailable();
            }
            catch
            {
                networkAvailable = false;
            }

            return "elapsedMs=" + elapsedMs +
                   "; responseTimeoutMs=" + _responseTimeoutMs +
                   "; overallTimeoutMs=" + _overallTimeoutMs +
                   "; networkAvailable=" + networkAvailable +
                   "; proxy=" + SanitizeLogValue(proxyDescription) +
                   "; command=" + SanitizeLogValue(command) +
                   "; stderr=" + SanitizeLogValue(standardError);
        }

        private void LogProxyConfigurationIfChanged(string proxyDescription)
        {
            string normalized = string.IsNullOrWhiteSpace(proxyDescription)
                ? "not-configured"
                : proxyDescription;

            lock (_logGate)
            {
                if (string.Equals(
                    _lastProxyDescription,
                    normalized,
                    StringComparison.Ordinal))
                {
                    return;
                }

                _lastProxyDescription = normalized;
                WriteDiagnosticLog(
                    "INFO",
                    "Codex 网络代理配置",
                    "proxy=" + normalized);
            }
        }

        private void WriteDiagnosticLog(string level, string message, string details)
        {
            if (string.IsNullOrWhiteSpace(_diagnosticLogPath)) return;

            try
            {
                lock (_logGate)
                {
                    string directory = Path.GetDirectoryName(_diagnosticLogPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    if (File.Exists(_diagnosticLogPath) &&
                        new FileInfo(_diagnosticLogPath).Length >= MaxDiagnosticLogBytes)
                    {
                        File.Copy(
                            _diagnosticLogPath,
                            _diagnosticLogPath + ".1",
                            true);
                        File.WriteAllText(
                            _diagnosticLogPath,
                            string.Empty,
                            new UTF8Encoding(false));
                    }

                    string line =
                        DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) +
                        " [" + level + "] " +
                        SanitizeLogValue(message);
                    if (!string.IsNullOrWhiteSpace(details))
                        line += " | " + SanitizeLogValue(details);

                    File.AppendAllText(
                        _diagnosticLogPath,
                        line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // 诊断日志不能影响额度读取主流程。
            }
        }

        private static string SanitizeLogValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "<empty>";
            return AnsiEscapePattern.Replace(value, string.Empty)
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private IDictionary<string, object> DeserializeDictionary(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return _json.DeserializeObject(json) as IDictionary<string, object>;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static void WriteMessage(Process proc, string message)
        {
            proc.StandardInput.WriteLine(message);
            proc.StandardInput.Flush();
        }

        private static void ConfigureRedirectedProcess(ProcessStartInfo startInfo)
        {
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
        }

        internal static string ApplySystemProxy(
            ProcessStartInfo startInfo,
            Func<Uri, Uri> proxyResolver)
        {
            if (startInfo == null) throw new ArgumentNullException("startInfo");
            if (proxyResolver == null) throw new ArgumentNullException("proxyResolver");

            try
            {
                return ApplyProxyEnvironment(
                    startInfo.EnvironmentVariables,
                    proxyResolver);
            }
            catch (ArgumentException)
            {
                return "source=unavailable; https=unchanged; http=unchanged";
            }
        }

        internal static string ApplyProxyEnvironment(
            StringDictionary environment,
            Func<Uri, Uri> proxyResolver)
        {
            if (environment == null) throw new ArgumentNullException("environment");
            if (proxyResolver == null) throw new ArgumentNullException("proxyResolver");

            string httpsProxy = environment["HTTPS_PROXY"];
            string httpProxy = environment["HTTP_PROXY"];

            bool inheritedHttps = !string.IsNullOrWhiteSpace(httpsProxy);
            bool inheritedHttp = !string.IsNullOrWhiteSpace(httpProxy);

            if (!inheritedHttps)
            {
                Uri resolvedHttps = proxyResolver(new Uri("https://chatgpt.com/"));
                if (resolvedHttps != null)
                {
                    httpsProxy = resolvedHttps.AbsoluteUri;
                    environment["HTTPS_PROXY"] = httpsProxy;
                }
            }

            if (!inheritedHttp)
            {
                Uri resolvedHttp = proxyResolver(new Uri("http://chatgpt.com/"));
                if (resolvedHttp != null)
                {
                    httpProxy = resolvedHttp.AbsoluteUri;
                    environment["HTTP_PROXY"] = httpProxy;
                }
            }

            string source =
                inheritedHttps || inheritedHttp
                    ? "environment"
                    : (!string.IsNullOrWhiteSpace(httpsProxy) ||
                       !string.IsNullOrWhiteSpace(httpProxy)
                        ? "windows-system"
                        : "direct");

            return "source=" + source +
                   "; https=" + DescribeProxy(httpsProxy) +
                   "; http=" + DescribeProxy(httpProxy);
        }

        private static Uri ResolveSystemProxy(Uri destination)
        {
            try
            {
                IWebProxy systemProxy = WebRequest.GetSystemWebProxy();
                if (systemProxy == null || systemProxy.IsBypassed(destination))
                    return null;

                Uri proxy = systemProxy.GetProxy(destination);
                if (proxy == null || proxy == destination)
                    return null;

                return proxy;
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeProxy(string proxy)
        {
            if (string.IsNullOrWhiteSpace(proxy)) return "direct";

            Uri uri;
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out uri))
                return "configured";

            return uri.Scheme + "://" + uri.Host +
                   (uri.IsDefaultPort
                       ? string.Empty
                       : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        private static ProcessStartInfo CreateCodexStartInfo()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] directories = path.Split(Path.PathSeparator);

            foreach (string rawDirectory in directories)
            {
                string directory = rawDirectory.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    continue;

                string executable = Path.Combine(directory, "codex.exe");
                if (File.Exists(executable))
                {
                    return new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "app-server"
                    };
                }

                string commandScript = Path.Combine(directory, "codex.cmd");
                if (File.Exists(commandScript))
                    return CreateCommandScriptStartInfo(commandScript);

                string batchScript = Path.Combine(directory, "codex.bat");
                if (File.Exists(batchScript))
                    return CreateCommandScriptStartInfo(batchScript);
            }

            return new ProcessStartInfo
            {
                FileName = "codex.exe",
                Arguments = "app-server"
            };
        }

        private static ProcessStartInfo CreateCommandScriptStartInfo(string scriptPath)
        {
            string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandInterpreter))
                commandInterpreter = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe");

            return new ProcessStartInfo
            {
                FileName = commandInterpreter,
                Arguments = "/d /s /c \"\"" + scriptPath + "\" app-server\""
            };
        }

        private static void ClassifyWindow(
            UsageWindowSnapshot window,
            ref UsageWindowSnapshot shortWindow,
            ref UsageWindowSnapshot weekWindow)
        {
            if (window == null) return;

            if (window.WindowMinutes == ShortWindowMinutes)
                shortWindow = window;
            else if (window.WindowMinutes == WeekWindowMinutes)
                weekWindow = window;
        }

        private static UsageWindowSnapshot ParseWindow(
            IDictionary<string, object> rateLimits,
            string key,
            DateTimeOffset now)
        {
            IDictionary<string, object> window = GetDictionary(rateLimits, key);
            if (window == null) return null;

            int minutes;
            double used;
            long resetsAt;
            if (!TryGetInt(window, "windowDurationMins", out minutes) ||
                !TryGetDouble(window, "usedPercent", out used) ||
                !TryGetLong(window, "resetsAt", out resetsAt))
            {
                return null;
            }

            try
            {
                return new UsageWindowSnapshot
                {
                    WindowMinutes = minutes,
                    UsedPercent = used,
                    ResetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAt),
                    ObservedAt = now,
                    SourceFile = "codex app-server"
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static UsageWindowSnapshot ParseDirectWindow(
            IDictionary<string, object> rateLimit,
            string key,
            DateTimeOffset now)
        {
            IDictionary<string, object> window = GetDictionary(rateLimit, key);
            if (window == null) return null;

            long windowSeconds;
            double used;
            long resetsAt;
            if (!TryGetLong(
                    window,
                    "limit_window_seconds",
                    out windowSeconds) ||
                !TryGetDouble(window, "used_percent", out used) ||
                !TryGetLong(window, "reset_at", out resetsAt) ||
                windowSeconds <= 0 ||
                windowSeconds % 60 != 0 ||
                windowSeconds / 60 > int.MaxValue)
            {
                return null;
            }

            try
            {
                return new UsageWindowSnapshot
                {
                    WindowMinutes = (int)(windowSeconds / 60),
                    UsedPercent = used,
                    ResetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAt),
                    ObservedAt = now,
                    SourceFile = "ChatGPT usage endpoint"
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static IDictionary<string, object> GetDictionary(
            IDictionary<string, object> source,
            string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value)
                ? value as IDictionary<string, object>
                : null;
        }

        private static string GetString(IDictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
        }

        private static bool TryGetInt(
            IDictionary<string, object> source,
            string key,
            out int value)
        {
            object raw;
            if (source != null && source.TryGetValue(key, out raw) && raw != null)
                return int.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out value);

            value = 0;
            return false;
        }

        private static bool TryGetLong(
            IDictionary<string, object> source,
            string key,
            out long value)
        {
            object raw;
            if (source != null && source.TryGetValue(key, out raw) && raw != null)
                return long.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out value);

            value = 0L;
            return false;
        }

        private static bool TryGetDouble(
            IDictionary<string, object> source,
            string key,
            out double value)
        {
            object raw;
            if (source != null && source.TryGetValue(key, out raw) && raw != null)
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out value);

            value = 0d;
            return false;
        }

        private static void CloseInput(Process proc)
        {
            try
            {
                proc.StandardInput.Close();
            }
            catch
            {
            }
        }

        private static void StopProcess(Process proc)
        {
            try
            {
                if (!proc.HasExited) proc.Kill();
            }
            catch
            {
            }

            try
            {
                proc.WaitForExit(1000);
            }
            catch
            {
            }
        }

        private static string GetErrorText(StringBuilder standardError)
        {
            lock (standardError)
            {
                return standardError.ToString().Trim();
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            string fileName = Path.GetFileName(e.FullPath);
            if (!string.IsNullOrEmpty(fileName) &&
                fileName.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
            {
                if (_debounceTimer != null)
                    _debounceTimer.Change(250, Timeout.Infinite);
                else
                    RequestRefresh();
            }
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
            RequestRefresh();
        }

        private void RaiseSnapshot(UsageSnapshot snapshot)
        {
            EventHandler<UsageSnapshot> handler = SnapshotChanged;
            if (handler != null) handler(this, snapshot);
        }

        private sealed class DirectAuthSnapshot
        {
            public DirectAuthSnapshot(string accessToken, string accountId)
            {
                AccessToken = accessToken;
                AccountId = accountId;
            }

            public string AccessToken { get; private set; }
            public string AccountId { get; private set; }
        }

        private sealed class DirectProxySettings
        {
            public DirectProxySettings(IWebProxy proxy, string description)
            {
                Proxy = proxy;
                Description = description;
            }

            public IWebProxy Proxy { get; private set; }
            public string Description { get; private set; }
        }

        private sealed class DirectUsageResult
        {
            public DirectUsageResult(
                UsageSnapshot snapshot,
                string failureMessage)
            {
                Snapshot = snapshot;
                FailureMessage = failureMessage;
            }

            public UsageSnapshot Snapshot { get; private set; }
            public string FailureMessage { get; private set; }
        }

        private sealed class DirectUsageException : Exception
        {
            public DirectUsageException(string message)
                : base(message)
            {
            }
        }

        private sealed class CodexProtocolException : Exception
        {
            public CodexProtocolException(
                string code,
                string protocolMessage,
                string protocolData,
                bool isTransient)
                : base(
                    string.IsNullOrWhiteSpace(protocolMessage)
                        ? "Codex 服务返回协议错误"
                        : "Codex 服务返回错误: " + protocolMessage)
            {
                Code = code;
                ProtocolMessage = protocolMessage;
                ProtocolData = protocolData;
                IsTransient = isTransient;
            }

            public string Code { get; private set; }
            public string ProtocolMessage { get; private set; }
            public string ProtocolData { get; private set; }
            public bool IsTransient { get; private set; }
            public int Attempt { get; set; }
        }
    }
}
