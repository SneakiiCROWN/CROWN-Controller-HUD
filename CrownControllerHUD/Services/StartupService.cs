using System;
using System.Diagnostics;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CrownControllerHUD.Models;

namespace CrownControllerHUD.Services
{
    public interface IStartupService
    {
        bool IsEnabled { get; }
        void SetEnabled(bool enabled);
        string GetExecutablePath();
    }

    public class StartupService : IStartupService
    {
        private readonly ILogger<StartupService> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private const string AppName = "CrownControllerHUD";
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                    var value = key?.GetValue(AppName) as string;
                    return !string.IsNullOrEmpty(value);
                }
                catch
                {
                    return false;
                }
            }
        }

        public StartupService(ILogger<StartupService> logger, IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null)
                {
                    _logger.LogError("Could not open Run registry key");
                    return;
                }

                if (enabled)
                {
                    var exePath = GetExecutablePath();
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                    _logger.LogInformation("Startup enabled: {Path}", exePath);
                }
                else
                {
                    key.DeleteValue(AppName, false);
                    _logger.LogInformation("Startup disabled");
                }

                _settings.CurrentValue.General.StartWithWindows = enabled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set startup state");
            }
        }

        public string GetExecutablePath()
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly();
            return assembly?.Location ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        }
    }
}