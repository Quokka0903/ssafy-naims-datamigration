using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace RawCollectMigrator
{
    public partial class App : Application
    {
        // [찾기:전역예외] 미처리 예외로 앱이 통째로 죽는 것을 막는 최후 방어선.
        /// <summary>크래시 로그 파일 경로 (exe 옆 logs\crash.log, append).</summary>
        private static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, "logs", "crash.log");

        /// <summary>여러 스레드(UI·태스크·도메인)가 동시에 기록해도 안전하도록 파일 접근을 직렬화한다.</summary>
        private static readonly object CrashLogLock = new();

        /// <summary>전역 예외 핸들러 3종을 등록한다. (UI 스레드 / 미관측 Task / 그 외 모든 스레드)</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            // [찾기:접속설정] 지난 실행의 접속·경로 설정을 복원한다(비밀번호는 저장 옵션에 따름).
            //   검증 상태는 복원하지 않는다 — 매 실행마다 시작 화면에서 다시 확인해야 한다.
            Infrastructure.AppSession.Load();

            base.OnStartup(e);
        }

        /// <summary>UI 스레드 미처리 예외 — 로그 기록 + 안내 후 e.Handled=true 로 앱을 살린다.</summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog("DispatcherUnhandledException", e.Exception);
            try
            {
                MessageBox.Show(
                    "예기치 않은 오류가 발생했습니다.\n\n" + MaskSecrets(e.Exception.Message) +
                    "\n\n자세한 내용은 logs\\crash.log 를 확인하세요.",
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 안내 실패가 2차 크래시가 되지 않게 한다 */ }
            e.Handled = true;   // 앱 생존
        }

        /// <summary>미관측 Task 예외 — 관측 처리(SetObserved)로 프로세스 종료를 막고 로그만 남긴다.</summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        /// <summary>그 외 스레드의 미처리 예외 — 프로세스 종료는 막을 수 없으므로 로그라도 남긴다.</summary>
        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrashLog(
                "AppDomain.UnhandledException" + (e.IsTerminating ? " (종료 중)" : ""),
                e.ExceptionObject as Exception);
        }

        /// <summary>예외를 crash.log 에 append 기록한다. 기록 실패는 무시(크래시 처리 중 2차 크래시 방지).</summary>
        private static void WriteCrashLog(string source, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                var sb = new StringBuilder();
                sb.AppendLine($"════ {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ════");
                sb.AppendLine(MaskSecrets(ex?.ToString() ?? "(예외 정보 없음)"));
                lock (CrashLogLock)
                    File.AppendAllText(CrashLogPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* 로그 기록 실패로 또 죽지 않는다 */ }
        }

        /// <summary>예외 메시지에 접속문자열이 섞일 수 있으므로 password/pwd 값을 마스킹한다.</summary>
        private static string MaskSecrets(string s) =>
            Regex.Replace(s, @"(password|pwd)\s*=\s*[^;""'\s]*", "$1=****", RegexOptions.IgnoreCase);
    }
}
