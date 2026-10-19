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

        public MainWindow(IServiceProvider services, MainViewModel viewModel, IControllerManager controllerManager, ITrayService trayService, ILogger<MainWindow> logger)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow constructor started");
            InitializeComponent();
            
            _services = services;
            _viewModel = viewModel;
            _controllerManager = controllerManager;
            _trayService = trayService;
            _logger = logger;

            DataContext = _viewModel;
            
            // Wire up tray service events
            _trayService.ShowWindowRequested += (s, e) => ShowWindow();
            _trayService.ExitRequested += (s, e) => ExitApplication();

            // Initialize tray
            _trayService.Initialize();

            // Load window position from settings
            LoadWindowPosition();
            
            // Force window to be visible
            this.Visibility = Visibility.Visible;
            this.Show();
            this.Activate();
            
            System.Diagnostics.Debug.WriteLine("MainWindow constructor completed, window shown");
            _logger.LogInformation("MainWindow constructor completed, window shown");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
                UpdateDetectorStatusIndicators();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Window loaded error");
                StatusText.Text = "Initialization error: " + ex.Message;
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                _trayService.ShowBalloonTip("CROWN Controller HUD", "Running in background", BalloonIcon.Info);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var settings = _services.GetRequiredService<ISettingsService>().Settings;
            
            if (settings.General.MinimizeToTray && !_isClosingToTray)
            {
                e.Cancel = true;
                Hide();
                _trayService.ShowBalloonTip("CROWN Controller HUD", "Still running in tray", BalloonIcon.Info);
            }
            else
            {
                ExitApplication();
            }
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Focus();
        }

        private void ExitApplication()
        {
            _isClosingToTray = true;
            _trayService.Dispose();
            _controllerManager.StopAsync().Wait();
            Application.Current.Shutdown();
        }

        private void SaveWindowPosition()
        {
            var settings = _services.GetRequiredService<ISettingsService>().Settings;
            if (settings.UI.RememberWindowPosition)
            {
                settings.UI.WindowWidth = Width;
                settings.UI.WindowHeight = Height;
                settings.UI.WindowLeft = Left;
                settings.UI.WindowTop = Top;
                _services.GetRequiredService<ISettingsService>().SaveAsync().Wait();
            }
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