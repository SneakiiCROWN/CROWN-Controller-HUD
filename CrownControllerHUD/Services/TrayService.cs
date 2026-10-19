using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
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

                // Try to load icon from the app folder; this is the most reliable path
                // for Hardcodet's TaskbarIcon, which wants a real System.Drawing.Icon.
                try
                {
                    var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                    if (File.Exists(iconPath))
                    {
                        _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                        DiagnosticLogger.Debug($"Loaded tray icon from {iconPath}");
                    }
                    else
                    {
                        _trayIcon.Icon = CreateDefaultIcon();
                        DiagnosticLogger.Debug("Created default tray icon");
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("Failed to load tray icon, using fallback", ex);
                    try { _trayIcon.Icon = CreateDefaultIcon(); } catch { }
                }

                // Create context menu
                CreateContextMenu();

                // Double-click to show
                _trayIcon.TrayLeftMouseDown += (s, e) =>
                {
                    if (e is System.Windows.Input.MouseButtonEventArgs mouseArgs && mouseArgs.ClickCount == 2)
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
                // Don't throw - let the app continue without tray
            }
        }

        private void CreateContextMenu()
        {
            if (_trayIcon == null) return;

            var contextMenu = new System.Windows.Controls.ContextMenu();

            // Show/Hide item
            var showItem = new System.Windows.Controls.MenuItem
            {
                Header = "Show CROWN Controller HUD",
                FontWeight = FontWeights.Bold
            };
            showItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            contextMenu.Items.Add(showItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            // Refresh item
            var refreshItem = new System.Windows.Controls.MenuItem { Header = "Refresh Controllers" };
            refreshItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            contextMenu.Items.Add(refreshItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            // Settings item
            var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
            settingsItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            contextMenu.Items.Add(settingsItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            // Test notification item - runs the real CROWN renderer, not a tray balloon.
            var testNotifyItem = new System.Windows.Controls.MenuItem { Header = "Test Notification" };
            testNotifyItem.Click += (s, e) =>
            {
                try
                {
                    _ = _notificationService.ShowTestNotificationAsync();
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("Test notification failed", ex);
                }
            };
            contextMenu.Items.Add(testNotifyItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            // Exit item
            var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;
        }

        private System.Drawing.Icon CreateDefaultIcon()
        {
            var size = 32;
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                using (var pen = new Pen(Color.FromArgb(0, 120, 212), 2.5f))
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
                // Clone so the Icon owns its own handle and survives DestroyIcon.
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
            // Handled by main window
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

                // Update tooltip with battery info
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
                    g.Clear(Color.Transparent);
                    
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

            using (var pen = new Pen(Color.White, 2))
            {
                g.DrawRectangle(pen, batteryX, batteryY, batteryWidth, batteryHeight);
                g.DrawRectangle(pen, batteryX + batteryWidth, batteryY + batteryHeight / 3, 2, batteryHeight / 3);
            }

            var fillWidth = (int)((batteryWidth - 2) * (percentage / 100.0));
            var fillColor = percentage switch
            {
                >= 50 => Color.FromArgb(0, 200, 0),
                >= 15 => Color.FromArgb(255, 165, 0),
                _ => Color.FromArgb(255, 50, 50)
            };

            using (var brush = new SolidBrush(fillColor))
            {
                g.FillRectangle(brush, batteryX + 1, batteryY + 1, fillWidth, batteryHeight - 2);
            }

            if (isCharging)
            {
                using (var font = new Font(new FontFamily("Segoe UI"), 8, System.Drawing.FontStyle.Bold))
                using (var brush = new SolidBrush(Color.Yellow))
                {
                    g.DrawString("⚡", font, brush, batteryX + batteryWidth / 2 - 4, batteryY - 2);
                }
            }

            using (var font = new Font(new FontFamily("Segoe UI"), 7, System.Drawing.FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
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
            using (var pen = new Pen(Color.FromArgb(0, 120, 212), 2.5f))
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