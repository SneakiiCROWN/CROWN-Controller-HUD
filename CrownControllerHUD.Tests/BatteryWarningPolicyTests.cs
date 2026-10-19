using System.Collections.Generic;
using System.Linq;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;

namespace CrownControllerHUD.Tests
{
    /// <summary>
    /// The battery warning policy is the only thing standing between "noisy repeated
    /// warnings" and "useful alerts", so its behaviour is pinned down here. These tests
    /// are pure logic - no hardware, no Windows APIs.
    /// Reconstructed after Git history cleanup lost the originals.
    /// </summary>
    public class BatteryWarningPolicyTests
    {
        private const string Device = "BTHLE\\DEV_3CFA061915B2";

        private static NotificationSettings Settings() => new NotificationSettings();

        private static BatteryWarningPolicy Policy(NotificationSettings settings) => new(() => settings);

        private static IEnumerable<BatteryWarningDecision> Ladder(BatteryWarningPolicy policy, params int[] percentages) =>
            percentages.Select(p => policy.Evaluate(Device, p, BatteryReading.StateFromPercentage(p), charging: false));

        [Fact]
        public void Appearing_already_empty_announces_only_the_most_severe_level()
        {
            var policy = Policy(Settings());

            var decision = policy.Evaluate(Device, 3, BatteryState.Critical, charging: false);

            Assert.True(decision.ShouldNotify);
            Assert.Equal(BatteryWarningKind.Threshold, decision.Kind);
            Assert.Equal(5, decision.ThresholdPercent);
            Assert.True(decision.IsCritical);
            Assert.Equal(3, decision.Percentage);
        }

        [Fact]
        public void A_full_discharge_announces_each_level_exactly_once_in_order()
        {
            var policy = Policy(Settings());

            var decisions = Ladder(policy, 45, 30, 18, 12, 8, 3, 2).ToList();

            Assert.Equal(
                new int?[] { 50, null, 20, 15, 10, 5, null },
                decisions.Select(d => d.ShouldNotify ? d.ThresholdPercent : null));
        }

        [Fact]
        public void Repeating_the_same_level_does_not_warn_twice()
        {
            var policy = Policy(Settings());

            var first = policy.Evaluate(Device, 12, BatteryState.Medium, charging: false);
            var again = policy.Evaluate(Device, 14, BatteryState.Medium, charging: false);
            var lower = policy.Evaluate(Device, 13, BatteryState.Medium, charging: false);

            Assert.True(first.ShouldNotify);
            Assert.Equal(15, first.ThresholdPercent);
            Assert.False(again.ShouldNotify);
            Assert.False(lower.ShouldNotify);
            Assert.Contains("already announced", again.Reason);
        }

        [Fact]
        public void Recovery_above_every_level_re_arms_the_ladder()
        {
            var policy = Policy(Settings());

            Assert.True(policy.Evaluate(Device, 12, BatteryState.Medium, charging: false).ShouldNotify);

            Assert.False(policy.Evaluate(Device, 40, BatteryState.Medium, charging: false).ShouldNotify);

            Assert.False(policy.Evaluate(Device, 90, BatteryState.Full, charging: false).ShouldNotify);

            var afterRecovery = policy.Evaluate(Device, 45, BatteryState.Medium, charging: false);
            Assert.True(afterRecovery.ShouldNotify);
            Assert.Equal(50, afterRecovery.ThresholdPercent);
        }

        [Fact]
        public void Confirmed_charging_re_arms_the_ladder_and_never_warns_while_charging()
        {
            var policy = Policy(Settings());

            Assert.True(policy.Evaluate(Device, 3, BatteryState.Critical, charging: false).ShouldNotify);

            var whileCharging = policy.Evaluate(Device, 3, BatteryState.Charging, charging: true);
            Assert.False(whileCharging.ShouldNotify);
            Assert.Contains("charging", whileCharging.Reason);

            Assert.True(policy.Evaluate(Device, 3, BatteryState.Critical, charging: false).ShouldNotify);
        }

        [Fact]
        public void Disabled_levels_are_ignored()
        {
            var settings = Settings();
            foreach (var level in settings.BatteryWarnings)
                level.Enabled = level.Percent == 20;

            var policy = Policy(settings);

            var decision = policy.Evaluate(Device, 3, BatteryState.Critical, charging: false);

            Assert.True(decision.ShouldNotify);
            Assert.Equal(20, decision.ThresholdPercent);
        }

