using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CrownControllerHUD.Models;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Builds the battery report used by the <c>--battery-report</c> diagnostic and by the
    /// in-app diagnostics panel.
    ///
    /// It reports exactly what is known, what the source cannot tell us and what failed.
    /// It never prints a value that no source produced, because "0%" and "unknown" are
    /// very different things to a gamer.
    /// </summary>
    public static class BatteryDiagnosticsReporter
    {
        public static async Task<string> BuildAsync(
            IReadOnlyList<ControllerInfo> controllers,
            IBatteryTelemetryService battery,
            bool force,
            CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CROWN Controller HUD - battery telemetry report");
            sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            if (controllers.Count == 0)
            {
                sb.AppendLine("No controllers to inspect (nothing is currently switched on).");
                return sb.ToString();
            }

            foreach (var controller in controllers)
            {
                sb.AppendLine($"controller : {controller.Name} ({controller.Type})");
                sb.AppendLine($"transport  : {controller.ConnectionType}");
                sb.AppendLine($"state      : {controller.ConnectionState} via {controller.ConnectionStateSource ?? "n/a"}");
                sb.AppendLine($"hardware   : VID {controller.VendorId:X4} PID {controller.ProductId:X4}" +
                              (string.IsNullOrWhiteSpace(controller.BluetoothAddress)
                                  ? string.Empty
                                  : $"  Bluetooth {DeviceIdentity.ToDisplayAddress(controller.BluetoothAddress)}"));

                if (controller.ConnectionState != ControllerConnectionState.Connected)
                {
                    sb.AppendLine("battery    : not queried - the device is not verified as switched on");
                    sb.AppendLine();
                    continue;
                }

                int duplicates = controllers.Count(c =>
                    !string.Equals(c.DeviceId, controller.DeviceId, StringComparison.OrdinalIgnoreCase)
                    && c.VendorId == controller.VendorId
                    && c.ProductId == controller.ProductId
                    && c.ConnectionState == ControllerConnectionState.Connected);

                BatteryReading? reading;
                try
                {
                    reading = await battery.ReadAsync(controller, force, duplicates, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    reading = null;
                    sb.AppendLine($"battery    : read failed - {ex.Message}");
                    sb.AppendLine();
                    continue;
                }

                if (reading == null)
                {
                    sb.AppendLine("battery    : no genuine telemetry available");
                    sb.AppendLine($"reason     : {battery.LastError}");
                    if (duplicates > 0)
                        sb.AppendLine($"note       : {duplicates + 1} connected controllers share this VID/PID, so" +
                                      " identity-less sources are deliberately not used");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"battery    : {(reading.Percentage.HasValue ? reading.Percentage.Value + "%" : "no exact percentage")}" +
                              $"  ({reading.State}{(reading.Charging ? ", charging" : string.Empty)})");
                sb.AppendLine($"source     : {reading.SourceDescription}");

                if (reading.RemainingMwh.HasValue || reading.FullChargeMwh.HasValue)
                {
                    sb.AppendLine($"capacity   : remaining {Format(reading.RemainingMwh)} mWh / full {Format(reading.FullChargeMwh)} mWh");
                }

                sb.AppendLine($"supports   : percentage={(reading.SupportsPercentage ? "yes" : "no")}" +
                              $"  charging={(reading.SupportsCharging ? "yes" : "no")}");

                if (!string.IsNullOrEmpty(reading.Error))
                    sb.AppendLine($"note       : {reading.Error}");

                sb.AppendLine($"freshness  : read at {reading.RetrievedAtUtc.ToLocalTime():HH:mm:ss}");

                if (controller.BatteryIsStale)
                    sb.AppendLine("warning    : this value is STALE");

                sb.AppendLine();
            }

            sb.AppendLine("--- source cache ---");
            sb.AppendLine(battery.DescribeCache());

            return sb.ToString();

            static string Format(int? value) => value.HasValue ? value.Value.ToString() : "not reported";
        }
    }
}
