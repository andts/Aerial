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
        /// Give up after this many consecutive failures (or after every video has failed once,
        /// whichever comes first) rather than spinning through a dead playlist forever.
        /// </summary>
        public const int GiveUpAfter = 10;

        private readonly IList<Asset> movies;
        private readonly RegSettings.VideoQualityEnum quality;
        private readonly bool cacheEnabled;
        private readonly bool shouldCache;

        // Sources known to be bad, so we stop handing them back.
        private readonly HashSet<string> blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What we served -> the remote url it came from, so a failure report can be traced back to
        // its asset even after we have moved on.
        private readonly Dictionary<string, string> servedToRemote =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Served urls that were local cache files.
        private readonly HashSet<string> servedFromCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int index = -1;
        private string pendingRemoteRetry;
        private int consecutiveFailures;

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

        /// <summary>
        /// True once failures have piled up enough that the playlist should be considered dead.
        /// The view uses this to show a single message instead of cycling forever.
        /// </summary>
        public bool ShouldGiveUp
        {
            get
            {
                if (movies.Count == 0) return true;
                return consecutiveFailures >= Math.Min(movies.Count, GiveUpAfter);
            }
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
                if (!blocked.Contains(retryUrl))
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
                if (blocked.Contains(remoteUrl)) continue;

                // An existing cached copy is always preferred, whether or not caching is currently
                // switched on - the setting governs downloading, not playing what we already have.
                var cachedPath = CachedPathFor(remoteUrl);
                if (cachedPath != null && !blocked.Contains(cachedPath))
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
            consecutiveFailures = 0;
        }

        /// <summary>
        /// Call when a clip failed to open or stopped delivering frames. <paramref name="url"/> is
        /// whatever was handed to the player, so this works for a preloaded clip that failed while
        /// a different one was still on screen.
        /// </summary>
        public void NotifyFailed(string url, PlaybackFailure kind)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            consecutiveFailures++;
            Trace.WriteLine("Playback failure (" + kind + ") on " + url);

            if (servedFromCache.Contains(url))
            {
                // A truncated or corrupt cache file shouldn't poison this video permanently:
                // drop the file and re-serve the same video from the network.
                string remote;
                servedToRemote.TryGetValue(url, out remote);

                if (TryDeleteCachedFile(url) && remote != null && !blocked.Contains(remote))
                {
                    pendingRemoteRetry = remote;
                }
                else
                {
                    // Couldn't remove it - at least stop handing it back.
                    blocked.Add(url);
                }

                servedFromCache.Remove(url);
            }
            else
            {
                blocked.Add(url);
            }
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
