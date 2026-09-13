# tools/

## convert_videos.py

Regenerates `ScreenSaver/Videos.json`, the video catalog bundled into the app as an embedded
resource (see `AerialContext.GetAllEntries` / `LoadBundledEntries` in `ScreenSaver/AerialEntities.cs`).

Source data is `videos.json` from the root of the `OrangeJedi/Aerial` fork
(https://github.com/OrangeJedi/aerial, MIT licensed), which maintains a hand-curated catalog of
Apple TV aerial videos hosted on `sylvan.apple.com`.

To refresh the bundled catalog after that file has been updated upstream:

```bash
git clone --depth 1 https://github.com/OrangeJedi/aerial /tmp/orangejedi-aerial
python3 tools/convert_videos.py /tmp/orangejedi-aerial/videos.json ScreenSaver/Videos.json
```

The script prints a summary (asset count, `timeOfDay`/`category` breakdown, any skipped/empty
entries) to stderr/stdout - compare it against the previous run before committing a refresh.

See `PORTING-PLAN.md` (Step 1) for the full rationale and the expected output as of the 2026
refresh (114 assets).