        [Fact]
        public void An_empty_ladder_never_warns()
        {
            var settings = Settings();
            settings.BatteryWarnings.Clear();

            var decision = Policy(settings).Evaluate(Device, 1, BatteryState.Critical, charging: false);

            Assert.False(decision.ShouldNotify);
            Assert.Contains("no battery warning levels", decision.Reason);
        }

        [Fact]
        public void Categorical_low_and_empty_each_warn_once()
        {
            var policy = Policy(Settings());

            var low = policy.Evaluate(Device, null, BatteryState.Low, charging: false);
            var lowAgain = policy.Evaluate(Device, null, BatteryState.Low, charging: false);
            var empty = policy.Evaluate(Device, null, BatteryState.Critical, charging: false);
            var emptyAgain = policy.Evaluate(Device, null, BatteryState.Critical, charging: false);

            Assert.True(low.ShouldNotify);
            Assert.Equal(BatteryWarningKind.CategoricalLow, low.Kind);
            Assert.True(low.IsCategorical);
            Assert.False(low.IsCritical);

            Assert.False(lowAgain.ShouldNotify);

            Assert.True(empty.ShouldNotify);
            Assert.Equal(BatteryWarningKind.CategoricalEmpty, empty.Kind);
            Assert.True(empty.IsCritical);

            Assert.False(emptyAgain.ShouldNotify);
        }

        [Theory]
        [InlineData(BatteryState.Unknown)]
        [InlineData(BatteryState.High)]
        [InlineData(BatteryState.Medium)]
        [InlineData(BatteryState.Full)]
        [InlineData(BatteryState.Charging)]
        [InlineData(BatteryState.Discharging)]
        public void Missing_or_healthy_telemetry_never_warns(BatteryState state)
        {
            var decision = Policy(Settings()).Evaluate(Device, null, state, charging: false);

            Assert.False(decision.ShouldNotify);
        }

        [Fact]
        public void No_telemetry_reports_that_no_telemetry_was_available()
        {
            var decision = Policy(Settings()).Evaluate(Device, null, BatteryState.Unknown, charging: false);

            Assert.False(decision.ShouldNotify);
            Assert.Contains("no genuine battery telemetry", decision.Reason);
        }

        [Fact]
        public void Recovery_can_be_announced_when_opted_in_and_only_once()
        {
            var settings = Settings();
            settings.NotifyOnBatteryRecovered = true;

            var policy = Policy(settings);

            policy.Evaluate(Device, 12, BatteryState.Medium, charging: false);

            var recovered = policy.Evaluate(Device, 95, BatteryState.Full, charging: false);
            Assert.True(recovered.ShouldNotify);
            Assert.Equal(BatteryWarningKind.Recovered, recovered.Kind);

            policy.Evaluate(Device, 12, BatteryState.Medium, charging: false);
            var secondRecovery = policy.Evaluate(Device, 95, BatteryState.Full, charging: false);
            Assert.False(secondRecovery.ShouldNotify);
        }

        [Fact]
        public void Devices_are_tracked_independently()
        {
            var policy = Policy(Settings());

            var first = policy.Evaluate("device-a", 12, BatteryState.Medium, charging: false);
            var second = policy.Evaluate("device-b", 12, BatteryState.Medium, charging: false);

            Assert.True(first.ShouldNotify);
            Assert.True(second.ShouldNotify);
        }

        [Fact]
        public void Forget_lets_the_same_reading_warn_again()
        {
            var policy = Policy(Settings());

            Assert.True(policy.Evaluate(Device, 12, BatteryState.Medium, charging: false).ShouldNotify);

            policy.Forget(Device);

            Assert.True(policy.Evaluate(Device, 12, BatteryState.Medium, charging: false).ShouldNotify);
        }

        [Fact]
        public void A_device_without_identity_is_never_warned_about()
        {
            var decision = Policy(Settings()).Evaluate(string.Empty, 1, BatteryState.Critical, charging: false);

            Assert.False(decision.ShouldNotify);
            Assert.Contains("no device identity", decision.Reason);
        }
    }
}
