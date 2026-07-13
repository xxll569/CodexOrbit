using CodexQuota.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace CodexQuota.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static void Main()
        {
            TestPrimaryAndSecondaryAreClassifiedByMinutes();
            TestSwappedWindowsStillParse();
            TestMalformedAndUnrelatedLinesAreIgnored();
            TestLatestSnapshotWinsAcrossFiles();
            TestNestedRolloutChangeTriggersRefresh();

            if (_failures > 0)
            {
                Console.Error.WriteLine("失败：" + _failures + " 项测试未通过");
                Environment.Exit(1);
            }

            Console.WriteLine("通过：全部额度解析测试成功");
        }

        private static void TestPrimaryAndSecondaryAreClassifiedByMinutes()
        {
            using (var reader = new CodexUsageReader("missing"))
            {
                var parsed = reader.ParseLine(Line("2026-07-13T02:05:00Z", 300, 32, 1784500000, 10080, 37, 1784600000), "fixture");
                Assert(parsed.Count == 2, "应解析两个额度窗口");
                Assert(parsed.Single(x => x.WindowMinutes == 300).RemainingPercent == 68, "5 小时剩余应为 68%");
                Assert(parsed.Single(x => x.WindowMinutes == 10080).RemainingPercent == 63, "周剩余应为 63%");
            }
        }

        private static void TestSwappedWindowsStillParse()
        {
            using (var reader = new CodexUsageReader("missing"))
            {
                var parsed = reader.ParseLine(Line("2026-07-13T02:05:00Z", 10080, 20, 1784600000, 300, 80, 1784500000), "fixture");
                Assert(parsed.Single(x => x.WindowMinutes == 300).UsedPercent == 80, "不应依赖 primary/secondary 顺序");
            }
        }

        private static void TestMalformedAndUnrelatedLinesAreIgnored()
        {
            using (var reader = new CodexUsageReader("missing"))
            {
                Assert(reader.ParseLine("not json rate_limits token_count", "fixture").Count == 0, "非法 JSON 应忽略");
                Assert(reader.ParseLine("{\"type\":\"response_item\",\"rate_limits\":{},\"token_count\":1}", "fixture").Count == 0, "非额度事件应忽略");
            }
        }

        private static void TestLatestSnapshotWinsAcrossFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexQuotaTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "rollout-old.jsonl"), Line("2026-07-12T02:05:00Z", 300, 90, 1784500000, 10080, 40, 1784600000));
                File.WriteAllText(Path.Combine(root, "rollout-new.jsonl"), Line("2026-07-13T02:05:00Z", 300, 10, 1784500000, 10080, 20, 1784600000));
                using (var reader = new CodexUsageReader(root))
                {
                    var snapshot = reader.ReadLatest();
                    Assert(snapshot.ShortWindow != null && snapshot.ShortWindow.UsedPercent == 10, "应采用事件时间最新的短时额度");
                    Assert(snapshot.WeekWindow != null && snapshot.WeekWindow.UsedPercent == 20, "应采用事件时间最新的周额度");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestNestedRolloutChangeTriggersRefresh()
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexQuotaWatcherTests-" + Guid.NewGuid().ToString("N"));
            string nested = Path.Combine(root, "2026", "07", "13");
            Directory.CreateDirectory(nested);
            try
            {
                using (var changed = new AutoResetEvent(false))
                using (var reader = new CodexUsageReader(root))
                {
                    reader.SnapshotChanged += delegate(object sender, CodexQuota.Models.UsageSnapshot snapshot)
                    {
                        if (snapshot.ShortWindow != null && snapshot.ShortWindow.UsedPercent == 12)
                            changed.Set();
                    };

                    reader.StartWatching();
                    File.WriteAllText(
                        Path.Combine(nested, "rollout-watcher.jsonl"),
                        Line(DateTimeOffset.UtcNow.ToString("O"), 300, 12, 1893456000, 10080, 34, 1893456000));

                    Assert(changed.WaitOne(TimeSpan.FromSeconds(5)), "嵌套目录中的日志变化应触发自动刷新");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static string Line(string timestamp, int pMinutes, double pUsed, long pReset, int sMinutes, double sUsed, long sReset)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\"timestamp\":\"{0}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"rate_limits\":{{\"primary\":{{\"used_percent\":{1},\"window_minutes\":{2},\"resets_at\":{3}}},\"secondary\":{{\"used_percent\":{4},\"window_minutes\":{5},\"resets_at\":{6}}}}}}}}}",
                timestamp, pUsed, pMinutes, pReset, sUsed, sMinutes, sReset);
        }

        private static void Assert(bool condition, string message)
        {
            if (condition) return;
            _failures++;
            Console.Error.WriteLine("未通过：" + message);
        }
    }
}
