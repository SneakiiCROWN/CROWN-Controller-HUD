using CrownControllerHUD.Services;

namespace CrownControllerHUD.Tests
{
    /// <summary>
    /// "Never aggressive" is a hard requirement for battery telemetry: these tests pin down
    /// how often a device that cannot report a battery is ever asked again.
    /// Reconstructed after Git history cleanup lost the originals.
    /// </summary>
    public class BatteryBackoffTests
    {
        [Theory]
        [InlineData(1, 300, 30)]
        [InlineData(2, 300, 60)]
        [InlineData(3, 300, 120)]
        [InlineData(4, 300, 240)]
        [InlineData(5, 300, 300)]
        [InlineData(50, 300, 300)]
        public void Repeated_failures_back_off_up_to_the_refresh_interval(int failures, int refresh, int expected)
        {
            Assert.Equal(expected, BatteryTelemetryService.FailureBackoffSeconds(failures, refresh));
        }

        [Fact]
        public void Backoff_never_goes_below_thirty_seconds_even_if_configured_lower()
        {
            Assert.Equal(30, BatteryTelemetryService.FailureBackoffSeconds(0, 5));
            Assert.Equal(30, BatteryTelemetryService.FailureBackoffSeconds(1, 5));
        }
    }
}
