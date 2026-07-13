using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace CodexQuota
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            string previewPath = null;
            if (e.Args != null && e.Args.Length == 2 &&
                string.Equals(e.Args[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
            {
                previewPath = e.Args[1];
            }

            bool createdNew;
            string mutexName = previewPath == null
                ? "CodexOrbit.Wpf.SingleInstance"
                : "CodexOrbit.Wpf.Preview." + Process.GetCurrentProcess().Id;
            _singleInstance = new Mutex(true, mutexName, out createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            // WPF 的字体缓存使用 WINDIR 组装字体目录。在部分精简环境中该变量
            // 可能缺失，但 SystemRoot 仍然存在，此时主动补齐以避免窗口初始化失败。
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
            {
                string systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                if (!string.IsNullOrWhiteSpace(systemRoot))
                    Environment.SetEnvironmentVariable("WINDIR", systemRoot, EnvironmentVariableTarget.Process);
            }

            base.OnStartup(e);
            MainWindow = new MainWindow(previewPath);
            MainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstance != null)
            {
                if (_ownsMutex) _singleInstance.ReleaseMutex();
                _singleInstance.Dispose();
            }
            base.OnExit(e);
        }
    }
}
