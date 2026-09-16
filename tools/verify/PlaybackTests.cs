using System;
using System.Collections.Generic;
using System.IO;
using Aerial;

class PlaybackTests
{
    static int failures = 0;

    static void Check(string label, Action body)
    {
        Console.Write("- " + label + " ... ");
        try
        {
            body();
            Console.WriteLine("ok");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.Message);
            failures++;
        }
    }

    static void Assert(bool cond, string msg)
    {
        if (!cond) throw new Exception(msg);
    }

    static void AssertEq(string expected, string actual, string msg)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception(msg + " (expected \"" + expected + "\", got \"" + actual + "\")");
    }

    static Asset Make(string id, string h264, string hevc1080 = null, string hevc4k = null)
    {
        return new Asset
        {
            id = id,
            accessibilityLabel = id,
            name = id,
            url = h264,
            src = new AssetSources
            {
                H2641080p = h264,
                H2651080p = hevc1080,
                H2654k = hevc4k,
            }
        };
    }

    static List<Asset> Playlist(int n)
    {
        var list = new List<Asset>();
        for (var i = 1; i <= n; i++)
            list.Add(Make("id" + i, "https://example.test/v" + i + ".mov"));
        return list;
    }

    static PlaybackController New(IList<Asset> movies)
    {
        // cacheEnabled:false so tests never queue real downloads.
        return new PlaybackController(movies, RegSettings.VideoQualityEnum.H264_1080p, false, false);
    }

    static int Main()
    {
        // Caching's static CacheFolder initialises from the registry; Setup() makes it usable
        // headlessly and points it at a real temp dir.
        Caching.Setup();
        Console.WriteLine("cache folder: " + Caching.CacheFolder);
        Console.WriteLine();

        Check("advances through the playlist and wraps around", () =>
        {
            var c = New(Playlist(3));
            Assert(c.MoveNext(), "first MoveNext");
            AssertEq("https://example.test/v1.mov", c.CurrentUrl, "1st");
            Assert(c.MoveNext(), "2nd");
            AssertEq("https://example.test/v2.mov", c.CurrentUrl, "2nd");
            Assert(c.MoveNext(), "3rd");
            AssertEq("https://example.test/v3.mov", c.CurrentUrl, "3rd");
            Assert(c.MoveNext(), "wrap");
            AssertEq("https://example.test/v1.mov", c.CurrentUrl, "wrapped");
        });

        Check("empty playlist reports no playable items and gives up", () =>
        {
            var c = New(new List<Asset>());
            Assert(!c.HasPlayableItems, "HasPlayableItems");
            Assert(!c.MoveNext(), "MoveNext should be false");
            Assert(c.ShouldGiveUp, "ShouldGiveUp");
        });

        Check("a failed remote url is never served again", () =>
        {
            var c = New(Playlist(3));
            c.MoveNext();
            var bad = c.CurrentUrl;
            c.NotifyFailed(bad, PlaybackFailure.LoadFailed);
            for (var i = 0; i < 10; i++)
            {
                c.MoveNext();
                Assert(c.CurrentUrl != bad, "served blocked url again: " + bad);
            }
        });

        Check("every asset failing terminates instead of spinning", () =>
        {
            var c = New(Playlist(3));
            var served = new List<string>();
            while (c.MoveNext())
            {
                served.Add(c.CurrentUrl);
                c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);
                Assert(served.Count <= 5, "MoveNext kept succeeding past the playlist length");
            }
            Assert(served.Count == 3, "expected 3 attempts, got " + served.Count);
            Assert(c.ShouldGiveUp, "ShouldGiveUp after all failed");
            Assert(c.CurrentUrl == null, "CurrentUrl should be cleared when exhausted");
        });

        Check("ShouldGiveUp threshold is min(count, GiveUpAfter)", () =>
        {
            var c = New(Playlist(50));
            for (var i = 0; i < PlaybackController.GiveUpAfter - 1; i++)
            {
                c.MoveNext();
                c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);
            }
            Assert(!c.ShouldGiveUp, "gave up too early at " + c.ConsecutiveFailures);
            c.MoveNext();
            c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);
            Assert(c.ShouldGiveUp, "should have given up at " + c.ConsecutiveFailures);
        });

        Check("a successful start clears the failure streak", () =>
        {
            var c = New(Playlist(5));
            c.MoveNext();
            c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);
            c.MoveNext();
            c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);
            Assert(c.ConsecutiveFailures == 2, "expected 2, got " + c.ConsecutiveFailures);
            c.MoveNext();
            c.NotifyStarted(c.CurrentUrl);
            Assert(c.ConsecutiveFailures == 0, "streak not reset");
            Assert(!c.ShouldGiveUp, "should not give up after a success");
        });

        Check("a cached copy is preferred over the remote url", () =>
        {
            var movies = Playlist(1);
            var remote = movies[0].ResolveUrl(RegSettings.VideoQualityEnum.H264_1080p);
            var cached = Path.Combine(Caching.CacheFolder, Path.GetFileName(remote));
            File.WriteAllText(cached, "not really a video");
            try
            {
                var c = New(movies);
                Assert(c.MoveNext(), "MoveNext");
                Assert(c.CurrentIsFromCache, "should have reported a cache hit");
                AssertEq(cached, c.CurrentUrl, "cached path");
            }
            finally { File.Delete(cached); }
        });

        Check("a corrupt cached file is deleted and the clip re-served from the network", () =>
        {
            var movies = Playlist(3);
            var remote = movies[0].ResolveUrl(RegSettings.VideoQualityEnum.H264_1080p);
            var cached = Path.Combine(Caching.CacheFolder, Path.GetFileName(remote));
            File.WriteAllText(cached, "truncated");
            try
            {
                var c = New(movies);
                c.MoveNext();
                Assert(c.CurrentIsFromCache, "expected a cache hit first");
                AssertEq(cached, c.CurrentUrl, "cached path");

                c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);
                Assert(!File.Exists(cached), "bad cache file was not deleted");

                Assert(c.MoveNext(), "MoveNext after cache failure");
                Assert(!c.CurrentIsFromCache, "should have fallen back to the network");
                AssertEq(remote, c.CurrentUrl, "should re-serve the SAME clip from remote");
            }
            finally { if (File.Exists(cached)) File.Delete(cached); }
        });

        Check("failing the remote retry too blocks the clip for good", () =>
        {
            var movies = Playlist(3);
            var remote = movies[0].ResolveUrl(RegSettings.VideoQualityEnum.H264_1080p);
            var cached = Path.Combine(Caching.CacheFolder, Path.GetFileName(remote));
            File.WriteAllText(cached, "truncated");
            try
            {
                var c = New(movies);
                c.MoveNext();
                c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);   // cache copy dies
                c.MoveNext();
                c.NotifyFailed(c.CurrentUrl, PlaybackFailure.LoadFailed);   // remote dies too
                for (var i = 0; i < 10; i++)
                {
                    c.MoveNext();
                    Assert(c.CurrentUrl != remote, "kept serving a doubly-failed clip");
                }
            }
            finally { if (File.Exists(cached)) File.Delete(cached); }
        });

        Check("a stale failure for a preloaded clip doesn't disturb the playing one", () =>
        {
            var c = New(Playlist(4));
            c.MoveNext();
            var playing = c.CurrentUrl;     // on screen
            c.MoveNext();
            var preloaded = c.CurrentUrl;   // preloaded, fails
            c.NotifyFailed(preloaded, PlaybackFailure.LoadFailed);

            for (var i = 0; i < 10; i++)
            {
                c.MoveNext();
                Assert(c.CurrentUrl != preloaded, "kept serving the failed preload");
            }
            // the clip that was on screen is still perfectly playable
            var seenPlayingAgain = false;
            for (var i = 0; i < 10; i++)
            {
                c.MoveNext();
                if (c.CurrentUrl == playing) seenPlayingAgain = true;
            }
            Assert(seenPlayingAgain, "the still-good clip got blocked by a stale failure");
        });

        Check("quality selection picks the matching encoding", () =>
        {
            var movies = new List<Asset> { Make("q1", "h264.mov", "hevc1080.mov", "hevc4k.mov") };
            var h264 = new PlaybackController(movies, RegSettings.VideoQualityEnum.H264_1080p, false, false);
            h264.MoveNext();
            AssertEq("h264.mov", h264.CurrentUrl, "h264");

            var hevc = new PlaybackController(movies, RegSettings.VideoQualityEnum.Hevc1080p, false, false);
            hevc.MoveNext();
            AssertEq("hevc1080.mov", hevc.CurrentUrl, "hevc1080");

            var uhd = new PlaybackController(movies, RegSettings.VideoQualityEnum.Hevc4k, false, false);
            uhd.MoveNext();
            AssertEq("hevc4k.mov", uhd.CurrentUrl, "hevc4k");
        });

        Check("a missing encoding falls back instead of yielding an empty url", () =>
        {
            var movies = new List<Asset> { Make("q2", "h264only.mov") };
            var uhd = new PlaybackController(movies, RegSettings.VideoQualityEnum.Hevc4k, false, false);
            Assert(uhd.MoveNext(), "MoveNext");
            AssertEq("h264only.mov", uhd.CurrentUrl, "should fall back to the one encoding present");
        });

        Check("assets with no usable url at all are skipped", () =>
        {
            var movies = new List<Asset>
            {
                new Asset { id = "empty", accessibilityLabel = "empty" },  // no url, no src
                Make("good", "https://example.test/good.mov"),
            };
            var c = New(movies);
            Assert(c.MoveNext(), "MoveNext");
            AssertEq("https://example.test/good.mov", c.CurrentUrl, "should skip the unusable asset");
        });

        Check("works against the real 114-video bundled catalog", () =>
        {
            var movies = AerialContext.GetAllMovies();
            Assert(movies.Count == 114, "expected 114 assets, got " + movies.Count);
            var c = New(movies);
            var seen = new HashSet<string>();
            for (var i = 0; i < movies.Count; i++)
            {
                Assert(c.MoveNext(), "MoveNext at " + i);
                Assert(!string.IsNullOrWhiteSpace(c.CurrentUrl), "empty url at " + i);
                Assert(c.CurrentUrl.StartsWith("https://"), "not https at " + i + ": " + c.CurrentUrl);
                Assert(seen.Add(c.CurrentUrl), "served a duplicate before wrapping: " + c.CurrentUrl);
            }
            Assert(seen.Count == 114, "expected 114 distinct urls, got " + seen.Count);
        });

        // The previous PR shipped a bug where an int setting was written as REG_DWORD but read
        // back with "as string", so it never round-tripped. Enums go through ToString() to
        // REG_SZ, but prove it rather than assume it.
        Check("VideoQuality round-trips through the registry", () =>
        {
            var settings = new RegSettings();
            settings.VideoQuality = RegSettings.VideoQualityEnum.Hevc4k;
            settings.SaveSettings();

            var reloaded = new RegSettings();
            Assert(reloaded.VideoQuality == RegSettings.VideoQualityEnum.Hevc4k,
                   "expected Hevc4k, got " + reloaded.VideoQuality);

            reloaded.VideoQuality = RegSettings.VideoQualityEnum.Hevc1080p;
            reloaded.SaveSettings();
            Assert(new RegSettings().VideoQuality == RegSettings.VideoQualityEnum.Hevc1080p,
                   "second round-trip failed");
        });

        Check("VideoQuality defaults to H.264 when absent or junk", () =>
        {
            var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\AerialScreenSaver");
            key.SetValue("VideoQuality", "NotARealQuality");
            Assert(new RegSettings().VideoQuality == RegSettings.VideoQualityEnum.H264_1080p,
                   "junk value should fall back to H264_1080p");
            key.DeleteValue("VideoQuality", false);
            Assert(new RegSettings().VideoQuality == RegSettings.VideoQualityEnum.H264_1080p,
                   "missing value should fall back to H264_1080p");
        });

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
