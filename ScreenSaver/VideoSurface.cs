using System;
using System.Diagnostics;
using System.Text;
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
        /// <summary>A gap between ticks this large means the machine slept, not that video stalled.</summary>
        private static readonly TimeSpan ClockJumpThreshold = TimeSpan.FromSeconds(10);

        private readonly MediaElement playerA;
        private readonly MediaElement playerB;
        private readonly StackPanel chrome;
        private readonly Border status;
        private TextBlock statusText;
        private readonly DispatcherTimer ticker;
        /// <summary>Fires once to start the next attempt after a failure, never immediately.</summary>
        private readonly DispatcherTimer retryTimer;
        private readonly string scope;
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
        private DateTime lastTickUtc;
        private bool fatalReported;
        /// <summary>True between a failure and its scheduled retry: nothing is loaded meanwhile.</summary>
        private bool awaitingRetry;
        private int nothingPlayableStreak;

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

            status = BuildStatus();
            Children.Add(status);

            ticker = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = SampleInterval,
            };
            ticker.Tick += OnTick;

            retryTimer = new DispatcherTimer(DispatcherPriority.Background);
            retryTimer.Tick += OnRetryTick;

            scope = "surface " + GetHashCode().ToString("X");
            Log.Write(scope, "created, crossfade " + (crossfadeEnabled ? "on" : "off")
                      + ", render tier " + (RenderCapability.Tier >> 16));
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

        /// <summary>
        /// The "something is wrong" panel. Deliberately part of the video surface rather than a
        /// MessageBox: a modal dialog blocks the dispatcher, which stops the retry timer, so the
        /// screensaver could never recover while its own error message was on screen.
        /// </summary>
        private Border BuildStatus()
        {
            statusText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
            };

            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(18, 14, 18, 14),
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Child = statusText,
            };
            Panel.SetZIndex(panel, 5);
            return panel;
        }

        private void ShowProblem(string message)
        {
            statusText.Text = message;
            status.Visibility = Visibility.Visible;
        }

        private void HideProblem()
        {
            if (status.Visibility != Visibility.Visible) return;
            status.Visibility = Visibility.Collapsed;
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
            if (!TryCreateMediaUri(url, out uri)) return;

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
            lastTickUtc = DateTime.UtcNow;
            // The ticker runs even if the first attempt fails: a failure schedules a retry rather
            // than ending playback, so there is always something to come back to.
            ticker.Start();
            PlayNext();
        }

        internal void Stop()
        {
            stopped = true;
            ticker.Stop();
            retryTimer.Stop();
            StopPlayer(playerA);
            StopPlayer(playerB);
            Log.Write(scope, "stopped");
        }

        /// <summary>Skips to the next clip immediately, abandoning any pending crossfade.</summary>
        internal void SkipToNext()
        {
            if (stopped) return;
            retryTimer.Stop();
            CancelPending();
            PlayNext();
        }

        /// <summary>
        /// Queues another attempt. Playback is never abandoned: an outage that outlasts the
        /// playlist simply means retrying at the backed-off interval until the network returns.
        /// </summary>
        private void ScheduleRetry()
        {
            // Exactly one retry may be pending. Re-arming the timer on every incoming failure
            // starved it completely: repeated failures kept pushing the next attempt further out,
            // so playback never came back even once the network did.
            if (stopped || awaitingRetry) return;

            var delay = controller == null ? TimeSpan.FromSeconds(5) : controller.SuggestedRetryDelay;
            if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(2);

            // When the whole playlist is temporarily skipped there is no per-clip failure to back
            // off from, so back off on the streak of empty attempts instead.
            if (nothingPlayableStreak > 0)
            {
                var seconds = Math.Min(5 * Math.Pow(2, Math.Min(nothingPlayableStreak - 1, 3)), 30);
                if (seconds > delay.TotalSeconds) delay = TimeSpan.FromSeconds(seconds);
            }

            awaitingRetry = true;
            retryTimer.Stop();
            retryTimer.Interval = delay;
            retryTimer.Start();
            Log.Write(scope, "retrying in " + delay.TotalSeconds.ToString("0.#") + "s");
        }

        private void OnRetryTick(object sender, EventArgs e)
        {
            retryTimer.Stop();
            awaitingRetry = false;
            if (stopped) return;
            PlayNext();
        }

        // --- playback ------------------------------------------------------------------------

        /// <summary>Hard-cuts to the next playable clip on the current element.</summary>
        private bool PlayNext()
        {
            if (controller == null || !controller.MoveNext())
            {
                // Everything is being skipped right now. Their cooldowns expire, so wait and ask
                // again instead of treating it as the end of the world.
                nothingPlayableStreak++;
                Log.Write(scope, "nothing playable right now; "
                          + (controller == null ? "no controller" : controller.DescribeFailures()));

                // Everything is in cooldown. Those cooldowns exist to skip rotten videos, not to
                // sit out an outage, so drop them and let the next attempt find out whether the
                // network is back.
                if (controller != null) controller.ForgetBlocks();

                MaybeReportFatal();
                ScheduleRetry();
                return false;
            }

            Uri uri;
            if (!TryCreateMediaUri(controller.CurrentUrl, out uri))
            {
                // Unusable url - tell the controller so it stops offering it, then try again.
                controller.NotifyFailed(controller.CurrentUrl, PlaybackFailure.LoadFailed, "malformed url");
                MaybeReportFatal();
                ScheduleRetry();
                return false;
            }

            nothingPlayableStreak = 0;
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
            Log.Write(scope, "playing " + currentUrl
                      + (controller.CurrentIsFromCache ? " (from cache)" : " (streaming)"));
            return true;
        }

        /// <summary>Loads the next clip into the idle element so it can be faded in later.</summary>
        private void Preload()
        {
            if (controller == null || !controller.MoveNext()) return;

            Uri uri;
            if (!TryCreateMediaUri(controller.CurrentUrl, out uri))
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

        /// <summary>
        /// Builds the Uri handed to a MediaElement. https sources are downgraded to http because
        /// .NET Framework WPF cannot stream https at all: MediaPlayerState.OpenMedia dereferences
        /// the ClickOnce site of origin for https URIs and throws NullReferenceException outside a
        /// ClickOnce deployment, and even with that bypassed the native WMP-based pipeline hangs
        /// on https. The caller keeps the original url for bookkeeping; cache downloads go through
        /// WebClient and stay on https.
        /// </summary>
        private static bool TryCreateMediaUri(string url, out Uri uri)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp };
                if (uri.IsDefaultPort) builder.Port = -1;
                uri = builder.Uri;
            }
            return true;
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
            // Nothing is loaded while a retry is pending; there is no stall to detect.
            if (awaitingRetry || currentUrl == null)
            {
                lastTickUtc = DateTime.UtcNow;
                return;
            }

            var now = DateTime.UtcNow;
            var sinceLastTick = now - lastTickUtc;
            lastTickUtc = now;

            // The machine slept, or the clock jumped. Wall-clock time passed but no video time
            // could have, so the stall check below would fire a false failure the moment the
            // screen comes back - and a few of those in a row used to end playback for good.
            if (sinceLastTick > ClockJumpThreshold)
            {
                Log.Write(scope, "clock jumped " + ((int)sinceLastTick.TotalSeconds)
                          + "s (sleep/resume); stall timer rebased");
                lastProgressUtc = now;
                lastPosition = SafePosition(current);
                return;
            }

            var position = SafePosition(current);
            // Real forward progress resets the stall clock. Buffering deliberately does not.
            if (position - lastPosition > TimeSpan.FromMilliseconds(250))
            {
                lastPosition = position;
                lastProgressUtc = DateTime.UtcNow;
            }

            if (!fading && now - lastProgressUtc > StallTimeout)
            {
                Fail(currentUrl, PlaybackFailure.Stalled,
                     "no frames for " + ((int)(now - lastProgressUtc).TotalSeconds) + "s");
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
            var url = player.Tag as string;            if (url == null) return;

            if (url == currentUrl)
            {
                currentDuration = player.NaturalDuration;
                lastPosition = TimeSpan.Zero;
                lastProgressUtc = DateTime.UtcNow;
                // Something is playing again: take the problem panel down and let a later outage
                // report itself afresh.
                HideProblem();
                fatalReported = false;
                Log.Write(scope, "opened " + player.NaturalVideoWidth + "x" + player.NaturalVideoHeight
                          + ", " + (currentDuration.HasTimeSpan
                                    ? ((int)currentDuration.TimeSpan.TotalSeconds) + "s" : "unknown length"));
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

            Log.Write(scope, "ended " + url);
            CancelPending();
            PlayNext();
        }

        private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var player = (MediaElement)sender;
            var url = player.Tag as string;
            var detail = Log.Describe(e.ErrorException);
            if (url == null) return;

            if (url == pendingUrl)
            {
                // A preload failed - the clip on screen is unaffected, so just drop the preload.
                Log.Write(scope, "preload failed: " + url + " - " + detail);
                if (controller != null) controller.NotifyFailed(url, PlaybackFailure.LoadFailed, detail);
                StopPlayer(player);
                pendingUrl = null;
                pendingReady = false;
                return;
            }

            if (url != currentUrl) return;   // stale
            Fail(url, PlaybackFailure.LoadFailed, detail);
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
            Fail(url, kind, null);
        }

        private void Fail(string url, PlaybackFailure kind, string detail)
        {
            // One failure per attempt. Without this the stall check fired on every tick of a dead
            // source, four times a second.
            if (awaitingRetry) return;

            if (controller != null) controller.NotifyFailed(url, kind, detail);
            CancelPending();

            // Let go of the source that just failed, so nothing else reports against it and the
            // decoder is released while we wait.
            StopPlayer(current);
            currentUrl = null;
            currentDuration = Duration.Automatic;
            lastProgressUtc = DateTime.UtcNow;

            MaybeReportFatal();

            // Always backs off rather than retrying at once. Instant retries burned through the
            // whole failure budget within seconds of a network blip, which is what made a brief
            // outage look like a permanent one.
            ScheduleRetry();
        }

        /// <summary>
        /// Tells the user once, and only once failures have persisted long enough to be worth
        /// mentioning. Playback keeps retrying either way, so it recovers on its own if the
        /// network comes back while the message is on screen.
        /// </summary>
        private void MaybeReportFatal()
        {
            if (fatalReported || controller == null || !controller.IsFailingPersistently) return;
            fatalReported = true;

            var summary = controller.DescribeFailures();
            Log.Write(scope, "reporting persistent failure: " + summary);

            var message = new StringBuilder();
            if (!controller.HasPlayableItems)
            {
                // Nothing even to attempt: an empty or unreadable catalog, not a playback problem.
                message.AppendLine("Aerial has no videos to play.");
                message.AppendLine();
                message.AppendLine("The video list came back empty. Check the video source in "
                                   + "Settings - leaving it blank uses the catalog built into Aerial.");
            }
            else
            {
                message.AppendLine("Aerial hasn't managed to play a video for "
                                   + ((int)controller.FailingFor.TotalMinutes) + " minute(s).");
                message.AppendLine();
                message.AppendLine("Last error: " + (controller.LastFailureDetail ?? "(none)"));
                message.AppendLine("Last video: " + (controller.LastFailureUrl ?? "(none)"));
                message.AppendLine(controller.TotalFailures + " failure(s) so far, "
                                   + controller.ConsecutiveFailures + " in a row.");
                message.AppendLine();
                message.AppendLine("It keeps retrying, so it will pick up again on its own if this is a "
                                   + "network problem. If not, check the video source and video quality "
                                   + "in Settings.");
            }
            if (Log.FilePath != null)
            {
                message.AppendLine();
                message.Append("Log: " + Log.FilePath);
            }

            ShowProblem(message.ToString());

            var handler = Fatal;
            if (handler != null) handler(this, summary);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
