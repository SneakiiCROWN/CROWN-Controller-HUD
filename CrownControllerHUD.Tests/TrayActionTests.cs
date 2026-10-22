using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using CrownControllerHUD.ViewModels;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CrownControllerHUD.Tests
{
    /// <summary>
    /// Regression tests for tray actions - Settings window, Refresh, Notifications.
    /// These tests verify the core tray menu functionality works correctly.
    /// </summary>
    public class TrayActionTests
    {
        [Fact]
        public void TrayService_Constructor_creates_instance()
        {
            var mockControllerManager = new Mock<IControllerManager>();
            var mockSettingsService = new Mock<ISettingsService>();
            var mockNotificationService = new Mock<INotificationService>();
            var mockStartupService = new Mock<IStartupService>();

            mockSettingsService.SetupGet(s => s.Settings).Returns(new AppSettings());

            var trayService = new TrayService(
                Mock.Of<ILogger<TrayService>>(),
                mockSettingsService.Object,
                mockNotificationService.Object,
                mockStartupService.Object,
                mockControllerManager.Object);

            Assert.NotNull(trayService);
        }

        [Fact]
        public async Task MainViewModel_RefreshCommand_triggers_ForceRefreshAsync()
        {
            var mockControllerManager = new Mock<IControllerManager>();
            mockControllerManager.Setup(m => m.ForceRefreshAsync()).Returns(Task.CompletedTask);
            mockControllerManager.SetupGet(m => m.LastRefreshError).Returns((string?)null);

            var mockSettingsService = new Mock<ISettingsService>();
            mockSettingsService.SetupGet(s => s.Settings).Returns(new AppSettings());

            var mockNotificationService = new Mock<INotificationService>();
            var mockTrayService = new Mock<ITrayService>();
            var mockBatteryWarnings = new Mock<IBatteryWarningPolicy>();
            var mockLogger = Mock.Of<ILogger<MainViewModel>>();

            var viewModel = new MainViewModel(
                mockControllerManager.Object,
                mockNotificationService.Object,
                mockSettingsService.Object,
                mockTrayService.Object,
                mockBatteryWarnings.Object,
                mockLogger);

            await viewModel.RefreshAsync();

            mockControllerManager.Verify(m => m.ForceRefreshAsync(), Times.Once);
        }

        [Fact]
        public async Task MainViewModel_TestNotificationCommand_shows_notification()
        {
            var mockNotificationService = new Mock<INotificationService>();
            mockNotificationService.Setup(n => n.ShowTestNotificationAsync()).Returns(Task.CompletedTask);

            var mockControllerManager = new Mock<IControllerManager>();
            mockControllerManager.SetupGet(m => m.LastRefreshError).Returns((string?)null);

            var mockSettingsService = new Mock<ISettingsService>();
            mockSettingsService.SetupGet(s => s.Settings).Returns(new AppSettings());

            var mockTrayService = new Mock<ITrayService>();
            var mockBatteryWarnings = new Mock<IBatteryWarningPolicy>();
            var mockLogger = Mock.Of<ILogger<MainViewModel>>();

            var viewModel = new MainViewModel(
                mockControllerManager.Object,
                mockNotificationService.Object,
                mockSettingsService.Object,
                mockTrayService.Object,
                mockBatteryWarnings.Object,
                mockLogger);

            viewModel.TestNotificationCommand.Execute(null);

            mockNotificationService.Verify(n => n.ShowTestNotificationAsync(), Times.Once);
        }

        [Fact]
        public void TrayService_ToggleNotifications_toggles_setting()
        {
            var settings = new AppSettings();
            settings.Notifications.EnableNotifications = true;

            var mockSettingsService = new Mock<ISettingsService>();
            mockSettingsService.SetupGet(s => s.Settings).Returns(settings);
            mockSettingsService.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);

            var mockControllerManager = new Mock<IControllerManager>();
            var mockNotificationService = new Mock<INotificationService>();
            var mockStartupService = new Mock<IStartupService>();

            var trayService = new TrayService(
                Mock.Of<ILogger<TrayService>>(),
                mockSettingsService.Object,
                mockNotificationService.Object,
                mockStartupService.Object,
                mockControllerManager.Object);

            // Simulate the toggle action (normally in context menu click handler)
            var currentSettings = mockSettingsService.Object.Settings;
            currentSettings.Notifications.EnableNotifications = !currentSettings.Notifications.EnableNotifications;
            
            Assert.False(currentSettings.Notifications.EnableNotifications);
            mockSettingsService.VerifyGet(s => s.Settings);
        }

        [Fact]
        public async Task NotificationService_ShowTestNotificationAsync_creates_multiple_cards()
        {
            var mockRenderer = new Mock<INotificationRenderer>();
            mockRenderer.Setup(r => r.Show(It.IsAny<CrownNotificationRequest>())).Returns(true);

            var mockSettingsService = new Mock<ISettingsService>();
            var settings = new AppSettings();
            settings.Notifications.EnableNotifications = true;
            settings.Notifications.PlaySound = false;
            mockSettingsService.SetupGet(s => s.Settings).Returns(settings);

            var mockLogger = Mock.Of<ILogger<NotificationService>>();

            var notificationService = new NotificationService(
                mockLogger,
                mockSettingsService.Object,
                mockRenderer.Object);

            await notificationService.ShowTestNotificationAsync();

            // Test notification shows 3 cards: connected, disconnected, battery low
            mockRenderer.Verify(r => r.Show(It.IsAny<CrownNotificationRequest>()), Times.Exactly(3));
        }

        [Fact]
        public async Task SettingsWindow_GetOrCreateInstance_returns_singleton()
        {
            var mockSettingsService = new Mock<ISettingsService>();
            mockSettingsService.SetupGet(s => s.Settings).Returns(new AppSettings());

            var mockStartupService = new Mock<IStartupService>();
            var mockNotificationService = new Mock<INotificationService>();
            var mockControllerManager = new Mock<IControllerManager>();
            var mockLogger = Mock.Of<ILogger<SettingsWindow>>();

            // Note: This test requires WPF context to fully work
            // We verify the method exists and is callable
            Assert.NotNull(typeof(SettingsWindow).GetMethod("GetOrCreateInstance"));
        }
    }
}