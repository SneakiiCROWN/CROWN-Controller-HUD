using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// The CROWN notification card. A borderless, never-activating topmost window
    /// that renders a compact, premium dark notification. It deliberately replaces
    /// both UWP toasts (which need a registered AppUserModelID that an unpackaged
    /// app does not have) and generic tray balloons.
    ///
    /// The window never positions itself: <see cref="CrownNotificationRenderer"/>
    /// owns placement so it can stack several cards on the target monitor.
    /// </summary>
    public partial class CrownNotificationWindow : Window
    {
        private const int TickMs = 33;

        private readonly DispatcherTimer _timer;
        private double _totalMs;
        private double _remainingMs;
        private DateTime _lastTickAt;
        private bool _paused;
        private bool _closing;
        private bool _entered;

        public CrownNotificationWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(TickMs)
            };
            _timer.Tick += OnTick;

            // Click anywhere on the card to dismiss it immediately.
            Card.MouseLeftButtonUp += (_, _) => Dismiss("clicked");
            Card.MouseEnter += (_, _) => _paused = true;
            Card.MouseLeave += (_, _) => _paused = false;
        }

        /// <summary>The underlying HWND, valid after SourceInitialized.</summary>
        public IntPtr Handle { get; private set; }

        /// <summary>Set by the renderer so it can identify this card in its log lines.</summary>
        public int CardId { get; set; }

        /// <summary>A stable description of the event shown, for logging.</summary>
        public string LogLabel { get; private set; } = "notification";

        /// <summary>True once the renderer has placed (and revealed) this card.</summary>
        public bool Placed { get; set; }

        /// <summary>Why the card went away: "timed out", "clicked", "superseded", ...</summary>
        public string DismissReason { get; private set; } = "closed";

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // Never take focus, and stay out of Alt+Tab.
            NativeWindowMethods.ApplyNoActivateToolWindow(Handle);
        }

        public void Configure(CrownNotificationRequest request)
        {
            LogLabel = request.LogLabel;

            TitleText.Text = request.Title;
            MessageText.Text = request.Message;
            DetailText.Text = request.Detail ?? string.Empty;
            DetailText.Visibility = string.IsNullOrWhiteSpace(request.Detail)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SubtitleText.Text = request.Subtitle;

            if (!string.IsNullOrWhiteSpace(request.TimestampText))
                TimeText.Text = request.TimestampText;

            ApplyKind(request.Kind);

            _totalMs = Math.Max(1500, request.DurationMs);
            _remainingMs = _totalMs;
            ProgressScale.ScaleX = 1.0;
        }

        private void ApplyKind(CrownNotificationKind kind)
        {
            string accentFrom, accentTo, iconColor, glyph;

            switch (kind)
            {
                case CrownNotificationKind.Connected:
                    accentFrom = "#FF2FBF52"; accentTo = "#FF54E67A"; iconColor = "#FF6BF08C";
                    glyph = "M4,10 L8,14.5 L17,4.5";
                    break;

                case CrownNotificationKind.Disconnected:
                    accentFrom = "#FFD24A3A"; accentTo = "#FFF0685A"; iconColor = "#FFFF8E7C";
                    glyph = "M5.5,5.5 L14.5,14.5 M14.5,5.5 L5.5,14.5";
                    break;

                case CrownNotificationKind.BatteryLow:
                    accentFrom = "#FFE08A18"; accentTo = "#FFFFB942"; iconColor = "#FFFFC061";
                    glyph = "M3,7 L13,7 L13,13 L3,13 Z M13.6,8.8 L15.5,9.6 L15.5,10.4 L13.6,11.2 Z";
                    break;

                case CrownNotificationKind.BatteryCritical:
                    accentFrom = "#FFD92B3B"; accentTo = "#FFFF5C5C"; iconColor = "#FFFF7B7B";
                    glyph = "M3,7 L13,7 L13,13 L3,13 Z M13.6,8.8 L15.5,9.6 L15.5,10.4 L13.6,11.2 Z";
                    break;

                case CrownNotificationKind.Charging:
                    accentFrom = "#FF1995C9"; accentTo = "#FF45CDF5"; iconColor = "#FF6FDCFF";
                    glyph = "M10.5,2.5 L4.5,11 L8.6,11 L7.4,17.5 L14.5,8.6 L10.2,8.6 Z";
                    break;

                default: // Info / test
                    accentFrom = "#FF4B6DE8"; accentTo = "#FF8E6BF0"; iconColor = "#FFA390FF";
                    glyph = "M10,3 L10,11 M10,14.2 L10,16.2";
                    break;
            }

            var from = (Color)ColorConverter.ConvertFromString(accentFrom);
            var to = (Color)ColorConverter.ConvertFromString(accentTo);
            var icon = (Color)ColorConverter.ConvertFromString(iconColor);

            AccentBar.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(from, 0),
                    new GradientStop(to, 1)
                }
            };

            ProgressFill.Background = new SolidColorBrush(to);

            var iconBrush = new SolidColorBrush(icon);
            IconPath.Stroke = iconBrush;
            IconPath.Fill = Brushes.Transparent;
            IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, icon.R, icon.G, icon.B));

            if (kind == CrownNotificationKind.BatteryCritical || kind == CrownNotificationKind.BatteryLow)
                IconPath.Fill = iconBrush;

            IconPath.Data = Geometry.Parse(glyph);
        }

        /// <summary>Called by the renderer once the card has been placed. Starts the countdown.</summary>
        public void PlayEnterAnimation()
        {
            if (_entered)
                return;

            _entered = true;

            Opacity = 0;
            RootScale.ScaleX = 0.965;
            RootScale.ScaleY = 0.965;

            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

            _timer.Start();
            _lastTickAt = DateTime.UtcNow;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            // Wall-clock accounting: a busy UI thread can delay DispatcherTimer ticks,
            // and a fixed per-tick decrement made the card outlive its stated duration.
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastTickAt).TotalMilliseconds;
            _lastTickAt = now;

            if (_paused || _closing)
                return;

            _remainingMs -= elapsed;

            if (_remainingMs <= 0)
            {
                Dismiss("timed out");
                return;
            }

            ProgressScale.ScaleX = _remainingMs / _totalMs;
        }

        /// <summary>Fades out and closes. Safe to call more than once.</summary>
        public void Dismiss(string reason)
        {
            if (_closing)
                return;

            _closing = true;
            DismissReason = reason;
            _timer.Stop();

            try
            {
                var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fade.Completed += (_, _) =>
                {
                    try { Close(); } catch { /* already gone */ }
                };
                BeginAnimation(OpacityProperty, fade);
            }
            catch
            {
                try { Close(); } catch { /* already gone */ }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                BeginAnimation(OpacityProperty, null);
                RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            catch
            {
                // Nothing meaningful to do while tearing down.
            }

            base.OnClosed(e);
        }
    }
}
