using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using CrownControllerHUD.ViewModels;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrownControllerHUD
{
    public partial class App : Application
    {
        private IHost? _host;
        private Mutex? _singleInstanceMutex;
        private const string MutexName = "Global\\CrownControllerHUD_SingleInstance";

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Single instance check
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            
            if (!createdNew)
            {
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            // Parse command line arguments
            var args = e.Args;
            var startMinimized = args.Contains("--minimized") || args.Contains("-minimized");
            var startHidden = args.Contains("--hidden") || args.Contains("-hidden");

            // Build host with DI
            _host = CreateHostBuilder(args).Build();

            // Start host
            await _host.StartAsync();

            // Get services
            var settingsService = _host.Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            System.Diagnostics.Debug.WriteLine("MainWindow retrieved from DI");
            
            // Apply theme
            ApplyTheme(settingsService.Settings.General.Theme);

            // Show or hide based on settings/args
            if (settingsService.Settings.General.StartMinimized || startMinimized || startHidden)
            {
                mainWindow.Hide();
                System.Diagnostics.Debug.WriteLine("MainWindow hidden");
            }
            else
            {
                mainWindow.Show();
                System.Diagnostics.Debug.WriteLine("MainWindow shown");
            }

            // Initialize tray (which will show balloon tip if minimized)
            var trayService = _host.Services.GetRequiredService<ITrayService>();
            if (startMinimized || startHidden || settingsService.Settings.General.StartMinimized)
            {
                trayService.ShowBalloonTip("CROWN Controller HUD", "Running in background", BalloonIcon.Info);
            }
        }

        private IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Configuration - use built-in options
                    services.AddOptions<AppSettings>();
                    services.AddSingleton<IOptionsMonitor<AppSettings>>(sp => 
                        sp.GetRequiredService<IOptionsMonitor<AppSettings>>());

                    // Settings
                    services.AddSingleton<ISettingsService, SettingsService>();

                    // Controller Detection
                    services.AddSingleton<IControllerDetector, WinRTControllerDetector>();
                    services.AddSingleton<IControllerDetector, HidControllerDetector>();
                    services.AddSingleton<IControllerDetector, BluetoothLEControllerDetector>();
                    services.AddSingleton<IControllerManager, ControllerManager>();

                    // Services
                    services.AddSingleton<INotificationService, NotificationService>();
                    services.AddSingleton<ITrayService, TrayService>();
                    services.AddSingleton<IStartupService, StartupService>();

                    // ViewModels
                    services.AddTransient<MainViewModel>();
                    services.AddTransient<SettingsViewModel>();

                    // Views
                    services.AddTransient<MainWindow>();

                    // Logging
                    services.AddLogging(builder =>
                    {
                        builder.ClearProviders();
                        builder.AddDebug();
                        builder.AddConsole();
                        builder.SetMinimumLevel(LogLevel.Information);
                    });
                });
                // Don't use UseConsoleLifetime - WPF has its own message loop
        }

        private void ApplyTheme(string theme)
        {
            // Theme application logic would go here
            // For now, we use the dark theme defined in resources
        }

        private void ActivateExistingInstance()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(current.ProcessName);
                
                foreach (var process in processes)
                {
                    if (process.Id != current.Id)
                    {
                        var handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                            NativeMethods.SetForegroundWindow(handle);
                        }
                        break;
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();

            base.OnExit(e);
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Handled in OnStartup
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            // Handled in OnExit
        }

        internal static class NativeMethods
        {
            public const int SW_RESTORE = 9;
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
        }
    }
}