using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Aerial
{
    /// <summary>
    /// The video surface: two <see cref="MediaElement"/>s that alternate so one clip can fade into
    /// the next, plus the small close/settings chrome.
    ///
    /// Built in code rather than XAML on purpose. This project uses an old-style (non-SDK) csproj,
    /// where XAML needs Page items and the WinFX targets wired up by hand; doing it in code keeps
    /// the build plain and avoids that whole class of breakage. There is no designer surface worth
    /// having here anyway - it is a black panel with two video elements on it.
    ///
    /// All the "which clip plays next" decisions live in <see cref="PlaybackController"/>, which
    /// has no WPF types and is unit-tested. This class only drives the players.
    /// </summary>
    internal sealed class VideoSurface : Grid
    {
        private static readonly TimeSpan PreloadLead = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

        private readonly MediaElement playerA;
        private readonly MediaElement playerB;
        private readonly StackPanel chrome;
        private readonly DispatcherTimer ticker;
        private readonly TimeSpan crossfadeDuration;
        private readonly bool crossfadeEnabled;

        private PlaybackController controller;
        private MediaElement current;
        private MediaElement idle;

        private string currentUrl;
        private string pendingUrl;      // loaded into `idle`, waiting to fade in
        private bool pendingReady;      // `idle` has opened and is paused at position 0
        private bool fading;
        private bool stopped;

        private Duration currentDuration;
        private TimeSpan lastPosition;
        private DateTime lastProgressUtc;

        /// <summary>Raised when the playlist is unusable and there is nothing left to try.</summary>
        internal event EventHandler<string> Fatal;

        internal event EventHandler CloseRequested;
        internal event EventHandler SettingsRequested;

        internal VideoSurface(bool enableCrossfade, TimeSpan crossfadeDuration)
        {
            this.crossfadeDuration = crossfadeDuration;
            // Software rendering (RDP sessions, GPU-less VMs) makes a two-decoder crossfade a bad
            // trade; fall back to hard cuts there.
            this.crossfadeEnabled = enableCrossfade && (RenderCapability.Tier >> 16) > 0;

            Background = Brushes.Black;
            ClipToBounds = true;

            playerA = CreatePlayer();
            playerB = CreatePlayer();
            Children.Add(playerA);
            Children.Add(playerB);

            current = playerA;
            idle = playerB;

            chrome = BuildChrome();
            Children.Add(chrome);

            ticker = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = SampleInterval,
            };
            ticker.Tick += OnTick;
        }

        private MediaElement CreatePlayer()
        {
            var player = new MediaElement
            {
                // Manual is required for Play/Pause/Stop/Position to do anything at all.
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.UniformToFill,
                StretchDirection = StretchDirection.Both,
                ScrubbingEnabled = false,
                IsMuted = true,
                Volume = 0,
                Opacity = 0,
                // Let mouse events through to the host window, which uses them to exit.
                IsHitTestVisible = false,
            };

            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;
            player.BufferingStarted += OnBufferingStarted;
            player.BufferingEnded += OnBufferingEnded;
            return player;
        }

        private StackPanel BuildChrome()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 8, 0),
                Visibility = Visibility.Hidden,
            };
            Panel.SetZIndex(panel, 10);

            var settings = CreateChromeButton("⚙");
            settings.Click += (s, e) => Raise(SettingsRequested);
            panel.Children.Add(settings);

            var close = CreateChromeButton("✖");
            close.Click += (s, e) => Raise(CloseRequested);
            panel.Children.Add(close);

            return panel;
        }

        /// <summary>Flat black/white button, matching the old WinForms overlay buttons.</summary>
        private static Button CreateChromeButton(string glyph)
        {
            var button = new Button
            {
                Content = glyph,
                Width = 22,
                Height = 24,
                Margin = new Thickness(2, 0, 2, 0),
                Background = Brushes.Black,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Tahoma"),
                FontSize = 13.6,   // 10.2pt
                Focusable = false,
            };

            // Strip the default chrome so it stays a flat black square like the original.
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            button.Template = template;

            return button;
        }

        internal void ShowChrome(bool visible)
        {
            chrome.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>Plays a single fixed url with no playlist. Used by the settings preview.</summary>
        internal void PlaySingle(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return;

            pendingUrl = null;
            pendingReady = false;
            fading = false;
            currentUrl = url;
            current.Tag = url;
            current.Opacity = 1;
            current.Source = uri;
            current.Play();
        }

        /// <summary>Starts playing the controller's playlist.</summary>
        internal void Start(PlaybackController playbackController)
        {
            controller = playbackController;
            stopped = false;
            if (!PlayNext()) return;
            ticker.Start();
        }

        internal void Stop()
        {
            stopped = true;
            ticker.Stop();
            StopPlayer(playerA);
            StopPlayer(playerB);
        }

        /// <summary>Skips to the next clip immediately, abandoning any pending crossfade.</summary>
        internal void SkipToNext()
        {
            if (stopped) return;
            CancelPending();
            PlayNext();
        }

        // --- playback ------------------------------------------------------------------------

        /// <summary>Hard-cuts to the next playable clip on the current element.</summary>
        private bool PlayNext()
        {
            if (controller == null || !controller.MoveNext())
            {
                ReportFatal();
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(controller.CurrentUrl, UriKind.Absolute, out uri))
            {
                // Unusable url - tell the controller so it stops offering it, then try again.
                controller.NotifyFailed(controller.CurrentUrl, PlaybackFailure.LoadFailed);
                return controller.ShouldGiveUp ? ReportFatalAndFail() : PlayNext();
            }

            currentUrl = controller.CurrentUrl;
            currentDuration = Duration.Automatic;
            lastPosition = TimeSpan.Zero;
            lastProgressUtc = DateTime.UtcNow;

            current.Tag = currentUrl;
            current.Opacity = 1;
            Panel.SetZIndex(current, 1);
            Panel.SetZIndex(idle, 0);
            current.Source = uri;
            current.Play();
            return true;
        }

        /// <summary>Loads the next clip into the idle element so it can be faded in later.</summary>
        private void Preload()
        {
            if (controller == null || !controller.MoveNext()) return;

            Uri uri;
            if (!Uri.TryCreate(controller.CurrentUrl, UriKind.Absolute, out uri))
            {
                controller.NotifyFailed(controller.CurrentUrl, PlaybackFailure.LoadFailed);
                return;
            }

            pendingUrl = controller.CurrentUrl;
            pendingReady = false;
            idle.Tag = pendingUrl;
            idle.Opacity = 0;
            idle.Source = uri;
            // Source alone doesn't open the media - it has to be told to play, then parked.
            idle.Play();
        }

        private void BeginCrossfade()
        {
            var incoming = idle;
            var outgoing = current;

            fading = true;

            // Animate ONLY the incoming element, over the outgoing one left fully opaque. Fading
            // both at once composites each against the black background and dips to black at the
            // halfway point, which looks like a flicker rather than a crossfade.
            Panel.SetZIndex(incoming, 1);
            Panel.SetZIndex(outgoing, 0);
            incoming.Opacity = 0;
            incoming.Play();

            var fade = new DoubleAnimation(0, 1, new Duration(crossfadeDuration))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            fade.Completed += (s, e) => CompleteCrossfade(incoming, outgoing);
            incoming.BeginAnimation(OpacityProperty, fade);
        }

        private void CompleteCrossfade(MediaElement incoming, MediaElement outgoing)
        {
            // Releasing the animation clock matters: while it holds the property, later direct
            // assignments to Opacity are silently ignored.
            incoming.BeginAnimation(OpacityProperty, null);
            incoming.Opacity = 1;

            StopPlayer(outgoing);

            current = incoming;
            idle = outgoing;

            currentUrl = pendingUrl;
            currentDuration = incoming.NaturalDuration;
            lastPosition = TimeSpan.Zero;
            lastProgressUtc = DateTime.UtcNow;

            pendingUrl = null;
            pendingReady = false;
            fading = false;

            if (controller != null) controller.NotifyStarted(currentUrl);
        }

        private void CancelPending()
        {
            if (pendingUrl == null && !fading) return;

            idle.BeginAnimation(OpacityProperty, null);
            StopPlayer(idle);
            pendingUrl = null;
            pendingReady = false;
            fading = false;
        }

        private static void StopPlayer(MediaElement player)
        {
            try
            {
                player.BeginAnimation(OpacityProperty, null);
                player.Opacity = 0;
                player.Stop();
                player.Close();
                player.Source = null;
                player.Tag = null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Failed to stop a media element: " + ex.Message);
            }
        }

        // --- timer ---------------------------------------------------------------------------

        private void OnTick(object sender, EventArgs e)
        {
            if (stopped || controller == null) return;

            var position = SafePosition(current);

            // Real forward progress resets the stall clock. Buffering deliberately does not.
            if (position - lastPosition > TimeSpan.FromMilliseconds(250))
            {
                lastPosition = position;
                lastProgressUtc = DateTime.UtcNow;
            }

            if (!fading && DateTime.UtcNow - lastProgressUtc > StallTimeout)
            {
                Trace.WriteLine("Stalled on " + currentUrl);
                Fail(currentUrl, PlaybackFailure.Stalled);
                return;
            }

            if (fading || !crossfadeEnabled) return;
            if (!currentDuration.HasTimeSpan) return;   // unknown length: hard-cut on MediaEnded

            var remaining = currentDuration.TimeSpan - position;
            if (remaining <= TimeSpan.Zero) return;

            if (pendingUrl == null && remaining <= PreloadLead)
            {
                Preload();
            }
            else if (pendingReady && remaining <= crossfadeDuration)
            {
                BeginCrossfade();
            }
        }

        private static TimeSpan SafePosition(MediaElement player)
        {
            try { return player.Position; }
            catch (Exception) { return TimeSpan.Zero; }
        }

        // --- media events --------------------------------------------------------------------

        private void OnMediaOpened(object sender, RoutedEventArgs e)
        {
            var player = (MediaElement)sender;
            var url = player.Tag as string;
            if (url == null) return;

            if (url == currentUrl)
            {
                currentDuration = player.NaturalDuration;
                lastPosition = TimeSpan.Zero;
                lastProgressUtc = DateTime.UtcNow;
                if (controller != null) controller.NotifyStarted(url);
                NativeMethods.EnableMonitorSleep();
            }
            else if (url == pendingUrl)
            {
                // Park the preloaded clip at its first frame until it's time to fade in.
                player.Pause();
                player.Position = TimeSpan.Zero;
                pendingReady = true;
            }
        }

        private void OnMediaEnded(object sender, RoutedEventArgs e)
        {
            var url = ((MediaElement)sender).Tag as string;

            // The outgoing element reaching its end during/after a crossfade is expected and must
            // not advance anything, or clips get cut short.
            if (url == null || url != currentUrl || fading) return;

            CancelPending();
            PlayNext();
        }

        private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var player = (MediaElement)sender;
            var url = player.Tag as string;
            Trace.WriteLine("MediaFailed on " + url + ": " +
                            (e.ErrorException == null ? "(no detail)" : e.ErrorException.Message));
            if (url == null) return;

            if (url == pendingUrl)
            {
                // A preload failed - the clip on screen is unaffected, so just drop the preload.
                if (controller != null) controller.NotifyFailed(url, PlaybackFailure.LoadFailed);
                StopPlayer(player);
                pendingUrl = null;
                pendingReady = false;
                return;
            }

            if (url != currentUrl) return;   // stale
            Fail(url, PlaybackFailure.LoadFailed);
        }

        private void OnBufferingStarted(object sender, RoutedEventArgs e)
        {
            // Intentionally does not touch lastProgressUtc: buffering forever is exactly the
            // condition the stall timeout exists to catch.
        }

        private void OnBufferingEnded(object sender, RoutedEventArgs e)
        {
            var url = ((MediaElement)sender).Tag as string;
            if (url != null && url == currentUrl) lastProgressUtc = DateTime.UtcNow;
        }

        private void Fail(string url, PlaybackFailure kind)
        {
            if (controller != null) controller.NotifyFailed(url, kind);
            CancelPending();

            if (controller != null && controller.ShouldGiveUp)
            {
                ReportFatal();
                return;
            }
            PlayNext();
        }

        private bool ReportFatalAndFail()
        {
            ReportFatal();
            return false;
        }

        private void ReportFatal()
        {
            Stop();
            var handler = Fatal;
            if (handler != null)
            {
                handler(this, "Aerial could not play any videos. Check your network connection, the "
                            + "video source, and the video quality setting, then restart the screensaver.");
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
