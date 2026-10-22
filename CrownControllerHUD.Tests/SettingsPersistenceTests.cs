using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CrownControllerHUD.Tests
{
    /// <summary>
    /// Regression tests for settings persistence.
    /// These tests verify that settings are correctly saved/loaded across restarts.
    /// </summary>
    public class SettingsPersistenceTests
    {
        private static SettingsService CreateSettingsService(string testFolder, ILogger<SettingsService>? logger = null)
        {
            var factory = LoggerFactory.Create(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));
            var actualLogger = logger ?? factory.CreateLogger<SettingsService>();
            var service = new SettingsService(actualLogger);
            // Override the settings path for testing
            var field = typeof(SettingsService).GetField("_settingsPath", BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(service, Path.Combine(testFolder, "settings.json"));
            return service;
        }

        [Fact]
        public async Task Settings_round_trip_preserves_all_values()
        {
            var testFolder = Path.Combine(Path.GetTempPath(), "CrownTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);

            try
            {
                var service = CreateSettingsService(testFolder);
                await service.LoadAsync();

                // Modify various settings
                var s = service.Settings;
                s.General.StartWithWindows = true;
                s.General.StartMinimized = true;
                s.General.Theme = "Dark";
                s.Notifications.EnableNotifications = false;
                s.Notifications.NotificationDurationMs = 3000;
                s.Notifications.BatteryWarnings[0].Enabled = false;
                s.UI.WindowWidth = 1200;
                s.UI.WindowHeight = 800;
                s.Controllers.ShowDisconnectedControllers = true;
                s.Controllers.HiddenControllerIds.Add("test-id-1");
                s.Controllers.Overrides["test-id-2"] = new ControllerOverrideSettings
                {
                    CustomName = "My Controller",
                    EnableNotifications = false,
                    CustomLowBatteryThreshold = 10
                };
                s.Advanced.EnableDebugLogging = true;
                s.Battery.RefreshIntervalSeconds = 600;

                await service.SaveAsync();

                // Create new service instance to simulate app restart
                var service2 = CreateSettingsService(testFolder);
                await service2.LoadAsync();

                var s2 = service2.Settings;
                Assert.True(s2.General.StartWithWindows);
                Assert.True(s2.General.StartMinimized);
                Assert.Equal("Dark", s2.General.Theme);
                Assert.False(s2.Notifications.EnableNotifications);
                Assert.Equal(3000, s2.Notifications.NotificationDurationMs);
                Assert.False(s2.Notifications.BatteryWarnings[0].Enabled);
                Assert.Equal(1200, s2.UI.WindowWidth);
                Assert.Equal(800, s2.UI.WindowHeight);
                Assert.True(s2.Controllers.ShowDisconnectedControllers);
                Assert.Contains("test-id-1", s2.Controllers.HiddenControllerIds);
                Assert.True(s2.Controllers.Overrides.ContainsKey("test-id-2"));
                Assert.Equal("My Controller", s2.Controllers.Overrides["test-id-2"].CustomName);
                Assert.False(s2.Controllers.Overrides["test-id-2"].EnableNotifications);
                Assert.Equal(10, s2.Controllers.Overrides["test-id-2"].CustomLowBatteryThreshold);
                Assert.True(s2.Advanced.EnableDebugLogging);
                Assert.Equal(600, s2.Battery.RefreshIntervalSeconds);
            }
            finally
            {
                try { Directory.Delete(testFolder, true); } catch { }
            }
        }

        [Fact]
        public async Task Settings_load_handles_corrupted_json_gracefully()
        {
            var testFolder = Path.Combine(Path.GetTempPath(), "CrownTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);

            try
            {
                // Write corrupted JSON
                var settingsPath = Path.Combine(testFolder, "settings.json");
                File.WriteAllText(settingsPath, "{ invalid json ");

                var service = CreateSettingsService(testFolder);
                await service.LoadAsync();

                // Should fall back to defaults without throwing
                var s = service.Settings;
                Assert.NotNull(s);
                Assert.NotNull(s.General);
                Assert.NotNull(s.Notifications);
                Assert.NotNull(s.Controllers);
            }
            finally
            {
                try { Directory.Delete(testFolder, true); } catch { }
            }
        }

        [Fact]
        public async Task Settings_load_handles_missing_file_gracefully()
        {
            var testFolder = Path.Combine(Path.GetTempPath(), "CrownTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);

            try
            {
                var service = CreateSettingsService(testFolder);
                await service.LoadAsync();

                // Should use defaults without throwing
                var s = service.Settings;
                Assert.NotNull(s);
                Assert.NotNull(s.General);
                Assert.NotNull(s.Notifications);
                Assert.NotNull(s.Controllers);
            }
            finally
            {
                try { Directory.Delete(testFolder, true); } catch { }
            }
        }

        [Fact]
        public void Settings_changed_event_fires_on_notify_section_changed()
        {
            var testFolder = Path.Combine(Path.GetTempPath(), "CrownTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);

            try
            {
                var service = CreateSettingsService(testFolder);
                // LoadAsync is not strictly needed for this test

                string? changedSection = null;
                service.SettingsChanged += (s, e) => changedSection = e.Section;

                service.NotifySectionChanged("General");

                Assert.Equal("General", changedSection);
            }
            finally
            {
                try { Directory.Delete(testFolder, true); } catch { }
            }
        }

        [Fact]
        public async Task ResetToDefaults_restores_all_values()
        {
            var testFolder = Path.Combine(Path.GetTempPath(), "CrownTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);

            try
            {
                var service = CreateSettingsService(testFolder);
                await service.LoadAsync();

                // Modify settings
                service.Settings.General.StartWithWindows = true;
                service.Settings.Notifications.EnableNotifications = false;
                await service.SaveAsync();

                // Reset
                service.ResetToDefaults();

                var s = service.Settings;
                Assert.False(s.General.StartWithWindows);
                Assert.True(s.Notifications.EnableNotifications);
                Assert.Equal(5, s.Notifications.BatteryWarnings.Count);
            }
            finally
            {
                try { Directory.Delete(testFolder, true); } catch { }
            }
        }
    }
}