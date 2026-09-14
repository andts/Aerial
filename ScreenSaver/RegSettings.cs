using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.IO;

namespace Aerial
{
    public class RegSettings
    {
        readonly string keyAddress = @"SOFTWARE\AerialScreenSaver";
        [Obsolete("Replaced with MultiMonitorMode")]
        private bool DifferentMoviesOnDual = false;
        [Obsolete("Replaced with MultiMonitorMode")]
        private bool MultiscreenDisabled = true;
        public MultiMonitorModeEnum MultiMonitorMode = RegSettings.MultiMonitorModeEnum.MainOnly;
        public VideoQualityEnum VideoQuality = RegSettings.VideoQualityEnum.H264_1080p;
        public bool UseTimeOfDay = true;
        public bool CacheVideos = true;
        public string CacheLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aerial");
        public string ChosenMovies = "";
        // Empty means: use the video catalog bundled with the app (see AerialContext.GetAllEntries).
        // Apple's old feed hosts (appleVideosURI, applefourKVideoURI) are dead / near-empty and are
        // no longer used as a default - see AerialGlobalVars for details.
        public string JsonURL = "";

        /// <summary>
        /// Bumped whenever a stored setting's meaning or format changes in a way that needs a
        /// one-time migration. See <see cref="MigrateIfNeeded"/>.
        /// </summary>
        public const int CurrentSettingsVersion = 1;
        public int SettingsVersion = 0;

#pragma warning disable CS0618 // Type or member is obsolete
        public RegSettings()
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(keyAddress);
            if (key != null)
            {
                DifferentMoviesOnDual = bool.Parse(key.GetValue(nameof(DifferentMoviesOnDual)) as string ?? "True");
                MultiscreenDisabled = bool.Parse(key.GetValue(nameof(MultiscreenDisabled)) as string ?? "True");

                if (!Enum.TryParse(key.GetValue(nameof(MultiMonitorMode)) as string, out MultiMonitorMode))
                {
                    // load value from legacy settings
                    MultiMonitorMode =
                        MultiscreenDisabled ? MultiMonitorModeEnum.MainOnly
                        : DifferentMoviesOnDual ? MultiMonitorModeEnum.DifferentVideos : MultiMonitorModeEnum.SameOnEach;
                }

                if (!Enum.TryParse(key.GetValue(nameof(VideoQuality)) as string, out VideoQuality))
                    VideoQuality = VideoQualityEnum.H264_1080p;

                UseTimeOfDay = bool.Parse(key.GetValue(nameof(UseTimeOfDay)) as string ?? "True");
                CacheVideos = bool.Parse(key.GetValue(nameof(CacheVideos)) as string ?? "True");
                CacheLocation = key.GetValue(nameof(CacheLocation)) as string;
                ChosenMovies = (key.GetValue(nameof(ChosenMovies)) as string ?? "");
                JsonURL = key.GetValue(nameof(JsonURL)) as string;
                int.TryParse(key.GetValue(nameof(SettingsVersion)) as string ?? "0", out SettingsVersion);
            }
        }

        /// <summary>
        /// Save text into the Registry.
        /// </summary>
        public void SaveSettings()
        {
            RegistryKey key = Registry.CurrentUser.CreateSubKey(keyAddress);

            key.SetValue(nameof(MultiMonitorMode), MultiMonitorMode);
            // Enums serialise through ToString() to REG_SZ, which is what the "as string" reads
            // above expect - unlike int, which SetValue would store as REG_DWORD (see
            // SettingsVersion below).
            key.SetValue(nameof(VideoQuality), VideoQuality);
            key.SetValue(nameof(UseTimeOfDay), UseTimeOfDay);
            key.SetValue(nameof(CacheVideos), CacheVideos);
            key.SetValue(nameof(CacheLocation), CacheLocation);
            key.SetValue(nameof(ChosenMovies), ChosenMovies);
            key.SetValue(nameof(JsonURL), JsonURL);
            // Every other value here is bool/enum/string, which SetValue(string, object)
            // auto-detects as REG_SZ (via ToString()) - matching the "as string" read pattern
            // used everywhere in this class. SettingsVersion is an int, which SetValue instead
            // auto-detects as REG_DWORD: written that way, "as string" on read silently yields
            // null, SettingsVersion would never advance past 0, and MigrateIfNeeded would refire
            // (and wipe ChosenMovies) on every single launch instead of once. Force REG_SZ.
            key.SetValue(nameof(SettingsVersion), SettingsVersion.ToString());

            // delete old keys
            key.DeleteValue(nameof(DifferentMoviesOnDual), throwOnMissingValue: false);
            key.DeleteValue(nameof(MultiscreenDisabled), throwOnMissingValue: false);
        }
#pragma warning restore CS0618 // Type or member is obsolete

        /// <summary>
        /// One-time migration for settings whose meaning changed between versions. Safe to call on
        /// every startup - it is a no-op once SettingsVersion reaches CurrentSettingsVersion.
        /// Must run before any form is constructed or the video catalog is fetched.
        /// </summary>
        public static void MigrateIfNeeded()
        {
            // A brand-new install (no registry key yet) has nothing to migrate - don't force the
            // key into existence here and lock in today's defaults; let SaveSettings() create it
            // lazily the same way it always has (first time the user opens or saves Settings).
            using (var existingKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AerialScreenSaver"))
            {
                if (existingKey == null) return;
            }

            var settings = new RegSettings();
            if (settings.SettingsVersion >= CurrentSettingsVersion) return;

            // The constructor doesn't fall back to the field-initializer default for these two
            // fields on a registry key that predates them (a known gap - see the porting plan's
            // Step 2), so an old-enough install can reach here with JsonURL/CacheLocation == null.
            // Normalize before SaveSettings() below, since RegistryKey.SetValue throws on null.
            if (settings.JsonURL == null) settings.JsonURL = "";
            if (settings.CacheLocation == null)
                settings.CacheLocation = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aerial");

            if (settings.SettingsVersion < 1)
            {
                // v0 -> v1: the catalog moved from Apple's dead/near-empty feeds to a video list
                // bundled with the app, and video ids changed from short codes (e.g. "b1-1") to
                // UUIDs. Both make old settings meaningless rather than merely stale.
                if (settings.JsonURL == AerialGlobalVars.appleVideosURI ||
                    settings.JsonURL == AerialGlobalVars.applefourKVideoURI)
                {
                    settings.JsonURL = "";
                }
                settings.ChosenMovies = "";
            }

            settings.SettingsVersion = CurrentSettingsVersion;
            settings.SaveSettings();
        }

        public enum MultiMonitorModeEnum
        {
            [Description("Show on Main Screen only")]
            MainOnly = 0,
            [Description("Show same video on each screen")]
            SameOnEach = 1,
            [Description("Show different video on each screen")]
            DifferentVideos = 5,
            [Description("Span single video across all screens")]
            SpanAll = 10,
        }

        /// <summary>
        /// Which encoding/resolution to play. Resolved per asset by Asset.ResolveUrl; the three
        /// encodings have distinct filenames, so switching quality never invalidates the cache.
        /// </summary>
        public enum VideoQualityEnum
        {
            [Description("1080p (H.264) - best compatibility")]
            H264_1080p = 0,
            [Description("1080p (HEVC) - needs HEVC codec")]
            Hevc1080p = 1,
            [Description("4K (HEVC) - needs HEVC codec")]
            Hevc4k = 2,
        }
    }
}
