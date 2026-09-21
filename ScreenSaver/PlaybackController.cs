using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Aerial
{
    /// <summary>
    /// Why a clip stopped playing, as reported by the view.
    /// </summary>
    public enum PlaybackFailure
    {
        /// <summary>The player could not open or decode the source at all.</summary>
        LoadFailed,
        /// <summary>The source opened but stopped delivering frames (buffering forever).</summary>
        Stalled,
    }

    /// <summary>
    /// Decides which video plays next and how to recover when one fails.
    ///
    /// Deliberately contains no WPF or WinForms types: the screensaver window is a thin view over
    /// this, so the interesting logic stays testable on any platform. Keep it that way.
    ///
    /// Every notification is keyed by the URL it concerns rather than by "the current video",
    /// because the view preloads the next clip while the previous one is still playing - so events
    /// routinely arrive for a clip that is no longer the current one, and a stale event must not
    /// be mistaken for a failure of what is on screen.
    /// </summary>
    public sealed class PlaybackController
    {
        /// <summary>
        /// How many consecutive failures count as "something is badly wrong" rather than a few
        /// rotten videos. On its own this is NOT enough to report a problem - see
        /// <see cref="IsFailingPersistently"/>, which also requires the failures to have lasted a
        /// while. A dropped network connection produces failures far faster than real playback,
        /// so a count alone was reached within ~20 seconds of an outage.
        /// </summary>
        public const int GiveUpAfter = 10;

        /// <summary>
        /// How long failures must keep happening, with nothing playing in between, before the view
        /// tells the user. Short outages (wifi blips, waking from sleep, a router reboot) resolve
        /// themselves well inside this.
        /// </summary>
        public static readonly TimeSpan PersistentFailureWindow = TimeSpan.FromMinutes(3);

        private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

        private readonly IList<Asset> movies;
        private readonly RegSettings.VideoQualityEnum quality;
        private readonly bool cacheEnabled;
        private readonly bool shouldCache;

        /// <summary>
        /// Sources that just failed, and when they may be tried again. Blocking used to be
        /// permanent for the life of the process, so every transient hiccup shrank the playlist
        /// for good and a long session slowly starved.
        /// </summary>
        private readonly Dictionary<string, Block> blocked =
            new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);

        private sealed class Block
        {
            public DateTime UntilUtc;
            public int Failures;
        }

        // What we served -> the remote url it came from, so a failure report can be traced back to
        // its asset even after we have moved on.
        private readonly Dictionary<string, string> servedToRemote =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Served urls that were local cache files.
        private readonly HashSet<string> servedFromCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int index = -1;
        private string pendingRemoteRetry;
        private int consecutiveFailures;
        private int totalFailures;
        private DateTime? failingSinceUtc;
        private string lastFailureDetail;
        private string lastFailureUrl;

        /// <param name="movies">The playlist, already filtered and ordered by AerialContext.</param>
        /// <param name="quality">Which encoding to resolve from each asset.</param>
        /// <param name="cacheEnabled">The user's "cache videos" setting.</param>
        /// <param name="shouldCache">
        /// Whether THIS window is the one responsible for populating the cache. Only one window
        /// should be (the primary screen), and never the preview pane - pass
        /// <c>shouldCache &amp;&amp; !previewMode</c>.
        /// </param>
        public PlaybackController(IList<Asset> movies, RegSettings.VideoQualityEnum quality,
                                  bool cacheEnabled, bool shouldCache)
        {
            this.movies = movies ?? new List<Asset>();
            this.quality = quality;
            this.cacheEnabled = cacheEnabled;
            this.shouldCache = shouldCache;
        }

        /// <summary>
        /// How long a failed source is skipped before being offered again. Exposed so tests can
        /// shorten it; the default is long enough to get past an outage, short enough that a
        /// machine left idling overnight recovers its full playlist.
        /// </summary>
        public TimeSpan BlockedRetryAfter = TimeSpan.FromMinutes(10);

        /// <summary>The asset selected by the last successful <see cref="MoveNext"/>.</summary>
        public Asset Current { get; private set; }

        /// <summary>
        /// What the player should open for <see cref="Current"/>: a local cache path when we have
        /// one, otherwise the remote URL.
        /// </summary>
        public string CurrentUrl { get; private set; }

        /// <summary>True when <see cref="CurrentUrl"/> is a local cached file rather than a URL.</summary>
        public bool CurrentIsFromCache { get; private set; }

        public bool HasPlayableItems { get { return movies.Count > 0; } }

        public int ConsecutiveFailures { get { return consecutiveFailures; } }

        /// <summary>Every failure this controller has seen, streaks included.</summary>
        public int TotalFailures { get { return totalFailures; } }

        /// <summary>How long failures have been happening with nothing playing in between.</summary>
        public TimeSpan FailingFor
        {
            get { return failingSinceUtc.HasValue ? DateTime.UtcNow - failingSinceUtc.Value : TimeSpan.Zero; }
        }

        /// <summary>Why the last failure happened, as reported by the player.</summary>
        public string LastFailureDetail { get { return lastFailureDetail; } }

        /// <summary>The source that failed last.</summary>
        public string LastFailureUrl { get { return lastFailureUrl; } }

        /// <summary>Sources currently being skipped because they recently failed.</summary>
        public int BlockedCount
        {
            get
            {
                var now = DateTime.UtcNow;
                var count = 0;
                foreach (var entry in blocked.Values)
                    if (now < entry.UntilUtc) count++;
                return count;
            }
        }

        /// <summary>
        /// True when failures have both piled up AND kept happening for
        /// <see cref="PersistentFailureWindow"/>. Both halves matter: an unreachable host fails
        /// almost instantly, so a count on its own is reached during outages far too short to
        /// bother the user about.
        ///
        /// This is not a reason to stop playing. The view keeps retrying either way; it only uses
        /// this to decide whether to say something.
        /// </summary>
        public bool IsFailingPersistently
        {
            get
            {
                if (movies.Count == 0) return true;
                return consecutiveFailures >= Math.Min(movies.Count, GiveUpAfter)
                       && FailingFor >= PersistentFailureWindow;
            }
        }

        /// <summary>
        /// How long the view should wait before the next attempt, backing off as failures repeat.
        /// Without this the player retried instantly, so a brief outage burned through every
        /// attempt in seconds.
        /// </summary>
        public TimeSpan SuggestedRetryDelay
        {
            get
            {
                if (consecutiveFailures <= 0) return TimeSpan.Zero;

                var seconds = MinRetryDelay.TotalSeconds * Math.Pow(2, Math.Min(consecutiveFailures - 1, 5));
                return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
            }
        }

        /// <summary>One line for the log and for the message shown to the user.</summary>
        public string DescribeFailures()
        {
            var text = totalFailures + " failure(s), " + consecutiveFailures + " in a row";
            if (failingSinceUtc.HasValue)
                text += ", failing for " + ((int)FailingFor.TotalSeconds) + "s";
            text += ", " + BlockedCount + " of " + movies.Count + " videos currently skipped";
            if (!string.IsNullOrWhiteSpace(lastFailureUrl))
                text += ". Last: " + lastFailureUrl;
            if (!string.IsNullOrWhiteSpace(lastFailureDetail))
                text += " - " + lastFailureDetail;
            return text;
        }

        /// <summary>
        /// Selects the next playable video and resolves <see cref="CurrentUrl"/>.
        /// Returns false when nothing in the playlist can be played.
        /// </summary>
        public bool MoveNext()
        {
            if (movies.Count == 0)
            {
                Current = null;
                CurrentUrl = null;
                return false;
            }

            // A cached copy just failed - re-serve that same video from the network before moving on.
            if (pendingRemoteRetry != null)
            {
                var retryUrl = pendingRemoteRetry;
                pendingRemoteRetry = null;
                if (!IsBlocked(retryUrl))
                {
                    var asset = FindByRemoteUrl(retryUrl);
                    if (asset != null)
                    {
                        Serve(asset, retryUrl, retryUrl, fromCache: false);
                        return true;
                    }
                }
            }

            // Bounded: one full pass at most, so an entirely failed playlist can't spin.
            for (var attempt = 0; attempt < movies.Count; attempt++)
            {
                index = (index + 1) % movies.Count;
                var asset = movies[index];

                var remoteUrl = ResolveRemote(asset);
                if (string.IsNullOrWhiteSpace(remoteUrl)) continue;
                if (IsBlocked(remoteUrl)) continue;

                // An existing cached copy is always preferred, whether or not caching is currently
                // switched on - the setting governs downloading, not playing what we already have.
                var cachedPath = CachedPathFor(remoteUrl);
                if (cachedPath != null && !IsBlocked(cachedPath))
                {
                    Serve(asset, cachedPath, remoteUrl, fromCache: true);
                    return true;
                }

                Serve(asset, remoteUrl, remoteUrl, fromCache: false);
                QueueForCaching(remoteUrl);
                return true;
            }

            Current = null;
            CurrentUrl = null;
            return false;
        }

        /// <summary>
        /// Call when a clip actually starts playing. Clears the failure streak. Ignored for a url
        /// that is no longer relevant.
        /// </summary>
        public void NotifyStarted(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            if (consecutiveFailures > 0)
                Log.Write("Playback recovered after " + consecutiveFailures + " failure(s): " + url);

            consecutiveFailures = 0;
            failingSinceUtc = null;

            // Something is genuinely playing, so whatever failed earlier deserves another chance
            // sooner rather than later - the failures were probably about the network, not the
            // videos.
            if (blocked.Count > 0) blocked.Clear();
        }

        /// <summary>
        /// Call when a clip failed to open or stopped delivering frames. <paramref name="url"/> is
        /// whatever was handed to the player, so this works for a preloaded clip that failed while
        /// a different one was still on screen.
        /// </summary>
        public void NotifyFailed(string url, PlaybackFailure kind)
        {
            NotifyFailed(url, kind, null);
        }

        /// <param name="detail">
        /// What the player said went wrong, for the log and the message shown to the user.
        /// </param>
        public void NotifyFailed(string url, PlaybackFailure kind, string detail)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            consecutiveFailures++;
            totalFailures++;
            if (!failingSinceUtc.HasValue) failingSinceUtc = DateTime.UtcNow;
            lastFailureUrl = url;
            lastFailureDetail = string.IsNullOrWhiteSpace(detail) ? kind.ToString() : kind + ": " + detail;

            Log.Write("Playback failure (" + kind + ") on " + url
                      + (string.IsNullOrWhiteSpace(detail) ? "" : " - " + detail)
                      + " [" + consecutiveFailures + " in a row]");

            if (servedFromCache.Contains(url))
            {
                // A truncated or corrupt cache file shouldn't poison this video permanently:
                // drop the file and re-serve the same video from the network.
                string remote;
                servedToRemote.TryGetValue(url, out remote);

                if (TryDeleteCachedFile(url) && remote != null && !IsBlocked(remote))
                {
                    pendingRemoteRetry = remote;
                }
                else
                {
                    // Couldn't remove it - at least stop handing it back for now.
                    BlockTemporarily(url);
                }

                servedFromCache.Remove(url);
            }
            else
            {
                BlockTemporarily(url);
            }
        }

        /// <summary>
        /// Drops every cooldown, so the next <see cref="MoveNext"/> considers the whole playlist
        /// again. The view calls this when everything is skipped at once, which means the problem
        /// is the network rather than the videos.
        /// </summary>
        public void ForgetBlocks()
        {
            if (blocked.Count == 0) return;
            Log.Write("clearing " + blocked.Count + " skipped source(s) to retry them");
            blocked.Clear();
        }

        /// <summary>
        /// Skips a source for a while, longer each time it fails again, so a genuinely dead video
        /// stops being retried often while a source that failed during an outage comes back.
        /// </summary>
        private void BlockTemporarily(string url)
        {
            Block entry;
            if (!blocked.TryGetValue(url, out entry))
            {
                entry = new Block();
                blocked[url] = entry;
            }

            entry.Failures++;
            var multiplier = Math.Pow(2, Math.Min(entry.Failures - 1, 3));   // 1x, 2x, 4x, 8x
            entry.UntilUtc = DateTime.UtcNow + TimeSpan.FromTicks((long)(BlockedRetryAfter.Ticks * multiplier));
        }

        private bool IsBlocked(string url)
        {
            Block entry;
            if (!blocked.TryGetValue(url, out entry)) return false;
            if (DateTime.UtcNow < entry.UntilUtc) return true;

            // The cooldown expired: let it be tried again, but remember it has failed before so
            // the next block lasts longer.
            entry.UntilUtc = DateTime.MinValue;
            return false;
        }

        private void Serve(Asset asset, string url, string remoteUrl, bool fromCache)
        {
            Current = asset;
            CurrentUrl = url;
            CurrentIsFromCache = fromCache;

            servedToRemote[url] = remoteUrl;
            if (fromCache) servedFromCache.Add(url);
            else servedFromCache.Remove(url);
        }

        private Asset FindByRemoteUrl(string remoteUrl)
        {
            foreach (var asset in movies)
                if (string.Equals(ResolveRemote(asset), remoteUrl, StringComparison.OrdinalIgnoreCase))
                    return asset;
            return null;
        }

        private string ResolveRemote(Asset asset)
        {
            return asset == null ? null : asset.ResolveUrl(quality);
        }

        /// <summary>Returns the cached file path for a url, or null if it isn't cached.</summary>
        private static string CachedPathFor(string remoteUrl)
        {
            try
            {
                return Caching.IsHit(remoteUrl) ? Caching.Get(remoteUrl) : null;
            }
            catch (Exception ex)
            {
                // A malformed url or an unreachable cache folder must not stop playback.
                Trace.WriteLine("Cache lookup failed for " + remoteUrl + ": " + ex.Message);
                return null;
            }
        }

        private void QueueForCaching(string remoteUrl)
        {
            if (!cacheEnabled || !shouldCache) return;

            try
            {
                if (!Caching.IsCaching(remoteUrl))
                    Caching.StartDelayedCache(remoteUrl);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Could not queue " + remoteUrl + " for caching: " + ex.Message);
            }
        }

        private static bool TryDeleteCachedFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                Trace.WriteLine("Removed bad cached file " + path);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Could not remove bad cached file " + path + ": " + ex.Message);
                return false;
            }
        }
    }
}
