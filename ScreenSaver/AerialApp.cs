using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Aerial
{
    /// <summary>
    /// Owns the WPF side of the process: window creation for each screensaver mode, the preview
    /// pane, and shutdown.
    ///
    /// The settings dialog is still WinForms and runs on the WinForms message loop instead
    /// (see Program.Main) - the two pumps are never started together.
    /// </summary>
    internal static class AerialApp
    {
        private static Application app;
        private static int fatalShown;

        private static Application EnsureApp()
        {
            if (app == null)
            {
                app = new Application
                {
                    // The screensaver opens one window per monitor, and closing any one of them
                    // must end the whole process. OnLastWindowClose would leave the black filler
                    // windows up. It also lets the preview run with no Window at all.
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
            }
            return app;
        }

        /// <summary>Full-screen screensaver across the monitors the user's mode calls for.</summary>
        internal static void RunScreenSaver()
        {
            var application = EnsureApp();
            CreateScreenSaverWindows();
            application.Run();
        }

        /// <summary>The resizable desktop window (/w, or running the .exe directly).</summary>
        internal static void RunWindowed()
        {
            var application = EnsureApp();
            var window = new ScreenSaverWindow(ComputeWindowModeBounds(),
                                               shouldCache: true, showVideo: true,
                                               isPrimary: true, windowMode: true);
            window.Show();
            application.Run();
        }

        /// <summary>
        /// The little preview pane inside the Windows screensaver settings dialog (/p).
        ///
        /// Uses HwndSource rather than a Window: HwndSourceParameters can pass ParentWindow and
        /// WS_CHILD at window-creation time, so the window is born a child. The old WinForms code
        /// had to create a top-level window, re-parent it, flip WS_CHILD afterwards and then fudge
        /// its size by a pixel to hide the leftover border.
        /// </summary>
        internal static void RunPreview(IntPtr parentHandle)
        {
            var application = EnsureApp();

            Drawing.Rectangle parentRect;
            NativeMethods.GetClientRect(parentHandle, out parentRect);

            var parameters = new HwndSourceParameters("AerialPreview")
            {
                ParentWindow = parentHandle,
                WindowStyle = unchecked((int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE)),
                PositionX = 0,
                PositionY = 0,
                Width = parentRect.Width,
                Height = parentRect.Height,
                UsesPerPixelOpacity = false,
            };

            var source = new HwndSource(parameters);

            // No crossfade here: a ~166x130 pane doesn't benefit, and it halves the decode cost
            // inside the Control Panel.
            var surface = new VideoSurface(enableCrossfade: false, crossfadeDuration: TimeSpan.Zero);
            source.RootVisual = surface;

            var settings = new RegSettings();
            var controller = new PlaybackController(AerialContext.GetMovies(), settings.VideoQuality,
                                                    cacheEnabled: false, shouldCache: false);
            surface.Start(controller);

            source.Disposed += (s, e) => Exit();

            // The Control Panel destroying its preview pane is our cue to exit. The polling
            // watchdog is deliberate belt-and-braces: preview hosts are idiosyncratic and an
            // orphaned Aerial.scr process is the failure everyone notices.
            var watchdog = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            watchdog.Tick += (s, e) =>
            {
                if (!NativeMethods.IsWindow(parentHandle)) Exit();
            };
            watchdog.Start();

            application.Run();
        }

        /// <summary>Ends the process, whichever mode it is running in.</summary>
        internal static void Exit()
        {
            // OverrideCursor is process-wide, so it must be cleared or the pointer stays hidden.
            try { Mouse.OverrideCursor = null; }
            catch (Exception) { /* no dispatcher yet - nothing to restore */ }

            var current = Application.Current;
            if (current != null) current.Dispatcher.BeginInvoke((Action)current.Shutdown);
            else Forms.Application.Exit();
        }

        /// <summary>Advances the video on every screensaver window, not just the focused one.</summary>
        internal static void SkipAllToNext()
        {
            var current = Application.Current;
            if (current == null) return;

            foreach (Window window in current.Windows)
            {
                var screenSaver = window as ScreenSaverWindow;
                if (screenSaver != null) screenSaver.SkipToNext();
            }
        }

        /// <summary>
        /// Shows an error at most once for the whole process. Without this, a four-monitor setup
        /// produced four stacked dialogs behind a topmost window.
        /// </summary>
        internal static void ShowFatalOnce(Window owner, string message)
        {
            if (Interlocked.Exchange(ref fatalShown, 1) != 0) return;

            Trace.WriteLine("Fatal: " + message);
            MessageBox.Show(owner, message, "Aerial", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void CreateScreenSaverWindows()
        {
            var mode = new RegSettings().MultiMonitorMode;

            if (mode == RegSettings.MultiMonitorModeEnum.SpanAll)
            {
                Show(Forms.Screen.AllScreens.GetBounds(), shouldCache: true, showVideo: true, isPrimary: true);
                return;
            }

            foreach (var screen in Forms.Screen.AllScreens)
            {
                // MainOnly still covers the other monitors, just with black.
                var showVideo = mode != RegSettings.MultiMonitorModeEnum.MainOnly || screen.Primary;
                Show(screen.Bounds, shouldCache: screen.Primary, showVideo: showVideo,
                     isPrimary: screen.Primary);
            }
        }

        private static void Show(Drawing.Rectangle bounds, bool shouldCache, bool showVideo, bool isPrimary)
        {
            var window = new ScreenSaverWindow(bounds, shouldCache, showVideo, isPrimary, windowMode: false);
            window.Show();
        }

        /// <summary>
        /// Window-mode bounds: 1920x1080 centred on the primary work area when it fits, or the
        /// whole virtual desktop when the user has chosen to span all screens. Ports the old
        /// ScreenSaverForm.MaximizeVideo().
        /// </summary>
        private static Drawing.Rectangle ComputeWindowModeBounds()
        {
            if (new RegSettings().MultiMonitorMode == RegSettings.MultiMonitorModeEnum.SpanAll)
                return Forms.Screen.AllScreens.GetBounds();

            var work = Forms.Screen.PrimaryScreen.WorkingArea;
            var width = Math.Min(1920, work.Width);
            var height = Math.Min(1080, work.Height);

            return new Drawing.Rectangle(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2,
                width,
                height);
        }
    }
}
