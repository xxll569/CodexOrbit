using CodexQuota.Models;
using CodexQuota.Services;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace CodexQuota.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0].StartsWith(
                "--fake-app-server",
                StringComparison.Ordinal))
            {
                RunFakeAppServer(args[0]);
                return;
            }

            if (args.Length == 1 &&
                args[0] == "--real-direct-check")
            {
                RunRealDirectCheck();
                return;
            }

            TestAppServerHandshakeAndWindowClassification();
            TestTransientFailureRetriesAndWritesLog();
            TestSlowFailureGetsFreshRetryTimeout();
            TestPersistentFailureKeepsLastSnapshot();
            TestPrimaryCanBeWeeklyWithoutSecondary();
            TestDirectUsageResponseParsing();
            TestMalformedDirectWindowIsIgnored();
            TestMalformedResponseReturnsStatus();
            TestProcessTimeoutIsEnforced();
            TestWindowsSystemProxyIsInherited();
            TestProxyEnvironmentTakesPrecedenceAndIsRedacted();
            TestSnapshotModelProperties();

            if (_failures > 0)
            {
                Console.Error.WriteLine("失败：" + _failures + " 项测试未通过");
                Environment.Exit(1);
            }

            Console.WriteLine("通过：全部额度测试成功");
        }

        private static void RunRealDirectCheck()
        {
            Console.WriteLine(
                "认证文件=" + CodexUsageReader.GetDefaultAuthPath());
            Console.WriteLine(
                "诊断日志=" + CodexUsageReader.GetDiagnosticLogPath());

            using (var reader = new CodexUsageReader(
                CodexUsageReader.GetDefaultSessionsPath()))
            {
                UsageSnapshot snapshot = reader.ReadLatest();
                Console.WriteLine(
                    "短窗口=" +
                    (snapshot.ShortWindow == null
                        ? "无"
                        : snapshot.ShortWindow.UsedPercent.ToString("0.##") +
                          "% (" + snapshot.ShortWindow.SourceFile + ")"));
                Console.WriteLine(
                    "周窗口=" +
                    (snapshot.WeekWindow == null
                        ? "无"
                        : snapshot.WeekWindow.UsedPercent.ToString("0.##") +
                          "% (" + snapshot.WeekWindow.SourceFile + ")"));
                Console.WriteLine("状态=" + snapshot.StatusMessage);
                if (!snapshot.HasAnyData) Environment.Exit(1);
            }
        }

        private static void TestTransientFailureRetriesAndWritesLog()
        {
            string logPath = Path.Combine(
                Path.GetTempPath(),
                "CodexQuotaTests-" + Guid.NewGuid().ToString("N") + ".log");

            try
            {
                using (var reader = CreateReader(
                    "--fake-app-server-retry",
                    5000,
                    logPath))
                {
                    UsageSnapshot snapshot = reader.ReadLatest();
                    Assert(snapshot.HasAnyData, "瞬时失败后重试应恢复额度数据");
                    Assert(
                        snapshot.WeekWindow != null &&
                        Math.Abs(snapshot.WeekWindow.UsedPercent - 34d) < 0.01,
                        "重试恢复后应返回正确周额度");
                }

                string log = File.ReadAllText(logPath);
                Assert(
                    log.IndexOf("failed to fetch codex rate limits", StringComparison.Ordinal) >= 0,
                    "诊断日志应记录原始额度接口错误");
                Assert(
                    log.IndexOf("code=-32000", StringComparison.Ordinal) >= 0 &&
                    log.IndexOf("\"status\":503", StringComparison.Ordinal) >= 0,
                    "诊断日志应记录错误码和 error.data");
                Assert(
                    log.IndexOf("重试后恢复", StringComparison.Ordinal) >= 0,
                    "诊断日志应记录恢复结果");
            }
            finally
            {
                try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
            }
        }

        private static void TestPersistentFailureKeepsLastSnapshot()
        {
            int launches = 0;
            string logPath = Path.Combine(
                Path.GetTempPath(),
                "CodexQuotaTests-" + Guid.NewGuid().ToString("N") + ".log");

            try
            {
                using (var reader = new CodexUsageReader(
                    "any-path",
                    delegate
                    {
                        launches++;
                        return CreateFakeStartInfo(
                            launches == 1
                                ? "--fake-app-server"
                                : "--fake-app-server-error");
                    },
                    6000,
                    logPath))
                {
                    UsageSnapshot first = reader.ReadLatest();
                    UsageSnapshot second = reader.ReadLatest();

                    Assert(first.HasAnyData, "首次读取应建立成功额度快照");
                    Assert(second.HasAnyData, "连续失败时应保留上次成功额度");
                    Assert(
                        second.StatusMessage != null &&
                        second.StatusMessage.IndexOf(
                            "上次成功数据",
                            StringComparison.Ordinal) >= 0,
                        "连续失败时应明确提示正在显示旧数据");
                    Assert(
                        second.WeekWindow != null &&
                        Math.Abs(second.WeekWindow.UsedPercent - 34d) < 0.01,
                        "连续失败后保留的周额度应与上次成功值一致");
                }
            }
            finally
            {
                try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
            }
        }

        private static void TestSlowFailureGetsFreshRetryTimeout()
        {
            using (var reader = new CodexUsageReader(
                "any-path",
                delegate
                {
                    return CreateFakeStartInfo(
                        "--fake-app-server-slow-retry");
                },
                1000,
                4000,
                null))
            {
                UsageSnapshot snapshot = reader.ReadLatest();
                Assert(
                    snapshot.HasAnyData,
                    "首次慢请求不应耗尽下一次重试的独立超时预算");
                Assert(
                    snapshot.WeekWindow != null &&
                    Math.Abs(snapshot.WeekWindow.UsedPercent - 34d) < 0.01,
                    "慢请求后的重试应返回正确额度");
            }
        }

        private static void TestAppServerHandshakeAndWindowClassification()
        {
            using (var reader = CreateReader("--fake-app-server", 3000))
            {
                UsageSnapshot snapshot = reader.ReadLatest();
                Assert(snapshot.HasAnyData, "完成 app-server 握手后应读取到额度");
                Assert(
                    snapshot.ShortWindow != null &&
                    snapshot.ShortWindow.WindowMinutes == 300 &&
                    Math.Abs(snapshot.ShortWindow.UsedPercent - 12d) < 0.01,
                    "应按窗口分钟数识别 5 小时额度");
                Assert(
                    snapshot.WeekWindow != null &&
                    snapshot.WeekWindow.WindowMinutes == 10080 &&
                    Math.Abs(snapshot.WeekWindow.UsedPercent - 34d) < 0.01,
                    "primary 为周窗口时仍应正确识别周额度");
            }
        }

        private static void TestPrimaryCanBeWeeklyWithoutSecondary()
        {
            const string response =
                "{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":10080,\"resetsAt\":1893456000},\"secondary\":null}}}";

            using (var reader = CreateReader("--fake-app-server", 3000))
            {
                UsageSnapshot snapshot = reader.ParseRateLimitsResponse(response);
                Assert(snapshot.ShortWindow == null, "仅返回周额度时不应误填 5 小时额度");
                Assert(
                    snapshot.WeekWindow != null &&
                    snapshot.WeekWindow.WindowMinutes == 10080,
                    "仅有 primary 周窗口时应填入周额度");
            }
        }

        private static void TestDirectUsageResponseParsing()
        {
            const string response =
                "{\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":true,\"limit_reached\":false,\"primary_window\":{\"used_percent\":34,\"limit_window_seconds\":604800,\"reset_after_seconds\":100,\"reset_at\":1893456000},\"secondary_window\":{\"used_percent\":12,\"limit_window_seconds\":18000,\"reset_after_seconds\":100,\"reset_at\":1893456000}}}";

            using (var reader = CreateReader("--fake-app-server", 3000))
            {
                UsageSnapshot snapshot =
                    reader.ParseDirectRateLimitsResponse(response);

                Assert(
                    snapshot.ShortWindow != null &&
                    snapshot.ShortWindow.WindowMinutes == 300 &&
                    Math.Abs(snapshot.ShortWindow.UsedPercent - 12d) < 0.01,
                    "直接用量响应应正确识别 5 小时窗口");
                Assert(
                    snapshot.WeekWindow != null &&
                    snapshot.WeekWindow.WindowMinutes == 10080 &&
                    Math.Abs(snapshot.WeekWindow.UsedPercent - 34d) < 0.01,
                    "直接用量响应应正确识别周窗口");
                Assert(
                    snapshot.WeekWindow != null &&
                    snapshot.WeekWindow.SourceFile ==
                        "ChatGPT usage endpoint",
                    "直接用量快照应标记数据来源");
            }
        }

        private static void TestMalformedDirectWindowIsIgnored()
        {
            const string response =
                "{\"rate_limit\":{\"primary_window\":{\"used_percent\":34,\"limit_window_seconds\":123,\"reset_at\":1893456000},\"secondary_window\":null}}";

            using (var reader = CreateReader("--fake-app-server", 3000))
            {
                UsageSnapshot snapshot =
                    reader.ParseDirectRateLimitsResponse(response);

                Assert(
                    !snapshot.HasAnyData,
                    "非整分钟的直接用量窗口不应生成额度数据");
                Assert(
                    !string.IsNullOrWhiteSpace(snapshot.StatusMessage),
                    "直接用量窗口格式异常时应返回明确状态");
            }
        }

        private static void TestMalformedResponseReturnsStatus()
        {
            using (var reader = CreateReader("--fake-app-server", 3000))
            {
                UsageSnapshot snapshot = reader.ParseRateLimitsResponse(
                    "{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10}}}}");

                Assert(!snapshot.HasAnyData, "字段不完整的窗口不应生成额度数据");
                Assert(
                    !string.IsNullOrWhiteSpace(snapshot.StatusMessage),
                    "字段不完整时应返回明确状态");
            }
        }

        private static void TestProcessTimeoutIsEnforced()
        {
            var stopwatch = Stopwatch.StartNew();
            using (var reader = CreateReader("--fake-app-server-hang", 200))
            {
                UsageSnapshot snapshot = reader.ReadLatest();
                stopwatch.Stop();
                Assert(
                    snapshot.StatusMessage != null &&
                    snapshot.StatusMessage.IndexOf("超时", StringComparison.Ordinal) >= 0,
                    "app-server 无响应时应返回超时状态");
                Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "超时应真正终止阻塞读取");
            }
        }

        private static void TestSnapshotModelProperties()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            var window = new UsageWindowSnapshot
            {
                WindowMinutes = 300,
                UsedPercent = 32,
                ResetsAt = now.AddHours(2),
                ObservedAt = now
            };
            Assert(Math.Abs(window.RemainingPercent - 68d) < 0.01, "剩余应为 68%");
            Assert(!window.IsExpired(now), "未过期窗口不应标记为过期");
            Assert(window.IsExpired(now.AddHours(3)), "过期窗口应正确检测");

            var unused = new UsageWindowSnapshot
            {
                WindowMinutes = 10080,
                UsedPercent = 0,
                ResetsAt = now.AddDays(7),
                ObservedAt = now
            };
            Assert(unused.IsUnusedInCurrentWindow, "使用量为 0 时应标记为新周期");
            Assert(Math.Abs(unused.RemainingPercent - 100d) < 0.01, "新周期剩余应为 100%");
        }

        private static void TestWindowsSystemProxyIsInherited()
        {
            var environment = new StringDictionary();

            string description = CodexUsageReader.ApplyProxyEnvironment(
                environment,
                delegate(Uri destination)
                {
                    return new Uri("http://127.0.0.1:7897/");
                });

            Assert(
                environment["HTTPS_PROXY"] ==
                    "http://127.0.0.1:7897/",
                "应把 Windows 系统代理传给 Codex 子进程");
            Assert(
                environment["HTTP_PROXY"] ==
                    "http://127.0.0.1:7897/",
                "HTTP 请求也应继承 Windows 系统代理");
            Assert(
                description.IndexOf("source=windows-system", StringComparison.Ordinal) >= 0 &&
                description.IndexOf("127.0.0.1:7897", StringComparison.Ordinal) >= 0,
                "代理诊断应记录系统代理来源和脱敏地址");
        }

        private static void TestProxyEnvironmentTakesPrecedenceAndIsRedacted()
        {
            var environment = new StringDictionary();
            environment["HTTPS_PROXY"] =
                "http://user:secret@127.0.0.1:9000/";

            string description = CodexUsageReader.ApplyProxyEnvironment(
                environment,
                delegate(Uri destination)
                {
                    return new Uri("http://127.0.0.1:7897/");
                });

            Assert(
                environment["HTTPS_PROXY"] ==
                    "http://user:secret@127.0.0.1:9000/",
                "已有代理环境变量时不应被系统代理覆盖");
            Assert(
                description.IndexOf("source=environment", StringComparison.Ordinal) >= 0 &&
                description.IndexOf("127.0.0.1:9000", StringComparison.Ordinal) >= 0,
                "代理诊断应标记环境变量来源");
            Assert(
                description.IndexOf("user", StringComparison.Ordinal) < 0 &&
                description.IndexOf("secret", StringComparison.Ordinal) < 0,
                "代理诊断不得泄露用户名或密码");
        }

        private static CodexUsageReader CreateReader(string mode, int timeoutMs)
        {
            return CreateReader(mode, timeoutMs, null);
        }

        private static CodexUsageReader CreateReader(
            string mode,
            int timeoutMs,
            string diagnosticLogPath)
        {
            return new CodexUsageReader(
                "any-path",
                delegate { return CreateFakeStartInfo(mode); },
                timeoutMs,
                diagnosticLogPath);
        }

        private static ProcessStartInfo CreateFakeStartInfo(string mode)
        {
            return new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = mode
            };
        }

        private static void RunFakeAppServer(string mode)
        {
            if (mode == "--fake-app-server-hang")
            {
                Thread.Sleep(5000);
                return;
            }

            string initialize = Console.ReadLine();
            if (initialize == null ||
                initialize.IndexOf("\"method\":\"initialize\"", StringComparison.Ordinal) < 0)
            {
                return;
            }

            Console.WriteLine(
                "{\"id\":1,\"result\":{\"userAgent\":\"fake-codex\",\"codexHome\":\"C:\\\\fake\"}}");
            Console.Out.Flush();

            string initialized = Console.ReadLine();
            string rateLimitRequest = Console.ReadLine();
            bool valid =
                initialized != null &&
                initialized.IndexOf("\"method\":\"initialized\"", StringComparison.Ordinal) >= 0 &&
                rateLimitRequest != null &&
                rateLimitRequest.IndexOf("\"method\":\"account/rateLimits/read\"", StringComparison.Ordinal) >= 0 &&
                rateLimitRequest.IndexOf("\"params\":null", StringComparison.Ordinal) >= 0;

            if (!valid)
            {
                Console.WriteLine(
                    "{\"id\":2,\"error\":{\"code\":-32602,\"message\":\"invalid handshake\"}}");
                Console.Out.Flush();
                return;
            }

            int responseId = 2;
            while (true)
            {
                bool returnError =
                    mode == "--fake-app-server-error" ||
                    ((mode == "--fake-app-server-retry" ||
                      mode == "--fake-app-server-slow-retry") &&
                     responseId == 2);

                if (returnError)
                {
                    if (mode == "--fake-app-server-slow-retry" && responseId == 2)
                        Thread.Sleep(700);

                    Console.WriteLine(
                        "{\"id\":" + responseId +
                        ",\"error\":{\"code\":-32000,\"message\":\"failed to fetch codex rate limits:\",\"data\":{\"kind\":\"backend\",\"status\":503}}}");
                    Console.Out.Flush();

                    if (mode == "--fake-app-server-error" && responseId >= 4)
                        return;

                    rateLimitRequest = Console.ReadLine();
                    responseId++;
                    if (rateLimitRequest == null ||
                        rateLimitRequest.IndexOf(
                            "\"id\":" + responseId,
                            StringComparison.Ordinal) < 0)
                    {
                        return;
                    }
                    continue;
                }

                Console.WriteLine(
                    "{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
                Console.WriteLine(
                    "{\"id\":" + responseId +
                    ",\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":34,\"windowDurationMins\":10080,\"resetsAt\":1893456000},\"secondary\":{\"usedPercent\":12,\"windowDurationMins\":300,\"resetsAt\":1893456000}}}}");
                Console.Out.Flush();
                return;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (condition) return;
            _failures++;
            Console.Error.WriteLine("未通过：" + message);
        }
    }
}
