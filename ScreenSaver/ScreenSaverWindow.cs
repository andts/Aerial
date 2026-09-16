using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Aerial
{
    /// <summary>
    /// A full-screen (or windowed) screensaver window.
    ///
    /// Built in code rather than XAML - see the note on <see cref="VideoSurface"/>.
    /// </summary>
    internal sealed class ScreenSaverWindow : Window
    {
        private const double MouseMoveThreshold = 5;   // device pixels
        private const double DragBorder = 12;          // DIPs
        private static readonly TimeSpan ChromeHideAfter = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CrossfadeDuration = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// Ignore mouse movement for a moment after showing. A window appearing under the pointer
        /// generates a move event by itself, which would exit the screensaver instantly.
        /// </summary>
        private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(750);

        private readonly Drawing.Rectangle boundsPx;
        private readonly bool showVideo;
        private readonly bool isPrimary;
        private readonly bool windowMode;
        private readonly bool shouldCache;

        private readonly VideoSurface surface;
        private readonly DispatcherTimer chromeTimer;
        private readonly RegSettings settings;

        private DateTime shownUtc;
        private Point? lastMouseScreen;
        private DateTime lastInteractionUtc;

        /// <param name="boundsPx">Where to put the window, in physical pixels.</param>
        /// <param name="shouldCache">True on the one window responsible for filling the cache.</param>
        /// <param name="showVideo">False for the black filler windows in "main screen only" mode.</param>
        /// <param name="isPrimary">True on the one window allowed to show error dialogs.</param>
        /// <param name="windowMode">True for the resizable /w window rather than a screensaver.</param>
        internal ScreenSaverWindow(Drawing.Rectangle boundsPx, bool shouldCache, bool showVideo,
                                   bool isPrimary, bool windowMode)
        {
            this.boundsPx = boundsPx;
            this.shouldCache = shouldCache;
            this.showVideo = showVideo;
            this.isPrimary = isPrimary;
            this.windowMode = windowMode;

            Title = "Aerial Screensaver";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Must stay false: a layered window forces software compositing, which makes
            // MediaElement render black.
            AllowsTransparency = false;
            ShowInTaskbar = windowMode;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Brushes.Black;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            // Read once here rather than per video, as the old code did.
            settings = new RegSettings();

            // Crossfading runs two decoders at once for a moment. That's cheap at 1080p and not
            // cheap at 4K HEVC, especially multiplied by monitor count, so skip it there. Software
            // rendering would composite both on the CPU, so skip it there too.
            var enableCrossfade = settings.VideoQuality != RegSettings.VideoQualityEnum.Hevc4k
                                  && !settings.SoftwareRendering;

            surface = new VideoSurface(enableCrossfade, CrossfadeDuration);
            surface.Fatal += OnFatal;
            surface.CloseRequested += (s, e) => AerialApp.Exit();
            surface.SettingsRequested += (s, e) => OpenSettings();
            Content = surface;

            chromeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            chromeTimer.Tick += OnChromeTick;

            Loaded += OnLoaded;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            MouseMove += OnMouseMove;
            PreviewMouseDown += OnPreviewMouseDown;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyBounds(hwnd);

#if !DEBUG
            if (!windowMode) Topmost = true;
#endif

            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null) source.AddHook(WndProc);
        }

        /// <summary>
        /// Positions the window with Win32 rather than WPF's Left/Top/Width/Height, because those
        /// are device-independent units while monitor bounds are physical pixels. On a mixed-DPI
        /// setup that difference puts windows on the wrong monitor or at the wrong size.
        /// </summary>
        private void ApplyBounds(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || boundsPx.Width <= 0 || boundsPx.Height <= 0) return;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                boundsPx.X, boundsPx.Y, boundsPx.Width, boundsPx.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Re-assert our bounds if the user changes scaling or rearranges monitors while we run;
            // WPF otherwise accepts the OS-suggested rectangle and can drift off the monitor.
            if (msg == NativeMethods.WM_DPICHANGED || msg == NativeMethods.WM_DISPLAYCHANGE)
            {
                if (!windowMode)
                    Dispatcher.BeginInvoke((Action)(() => ApplyBounds(hwnd)));
            }
            return IntPtr.Zero;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            shownUtc = DateTime.UtcNow;
            lastInteractionUtc = DateTime.UtcNow;

            if (!windowMode) Mouse.OverrideCursor = Cursors.None;

            // A borderless window needs to ask for focus or it never sees key presses.
            Focus();
            Keyboard.Focus(this);

            if (windowMode)
            {
                surface.ShowChrome(true);
                chromeTimer.Start();
            }

            if (!showVideo) return;   // black filler window in "main screen only" mode

            var movies = AerialContext.GetMovies();
            var controller = new PlaybackController(
                movies,
                settings.VideoQuality,
                settings.CacheVideos,
                shouldCache);

            surface.Start(controller);
        }

        internal void SkipToNext()
        {
            if (showVideo) surface.SkipToNext();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            chromeTimer.Stop();
            surface.Stop();
        }

        private void OnFatal(object sender, string message)
        {
            // Only one window reports; AerialApp additionally guarantees a single dialog process-wide.
            if (!isPrimary) return;
            AerialApp.ShowFatalOnce(this, message);
        }

        // --- input ---------------------------------------------------------------------------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            lastInteractionUtc = DateTime.UtcNow;

            if (e.Key == Key.N)
            {
                // Only the focused window receives the key, but every monitor should advance.
                AerialApp.SkipAllToNext();
                e.Handled = true;
                return;
            }

            if (!windowMode) AerialApp.Exit();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            lastInteractionUtc = DateTime.UtcNow;

            if (windowMode)
            {
                surface.ShowChrome(true);
                UpdateResizeCursor(e.GetPosition(this));
                return;
            }

            if (DateTime.UtcNow - shownUtc < StartupGrace) return;

            // One coordinate space for every event, so there is nothing to get out of sync.
            var screenPoint = PointToScreen(e.GetPosition(this));

            if (lastMouseScreen.HasValue)
            {
                var previous = lastMouseScreen.Value;
                if (Math.Abs(previous.X - screenPoint.X) > MouseMoveThreshold ||
                    Math.Abs(previous.Y - screenPoint.Y) > MouseMoveThreshold)
                {
                    AerialApp.Exit();
                    return;
                }
            }

            lastMouseScreen = screenPoint;
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            lastInteractionUtc = DateTime.UtcNow;

            if (!windowMode)
            {
                AerialApp.Exit();
                return;
            }

            if (e.ChangedButton != MouseButton.Left) return;

            var m = e.GetPosition(this);
            bool? toTop = HotZone(m.Y, ActualHeight);
            bool? toLeft = HotZone(m.X, ActualWidth);
            var hwnd = new WindowInteropHelper(this).Handle;

            if (toTop == null && toLeft == null) NativeMethods.DragWindow(hwnd);
            else NativeMethods.ResizeWindow(hwnd, toTop, toLeft);
        }

        /// <summary>True near the start, false near the end, null in between.</summary>
        private static bool? HotZone(double value, double extent)
        {
            if (value < DragBorder) return true;
            if (value > extent - DragBorder) return false;
            return null;
        }

        private void UpdateResizeCursor(Point m)
        {
            bool? toTop = HotZone(m.Y, ActualHeight);
            bool? toLeft = HotZone(m.X, ActualWidth);

            if (toTop == null && toLeft == null) { Cursor = Cursors.Arrow; return; }
            if (toTop == null) { Cursor = Cursors.SizeWE; return; }
            if (toLeft == null) { Cursor = Cursors.SizeNS; return; }
            Cursor = toTop == toLeft ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }

        private void OnChromeTick(object sender, EventArgs e)
        {
            if (DateTime.UtcNow - lastInteractionUtc > ChromeHideAfter)
                surface.ShowChrome(false);
        }

        private void OpenSettings()
        {
            // The settings dialog is still WinForms. Give it this window as its owner so it opens
            // in front of a topmost screensaver instead of behind it.
            var wasTopmost = Topmost;
            Topmost = false;
            try
            {
                using (var form = new ScreenSaver.SettingsForm())
                {
                    form.StartPosition = Forms.FormStartPosition.CenterScreen;
                    form.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Could not open settings: " + ex);
            }
            finally
            {
                Topmost = wasTopmost;
            }
        }

        /// <summary>Adapter so a WinForms dialog can be owned by a WPF window's HWND.</summary>
        private sealed class Win32Window : Forms.IWin32Window
        {
            public Win32Window(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }
    }
}
