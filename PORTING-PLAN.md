# Porting Plan: Refresh the Video Catalog and Harden Playback

Status: **proposed, not yet implemented**
Scope: steps 1–5 below. Each step is independently shippable and listed in dependency order.

This plan is written to be executed by someone (or something) with no prior context on this
repository. Read the "Orientation" section first; every later section assumes it.

---

## Orientation

### What this repo is

`cDima/Aerial` — a Windows screensaver (`.scr`) that plays Apple TV aerial videos. C# /
WinForms / .NET Framework **4.5.2**, built with Visual Studio. The video surface is the
**Windows Media Player ActiveX control** (`AxWMPLib.AxWindowsMediaPlayer`, wrapped interop
DLLs live in `ScreenSaver/libs/` and are loaded as embedded resources by the
`AppDomain.CurrentDomain.AssemblyResolve` hook at `ScreenSaver/Program.cs:29`).

Project root namespace and assembly name are both `Aerial`
(`ScreenSaver/ScreenSaver.csproj:11-12`). Source layout:

| File | Role |
| --- | --- |
| `ScreenSaver/Program.cs` | `Main`, screensaver command-line args (`/c` `/p` `/s` `/w`), multi-monitor window creation |
| `ScreenSaver/ScreenSaverForm.cs` | The playing window: video rotation, input handling, exit |
| `ScreenSaver/SettingsForm.cs` | Config dialog (Preferences / Cache / Video Source / About tabs) |
| `ScreenSaver/SettingsForm.Designer.cs` | Designer-generated layout for the above |
| `ScreenSaver/AerialEntities.cs` | Video catalog: fetch, parse, filter, the `Asset` / `IdAsset` models |
| `ScreenSaver/Caching.cs` | Download-to-disk cache for videos |
| `ScreenSaver/RegSettings.cs` | All persisted settings, stored under `HKCU\SOFTWARE\AerialScreenSaver` |
| `ScreenSaver/AerialGlobalVars.cs` | Hardcoded URLs |
| `ScreenSaver/Controls/EntitiesTreeView.cs` | The checkbox tree used to pick videos |
| `ScreenSaver/FormsHelpers.cs` | `DataBindEnum<T>` combobox helper, `Screen[].GetBounds()` |

### Build constraints

- **The repo cannot be built on Linux.** WinForms + an ActiveX interop assembly require
  Windows and MSBuild / Visual Studio. If you are working in a Linux container you can edit
  and reason about the code and validate JSON with Python, but you **cannot** compile or run
  it. Say so plainly in your report rather than claiming a change is verified.
- Language level is at least C# 6 — `?.`, `??`, `nameof`, and expression-bodied members are
  already used (see `ScreenSaver/FormsHelpers.cs:20`, `ScreenSaver/RegSettings.cs:33`). Do not
  use C# 7+ syntax (no tuples, no `out var`, no local functions) without checking the csproj's
  toolchain first.
- JSON is parsed with `System.Web.Script.Serialization.JavaScriptSerializer`
  (`ScreenSaver/AerialEntities.cs:7`). Not Newtonsoft. Keep it that way — adding a NuGet
  dependency to a single-file screensaver that ships as one `.scr` is not worth it.
- **Do not implement on `master`.** Use a feature branch per step.

### Why this work exists

The upstream video source is gone. `AerialGlobalVars.appleVideosURI` points at
`http://a1.phobos.apple.com/us/r1000/000/Features/atv/AutumnResources/videos/entries.json`,
which Apple retired years ago. The "4K" alternative, `applefourKVideoURI`
(`https://t27q97zg19.execute-api.us-east-1.amazonaws.com/prod/aerialAltJSON/4kEntites.json`),
still responds HTTP 200 but serves **9 assets covering only 4 locations** (Dubai, Los Angeles,
Liwa, Hong Kong, with repeats). That is the entire experience a user gets today.

The actively maintained fork, `OrangeJedi/Aerial` (Electron rewrite, MIT licensed), ships a
hand-curated `videos.json` with **114 videos × 3 encodings each**, all hosted on
`sylvan.apple.com`. Steps 1 and 5 below bring that catalog here. Steps 2–4 fix the three
failure modes most likely to be generating bug reports.

### A caveat you must respect

