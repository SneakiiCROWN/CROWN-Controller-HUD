using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using CrownControllerHUD.ViewModels;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD
{
    public partial class MainWindow : Window
    {
        private readonly IServiceProvider _services;
        private readonly MainViewModel _viewModel;
        private readonly IControllerManager _controllerManager;
        private readonly ITrayService _trayService;
        private readonly ILogger<MainWindow> _logger;
        private bool _isClosingToTray = false;
        private System.Windows.Threading.DispatcherTimer? _positionSaveTimer;

        public MainWindow(IServiceProvider services, MainViewModel viewModel, IControllerManager controllerManager, ITrayService trayService, ILogger<MainWindow> logger)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow constructor started");
            DiagnosticLogger.Info("MainWindow ctor: before InitializeComponent");
            InitializeComponent();
            DiagnosticLogger.Info("MainWindow ctor: after InitializeComponent");
            
            _services = services;
            _viewModel = viewModel;
            _controllerManager = controllerManager;
            _trayService = trayService;
            _logger = logger;

            DataContext = _viewModel;
            
            // Wire up tray service events (icon itself is created after the window is visible)
            _trayService.ShowWindowRequested += (s, e) => ShowWindow();
            _trayService.ExitRequested += (s, e) => ExitApplication();

            // Load window position from settings
            LoadWindowPosition();

            // NOTE: Do not Show()/Activate() from the constructor. App.OnStartup owns
            // window presentation so that startup order is deterministic and a
            // constructor failure cannot leave a half-created invisible window.
            DiagnosticLogger.Info("MainWindow ctor: completed");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            DiagnosticLogger.Info("MainWindow Loaded event fired");
            try
            {
                // Detector/hardware failures must never take the UI down.
                await _viewModel.InitializeAsync();
                UpdateDetectorStatusIndicators();
                DiagnosticLogger.Info("MainWindow initialization completed");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("Window_Loaded initialization error", ex);
                StatusText.Text = "Initialization error: " + ex.Message;
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _trayService != null)
            {
                Hide();
                // No balloon tip - CROWN starts quietly
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var settings = _services.GetRequiredService<ISettingsService>().Settings;

            // Only hide to tray if a tray icon actually exists. Otherwise closing the
            // last window with OnExplicitShutdown would leave an unreachable process.
            if (settings.General.MinimizeToTray && !_isClosingToTray && _trayService != null)
            {
                e.Cancel = true;
                Hide();
                // No balloon tip - CROWN runs quietly in background
            }
            else
            {
                ExitApplication();
            }
        }

        private void ShowWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Focus();
        }

        private void ExitApplication()
        {
            _isClosingToTray = true;
            try { _trayService?.Dispose(); } catch { }
            // Do not block the UI thread waiting on detector shutdown.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await _controllerManager.StopAsync(); } catch { }
            });
            Application.Current.Shutdown();
        }

        private void SaveWindowPosition()
        {
            var settings = _services.GetRequiredService<ISettingsService>().Settings;
            if (!settings.UI.RememberWindowPosition) return;

            settings.UI.WindowWidth = Width;
            settings.UI.WindowHeight = Height;
            settings.UI.WindowLeft = Left;
            settings.UI.WindowTop = Top;

            // CRITICAL: never block the UI thread on an async file write. Doing so
            // deadlocks the layout pass triggered by Show() and the window is created
            // but never becomes visible. Debounce instead and save in the background.
            _positionSaveTimer ??= new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(750),
                System.Windows.Threading.DispatcherPriority.Background,
                (s, e) =>
                {
                    _positionSaveTimer!.Stop();
                    var settingsService = _services.GetRequiredService<ISettingsService>();
                    _ = settingsService.SaveAsync();
                },
                Dispatcher);

            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        }

        private void LoadWindowPosition()
        {
            var settings = _services.GetRequiredService<ISettingsService>().Settings;
            if (settings.UI.RememberWindowPosition && settings.UI.WindowLeft >= 0)
            {
                Left = settings.UI.WindowLeft;
                Top = settings.UI.WindowTop;
                Width = settings.UI.WindowWidth;
                Height = settings.UI.WindowHeight;
            }
        }

        private void UpdateDetectorStatusIndicators()
        {
            // This would be updated based on actual detector status
            // For now, just show them as active
        }

        // Tray event handlers
        private void TrayIcon_TrayLeftMouseDown(object sender, RoutedEventArgs e)
        {
            if (e is MouseButtonEventArgs mouseArgs && mouseArgs.ClickCount == 2)
            {
                ShowWindow();
            }
        }

        private void Tray_Show_Click(object sender, RoutedEventArgs e) => ShowWindow();
        private void Tray_Refresh_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshCommand.Execute(null);
        private void Tray_Settings_Click(object sender, RoutedEventArgs e) => _viewModel.SettingsCommand.Execute(null);
        private void Tray_TestNotify_Click(object sender, RoutedEventArgs e) => _viewModel.TestNotificationCommand.Execute(null);
        private void Tray_Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            SaveWindowPosition();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            SaveWindowPosition();
        }
    }
}