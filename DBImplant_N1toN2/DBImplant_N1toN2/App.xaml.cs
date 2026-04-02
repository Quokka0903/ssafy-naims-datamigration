using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DBImplant_N1toN2
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "crash.log");

        static App()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // UI 스레드 예외
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 비UI 스레드 예외
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // Task 내 미관찰 예외
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            WriteLog("=== 앱 시작 ===");
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteLog($"[UI 스레드 예외] {e.Exception}");
            MessageBox.Show(
                $"예기치 않은 오류가 발생했습니다.\n\n{e.Exception.Message}\n\n상세 로그: {LogPath}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true; // 앱 종료 방지
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            WriteLog($"[AppDomain 예외] {ex}");
            MessageBox.Show(
                $"심각한 오류가 발생했습니다.\n\n{ex?.Message}\n\n상세 로그: {LogPath}",
                "심각한 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteLog($"[Task 예외] {e.Exception}");
            e.SetObserved(); // 앱 종료 방지
        }

        public static void WriteLog(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch { /* 로그 쓰기 자체 실패는 무시 */ }
        }
    }
}
