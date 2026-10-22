using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrownControllerHUD.Models;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrownControllerHUD.Services
{
    public interface ITrayService : IDisposable
    {
        void Initialize();
        void ShowMainWindow();
        void HideMainWindow();
        void UpdateTrayIcon(ControllerInfo? primaryController = null);
        void ShowBalloonTip(string title, string message, BalloonIcon icon = BalloonIcon.Info);
        event EventHandler? ShowWindowRequested;
        event EventHandler? ExitRequested;
    }

    public class TrayService : ITrayService, IDisposable
    {
        private readonly ILogger<TrayService> _logger;
        private readonly IOptionsMonitor<AppSettings> _settings;
        private readonly INotificationService _notificationService;
        private TaskbarIcon? _trayIcon;
        private bool _disposed = false;
        private bool _initialized = false;

        public event EventHandler? ShowWindowRequested;
        public event EventHandler? ExitRequested;

        public TrayService(
            ILogger<TrayService> logger,
            IOptionsMonitor<AppSettings> settings,
            INotificationService notificationService)
        {
            _logger = logger;
            _settings = settings;
            _notificationService = notificationService;
        }

        public void Initialize()
        {
            if (_initialized) return;

            try
            {
                DiagnosticLogger.Info("TrayService.Initialize() starting");

                _trayIcon = new TaskbarIcon
                {
                    ToolTipText = "CROWN Controller HUD",
                    Visibility = Visibility.Visible,
                    MenuActivation = PopupActivationMode.RightClick,
                    NoLeftClickDelay = true
                };

                try
                {
                    var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                    if (File.Exists(iconPath))
                    {
                        _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                    }
                    else
                    {
                        _trayIcon.Icon = CreateDefaultIcon();
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("Failed to load tray icon, using fallback", ex);
                    try { _trayIcon.Icon = CreateDefaultIcon(); } catch { }
                }

                CreateContextMenu();

                _trayIcon.TrayLeftMouseDown += (s, e) =>
                {
                    if (e is MouseButtonEventArgs mouseArgs && mouseArgs.ClickCount == 2)
                    {
                        ShowWindowRequested?.Invoke(this, EventArgs.Empty);
                    }
                };

                _initialized = true;
                DiagnosticLogger.Info("TrayService initialized successfully");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("TrayService.Initialize() failed", ex);
                _initialized = false;
            }
        }

        private void CreateContextMenu()
        {
            if (_trayIcon == null) return;

            var menu = new ContextMenu();
            menu.Background = new SolidColorBrush(MediaColor(0x1F, 0x1F, 0x1F));
            menu.BorderBrush = new SolidColorBrush(MediaColor(0x33, 0x33, 0x33));
            menu.BorderThickness = new Thickness(1);
            menu.Padding = new Thickness(2);
            menu.HasDropShadow = true;

            // Item style
            var itemStyle = new Style(typeof(MenuItem));
            itemStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(MediaColor(0xDD, 0xDD, 0xDD))));
            itemStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(14, 7, 14, 7)));
            itemStyle.Setters.Add(new Setter(MenuItem.FontSizeProperty, 12.0));
            itemStyle.Setters.Add(new Setter(MenuItem.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI")));
            itemStyle.Setters.Add(new Setter(MenuItem.CursorProperty, Cursors.Hand));

            var hoverTrigger = new Trigger { Property = MenuItem.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(MenuItem.BackgroundProperty, new SolidColorBrush(MediaColor(0x2A, 0x2A, 0x2A))));
            hoverTrigger.Setters.Add(new Setter(MenuItem.ForegroundProperty, System.Windows.Media.Brushes.White));
            itemStyle.Triggers.Add(hoverTrigger);

            menu.Resources.Add(typeof(MenuItem), itemStyle);

            // Separator style
            var sepStyle = new Style(typeof(Separator));
            sepStyle.Setters.Add(new Setter(Separator.BackgroundProperty, new SolidColorBrush(MediaColor(0x33, 0x33, 0x33))));
            sepStyle.Setters.Add(new Setter(Separator.MarginProperty, new Thickness(8, 2, 8, 2)));
            sepStyle.Setters.Add(new Setter(Separator.HeightProperty, 1.0));
            menu.Resources.Add(typeof(Separator), sepStyle);

            // Header
            var header = new MenuItem
            {
                Header = "CROWN Controller HUD",
                FontWeight = FontWeights.Bold,
                FontSize = 12.0,
                Foreground = new SolidColorBrush(MediaColor(0x00, 0x78, 0xD4)),
                IsEnabled = false,
                Padding = new Thickness(14, 8, 14, 8)
            };
            menu.Items.Add(header);

            menu.Items.Add(new Separator());

            // Show Dashboard
            var showItem = new MenuItem { Header = "Show Dashboard" };
            showItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(showItem);

            // Refresh
            var refreshItem = new MenuItem { Header = "Refresh Controllers" };
            refreshItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(refreshItem);

            // Settings
            var settingsItem = new MenuItem { Header = "Settings" };
            settingsItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(settingsItem);

            menu.Items.Add(new Separator());

            // Test Notification
            var testNotifyItem = new MenuItem { Header = "Test Notification" };
            testNotifyItem.Click += (s, e) =>
            {
                try { _ = _notificationService.ShowTestNotificationAsync(); }
                catch (Exception ex) { DiagnosticLogger.Error("Test notification failed", ex); }
            };
            menu.Items.Add(testNotifyItem);

            // Toggle Notifications
            var toggleItem = new MenuItem { Header = "Toggle Notifications" };
            toggleItem.Click += (s, e) =>
            {
                try
                {
                    var settings = _settings.CurrentValue;
                    settings.Notifications.EnableNotifications = !settings.Notifications.EnableNotifications;
                    DiagnosticLogger.Info($"[Tray] Notifications {(settings.Notifications.EnableNotifications ? "enabled" : "disabled")}");
                }
                catch (Exception ex) { DiagnosticLogger.Error("Toggle notifications failed", ex); }
            };
            menu.Items.Add(toggleItem);

            menu.Items.Add(new Separator());

            // Exit
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenu = menu;
        }

        // Helper aliases for WPF media types
        private static System.Windows.Media.Color MediaColor(byte r, byte g, byte b) =>
            System.Windows.Media.Color.FromRgb(r, g, b);

        private static System.Windows.Media.FontFamily MediaFontFamily(string name) =>
            new System.Windows.Media.FontFamily(name);

        private System.Drawing.Icon CreateDefaultIcon()
        {
            var size = 32;
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.Transparent);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0, 120, 212), 2.5f))
                {
                    var points = new[]
                    {
                        new System.Drawing.Point(size/2, 4),
                        new System.Drawing.Point(size/2 - 8, size - 4),
                        new System.Drawing.Point(size/2 + 8, size - 4)
                    };
                    g.DrawPolygon(pen, points);
                    g.DrawLine(pen, size/2 - 8, size - 4, size/2 - 4, size/2);
                    g.DrawLine(pen, size/2 - 4, size/2, size/2, size - 4);
                    g.DrawLine(pen, size/2, size - 4, size/2 + 4, size/2);
                    g.DrawLine(pen, size/2 + 4, size/2, size/2 + 8, size - 4);
                }
            }

            var hIcon = bitmap.GetHicon();
            try
            {
                using var fromHandle = System.Drawing.Icon.FromHandle(hIcon);
                return (System.Drawing.Icon)fromHandle.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
                bitmap.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void ShowMainWindow()
        {
            ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        }

        public void HideMainWindow()
        {
        }

        public void UpdateTrayIcon(ControllerInfo? primaryController = null)
        {
            if (_trayIcon == null || !_initialized) return;

            try
            {
                var icon = CreateTrayIcon(primaryController);
                if (icon != null)
                {
                    _trayIcon.Icon = icon;
                }

                if (primaryController != null && primaryController.BatteryPercentage.HasValue)
                {
                    var chargingText = primaryController.IsCharging ? " (Charging)" : "";
                    _trayIcon.ToolTipText = $"CROWN Controller HUD - {primaryController.Type.GetDisplayName()}: {primaryController.BatteryPercentage}%{chargingText}";
                }
                else
                {
                    _trayIcon.ToolTipText = "CROWN Controller HUD";
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("UpdateTrayIcon failed", ex);
            }
        }

        private System.Drawing.Icon? CreateTrayIcon(ControllerInfo? controller)
        {
            try
            {
                var size = 32;
                var bitmap = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(System.Drawing.Color.Transparent);

                    if (controller?.BatteryPercentage.HasValue == true)
                    {
                        DrawBatteryIndicator(g, controller.BatteryPercentage.Value, controller.IsCharging, size);
                    }
                    else
                    {
                        DrawDefaultIcon(g, size);
                    }
                }

                var hIcon = bitmap.GetHicon();
                try
                {
                    using var fromHandle = System.Drawing.Icon.FromHandle(hIcon);
                    return (System.Drawing.Icon)fromHandle.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                    bitmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("CreateTrayIcon failed", ex);
                return null;
            }
        }

        private void DrawBatteryIndicator(Graphics g, int percentage, bool isCharging, int size)
        {
            var rect = new Rectangle(4, 4, size - 8, size - 8);
            var batteryWidth = rect.Width - 4;
            var batteryHeight = rect.Height - 4;
            var batteryX = rect.X + 2;
            var batteryY = rect.Y + 2;

            using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2))
            {
                g.DrawRectangle(pen, batteryX, batteryY, batteryWidth, batteryHeight);
                g.DrawRectangle(pen, batteryX + batteryWidth, batteryY + batteryHeight / 3, 2, batteryHeight / 3);
            }

            var fillWidth = (int)((batteryWidth - 2) * (percentage / 100.0));
            var fillColor = percentage switch
            {
                >= 50 => System.Drawing.Color.FromArgb(0, 200, 0),
                >= 15 => System.Drawing.Color.FromArgb(255, 165, 0),
                _ => System.Drawing.Color.FromArgb(255, 50, 50)
            };

            using (var brush = new SolidBrush(fillColor))
            {
                g.FillRectangle(brush, batteryX + 1, batteryY + 1, fillWidth, batteryHeight - 2);
            }

            if (isCharging)
            {
                using (var font = new Font(new System.Drawing.FontFamily("Segoe UI"), 8, System.Drawing.FontStyle.Bold))
                using (var brush = new SolidBrush(System.Drawing.Color.Yellow))
                {
                    g.DrawString("⚡", font, brush, batteryX + batteryWidth / 2 - 4, batteryY - 2);
                }
            }

            using (var font = new Font(new System.Drawing.FontFamily("Segoe UI"), 7, System.Drawing.FontStyle.Bold))
            using (var brush = new SolidBrush(System.Drawing.Color.White))
            {
                var text = $"{percentage}%";
                var textSize = g.MeasureString(text, font);
                g.DrawString(text, font, brush,
                    size / 2 - textSize.Width / 2,
                    size - textSize.Height - 1);
            }
        }

        private void DrawDefaultIcon(Graphics g, int size)
        {
            using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0, 120, 212), 2.5f))
            {
                var points = new[]
                {
                    new System.Drawing.Point(size/2, 4),
                    new System.Drawing.Point(size/2 - 8, size - 4),
                    new System.Drawing.Point(size/2 + 8, size - 4)
                };
                g.DrawPolygon(pen, points);

                g.DrawLine(pen, size/2 - 8, size - 4, size/2 - 4, size/2);
                g.DrawLine(pen, size/2 - 4, size/2, size/2, size - 4);
                g.DrawLine(pen, size/2, size - 4, size/2 + 4, size/2);
                g.DrawLine(pen, size/2 + 4, size/2, size/2 + 8, size - 4);
            }
        }

        public void ShowBalloonTip(string title, string message, BalloonIcon icon = BalloonIcon.Info)
        {
            try
            {
                _trayIcon?.ShowBalloonTip(title, message, icon);
                DiagnosticLogger.Info($"Balloon tip shown: {title}");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("ShowBalloonTip failed", ex);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    _trayIcon?.Dispose();
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("TrayService.Dispose error", ex);
                }
                _trayIcon = null;
                _disposed = true;
            }
        }
    }
}
