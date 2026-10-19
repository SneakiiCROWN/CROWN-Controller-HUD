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
        private TaskbarIcon? _trayIcon;
        private bool _disposed = false;

        public event EventHandler? ShowWindowRequested;
        public event EventHandler? ExitRequested;

        public TrayService(ILogger<TrayService> logger, IOptionsMonitor<AppSettings> settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public void Initialize()
        {
            if (_trayIcon != null) return;

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "CROWN Controller HUD",
                Visibility = Visibility.Visible,
                MenuActivation = PopupActivationMode.RightClick,
                NoLeftClickDelay = true
            };

            // Create context menu
            CreateContextMenu();

            // Set initial icon
            UpdateTrayIcon();

            _logger.LogInformation("System tray initialized");
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

            // Controllers submenu
            var controllersMenu = new System.Windows.Controls.MenuItem { Header = "Controllers" };
            contextMenu.Items.Add(controllersMenu);

            // Refresh item
            var refreshItem = new System.Windows.Controls.MenuItem { Header = "Refresh Controllers" };
            refreshItem.Click += async (s, e) => 
            {
                // This would trigger a refresh via the controller manager
                // For now, just show the window
                ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            };
            contextMenu.Items.Add(refreshItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            // Settings item
            var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
            settingsItem.Click += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            contextMenu.Items.Add(settingsItem);

            contextMenu.Items.Add(new System.Windows.Controls.Separator());

            // Exit item
            var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;

            // Double-click to show
            _trayIcon.TrayLeftMouseDown += (s, e) =>
            {
                if (e is System.Windows.Input.MouseButtonEventArgs mouseArgs && mouseArgs.ClickCount == 2)
                {
                    ShowWindowRequested?.Invoke(this, EventArgs.Empty);
                }
            };
        }

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
            if (_trayIcon == null) return;

            try
            {
                var icon = CreateTrayIcon(primaryController);
                if (icon != null)
                {
                    _trayIcon.Icon = icon;
                    
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update tray icon");
            }
        }

        private Icon? CreateTrayIcon(ControllerInfo? controller)
        {
            try
            {
                // Create a simple icon with battery indicator
                var size = 32;
                var bitmap = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    
                    // Draw controller silhouette or battery indicator
                    if (controller?.BatteryPercentage.HasValue == true)
                    {
                        DrawBatteryIndicator(g, controller.BatteryPercentage.Value, controller.IsCharging, size);
                    }
                    else
                    {
                        DrawDefaultIcon(g, size);
                    }
                }

                return Icon.FromHandle(bitmap.GetHicon());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create tray icon");
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

            // Battery outline
            using (var pen = new Pen(Color.White, 2))
            {
                g.DrawRectangle(pen, batteryX, batteryY, batteryWidth, batteryHeight);
                // Terminal
                g.DrawRectangle(pen, batteryX + batteryWidth, batteryY + batteryHeight / 3, 2, batteryHeight / 3);
            }

            // Battery fill
            var fillWidth = (int)((batteryWidth - 2) * (percentage / 100.0));
            var fillColor = percentage switch
            {
                >= 50 => Color.FromArgb(0, 200, 0),     // Green
                >= 15 => Color.FromArgb(255, 165, 0),   // Orange
                _ => Color.FromArgb(255, 50, 50)        // Red
            };

            using (var brush = new SolidBrush(fillColor))
            {
                g.FillRectangle(brush, batteryX + 1, batteryY + 1, fillWidth, batteryHeight - 2);
            }

            // Charging indicator
            if (isCharging)
            {
                using (var font = new Font("Segoe UI", 8, System.Drawing.FontStyle.Bold))
                using (var brush = new SolidBrush(Color.Yellow))
                {
                    g.DrawString("⚡", font, brush, batteryX + batteryWidth / 2 - 4, batteryY - 2);
                }
            }

            // Percentage text
            using (var font = new Font("Segoe UI", 7, System.Drawing.FontStyle.Bold))
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
            // Draw a simple crown icon
            using (var pen = new Pen(Color.FromArgb(0, 120, 212), 2.5f))
            {
                var points = new[]
                {
                    new System.Drawing.Point(size/2, 4),
                    new System.Drawing.Point(size/2 - 8, size - 4),
                    new System.Drawing.Point(size/2 + 8, size - 4)
                };
                g.DrawPolygon(pen, points);
                
                // Crown points
                g.DrawLine(pen, size/2 - 8, size - 4, size/2 - 4, size/2);
                g.DrawLine(pen, size/2 - 4, size/2, size/2, size - 4);
                g.DrawLine(pen, size/2, size - 4, size/2 + 4, size/2);
                g.DrawLine(pen, size/2 + 4, size/2, size/2 + 8, size - 4);
            }
        }

        public void ShowBalloonTip(string title, string message, BalloonIcon icon = BalloonIcon.Info)
        {
            _trayIcon?.ShowBalloonTip(title, message, icon);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                _disposed = true;
            }
        }
    }
}