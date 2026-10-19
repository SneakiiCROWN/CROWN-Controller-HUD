using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// File-based diagnostic logger that works without debugger attached.
    /// Logs to %LOCALAPPDATA%\CrownControllerHUD\diagnostic.log
    /// </summary>
    public static class DiagnosticLogger
    {
        private static readonly string LogPath;
        private static readonly object Lock = new();
        private static bool _initialized = false;

        static DiagnosticLogger()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appData, "CrownControllerHUD");
            Directory.CreateDirectory(appFolder);
            LogPath = Path.Combine(appFolder, "diagnostic.log");
        }

        public static void Initialize()
        {
            lock (Lock)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    Write("=== CrownControllerHUD Diagnostic Log Started ===");
                    Write($"Process ID: {Environment.ProcessId}");
                    Write($"Command Line: {Environment.CommandLine}");
                    Write($"Working Directory: {Environment.CurrentDirectory}");
                    Write($"OS: {Environment.OSVersion}");
                    Write($".NET Runtime: {Environment.Version}");
                    Write($"Is64BitProcess: {Environment.Is64BitProcess}");
                    Write($"User Interactive: {Environment.UserInteractive}");
                }
            }
        }

        public static void Write(string message, string category = "INFO")
        {
            lock (Lock)
            {
                try
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var threadId = Thread.CurrentThread.ManagedThreadId;
                    var line = $"[{timestamp}] [{category}] [T{threadId}] {message}";
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Swallow logging errors
                }
            }
        }

        public static void Info(string message) => Write(message, "INFO");
        public static void Debug(string message) => Write(message, "DEBUG");
        public static void Warn(string message) => Write(message, "WARN");
        public static void Error(string message) => Write(message, "ERROR");
        public static void Error(string message, Exception ex) => Write($"{message}: {ex}", "ERROR");

        public static string GetLogPath() => LogPath;
    }
}