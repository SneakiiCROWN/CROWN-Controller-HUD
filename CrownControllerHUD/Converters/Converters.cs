using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CrownControllerHUD.Models;

namespace CrownControllerHUD.Converters
{
    // Boolean to Visibility
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                var inverse = parameter?.ToString()?.Equals("inverse", StringComparison.OrdinalIgnoreCase) == true;
                return (boolValue ^ inverse) ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility vis)
            {
                var inverse = parameter?.ToString()?.Equals("inverse", StringComparison.OrdinalIgnoreCase) == true;
                return (vis == Visibility.Visible) ^ inverse;
            }
            return false;
        }
    }

    // Null to Visibility
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var inverse = parameter?.ToString()?.Equals("inverse", StringComparison.OrdinalIgnoreCase) == true;
            return (value != null) ^ inverse ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Count to Visibility
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (value is System.Collections.ICollection collection)
            {
                return collection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // ControllerType to Display Name
    public class ControllerTypeToDisplayNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ControllerType type)
            {
                return type.GetDisplayName();
            }
            return "Unknown Controller";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // ControllerType to Icon
    public class ControllerTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ControllerType type)
            {
                return type switch
                {
                    ControllerType.XboxOne or ControllerType.XboxOneS or ControllerType.XboxOneElite or
                    ControllerType.XboxEliteSeries2 or ControllerType.XboxSeriesX or ControllerType.XboxSeriesS
                        => "🎮",
                    ControllerType.PlayStation4 or ControllerType.PlayStation5
                        => "🎮",
                    ControllerType.NintendoSwitchPro
                        => "🎮",
                    ControllerType.NintendoSwitchJoyConLeft or ControllerType.NintendoSwitchJoyConRight
                        => "🎮",
                    ControllerType.SteamController or ControllerType.SteamDeck
                        => "🎮",
                    ControllerType.Xbox360
                        => "🎮",
                    ControllerType.XboxAdaptive
                        => "🎮",
                    _ => "🎮"
                };
            }
            return "🎮";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Connection to Color
    public class ConnectionToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected)
            {
                return isConnected ? new SolidColorBrush(Color.FromRgb(0x10, 0x6E, 0xBE)) : new SolidColorBrush(Color.FromRgb(0x6E, 0x10, 0x10));
            }
            return new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Connection to Text
    public class ConnectionToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected)
            {
                return isConnected ? "CONNECTED" : "DISCONNECTED";
            }
            return "UNKNOWN";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Connection to Indicator Color
    public class ConnectionToIndicatorColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected)
            {
                return isConnected ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)) : new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            }
            return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Battery Percentage to Color
    public class BatteryPercentageToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int percentage)
            {
                return percentage switch
                {
                    >= 50 => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                    >= 15 => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
                    >= 5 => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                    _ => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))
                };
            }
            if (value is int nullableInt)
            {
                return Convert(nullableInt, targetType, parameter, culture);
            }
            return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Dictionary to String
    public class DictionaryToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Dictionary<string, object> dict)
            {
                var sb = new StringBuilder();
                foreach (var kvp in dict.OrderBy(k => k.Key))
                {
                    sb.AppendLine($"{kvp.Key}: {FormatValue(kvp.Value)}");
                }
                return sb.ToString();
            }
            return string.Empty;
        }

        private string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is byte[] bytes) return $"[{bytes.Length} bytes] {BitConverter.ToString(bytes)}";
            if (value is Array array) return $"[{array.Length} items]";
            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // MultiBinding Converter for VID/PID
    public class VidPidMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is ushort vid && values[1] is ushort pid)
            {
                return $"VID: {vid:X4} / PID: {pid:X4}";
            }
            return "Unknown";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Inverse Boolean
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return !boolValue;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return !boolValue;
            return false;
        }
    }

    // Battery State to Color
    public class BatteryStateToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is BatteryState state)
            {
                return state switch
                {
                    BatteryState.Full => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                    BatteryState.High => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                    BatteryState.Medium => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
                    BatteryState.Low => new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
                    BatteryState.Critical => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
                    BatteryState.Charging => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                    _ => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))
                };
            }
            return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}