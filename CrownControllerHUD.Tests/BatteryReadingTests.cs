using CrownControllerHUD.Models;

namespace CrownControllerHUD.Tests
{
    /// <summary>
    /// The battery model must never turn "unknown" into a number. These tests pin down the
    /// capacity maths and the categorical mapping, which is where a fabricated 0% would
    /// come from. Reconstructed after Git history cleanup lost the originals.
    /// </summary>
    public class BatteryReadingTests
    {
        [Theory]
        [InlineData(50, 100, 50)]
        [InlineData(100, 100, 100)]
        [InlineData(1, 3, 33)]
        [InlineData(2, 3, 67)]
        [InlineData(0, 100, 0)]
        public void Percentage_is_derived_only_from_a_valid_capacity_pair(int remaining, int full, int expected)
        {
            Assert.Equal(expected, BatteryReading.PercentageFromCapacity(remaining, full));
        }

        [Theory]
        [InlineData(null, 100)]
        [InlineData(50, null)]
        [InlineData(null, null)]
        [InlineData(50, 0)]
        [InlineData(0, 0)]
        [InlineData(-1, 100)]
        [InlineData(101, 100)]
        [InlineData(-5, -10)]
        public void No_percentage_is_invented_when_capacities_are_missing_or_implausible(int? remaining, int? full)
        {
            Assert.Null(BatteryReading.PercentageFromCapacity(remaining, full));
        }

        [Theory]
        [InlineData(100, BatteryState.Full)]
        [InlineData(95, BatteryState.Full)]
        [InlineData(94, BatteryState.High)]
        [InlineData(50, BatteryState.High)]
        [InlineData(49, BatteryState.Medium)]
        [InlineData(15, BatteryState.Medium)]
        [InlineData(14, BatteryState.Low)]
        [InlineData(5, BatteryState.Low)]
        [InlineData(4, BatteryState.Critical)]
        [InlineData(0, BatteryState.Critical)]
        public void Percentage_maps_onto_the_categorical_levels(int percentage, BatteryState expected)
        {
            Assert.Equal(expected, BatteryReading.StateFromPercentage(percentage));
        }

        [Fact]
        public void A_reading_with_no_data_says_so_instead_of_reporting_zero()
        {
            var reading = new BatteryReading { Source = BatterySourceKind.None };

            Assert.Null(reading.Percentage);
            Assert.Equal(BatteryState.Unknown, reading.State);
            Assert.False(reading.SupportsPercentage);
            Assert.False(reading.SupportsCharging);
            Assert.False(reading.Charging);
        }
    }
}
