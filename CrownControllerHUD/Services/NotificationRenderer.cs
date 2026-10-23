using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.Services
{
    public enum CrownNotificationKind
    {
        Info,
        Connected,
        Disconnected,
        BatteryLow,
        BatteryCritical,
        Charging
    }

    /// <summary>Everything the CROWN notification card needs to render one event.</summary>
    public sealed class CrownNotificationRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public string Subtitle { get; set; } = "CROWN CONTROLLER HUD";
        public string? TimestampText { get; set; }
        public CrownNotificationKind Kind { get; set; } = CrownNotificationKind.Info;
        public int DurationMs { get; set; } = 5000;
        public bool PlaySound { get; set; }

        /// <summary>Short tag used in the diagnostic log, e.g. "connected".</summary>
        public string LogLabel { get; set; } = "notification";
    }

    public interface INotificationRenderer
    {
        /// <summary>True when a WPF Application/dispatcher exists so a card can be shown.</summary>
        bool IsAvailable { get; }

        /// <summary>Number of cards currently on screen.</summary>
        int ActiveCount { get; }

        /// <summary>Queues a card for display. Never blocks the caller.</summary>
        bool Show(CrownNotificationRequest request);

        /// <summary>Closes every card that is currently on screen.</summary>
        void DismissAll(string reason);
    }

    /// <summary>
    /// Renders CROWN notifications as our own WPF windows.
    ///
    /// This is the ONLY renderer: UWP toasts cannot work for this unpackaged app
    /// (no registered AppUserModelID) and tray balloons are explicitly not used for
    /// controller events. All work is marshalled onto the WPF dispatcher without
    /// ever blocking the controller-monitoring threads.
    /// </summary>
    public sealed class CrownNotificationRenderer : INotificationRenderer, IDisposable
    {
        private const int MaxConcurrent = 4;
        private const double HorizontalMarginDip = 18;
        private const double VerticalMarginDip = 18;
        private const double GapDip = 10;

        private readonly ILogger<CrownNotificationRenderer> _logger;
        private readonly ISettingsService _settingsService;
        private readonly List<CrownNotificationWindow> _open = new();
        private int _nextCardId = 1;
        private bool _disposed;

        public CrownNotificationRenderer(
            ILogger<CrownNotificationRenderer> logger,
            ISettingsService settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        public bool IsAvailable => Application.Current != null;

        public int ActiveCount
        {
            get
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || !dispatcher.CheckAccess())
                    return _open.Count;

                return _open.Count;
            }
        }

        public bool Show(CrownNotificationRequest request)
        {
            if (_disposed || request == null)
                return false;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _logger.LogWarning(
                    "[Notify] No WPF application dispatcher available - dropping {Kind} notification ({Label})",
                    request.Kind, request.LogLabel);
                return false;
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    ShowOnUiThread(request);
                }
                else
                {
                    // BeginInvoke only: the caller (a monitor callback or a controller
                    // event) must never block waiting for the UI thread.
                    dispatcher.BeginInvoke(new Action(() => ShowOnUiThread(request)), DispatcherPriority.Normal);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Notify] Failed to marshal {Label} notification to the UI thread", request.LogLabel);
                return false;
            }
        }

        private void ShowOnUiThread(CrownNotificationRequest request)
        {
            try
            {
                if (_disposed)
                    return;

                while (_open.Count >= MaxConcurrent)
                {
                    var oldest = _open[0];
                    _logger.LogInformation(
                        "[Notify] Too many cards open ({Count}); dismissing oldest card={Id} to make room",
                        _open.Count, oldest.CardId);
                    oldest.Dismiss("superseded");
                    _open.Remove(oldest);
                }

                var card = new CrownNotificationWindow
                {
                    CardId = _nextCardId++,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Opacity = 0,
                    // Park offscreen until measured+placed so nothing flashes in the wrong spot.
                    Left = -32000,
                    Top = -32000
                };

                card.Configure(request);
                card.Closed += (_, _) => OnCardClosed(card);
                card.SizeChanged += (_, _) => Restack($"card {card.CardId} resized");

                _open.Add(card);
                card.Show();

                _logger.LogInformation(
                    "[Notify] Window created card={Id} kind={Kind} label={Label} hwnd=0x{Hwnd:X}",
                    card.CardId, request.Kind, request.LogLabel, card.Handle.ToInt64());

                Restack($"card {card.CardId} created");

                if (request.PlaySound)
                    NotificationSound.Play(request.Kind);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Notify] Failed to create the notification window for {Label}", request.LogLabel);
            }
        }

        private void OnCardClosed(CrownNotificationWindow card)
        {
            try
            {
                bool removed = _open.Remove(card);
                _logger.LogInformation(
                    "[Notify] Window dismissed card={Id} label={Label} reason='{Reason}' cardsStillOpen={Remaining}",
                    card.CardId, card.LogLabel, card.DismissReason, _open.Count);

                if (removed)
                    Restack($"card {card.CardId} closed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Notify] Error while tearing down card {Id}", card.CardId);
            }
        }

        /// <summary>
        /// Places every open card on the target monitor according to the configured
        /// NotificationPosition setting. Position is applied in physical pixels so DPI
        /// scaling cannot push a card off screen.
        /// </summary>
        private void Restack(string reason)
        {
            try
            {
                if (_open.Count == 0)
                    return;

                var area = ResolveTargetWorkArea();
                if (area == null)
                    return;

                int margin = area.Scale(HorizontalMarginDip);
                int vmargin = area.Scale(VerticalMarginDip);
                int gap = area.Scale(GapDip);

                // Get notification position from settings
                var position = _settingsService.Settings.UI.NotificationPosition;

                foreach (var card in _open)
                {
                    if (card.Handle == IntPtr.Zero)
                        continue;

                    if (!NativeWindowMethods.TryGetWindowRect(card.Handle, out var rect))
                        continue;

                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;

                    int x, y;
                    CalculatePosition(position, area, rect, margin, vmargin, gap, out x, out y);

                    bool moved = NativeWindowMethods.MoveTo(card.Handle, x, y);

                    if (!card.Placed)
                    {
                        card.Placed = true;
                        card.PlayEnterAnimation();
                        NativeWindowMethods.ReassertTopmost(card.Handle);

                        _logger.LogInformation(
                            "[Notify] Window displayed card={Id} label={Label} at x={X} y={Y} size={W}x{H} " +
                            "monitor=[{ML},{MT},{MR},{MB}] dpi={Dpi} moved={Moved} reason={Reason}",
                            card.CardId, card.LogLabel, x, y, rect.Width, rect.Height,
                            area.Work.Left, area.Work.Top, area.Work.Right, area.Work.Bottom,
                            area.Dpi, moved, reason);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Notify] Failed to reposition notification cards");
            }
        }

        private void CalculatePosition(string position, NativeWindowMethods.MonitorWorkArea area, 
            NativeWindowMethods.RECT rect, int margin, int vmargin, int gap, out int x, out int y)
        {
            // Default to bottom-right
            int right = area.Work.Right - margin;
            int bottom = area.Work.Bottom - vmargin;

            // Calculate base positions
            int left = area.Work.Left + margin;
            int top = area.Work.Top + vmargin;
            int centerX = area.Work.Left + (area.Work.Width - rect.Width) / 2;
            int centerY = area.Work.Top + (area.Work.Height - rect.Height) / 2;

            switch (position)
            {
                case "BottomLeft":
                    x = left;
                    y = bottom - rect.Height;
                    break;
                case "TopRight":
                    x = right - rect.Width;
                    y = top;
                    break;
                case "TopLeft":
                    x = left;
                    y = top;
                    break;
                case "TopCenter":
                    x = centerX;
                    y = top;
                    break;
                case "BottomCenter":
                    x = centerX;
                    y = bottom - rect.Height;
                    break;
                case "BottomRight":
                default:
                    x = right - rect.Width;
                    y = bottom - rect.Height;
                    break;
            }
        }

        /// <summary>
        /// The monitor the dashboard is on (that is the "selected" monitor), falling
        /// back to the primary monitor when the dashboard has never been shown
        /// (e.g. --hidden at sign-in).
        /// </summary>
        private NativeWindowMethods.MonitorWorkArea? ResolveTargetWorkArea()
        {
            try
            {
                var monitors = NativeWindowMethods.GetMonitors();
                if (monitors.Count == 0)
                    return null;

                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    var main = Application.Current?.MainWindow;
                    if (main != null && PresentationSource.FromVisual(main) is HwndSource source)
                        hwnd = source.Handle;
                }
                catch
                {
                    // Main window not shown / already closed - fall back to primary.
                }

                if (hwnd != IntPtr.Zero)
                {
                    var target = NativeWindowMethods.MonitorHandleFromWindow(hwnd);
                    var match = monitors.FirstOrDefault(m => m.Handle == target);
                    if (match != null)
                        return match;

                    _logger.LogInformation("[Notify] Dashboard monitor not found in enumeration; using primary monitor");
                }

                return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Notify] Failed to resolve the target monitor");
                return null;
            }
        }

        public void DismissAll(string reason)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.BeginInvoke(new Action(() => DismissAll(reason)), DispatcherPriority.Normal);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Notify] Failed to marshal DismissAll");
                }

                return;
            }

            var cards = _open.ToArray();
            if (cards.Length > 0)
                _logger.LogInformation("[Notify] Dismissing {Count} card(s): {Reason}", cards.Length, reason);

            foreach (var card in cards)
            {
                try { card.Dismiss(reason); }
                catch { /* shutting down */ }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DismissAll("application shutdown");
        }
    }
}
