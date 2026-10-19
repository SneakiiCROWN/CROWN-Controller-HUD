using System.Linq;
using CrownControllerHUD.Models;

namespace CrownControllerHUD.Tests
{
    /// <summary>
    /// A hand-edited or half-written settings file must never be able to put nonsense
    /// ranges into the running app.
    /// Reconstructed after Git history cleanup lost the originals.
    /// </summary>
    public class SettingsNormalizeTests
    {
        [Fact]
        public void Missing_battery_warning_ladder_is_replaced_with_the_defaults()
        {
            var settings = new AppSettings();
            settings.Notifications.BatteryWarnings.Clear();

            settings.Normalize();

            Assert.Equal(new[] { 50, 20, 15, 10, 5 }, settings.Notifications.BatteryWarnings.Select(l => l.Percent));
        }

        [Fact]
        public void A_null_ladder_is_replaced_with_the_defaults()
        {
            var settings = new AppSettings();
            settings.Notifications.BatteryWarnings = null!;

            settings.Normalize();

            Assert.Equal(5, settings.Notifications.BatteryWarnings.Count);
        }

        [Fact]
        public void The_ladder_is_deduplicated_and_ordered_from_highest_to_lowest()
        {
            var settings = new AppSettings();
            settings.Notifications.BatteryWarnings = new System.Collections.Generic.List<BatteryWarningLevel>
            {
                new BatteryWarningLevel(10),
                new BatteryWarningLevel(90),
                new BatteryWarningLevel(10),
                new BatteryWarningLevel(50)
            };

            settings.Normalize();

            Assert.Equal(new[] { 90, 50, 10 }, settings.Notifications.BatteryWarnings.Select(l => l.Percent));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-20, 1)]
        [InlineData(1000, 99)]
        [InlineData(37, 37)]
        public void Warning_levels_are_clamped_to_a_sane_percentage(int input, int expected)
        {
            var settings = new AppSettings();
            settings.Notifications.BatteryWarnings = new System.Collections.Generic.List<BatteryWarningLevel>
            {
                new BatteryWarningLevel(input)
            };

            settings.Normalize();

            Assert.Equal(expected, settings.Notifications.BatteryWarnings.Single().Percent);
        }

        [Fact]
        public void Absurd_battery_intervals_are_brought_back_into_range()
        {
            var settings = new AppSettings();
            settings.Battery.RefreshIntervalSeconds = 1;
            settings.Battery.GattTimeoutMs = 100;
            settings.Battery.StaleAfterSeconds = 5;

            settings.Normalize();

            Assert.Equal(30, settings.Battery.RefreshIntervalSeconds);
            Assert.Equal(1000, settings.Battery.GattTimeoutMs);
            Assert.True(settings.Battery.StaleAfterSeconds >= settings.Battery.RefreshIntervalSeconds);
        }

        [Fact]
        public void Default_battery_settings_never_poll_aggressively()
        {
            var settings = new AppSettings();

            Assert.True(settings.Battery.RefreshIntervalSeconds >= 60);
            Assert.True(settings.Battery.StaleAfterSeconds >= settings.Battery.RefreshIntervalSeconds);
        }

        [Fact]
        public void Notification_duration_and_thresholds_are_clamped()
        {
            var settings = new AppSettings();
            settings.Notifications.NotificationDurationMs = 0;
            settings.Notifications.LowBatteryThreshold = 500;
            settings.Notifications.CriticalBatteryThreshold = -3;

            settings.Normalize();

            Assert.True(settings.Notifications.NotificationDurationMs >= 1500);
            Assert.InRange(settings.Notifications.LowBatteryThreshold, 1, 99);
            Assert.InRange(settings.Notifications.CriticalBatteryThreshold, 1, 99);
        }

        [Fact]
        public void Battery_warnings_are_enabled_by_default()
        {
            var settings = new AppSettings();

            Assert.True(settings.Battery.EnableBatteryTelemetry);
            Assert.True(settings.Notifications.NotifyOnBatteryWarnings);
            Assert.All(settings.Notifications.BatteryWarnings, level => Assert.True(level.Enabled));
        }
    }
}
