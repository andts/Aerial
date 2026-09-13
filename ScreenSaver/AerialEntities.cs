using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace Aerial
{
    // Parses either a bundled or remote catalog in the shape Apple's old
    // http://a1.phobos.apple.com/.../entries.json feed used: an array of {id, assets[]}.
    public class AerialContext
    {
        static IdAsset[] cachedEntities;
        static List<Asset> cachedPlaylist;

        public static List<Asset> GetMovies()
        {
            var urls = GetAllEntries();

            return FilterEntries(urls);
        }
        public static List<Asset> GetAllMovies()
        {
            //if no Entries, just return an empty list
            if (GetAllEntries() == null) { return new List<Asset>(); }

            return GetAllEntries().SelectMany(s => s.assets).ToList();
        }

        private static List<Asset> FilterEntries(IdAsset[] urls)
        {
            if (urls == null) { return new List<Asset>(); }; //if no URLS, return an empty list

            var time = (DateTime.Now.Hour < 6 || DateTime.Now.Hour > 19) ? "night" : "day";
            var ran = new Random();
            var settings = new RegSettings();
            List<Asset> links = urls.SelectMany(s => s.assets)
                .Where(t => AssetSelected(t)) //only return videos that have been selected to be played
                .OrderBy(t => ran.Next()) // randomize
                .OrderByDescending(t => settings.UseTimeOfDay && t.timeOfDay == time)
                .ToList();

            //If the links list is empty or null for some reason, just populate with all movies
            if (links == null || links.Count == 0)
            {
                links = urls.SelectMany(s => s.assets).ToList();
            }

            if (settings.MultiMonitorMode == RegSettings.MultiMonitorModeEnum.DifferentVideos)
                return links;

            if (cachedPlaylist == null)
                cachedPlaylist = links;

            return cachedPlaylist;
        }

        /// <summary>
        /// Manifest resource name of the video catalog bundled with the app. Used whenever
        /// JsonURL is unset, or as a fallback if a remote JsonURL can't be reached or parsed, so
        /// a bad/unset setting never leaves the user with no videos at all.
        /// </summary>
        private const string BundledCatalogResourceName = "Aerial.Videos.json";

        public static IdAsset[] GetAllEntries()
        {
            if (cachedEntities != null) return cachedEntities;

            var settings = new RegSettings();
            var aerialUrl = settings.JsonURL;
#if OFFLINE
            aerialUrl = "http://BOGUS/entries.json";
#endif

            string entries = null;
            if (!string.IsNullOrWhiteSpace(aerialUrl))
            {
                try
                {
                    // update anyway
                    Caching.StartDelayedCache(aerialUrl);

                    entries = Caching.IsHit(aerialUrl)
                        ? File.ReadAllText(Caching.Get(aerialUrl))
                        : new WebClient().DownloadString(aerialUrl);
                }
                catch (Exception ex)
                {
                    // Network failure, bad URL, access denied, etc. Fall back to the bundled
                    // catalog below rather than leaving the user with no videos.
                    Trace.WriteLine("Falling back to bundled catalog; remote fetch of " + aerialUrl + " failed: " + ex);
                    entries = null;
                }
            }

            if (string.IsNullOrWhiteSpace(entries))
            {
                entries = LoadBundledEntries();
            }

            try
            {
                cachedEntities = new JavaScriptSerializer().Deserialize<IdAsset[]>(entries);
            }
            catch (Exception ex)
            {
                //the passed in entities document is invalid - most likely a malformed remote
                //JsonURL response. Fall back to the bundled catalog rather than returning null.
                Trace.WriteLine("Catalog parse failed, falling back to bundled catalog: " + ex);
                try
                {
                    cachedEntities = new JavaScriptSerializer().Deserialize<IdAsset[]>(LoadBundledEntries());
                }
                catch (Exception fallbackEx)
                {
                    Trace.WriteLine("Bundled catalog failed to parse: " + fallbackEx);
                    cachedEntities = null;
                }
            }

            return cachedEntities;
        }

        /// <summary>
        /// Reads the video catalog embedded in the assembly as a resource (Videos.json,
        /// see ScreenSaver.csproj). This is the default catalog and the last-resort fallback
        /// if a configured JsonURL can't be fetched or parsed.
        /// </summary>
        private static string LoadBundledEntries()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(BundledCatalogResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Embedded resource " + BundledCatalogResourceName + " is missing");

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        /**
         * Returns true if the asset (movie) is in the chosen movies in the registry key, false if it isn't 
         */
        private static bool AssetSelected(Asset a)
        {
            var settings = new RegSettings();

            //if no movies are selected to be played, just allow all
            if(String.IsNullOrEmpty(settings.ChosenMovies))
            {
                return true;
            }

            var selected = new RegSettings().ChosenMovies.Split(';').ToList();
            List<string> selectedIds = selected.Select(s => GetIdFromTimeAndIdNumbered(s)).ToList(); ;

            return selectedIds.Contains(a.id);

        }

        /*
         * Parses the ID from the TimeAndIdNumbered string. Expecting the ID to be between parenthasis ex: China/day 1 (b4-1)
         * Added the ID to the node for the movie filtering
         */
        public static string GetIdFromTimeAndIdNumbered(string TimeAndId)
        {
            var splitString = TimeAndId.Split('(', ')');

            if (splitString.Length > 1)
            {
                return splitString[1];
            } else
            {
                return "NO ID IN STRING";
            }
        }
    }

    public class IdAsset
    {
        public string id;
        public Asset[] assets;
    }

    public class Asset : IComparable<Asset>
    {
        public string url;//" : "http://a1.phobos.apple.com/us/r1000/000/Features/atv/AutumnResources/videos/b1-1.mov",
        public string accessibilityLabel;//" : "Hawaii",
        public string type;//" : "video",
        public string id;// : "b1-1",
        public string timeOfDay;//" : "day"

        // Present on the bundled catalog (see Videos.json / tools/convert_videos.py); absent on
        // Apple's old entries.json shape, in which case these are null/empty and every method
        // below falls back gracefully.
        public string name;
        public string category; // landscape / cityscape / space / underwater
        public AssetSources src;

        [NonSerialized]
        internal int numeric = 0;

        /// <summary>
        /// Label used for grouping/sorting when timeOfDay is blank, which is true for most of
        /// the bundled catalog (84 of 114 videos as of the 2026 refresh - see the porting plan).
        /// Falls back to category, then to a fixed placeholder so the tree/settings text is
        /// never just a stray leading space.
        /// </summary>
        private string TimeOrCategoryLabel()
        {
            if (!string.IsNullOrWhiteSpace(timeOfDay)) return timeOfDay;
            if (!string.IsNullOrWhiteSpace(category)) return category;
            return "video";
        }

        public override string ToString()
        {
            return accessibilityLabel + (numeric == 0 ? "" : " " + numeric) + " " + TimeOrCategoryLabel();
        }
        public string ShortName()
        {
            return accessibilityLabel + " — " + TimeOrCategoryLabel();
        }
        public string ToFullName()
        {
            return accessibilityLabel + " — " + TimeOrCategoryLabel() + " (" + id + ")";
        }
        public string TimeNumbered()
        {
            return TimeOrCategoryLabel() + (numeric == 0 ? "" : " " + numeric);
        }

        public string TimeAndIdNumbered()
        {
            return TimeOrCategoryLabel() + (numeric == 0 ? "" : " " + numeric) + " (" + id + ")";
        }

        /// <summary>
        /// Resolves the URL to play for the requested quality, falling back through the other
        /// encodings (if the bundled catalog is in use) and finally to the legacy flat url field
        /// (always present, and the only field Apple's old entries.json shape provided).
        /// </summary>
        public string ResolveUrl(RegSettings.VideoQualityEnum quality)
        {
            var fromSrc = src == null ? null : src.For(quality);
            return string.IsNullOrWhiteSpace(fromSrc) ? url : fromSrc;
        }

        public int CompareTo(Asset other)
        {
            if (other == null) return 1;
            if (other == this) return 0;
            return NativeMethods.StrCmpLogicalW(ToFullName(), other.ToFullName());
        }
    }

    /// <summary>
    /// The three encodings the bundled catalog provides per video (see Videos.json). Field names
    /// must match the JSON keys exactly - JavaScriptSerializer maps by member name.
    /// </summary>
    public class AssetSources
    {
        public string H2641080p;
        public string H2651080p;
        public string H2654k;

        public string For(RegSettings.VideoQualityEnum quality)
        {
            switch (quality)
            {
                case RegSettings.VideoQualityEnum.Hevc4k:
                    return FirstNonEmpty(H2654k, H2651080p, H2641080p);
                case RegSettings.VideoQualityEnum.Hevc1080p:
                    return FirstNonEmpty(H2651080p, H2641080p, H2654k);
                default:
                    return FirstNonEmpty(H2641080p, H2651080p, H2654k);
            }
        }

        private static string FirstNonEmpty(params string[] candidates)
        {
            foreach (var c in candidates)
                if (!string.IsNullOrWhiteSpace(c)) return c;
            return null;
        }
    }
}
