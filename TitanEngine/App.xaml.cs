using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TitanEngine
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Crash log lives next to the other TitanEngine temp artifacts, bounded at 2 MB.</summary>
        private const long MaxCrashLogBytes = 2L * 1024L * 1024L;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        /// <summary>
        /// Writes any unhandled UI-thread exception to <c>%TEMP%\TitanEngine\crash.log</c> before the
        /// default crash behavior takes over. Purely diagnostic: <see cref="DispatcherUnhandledExceptionEventArgs.Handled"/>
        /// is deliberately left false so the app still terminates exactly as it did before - the log only
        /// leaves evidence behind. The write itself is guarded: a logging failure must never mask or
        /// change the original exception.
        /// </summary>
        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "TitanEngine");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "crash.log");

                try
                {
                    if (new FileInfo(logPath).Length > MaxCrashLogBytes)
                        File.Delete(logPath);
                }
                catch
                {
                    // Rotation is best-effort; appending is what matters.
                }

                var sb = new StringBuilder(4096);
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                sb.AppendLine(e.Exception.ToString());
                File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Never let the logger throw: the original exception must reach the default handler.
            }
        }
    }
}