`sylvan.apple.com` could not be reached from the environment where this plan was written (the
sandbox's egress allowlist blocks `apple.com` hosts — HTTP 403 at the proxy, not from Apple).
**Nobody has verified from this repo that those URLs are live or that WMP can play them.**
Step 1 includes a mandatory manual smoke test on Windows for exactly this reason. If the URLs
turn out to be dead, stop and report — the rest of the plan is built on them.

Also unverified: the fork bypasses TLS validation for that host in two places
(`app.js:751` registers a `certificate-error` handler that force-accepts anything matching
`^https://sylvan.apple.com`, and `app.js:794` sets `rejectUnauthorized: false` on its download
agent). That suggests Electron rejected Apple's certificate chain. If .NET's `WebClient` throws
`WebException` with an inner `AuthenticationException` on these URLs, the correct fix is to
enable TLS 1.2 (`ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;` in
`Program.Main` — .NET 4.5.2 defaults to SSL3/TLS1.0, which modern CDNs reject). **Do not**
blanket-disable certificate validation the way the fork did.

---

## Step 1 — Bundle a current video catalog

**Goal:** ship 114 working videos inside the assembly, keep the URL override for people who
want a live/custom list, and migrate existing users off the two dead defaults.

### 1.1 Generate `ScreenSaver/Videos.json`

Source data: `videos.json` from the root of `https://github.com/OrangeJedi/aerial` (MIT).
Clone it (`git clone --depth 1 https://github.com/OrangeJedi/aerial`) and run the converter
below. The converter has been run against the real file; the numbers in "Expected output" are
what it actually produced, so treat any deviation as a signal the upstream data changed.

Save as `tools/convert_videos.py` (new directory) so it can be re-run when the fork updates:

```python
#!/usr/bin/env python3
"""Convert OrangeJedi/Aerial videos.json into Aerial's IdAsset[] envelope format."""
import json, sys, collections

def convert(src_path, dst_path):
    with open(src_path, encoding="utf-8") as f:
        src = json.load(f)
    if not isinstance(src, list):
        sys.exit("expected a top-level JSON array")

    assets = []
    seen = set()
    for v in src:
        vid = v.get("id")
        srcs = v.get("src") or {}
        if not vid or not srcs.get("H2641080p"):
            print("SKIP (no id or no H.264 source): %r" % v.get("name"), file=sys.stderr)
            continue
        if vid in seen:
            print("SKIP (duplicate id): %s" % vid, file=sys.stderr)
            continue
        seen.add(vid)
        assets.append({
            "id": vid,
            "accessibilityLabel": v.get("accessibilityLabel") or v.get("name") or "Unknown",
            "name": v.get("name") or v.get("accessibilityLabel") or "Unknown",
            "category": v.get("type") or "",
            "timeOfDay": v.get("timeOfDay") or "",
            "url": srcs.get("H2641080p"),
            "src": {
                "H2641080p": srcs.get("H2641080p") or "",
                "H2651080p": srcs.get("H2651080p") or "",
                "H2654k":    srcs.get("H2654k") or "",
            },
            "pointsOfInterest": v.get("pointsOfInterest") or {},
        })

    assets.sort(key=lambda a: (a["accessibilityLabel"].upper(), a["timeOfDay"], a["id"]))
    out = [{"id": "bundled", "assets": assets}]
    with open(dst_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print("assets: %d" % len(assets))
    print("timeOfDay: %r" % dict(collections.Counter(a["timeOfDay"] for a in assets)))
    print("category:  %r" % dict(collections.Counter(a["category"] for a in assets)))
    print("empty src entries: %d" % len([k for a in assets for k, u in a["src"].items() if not u]))

if __name__ == "__main__":
    convert(sys.argv[1], sys.argv[2])
```

**Expected output** (verified against `OrangeJedi/Aerial` @ `ba9259b`, v1.2.1):

```
assets: 114
timeOfDay: {'day': 7, 'night': 23, '': 84}
category:  {'space': 22, 'underwater': 21, 'landscape': 40, 'cityscape': 31}
empty src entries: 0
```

Resulting file is ~108 KB.

Design notes on the output shape — read these before changing it:

- It is wrapped in the **existing** `IdAsset[]` envelope (`[{"id":"bundled","assets":[…]}]`) so
  that one parser handles both the bundled file and any legacy remote URL. No second code path.
- Each asset keeps a flat `"url"` (the H.264 1080p one) **in addition to** the `"src"` object.
  That means the file still parses correctly against the pre-Step-5 model, and any code path
  that has not been updated yet degrades to 1080p H.264 instead of crashing.
- The fork's `"type"` field (landscape/cityscape/space/underwater) is renamed to `"category"`.
  The legacy `Asset.type` field means something different (it held the string `"video"` in
  Apple's old feed). `Asset.type` is **read nowhere in this codebase** — verified by grep — so
  this rename costs nothing and prevents a confusing collision.
- `pointsOfInterest` is carried through unused. `JavaScriptSerializer` ignores JSON keys with
  no matching member — the current 4K feed already relies on this, since its `version` and
  `initialAssetCount` keys have no counterpart on `IdAsset`. Do not declare a member for it.
  **Verify this assumption early** (deserialize the file once in a scratch console app); if it
  turns out `JavaScriptSerializer` throws on unknown members, strip `pointsOfInterest` in the
  converter instead of fighting it.
- Sorted by `(accessibilityLabel, timeOfDay, id)` so the committed file has a stable diff when
  regenerated.

### 1.2 Embed it

In `ScreenSaver/ScreenSaver.csproj`, alongside the existing embedded interop DLLs at lines
202–204:

```xml
<EmbeddedResource Include="Videos.json" />
```

The manifest resource name will be **`Aerial.Videos.json`** (root namespace + path). This
follows the same rule as `libs\AxInterop.WMPLib.dll` → `Aerial.libs.AxInterop.WMPLib.dll`,
which `Program.cs:31` already depends on.

### 1.3 Extend the model — `ScreenSaver/AerialEntities.cs`

Add to the `Asset` class (which currently declares `url`, `accessibilityLabel`, `type`, `id`,
`timeOfDay` at lines 137–142):

```csharp
public string name;
public string category;
public AssetSources src;

/// <summary>
/// The URL to play for the requested quality, falling back through the other
/// encodings and finally to the legacy flat url field.
/// </summary>
public string ResolveUrl(RegSettings.VideoQualityEnum quality)
{
    var fromSrc = src == null ? null : src.For(quality);
    return string.IsNullOrWhiteSpace(fromSrc) ? url : fromSrc;
}
```

And a new class in the same file:

```csharp
public class AssetSources
{
    // Field names must match the JSON keys exactly - JavaScriptSerializer maps by member name.
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
```

(`VideoQualityEnum` is defined in Step 5. If you are implementing Step 1 alone, add the enum
now with the three members and leave the UI for later — `ResolveUrl` needs it.)

### 1.4 Load the bundled list — `GetAllEntries()`

Replace `ScreenSaver/AerialEntities.cs:57-88` wholesale. Current code unconditionally fires a
cache request and a `DownloadString` at `settings.JsonURL`, and swallows only
`ArgumentException` (with an unused variable `e`, CS0168).

New behaviour:

```csharp
public static IdAsset[] GetAllEntries()
{
    if (cachedEntities != null) return cachedEntities;

    var settings = new RegSettings();
    var aerialUrl = settings.JsonURL;

    string entries = null;
    if (!string.IsNullOrWhiteSpace(aerialUrl))
    {
        try
        {
            Caching.Enqueue(aerialUrl);          // keep the JSON itself cached, see Step 3
            entries = Caching.IsHit(aerialUrl)
                ? File.ReadAllText(Caching.Get(aerialUrl))
                : new WebClient().DownloadString(aerialUrl);
        }
        catch (Exception ex)
        {
            Trace.WriteLine("Falling back to bundled catalog; remote fetch failed: " + ex);
            entries = null;
        }
    }

    if (string.IsNullOrWhiteSpace(entries))
        entries = LoadBundledEntries();

    try
    {
        cachedEntities = new JavaScriptSerializer().Deserialize<IdAsset[]>(entries);
    }
    catch (Exception ex)
    {
        Trace.WriteLine("Catalog parse failed: " + ex);
        // A malformed *remote* list must not leave the user with nothing.
        try { cachedEntities = new JavaScriptSerializer().Deserialize<IdAsset[]>(LoadBundledEntries()); }
        catch { cachedEntities = null; }
    }

    return cachedEntities;
}

private static string LoadBundledEntries()
{
    var asm = Assembly.GetExecutingAssembly();
    using (var stream = asm.GetManifestResourceStream("Aerial.Videos.json"))
    {
        if (stream == null)
            throw new InvalidOperationException("Embedded resource Aerial.Videos.json is missing");
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            return reader.ReadToEnd();
    }
}
```

Add `using System.Reflection;` and `using System.Diagnostics;` to the file.

Note: `JavaScriptSerializer.MaxJsonLength` defaults to 2 MB; the bundled file is ~108 KB, so no
change is needed. If a user points `JsonURL` at something larger it will throw and fall back to
bundled, which is acceptable.

### 1.5 Retire the dead URLs

`ScreenSaver/RegSettings.cs:20` — change the default:

```csharp
public string JsonURL = "";   // empty means: use the bundled catalog
```

`ScreenSaver/AerialGlobalVars.cs` — keep `githubLatestReleaseDetails` and `githubAllReleases`
as they are. Keep `appleVideosURI` and `applefourKVideoURI` **as constants only**, referenced
solely by the migration in 1.6 so it can recognise them. Remove every other reference. Add a
comment saying both are dead endpoints retained for migration matching.

### 1.6 Migrate existing users

Video IDs change shape: old ones were `b1-1`, new ones are UUIDs like
`2F72BC1E-3D76-456C-81EB-842EBA488C27`. Stored selections live in the `ChosenMovies` registry
string as tree paths (`"Hawaii\day 1 (b4-1)"`) and `AssetSelected`
(`ScreenSaver/AerialEntities.cs:91`) matches on the id parsed out from between the parentheses.
After the swap, zero stored selections match. Playback survives — `FilterEntries` falls back to
"all movies" when the filtered list comes out empty (`AerialEntities.cs:43`) — but the settings
tree renders with every box unchecked and the user's curation is silently gone.

Add to `RegSettings`:

```csharp
public const int CurrentSettingsVersion = 1;
public int SettingsVersion = 0;
```

Read it in the constructor with `int.TryParse(key.GetValue(nameof(SettingsVersion)) as string ?? "0", out SettingsVersion);`
and write it in `SaveSettings()` alongside the other values.

Add a **static** migration method — do not migrate inside the constructor, which runs dozens of
times per playlist build:

```csharp
public static void MigrateIfNeeded()
{
    var settings = new RegSettings();
    if (settings.SettingsVersion >= CurrentSettingsVersion) return;

    // v0 -> v1: the catalog moved to Apple's sylvan.apple.com UUID-keyed list.
    if (settings.JsonURL == AerialGlobalVars.appleVideosURI ||
        settings.JsonURL == AerialGlobalVars.applefourKVideoURI)
    {
        settings.JsonURL = "";          // both endpoints are dead / near-empty
    }
    settings.ChosenMovies = "";         // old ids cannot match the new catalog

    settings.SettingsVersion = CurrentSettingsVersion;
    settings.SaveSettings();
}
```

Call it from `ScreenSaver/Program.cs` in `Main`, immediately after `Caching.Setup();` (line 42):

```csharp
RegSettings.MigrateIfNeeded();
```

`MigrateIfNeeded` must run before any form is constructed. Note that
`Caching.CacheFolder`'s static initializer (`Caching.cs:14`) runs earlier still, but it only
touches `CacheLocation`, which the migration does not modify.

### 1.7 Fix the tree labels for videos with no `timeOfDay`

84 of the 114 videos have **no** `timeOfDay`. `Asset.TimeAndIdNumbered()`
(`AerialEntities.cs:165`) builds the tree node text as `timeOfDay + numeric + " (" + id + ")"`,
which for those videos degenerates to `" (2F72BC1E-…)"` — a leading space and no useful label.
That text is also what gets persisted into `ChosenMovies`, so it must be stable.

Rewrite it to fall back to `category`:

```csharp
public string TimeAndIdNumbered()
{
    var label = !string.IsNullOrWhiteSpace(timeOfDay) ? timeOfDay
              : !string.IsNullOrWhiteSpace(category) ? category
              : "video";
    return label + (numeric == 0 ? "" : " " + numeric) + " (" + id + ")";
}
```

Apply the same fallback to `ShortName()` (line 156) and `ToFullName()` (line 160), which feed
sorting and the duplicate-numbering pass. This matters more than it looks: with the new
catalog there are **52 distinct labels, 19 of which have multiple videos**, and 76 videos land
in a group where `ShortName()` collides — so `AddHumanNumbers`
(`ScreenSaver/SettingsForm.cs:99`) is doing real work now, where before it rarely fired.

### 1.8 Verification

Automatable on any platform:

```bash
python3 -c "
import json;d=json.load(open('ScreenSaver/Videos.json'))
a=d[0]['assets']
assert len(d)==1 and d[0]['id']=='bundled'
assert len(a)==114, len(a)
assert len({x['id'] for x in a})==114
assert all(x['src']['H2641080p'].startswith('https://') for x in a)
print('ok')"
```

Manual, on Windows — **required, none of this has been verified**:

1. Build. Run `Aerial.exe` (windowed mode). Confirm video plays.
2. Delete `HKCU\SOFTWARE\AerialScreenSaver` entirely, run again — confirm the bundled list
   loads with no registry key present.
3. Recreate the key with `JsonURL` set to the old phobos URL and a non-empty `ChosenMovies`,
   run, and confirm the migration clears both and `SettingsVersion` is `1`.
4. Open Settings → Preferences and confirm the tree shows 52 groups / 114 leaves, with
   sensible labels on the videos that have no time of day.
5. Set `JsonURL` to a deliberately broken URL and confirm it falls back to bundled rather than
   crashing.

---

## Step 2 — Stop `RegSettings` from overwriting its own defaults with `null`

**This is a hard crash for anyone upgrading, and Step 1 ships an upgrade. Do it first if you
are shipping steps separately.**

`ScreenSaver/RegSettings.cs:41,43`:

```csharp
CacheLocation = key.GetValue(nameof(CacheLocation)) as string;   // no fallback
JsonURL       = key.GetValue(nameof(JsonURL)) as string;         // no fallback
```

Every other field in that constructor has one (`as string ?? "True"`). These two do not, so a
registry key written by a build that predates the `JsonURL` value yields `JsonURL == null`, and
the field initializer at line 20 is thrown away. The crash path:
`AerialEntities.GetAllEntries` → `Caching.IsHit(null)` → `Path.Combine(CacheFolder, null)` →
`ArgumentNullException` on the UI thread inside `ScreenSaverForm_Load`.

The author knew — `SettingsForm.cs:40` and `:49` both guard against exactly this null. The
guard just never reached the other two consumers.

Fix in the constructor (field initializers run first, so the right-hand side reads the default):

```csharp
CacheLocation = key.GetValue(nameof(CacheLocation)) as string ?? CacheLocation;
ChosenMovies  = key.GetValue(nameof(ChosenMovies)) as string ?? ChosenMovies;
JsonURL       = key.GetValue(nameof(JsonURL)) as string ?? JsonURL;
```

Then harden the rest while you are here — `bool.Parse` throws `FormatException` on a corrupt
value, which is an unhandled exception in a constructor called from everywhere. Add a private
helper and use it for `DifferentMoviesOnDual`, `MultiscreenDisabled`, `UseTimeOfDay`,
`CacheVideos`:

```csharp
private static bool ReadBool(RegistryKey key, string name, bool fallback)
{
    bool parsed;
    return bool.TryParse(key.GetValue(name) as string, out parsed) ? parsed : fallback;
}
```

Finally, guard whitespace in the two path/URL settings after loading:

```csharp
if (string.IsNullOrWhiteSpace(CacheLocation))
    CacheLocation = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Aerial");
```

Once this is in, the defensive checks at `SettingsForm.cs:40` and `:49` become redundant. Leave
them; they are harmless and removing them widens the diff.

**Verification:** create `HKCU\SOFTWARE\AerialScreenSaver` containing *only*
`UseTimeOfDay="True"` (simulating an old install), run the screensaver, confirm it plays instead
of throwing.

---

## Step 3 — Serialize downloads, and fix the cache-relocation bugs

All changes in `ScreenSaver/Caching.cs`.

### 3.1 One download at a time

Today `StartDelayedCache` (line 62) schedules each URL on its own `Task.Delay(10s).ContinueWith`.
`SettingsForm.fullDownloadBtn_Click` (line 258) calls it for **every** movie in a loop, so all
114 downloads fire in the same instant ten seconds later. The 1 GB free-space check
(`EnsureEnoughSpace`, line 176) was evaluated per-file *before* any of them started, so it
cannot stop the disk filling.

There is also a latent disposal problem: the `WebClient` is created in a `using` block (line 70)
and `DownloadFileAsync` returns immediately, so the block exits — and the client is disposed —
before a single byte arrives. The completion event then fires on a disposed object. Whether the
transfer survives depends on `WebClient.Dispose` not tearing down in-flight operations, which is
incidental behaviour, not a guarantee.

Replace the whole mechanism with a single-consumer queue:

```csharp
private static readonly object QueueLock = new object();
private static readonly Queue<string> Pending = new Queue<string>();
private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
private static bool workerRunning;

/// <summary>Queue a url for caching. Returns immediately; downloads run one at a time.</summary>
internal static void Enqueue(string url)
{
    if (string.IsNullOrWhiteSpace(url)) return;
    if (IsHit(url)) return;

    lock (QueueLock)
    {
        if (!Queued.Add(url)) return;
        Pending.Enqueue(url);
        if (workerRunning) return;
        workerRunning = true;
    }

    Task.Run(() => RunQueueAsync()).ContinueWith(
        t => Trace.WriteLine("Cache worker faulted: " + t.Exception),
        TaskContinuationOptions.OnlyOnFaulted);
}

private static async Task RunQueueAsync()
{
    // Preserve the original intent: don't compete with the video that just started.
    await Task.Delay(DelayAmount).ConfigureAwait(false);

    try
    {
        while (true)
        {
            string url;
            lock (QueueLock)
            {
                if (Pending.Count == 0) { workerRunning = false; return; }
                url = Pending.Dequeue();
            }

            try
            {
                if (!EnsureEnoughSpace())
                {
                    Trace.WriteLine("Cache: out of space, dropping queue");
                    lock (QueueLock) { Pending.Clear(); Queued.Clear(); workerRunning = false; }
                    return;
                }
                await DownloadOneAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Cache: failed " + url + " - " + ex.Message);
            }
            finally
            {
                lock (QueueLock) { Queued.Remove(url); }
            }
        }
    }
    catch
    {
        lock (QueueLock) { workerRunning = false; }
        throw;
    }
}
```

Keep `StartDelayedCache` as a one-line forwarder to `Enqueue` so existing call sites
(`AerialEntities.cs:68`, `ScreenSaverForm.cs:326`, `SettingsForm.cs:268`) keep compiling, or
update them all — either is fine, just be consistent.

### 3.2 Download with verification, and copy instead of move

Replace `OnDownloadFileComplete` (lines 81–102) entirely:

```csharp
private static async Task DownloadOneAsync(string url)
{
    var filename = Path.GetFileName(url);
    if (string.IsNullOrWhiteSpace(filename)) return;

    var tempFullPath  = Path.Combine(TempFolder, filename);
    var cacheFullPath = Path.Combine(CacheFolder, filename);

    Interlocked.Increment(ref NumOfCurrentDownloads);
    try
    {
        using (var client = new WebClient())
        {
            await client.DownloadFileTaskAsync(new Uri(url), tempFullPath).ConfigureAwait(false);

            // A truncated transfer must never be promoted into the cache.
            long expected;
            var header = client.ResponseHeaders == null ? null : client.ResponseHeaders["Content-Length"];
            if (long.TryParse(header, out expected) && expected > 0)
            {
                var actual = new FileInfo(tempFullPath).Length;
                if (actual != expected)
                    throw new IOException(string.Format(
                        "Truncated download: got {0} of {1} bytes for {2}", actual, expected, filename));
            }
        }

        // Copy + delete, not Directory.Move: temp and cache can be on different volumes.
        File.Copy(tempFullPath, cacheFullPath, overwrite: true);
    }
    finally
    {
        Interlocked.Decrement(ref NumOfCurrentDownloads);
        try { if (File.Exists(tempFullPath)) File.Delete(tempFullPath); }
        catch (IOException) { /* swept up by Setup() on next launch */ }
    }
}
```

Three specific bugs this closes, beyond the concurrency:

- `Directory.Move(tempFullPath, cacheFullpath)` (line 92) is `MoveFile` under the hood and
  throws `IOException` across volumes. `UpdateCachePath` makes that reachable — see 3.3.
- `NumOfCurrentDownloads` was incremented *after* the download started (line 75) and
  decremented in the completion handler, so a fast completion could drive the counter negative.
  It is now incremented before and decremented in a `finally`.
- Nothing ever checked that the bytes received matched `Content-Length`.

### 3.3 Fix `UpdateCachePath`

`Caching.cs:104-137` has three defects:

1. **Line 107** sets `CacheFolder = cacheLocation` but never updates `TempFolder`, which keeps
   pointing at `<old>/temp`. **Line 129** then recursively deletes the old directory, temp
   folder included. Every later download targets a path that no longer exists.
2. **Line 124** calls `DeleteCache(oldCacheDirectory)` *unconditionally*, outside the `if` that
   guards the move. When the target drive lacked space the files were never copied — and then
   get deleted anyway. Silent, total cache loss on a settings save.
3. `DeleteCache` is `async void` and is not awaited, so its per-file deletes race the recursive
   `Directory.Delete` on line 129. Only `UnauthorizedAccessException` is caught (line 131); an
   `IOException` from that race escapes an `async void` method and kills the process.

Rewrite as a `Task`-returning method (never `async void`), and have
`SettingsForm.SaveSettings` (line 167) await or block on it:

```csharp
internal static async Task UpdateCachePathAsync(string oldCacheDirectory, string cacheLocation)
{
    if (string.IsNullOrWhiteSpace(cacheLocation)) return;
    if (string.Equals(oldCacheDirectory, cacheLocation, StringComparison.OrdinalIgnoreCase)) return;

    var moved = false;
    var newTemp = Path.Combine(cacheLocation, "temp");
    Directory.CreateDirectory(cacheLocation);
    Directory.CreateDirectory(newTemp);

    if (!string.IsNullOrWhiteSpace(oldCacheDirectory) && Directory.Exists(oldCacheDirectory))
    {
        CacheFolder = cacheLocation;            // CacheSpace() must measure the TARGET drive
        if (GetDirectorySize(oldCacheDirectory) < CacheSpace() - (1000L * 1000L * 1000L))
        {
            moved = true;
            foreach (var f in Directory.GetFiles(oldCacheDirectory))
            {
                var newfile = Path.Combine(cacheLocation, Path.GetFileName(f));
                try
                {
                    if (!File.Exists(newfile))
                        await Task.Run(() => File.Copy(f, newfile)).ConfigureAwait(false);
                    await Task.Run(() => File.Delete(f)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    moved = false;              // leave the old cache alone if anything failed
                    Trace.WriteLine("Cache move failed for " + f + ": " + ex.Message);
                }
            }
        }
    }

    CacheFolder = cacheLocation;
    TempFolder  = newTemp;                      // <- the bug that broke all later downloads

    // Only remove the old location once its contents are safely at the new one.
    if (moved)
    {
        try { await Task.Run(() => Directory.Delete(oldCacheDirectory, true)).ConfigureAwait(false); }
        catch (Exception ex) { Trace.WriteLine("Could not remove old cache dir: " + ex.Message); }
    }
}
```

Also make `DeleteCache` (line 139) `Task`-returning rather than `async void`, and catch
`IOException` as well as `UnauthorizedAccessException` — a video file in use by the player
throws the former, not the latter.

And null-guard `CacheSpace()` (line 157): `CacheFolder.StartsWith(drive.Name)` throws if
`CacheFolder` is null, which Step 2 makes much less likely but does not make impossible.

### 3.4 Verification

- Queue all 114 videos from Settings → Cache → "Download all". Watch the temp folder: exactly
  one `.mov` should be present at any moment. The `# of files downloading` label
  (`SettingsForm.cs:118`) should read `1`, never `114`.
- Interrupt the network mid-download; confirm the partial file is deleted and the worker moves
  to the next URL rather than throwing.
- Change the cache location to a folder **on a different drive** with files already cached.
  Confirm files arrive at the new location, the old directory is removed, and a subsequent
  download still succeeds (this is the `TempFolder` regression — it will fail without 3.3).
- Change the cache location to a drive with insufficient free space. Confirm the old cache is
  **still there** afterwards.

---

## Step 4 — Skip videos that fail to play

`ScreenSaverForm` currently has no error handling at all. A dead or unplayable URL just stalls;
the only thing that eventually notices is `NextVideoTimer_Tick` (line 347) checking for
`wmppsReady`/`wmppsUndefined`/`wmppsStopped` once a second. With a bundled list of 114 URLs
that will inevitably rot, and with HEVC entries that WMP may not decode at all (Step 5), skipping
on error becomes mandatory rather than nice.

### 4.1 Hook the player's error event

In `RegisterEvents()` (`ScreenSaverForm.cs:66`), alongside the existing player hooks:

```csharp
this.player.MediaError += player_MediaError;
```

The exact delegate type comes from the interop assembly — expect
`AxWMPLib._WMPOCXEvents_MediaErrorEvent`, whose `e.pMediaObject` can be cast to
`WMPLib.IWMPMedia`. **Confirm the signature against IntelliSense** when you build; the interop
wrapper in `libs/` is the authority, not this document.

### 4.2 Track and skip

Add fields:

```csharp
private readonly HashSet<string> failedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
private int consecutiveFailures;
private bool fatalErrorShown;
private DateTime lastStateChange = DateTime.Now;
```

Handler:

```csharp
private void player_MediaError(object sender, EventArgs e)
{
    var url = player.URL;
    Trace.WriteLine("MediaError on " + url);

    // A corrupt cached file should not poison the video forever - drop it and re-stream.
    if (!string.IsNullOrEmpty(url) && File.Exists(url))
    {
        try { File.Delete(url); Trace.WriteLine("Removed bad cache file " + url); }
        catch (IOException) { }
    }
    else if (!string.IsNullOrEmpty(url))
    {
        failedUrls.Add(url);
    }

    consecutiveFailures++;
    if (Movies != null && consecutiveFailures >= Math.Min(Movies.Count, 10))
    {
        ShowFatalOnce("Could not play any videos. Check your network connection, video source, "
                    + "and video quality setting.");
        return;
    }

    SetNextVideo();
}
```

In `SetNextVideo()` (line 300), skip assets already known bad, and reset the counter on success:

- Before assigning `player.URL`, if `failedUrls.Contains(url)`, advance `currentVideoIndex` and
  try the next asset. Bound the loop at `Movies.Count` attempts so an all-failed list cannot spin.
- In `player_PlayStateChange`, when the state reaches `wmppsPlaying`, set
  `consecutiveFailures = 0`.

### 4.3 Detect stalls, not just errors

WMP can sit in `wmppsBuffering` or `wmppsTransitioning` indefinitely when a host accepts the
connection but never delivers. Update `player_PlayStateChange` (line 358) to record
`lastStateChange = DateTime.Now;` on every state change, and extend `NextVideoTimer_Tick`
(line 345):

```csharp
if ((state == WMPLib.WMPPlayState.wmppsBuffering ||
     state == WMPLib.WMPPlayState.wmppsTransitioning) &&
    lastStateChange.AddSeconds(30) < DateTime.Now)
{
    Trace.WriteLine("Stalled in " + state + ", skipping");
    consecutiveFailures++;
    SetNextVideo();
}
```

### 4.4 Stop showing one message box per monitor

`SetNextVideo` at lines 306–314 pops a `MessageBox` when `Movies` is empty. On a four-monitor
setup that is four modal dialogs behind a topmost window. Route every user-facing error through:

```csharp
private void ShowFatalOnce(string message)
{
    showVideo = false;
    NextVideoTimer.Enabled = false;
    if (fatalErrorShown || !shouldCache) return;   // shouldCache is true only on the primary screen
    fatalErrorShown = true;
    MessageBox.Show(message, "Aerial", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
```

and replace the existing inline `MessageBox.Show` call with it. Note the existing message has a
typo — "Resart" — fix it while you are there.

### 4.5 Verification

- Point `JsonURL` at a JSON whose `url` fields are all `https://example.invalid/x.mov`. Confirm
  the screensaver cycles through, gives up after 10 attempts, and shows exactly one dialog.
- Put a zero-byte or truncated `.mov` in the cache folder with a filename matching a catalog
  entry. Confirm it is deleted and the video re-streams from the network.
- Confirm a normal run never fires the handler and that `consecutiveFailures` returns to 0.

---

## Step 5 — Video quality selector

Replaces the "Set to 4k Video" button, which points at the near-empty 9-asset AWS endpoint.

### 5.1 The setting

In `ScreenSaver/RegSettings.cs`, mirroring the existing `MultiMonitorModeEnum` pattern
(lines 68–79), which is already wired for combobox binding via `FormsHelpers.DataBindEnum<T>`:

```csharp
public VideoQualityEnum VideoQuality = VideoQualityEnum.H264_1080p;

public enum VideoQualityEnum
{
    [Description("1080p (H.264) - best compatibility")]
    H264_1080p = 0,
    [Description("1080p (HEVC) - needs HEVC codec")]
    Hevc1080p = 1,
    [Description("4K (HEVC) - needs HEVC codec")]
    Hevc4k = 2,
}
```

Load in the constructor next to `MultiMonitorMode` (line 35):

```csharp
if (!Enum.TryParse(key.GetValue(nameof(VideoQuality)) as string, out VideoQuality))
    VideoQuality = VideoQualityEnum.H264_1080p;
```

Save in `SaveSettings()`: `key.SetValue(nameof(VideoQuality), VideoQuality);` — the existing code
relies on `RegistryKey.SetValue` storing enums and bools as `REG_SZ` strings, which round-trips
through the `as string` reads. Follow the same pattern.

### 5.2 Wire it into playback

`ScreenSaverForm.SetNextVideo()` (line 316) currently reads `Movies[currentVideoIndex].url`.
Change to:

```csharp
var quality = new RegSettings().VideoQuality;     // or hoist to a field, see note below
string url = Movies[currentVideoIndex].ResolveUrl(quality);
```

Note: `SetNextVideo` already constructs a `RegSettings` on every call (line 302) — a full
registry read per video. Prefer reading settings once in `ScreenSaverForm_Load` and caching them
in a field. (`AerialContext.AssetSelected` at `AerialEntities.cs:91` is worse — it builds *two*
`RegSettings` per asset inside a `Where` over 114 assets. Out of scope here, but worth a
follow-up.)

`SettingsForm.tvChosen_AfterSelect` (line 88) previews via `tvChosen.GetUrl(...)`. Change
`EntitiesTreeView.GetUrl` (`Controls/EntitiesTreeView.cs:105`) to
`return Movies[fullPath].ResolveUrl(quality);`, passing the quality in.

### 5.3 The UI

In `ScreenSaver/SettingsForm.Designer.cs`, on the `tabSource` page (block at line 351). The page
currently holds `SetToFourK_btn`, `videoSourceResetButton`, `lbl_VideoSourceURL`,
`changeVideoSourceText`.

- **Remove** `SetToFourK_btn`: its field declaration (line 59), its `Controls.Add` (line 353),
  its property block (lines ~365–373), and the `SetToFourK_btn_Click` handler in
  `SettingsForm.cs:250`.
- **Add** `lblVideoQuality` (Label) and `cbVideoQuality` (ComboBox) in the freed space. Follow
  the `cbMultiScreenMode` block at Designer lines 222–231 for conventions —
  `DropDownStyle = ComboBoxStyle.DropDownList`, `FormattingEnabled = true`. Place them around
  `Location = new Point(11, 95)` / `(11, 115)` with `Size = new Size(377, 21)`; adjust to taste
  in the designer, the exact pixels are not load-bearing.
- **Add** a wrapping warning label under the dropdown: *"HEVC requires the HEVC Video Extensions
  codec from the Microsoft Store. If video is black or does not start, switch back to
  1080p (H.264)."* This is the single most likely support question the dropdown will generate.
- Update `lbl_VideoSourceURL`'s text to explain that leaving the URL **empty** uses the built-in
  catalog of 114 videos.
- `videoSourceResetButton_Click` (`SettingsForm.cs:245`) currently sets the text box to
  `AerialGlobalVars.appleVideosURI`. Change it to set the text box to `""` (bundled catalog).

In `SettingsForm.LoadSettings()` (line 32), next to the existing `cbMultiScreenMode` bind:

```csharp
cbVideoQuality.DataBindEnum(settings.VideoQuality);
```

In `SettingsForm.SaveSettings()` (line 155):

```csharp
settings.VideoQuality = (RegSettings.VideoQualityEnum)cbVideoQuality.SelectedValue;
```

### 5.4 Cache implications — none, and this was checked

The cache keys on `Path.GetFileName(url)` (`Caching.cs:44`). Across all 114 videos × 3
encodings, all **342 filenames are distinct** (verified: `..._2K_AVC.mov`, `..._2K_HEVC.mov`,
`..._4K_HEVC.mov`). So switching quality neither collides with nor invalidates anything already
cached, and a user who flips back and forth keeps both copies. No cache migration is needed.

Worth noting in the UI or release notes: the 4K files are substantially larger, so
"Download all" at 4K will consume far more than the ~10 GB the existing confirmation dialog
warns about (`SettingsForm.cs:263`). Update that message to reflect the selected quality.

### 5.5 Verification

- Switch quality, confirm the registry value changes and playback picks the matching URL
  (check `Trace` output or the cache filenames that appear).
- With HEVC selected on a machine **without** the HEVC codec, confirm Step 4's error handling
  produces a clean message rather than a black screen — this is the main reason Step 4 comes
  first.
- Confirm an asset with a missing `src` entry falls back through `FirstNonEmpty` rather than
  yielding a null URL.

---

## Suggested sequencing

| Order | Step | Why here |
| --- | --- | --- |
| 1 | **Step 2** (RegSettings null) | Hard crash on upgrade; Step 1 *is* an upgrade |
| 2 | **Step 1** (bundled catalog) | The headline fix; everything else is in service of it |
| 3 | **Step 3** (download queue) | Independent; makes the 114-video "download all" survivable |
| 4 | **Step 4** (playback errors) | Must land before Step 5 exposes codecs that may not decode |
| 5 | **Step 5** (quality selector) | Depends on `ResolveUrl` from Step 1 and the safety net from Step 4 |

Steps 2, 3 and 4 touch disjoint files and can be done in parallel if convenient. Step 5 touches
the Designer file, so do it last to avoid merge pain.

## Out of scope

Deliberately excluded — do not start these without a new decision:

- On-screen text overlays, points-of-interest captions, clock/date/location. The fork renders
  all of it by compositing video into a `<canvas>` every frame; there is no equivalent on top of
  the WMP ActiveX control. This needs WPF `MediaElement` or WebView2 first.
- Crossfades and transitions between videos — same reason (needs two players and a compositor).
- Tray-resident mode with a custom idle timer, auto-launch, blank-then-sleep, global hotkey.
  Architecturally significant; the fork abandoned being a real `.scr` to get them.
- Custom local video folders, saved profiles, sunrise/sunset by latitude/longitude.
- The remaining known defects from the code review: `EntitiesTreeView.BuildTree`'s `allChecked`
  never resets per group (`Controls/EntitiesTreeView.cs:31-46`), `getLatestReleaseURI`
  deserializing before its empty-check (`SettingsForm.cs:244`), the undisposed download-counter
  timer (`SettingsForm.cs:22`), `AssemblyVersion` using `.Hours` instead of `.TotalHours`
  (`AssemblyVersion.cs:71`), and the `SetCapture()` P/Invoke declared with no parameters
  (`NativeMethods.cs:39`).

## Do not copy these from the fork

Reviewed and found broken in `OrangeJedi/Aerial` — take the ideas, not the implementations:

- `getWakeLock()` (`app.js:1119`) returns `undefined` for admin users: it returns the value of an
  async `exec` callback, which goes nowhere. Since the idle launcher gates on it, an elevated
  user's screensaver never auto-starts.
- `downloadFile` (`app.js:783`) reads `Content-Length` only to compute a progress percentage it
  then discards, fires its callback on the *request* stream's `end` rather than the write
  stream's `finish`, and has an empty `error` handler — so a truncated file gets promoted into
  the cache as if it were good. Step 3.2 above does this correctly.
- `randomInt(min, max)` (`app.js:1134`) is `Math.floor(Math.random() * max) - min`, which is
  wrong; it only works because every caller passes `min = 0`.
- `getVideosToDownload` (`app.js:869`) uses `arr = arr.splice(i, 1)`, which assigns the *removed*
  element rather than the remainder — one entry on the never-download list collapses the whole
  queue to a single item.
