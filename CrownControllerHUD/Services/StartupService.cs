using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Manages Windows startup registration via the per-user Run registry key.
    /// No administrator privileges required. No background service or scheduled task.
    /// </summary>
    public interface IStartupService
    {
        bool IsStartupEnabled { get; }
        bool IsEnabled { get; }
        bool EnableStartup();
        bool DisableStartup();
        bool SetEnabled(bool enabled);
        bool IsRegisteredPathValid(string? registeredValue = null);
    }

    public class StartupService : IStartupService
    {
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RegistryValueName = "CrownControllerHUD";

        private readonly ILogger<StartupService> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;

        public StartupService(ILogger<StartupService> logger, IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public bool IsStartupEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                    if (key == null) return false;

                    var value = key.GetValue(RegistryValueName) as string;
                    if (string.IsNullOrEmpty(value)) return false;

                    // Verify the path actually points to our executable
                    return IsRegisteredPathValid(value);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("IsStartupEnabled check failed", ex);
                    _logger.LogError(ex, "Failed to check startup status");
                    return false;
                }
            }
        }

        public bool IsEnabled => IsStartupEnabled;

        public bool SetEnabled(bool enabled)
        {
            if (enabled)
                return EnableStartup();
            else
                return DisableStartup();
        }

        public bool EnableStartup()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    DiagnosticLogger.Error("EnableStartup: executable path not found");
                    _logger.LogError("Cannot enable startup: executable path not found");
                    return false;
                }

                // Use quotes around the path in case it contains spaces
                var command = $"\"{exePath}\" --hidden";

                using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
                if (key == null)
                {
                    DiagnosticLogger.Error("EnableStartup: could not open registry key");
                    return false;
                }

                key.SetValue(RegistryValueName, command, RegistryValueKind.String);

                // Update settings
                _settings.CurrentValue.General.StartWithWindows = true;
                _settings.CurrentValue.General.StartMinimized = true;

                DiagnosticLogger.Info($"[Startup] Enabled: {command}");
                _logger.LogInformation("Windows startup enabled: {Command}", command);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("EnableStartup failed", ex);
                _logger.LogError(ex, "Failed to enable Windows startup");
                return false;
            }
        }

        public bool DisableStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                if (key != null)
                {
                    key.DeleteValue(RegistryValueName, false);
                }

                _settings.CurrentValue.General.StartWithWindows = false;

                DiagnosticLogger.Info("[Startup] Disabled");
                _logger.LogInformation("Windows startup disabled");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("DisableStartup failed", ex);
                _logger.LogError(ex, "Failed to disable Windows startup");
                return false;
            }
        }

        public bool IsRegisteredPathValid(string? registeredValue = null)
        {
            try
            {
                if (string.IsNullOrEmpty(registeredValue))
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                    if (key == null) return false;
                    registeredValue = key.GetValue(RegistryValueName) as string;
                    if (string.IsNullOrEmpty(registeredValue)) return true; // Not registered = valid (nothing to check)
                }

                // Extract the path from the command (handle quotes)
                var path = registeredValue!.Trim();
                if (path.StartsWith("\""))
                {
                    var endQuote = path.IndexOf('"', 1);
                    if (endQuote > 0)
                        path = path.Substring(1, endQuote - 1);
                }
                else
                {
                    // No quotes - take up to first space or end
                    var spaceIndex = path.IndexOf(' ');
                    if (spaceIndex > 0)
                        path = path.Substring(0, spaceIndex);
                }

                // Check if the file exists
                return File.Exists(path);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("IsRegisteredPathValid failed", ex);
                return false;
            }
        }
    }
}