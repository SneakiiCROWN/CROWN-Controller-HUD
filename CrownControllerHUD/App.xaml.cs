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
        public static IServiceProvider? ServiceProvider { get; private set; }
        private IHost? _host;
        private Mutex? _singleInstanceMutex;
        private const string MutexName = "Global\\CrownControllerHUD_SingleInstance";

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize diagnostic logging FIRST - before anything else
            DiagnosticLogger.Initialize();
            DiagnosticLogger.Info("Application OnStartup beginning");

            base.OnStartup(e);

            // Set up global exception handlers
            this.DispatcherUnhandledException += (s, args) =>
            {
                DiagnosticLogger.Error("DispatcherUnhandledException", args.Exception);
                args.Handled = true;
                // Show error to user
                MessageBox.Show($"Unhandled UI error:\n{args.Exception.Message}\n\nSee diagnostic.log for details.",
                    "CROWN Controller HUD - Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    DiagnosticLogger.Error("AppDomain.UnhandledException", ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                DiagnosticLogger.Error("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            try
            {
                // Single instance check
                DiagnosticLogger.Debug("Creating single-instance mutex");
                bool createdNew;
                _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);

                if (!createdNew)
                {
                    DiagnosticLogger.Info("Another instance detected, activating existing");
                    ActivateExistingInstance();
                    Shutdown();
                    return;
                }

                DiagnosticLogger.Debug("Single instance confirmed, continuing startup");

                // Parse command line arguments
                var argsList = e.Args;
                var startMinimized = argsList.Contains("--minimized") || argsList.Contains("-minimized");
                var startHidden = argsList.Contains("--hidden") || argsList.Contains("-hidden");
                var forceVisible = argsList.Contains("--visible") || argsList.Contains("-visible");

                DiagnosticLogger.Info($"Args: startMinimized={startMinimized}, startHidden={startHidden}, forceVisible={forceVisible}");

                // Build host with DI
                DiagnosticLogger.Debug("Building DI host");
                _host = CreateHostBuilder(argsList).Build();
                ServiceProvider = _host.Services;

                // Start host off the UI thread so it cannot capture the dispatcher
                // SynchronizationContext and deadlock the blocking wait below.
                DiagnosticLogger.Debug("Starting host");
                Task.Run(async () => await _host.StartAsync()).GetAwaiter().GetResult();
                DiagnosticLogger.Debug("Host started successfully");

                // Get services
                DiagnosticLogger.Debug("Retrieving services from DI");
                var settingsService = _host.Services.GetRequiredService<ISettingsService>();
                DiagnosticLogger.Debug("Loading settings");
                settingsService.LoadAsync().GetAwaiter().GetResult();
                DiagnosticLogger.Debug("Settings loaded");

                DiagnosticLogger.Debug("Resolving IControllerManager");
                _host.Services.GetRequiredService<IControllerManager>();
                DiagnosticLogger.Debug("IControllerManager resolved");

                DiagnosticLogger.Debug("Resolving ITrayService");
                _host.Services.GetRequiredService<ITrayService>();
                DiagnosticLogger.Debug("ITrayService resolved");

                DiagnosticLogger.Debug("Resolving MainViewModel");
                var viewModel = _host.Services.GetRequiredService<MainViewModel>();
                DiagnosticLogger.Debug("MainViewModel resolved");

                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                DiagnosticLogger.Debug("MainWindow retrieved from DI");

                // CRITICAL: Set Application.MainWindow so WPF knows about it
                this.MainWindow = mainWindow;
                DiagnosticLogger.Debug("Application.MainWindow set");

                // Apply theme
                ApplyTheme(settingsService.Settings.General.Theme);

                // Start controller monitoring now, independently of window visibility.
                // Window.Loaded does not fire for a window that is never shown (e.g. --hidden
                // when starting with Windows), which previously meant no detection or live
                // connection monitoring ran at all.
                _ = viewModel.InitializeAsync();

                // CRITICAL: On first launch, ALWAYS show the window
                // Only hide if explicitly requested via command line
                if (startHidden)
                {
                    mainWindow.Hide();
                    DiagnosticLogger.Info("MainWindow hidden (--hidden flag)");
                }
                else if (startMinimized && !forceVisible)
                {
                    mainWindow.Hide();
                    DiagnosticLogger.Info("MainWindow hidden (--minimized flag)");
                }
                else
                {
                    // Ensure window is visible and properly positioned
                    mainWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    mainWindow.Show();
                    mainWindow.Activate();
                    mainWindow.Focus();
                    DiagnosticLogger.Info("MainWindow shown and activated");
                }

                // Initialize tray AFTER the window is visible and in its own try/catch.
                try
                {
                    var trayService = _host.Services.GetRequiredService<ITrayService>();
                    trayService.Initialize();
                    DiagnosticLogger.Debug("TrayService initialized");

                    if (startMinimized || startHidden)
                    {
                        // No balloon tip - CROWN starts quietly in the tray.
                        DiagnosticLogger.Info("Application started hidden/minimized to tray");
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("TrayService initialization failed (non-fatal)", ex);
                }

                // Bring the window to the foreground once the message loop is running.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (mainWindow.IsVisible)
                        {
                            mainWindow.Activate();
                            mainWindow.Focus();
                            DiagnosticLogger.Info("MainWindow activated on dispatcher");
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Error("Window activation failed (non-fatal)", ex);
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                // Diagnostic aid: fire the real CROWN test notification at startup.
                // Handy for verifying the renderer from a log/automation without
                // needing to click the tray menu. Uses exactly the same path as the
                // UI button and as genuine controller events.
                if (argsList.Contains("--test-notification") || argsList.Contains("-test-notification"))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            DiagnosticLogger.Info("Diagnostic: --test-notification requested");
                            var notifier = _host.Services.GetRequiredService<INotificationService>();
                            _ = notifier.ShowTestNotificationAsync();
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Error("Diagnostic test notification failed", ex);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                // Diagnostic aid: query genuine battery telemetry for every controller that
                // is currently switched on, write a report next to the log and exit. This is
                // how real hardware battery reporting gets verified without the dashboard.
                if (argsList.Contains("--battery-report") || argsList.Contains("-battery-report"))
                {
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            DiagnosticLogger.Info("Diagnostic: --battery-report requested");

                            var manager = _host.Services.GetRequiredService<IControllerManager>();
                            var battery = _host.Services.GetRequiredService<IBatteryTelemetryService>();

                            await manager.ForceRefreshAsync().ConfigureAwait(true);

                            // Give the live signal watcher a moment to report a controller
                            // that is genuinely switched on.
                            await Task.Delay(1500).ConfigureAwait(true);
                            await manager.ForceRefreshAsync().ConfigureAwait(true);

                            var controllers = manager.Controllers
                                .Where(c => c.ConnectionState == ControllerConnectionState.Connected)
                                .ToList();

                            var report = await BatteryDiagnosticsReporter
                                .BuildAsync(controllers, battery, force: true, CancellationToken.None)
                                .ConfigureAwait(true);

                            DiagnosticLogger.Info("[Battery] report:\n" + report);

                            var reportPath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "CrownControllerHUD",
                                "battery-report.txt");

                            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                            File.WriteAllText(reportPath, report);
                            Console.Out.WriteLine(report);
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Error("Diagnostic battery report failed", ex);
                        }
                        finally
                        {
                            Shutdown(0);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                DiagnosticLogger.Info("Application startup completed successfully");            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("FATAL: Application startup failed", ex);
                
                // Try to show error to user even if DI failed
                try
                {
                    MessageBox.Show($"CROWN Controller HUD failed to start:\n\n{ex.Message}\n\nCheck diagnostic.log at:\n{DiagnosticLogger.GetLogPath()}",
                        "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                
                Shutdown(-1);
            }
        }

        private IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Settings - single source of truth for all settings.
                    // SettingsService implements ISettingsService. All services read from it.
                    services.AddSingleton<ISettingsService, SettingsService>();

                    // Controller Detection
                    // Live connection state is the single authority on whether a Bluetooth
                    // controller is actually switched on (device presence is not).
                    services.AddSingleton<ILiveConnectionMonitor, WinRTLiveConnectionMonitor>();

                    // USB live connection monitor: tracks actual USB controller presence
                    // via PnP, Windows.Gaming.Input and XInput. Never forces connections.
                    services.AddSingleton<IUsbLiveConnectionMonitor, UsbLiveConnectionMonitor>();

                    services.AddSingleton<IControllerDetector, WinRTControllerDetector>();
                    services.AddSingleton<IControllerDetector, HidControllerDetector>();
                    services.AddSingleton<IControllerDetector, BluetoothLEControllerDetector>();
                    services.AddSingleton<IControllerDetector, PnpControllerDetector>();

                    // Genuine battery telemetry. Windows.Gaming.Input TryGetBatteryReport
                    // first, then Bluetooth GATT 0x180F/0x2A19 on an already-connected
                    // device, then XInput's categorical levels. Never invents a value and
                    // never dials a powered-off controller.
                    services.AddSingleton<IBatteryTelemetryService, BatteryTelemetryService>();

                    // Threshold/debounce policy for battery warnings. Reads the live
                    // settings object so a settings change takes effect immediately.
                    services.AddSingleton<IBatteryWarningPolicy>(sp =>
                    {
                        var settingsService = sp.GetRequiredService<ISettingsService>();
                        return new BatteryWarningPolicy(() => settingsService.Settings.Notifications);
                    });

                    services.AddSingleton<IControllerManager, ControllerManager>();

                    // Services
                    // Notifications are rendered by our own CROWN WPF window: an
                    // unpackaged app has no registered AppUserModelID, so the UWP toast
                    // API silently produced nothing (and tray balloons are not used for
                    // controller events).
                    services.AddSingleton<INotificationRenderer, CrownNotificationRenderer>();
                    services.AddSingleton<INotificationService, NotificationService>();
                    services.AddSingleton<ITrayService, TrayService>();
                    services.AddSingleton<IStartupService, StartupService>();

                    // ViewModels
                    // MainViewModel MUST be a singleton: it subscribes to controller
                    // manager events, and it is resolved both explicitly (to start
                    // monitoring) and by MainWindow. As a transient that produced TWO
                    // instances each subscribed to the same events, so every controller
                    // transition raised two notifications.
                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<SettingsViewModel>();

                    // Views
                    services.AddTransient<MainWindow>();

                    // Logging - add file logging
                    services.AddLogging(builder =>
                    {
                        builder.ClearProviders();
                        builder.AddDebug();
                        builder.AddConsole();
                        builder.AddProvider(new FileLoggerProvider());
                        builder.SetMinimumLevel(LogLevel.Information);
                    });
                });
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
            catch (Exception ex)
            {
                DiagnosticLogger.Error("ActivateExistingInstance failed", ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DiagnosticLogger.Info("Application OnExit");

            if (_host != null)
            {
                try
                {
                    Task.Run(async () => await _host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
                    _host.Dispose();
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("Host shutdown error", ex);
                }
            }

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("Mutex release error", ex);
            }
            _singleInstanceMutex?.Dispose();

            base.OnExit(e);
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

    // File logger provider for Microsoft.Extensions.Logging
    internal class FileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
        public void Dispose() { }
    }

    internal class FileLogger : ILogger
    {
        private readonly string _category;
        public FileLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            
            var message = formatter(state, exception);
            var level = logLevel switch
            {
                LogLevel.Critical => "CRITICAL",
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARN",
                LogLevel.Information => "INFO",
                LogLevel.Debug => "DEBUG",
                LogLevel.Trace => "TRACE",
                _ => "INFO"
            };
            
            DiagnosticLogger.Write($"[{_category}] {message}", level);
            
            if (exception != null)
            {
                DiagnosticLogger.Write($"[{_category}] Exception: {exception}", level);
            }
        }
    }
}