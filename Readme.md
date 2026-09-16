<p align="center">
<img align="center" alt="Aerial screen saver playing an Apple TV aerial video" src="imgs/win.gif" />
</p>

# Aerial for Windows (andts fork)

A Windows screen saver that plays the Apple TV aerial videos: cities, landscapes, underwater and
views from space.

This is a fork of [cDima/Aerial](https://github.com/cDima/Aerial), which is no longer maintained.
Upstream stopped working when Apple retired the video feed it depended on. This fork keeps it a
small, native `.scr` and brings it back to life:

- **A current video catalog is built in**: 114 videos, each in three encodings, streamed from
  Apple's servers. The catalog comes from [OrangeJedi/Aerial](https://github.com/OrangeJedi/Aerial)
  (MIT), an actively maintained Electron rewrite. Use that project if you want a
  feature-rich app instead of a classic screen saver.
- **Playback moved from the Windows Media Player ActiveX control to WPF `MediaElement`.**
  Crossfades between clips became possible, and a dead video now gets skipped instead of hanging
  the screen saver.
- **Video quality setting**: 1080p H.264, 1080p HEVC or 4K HEVC.

Aerial for Windows was originally created by [Dmitry Sadakov](https://github.com/cDima). It is based
on the [Mac Aerial screen saver](https://github.com/JohnCoates/Aerial) by
[John Coates](https://github.com/JohnCoates).

## Requirements

- **Windows 10 (1903 or later) or Windows 11.** .NET Framework 4.8 ships with these, so there is
  nothing extra to install.
- **An internet connection**, unless the videos you play are already cached.
- **For HEVC quality settings, an HEVC decoder of the same bitness as Windows**, which on 64-bit
  Windows means a 64-bit decoder. Either Microsoft's
  [HEVC Video Extensions](https://apps.microsoft.com/detail/9nmzlz57r3t7) or a codec pack such as
  K-Lite with LAV Filters. The K-Lite setup is tested; the Microsoft extension is not yet. The
  default 1080p H.264 setting needs no extra codec.

## Installation

There are no prebuilt releases for this fork yet, so build it from source:

1. Open `ScreenSaver.sln` in Visual Studio with the **.NET desktop development** workload and the
   .NET Framework 4.8 targeting pack (tested with Visual Studio 2026), or build from a Developer
   Command Prompt:
   ```bat
   msbuild ScreenSaver.sln /p:Configuration=Release
   ```
2. The build produces `ScreenSaver\bin\Release\Aerial.scr` along with `Aerial.scr.config`.
3. Copy both files to `C:\Windows`. Then right-click `Aerial.scr` and choose **Install**, or pick
   **Aerial** under *Settings → Personalization → Lock screen → Screen saver*.

To uninstall, delete `Aerial.scr` and `Aerial.scr.config` from `C:\Windows`. Settings are stored
in `HKCU\Software\AerialScreenSaver`, and cached videos live in `%LOCALAPPDATA%\Aerial`.

## Usage

Run the file with an argument, following the standard Windows screen saver conventions:

| Command | Mode |
|---|---|
| `Aerial.scr` or `Aerial.scr /c` | Settings dialog |
| `Aerial.scr /s` | Full-screen screen saver |
| `Aerial.scr /p <hwnd>` | Small preview inside Windows' Screen Saver Settings |
| `Aerial.exe` or `Aerial.scr /w` | Resizable window. Drag to move, drag the edges to resize, and use the ⚙ / ✖ buttons in the top-right corner |

While playing, press **N** to skip to the next video on every monitor. Any other key, a click or
moving the mouse exits the screen saver.

## Settings

- **Chosen videos**: pick individual videos, with a live preview. If nothing is ticked, all
  videos play. With *Prioritize current time of day* on, day or night clips are played first
  depending on the time.
- **Multiple monitors**:
  - main screen only (other screens stay black)
  - the same video on each screen
  - a different video on each screen
  - one video spanning all screens

  Mixed-DPI monitor setups are supported.
- **Cache**: saves videos to disk as they play, or downloads the whole catalog. The cache folder
  can be moved.
- **Video Source**:
  - **Video quality**: 1080p H.264, 1080p HEVC or 4K HEVC.
  - **Software rendering**: see Troubleshooting.
  - **Custom catalog URL**: leave it empty to use the built-in catalog.

## Video quality: which to pick

For the same clip, Apple's files are about 5.8 Mbps for 1080p H.264, about 4 Mbps for 1080p HEVC
and about 7.8 Mbps for 4K HEVC. All three are SDR. In practice:

- **1080p H.264**: the most compatible option. It needs no extra codec.
- **1080p HEVC**: looks about the same as H.264, but the files are roughly a third smaller. Worth
  it if you cache videos.
- **4K HEVC**: a visibly sharper picture on 4K displays. It uses more CPU; the crossfade between
  clips is turned off at this setting to limit decoding work.

## Troubleshooting

> Black screen or "Aerial could not play any videos" with an HEVC quality selected

No 64-bit HEVC decoder is installed. Install one (see Requirements) or switch back to
1080p (H.264).

> Video freezes for a few seconds right after a clip starts, on one monitor only

This is a quirk of WPF's hardware video path on some multi-monitor setups. It has been seen on a
60 Hz secondary monitor next to a high-refresh primary. Enable **Use software rendering** on the
Video Source tab. It fixes the freeze but uses noticeably more CPU, and it turns crossfades off.

> Where are the videos stored?

They stream from `sylvan.apple.com`. If caching is on, they are saved to `%LOCALAPPDATA%\Aerial`,
or to the folder chosen in Settings → Cache.

> Anti-virus flags the `.scr` file

`.scr` files are ordinary executables, and some anti-virus tools are suspicious of them. This is a
[long-standing false positive](https://github.com/cDima/Aerial/issues/9) for this project. Since
there are no signed releases, building from source lets you verify exactly what you run.

## Known limitations

- **Videos stream over plain HTTP.** WPF on .NET Framework cannot stream `https` media at all,
  so the player downgrades Apple's `https` links. Cache downloads still use HTTPS.
- **Two items from `PORTING-PLAN.md` are not implemented:**
  - Step 2: some registry values have no fallback when an old install lacks them.
  - Step 3: "Download all" starts every download at once instead of queueing them. Moving the
    cache folder to a drive without enough free space can delete the existing cache.

## Development

- **Stack**: C# on .NET Framework 4.8. The settings dialog is WinForms; playback is WPF, built in
  code with no XAML. The project uses an old-style (non-SDK) `.csproj`.
- **Where the logic lives**: playlist and failure handling are in
  `ScreenSaver/PlaybackController.cs`, which uses no UI types. `ScreenSaver/VideoSurface.cs` and
  `ScreenSaver/ScreenSaverWindow.cs` are thin views on top of it.
- **Tests**: `tools/verify/run-tests.sh` compiles and runs the UI-free tests under Mono, so that
  logic can be checked without Windows.
- **Catalog**: `tools/convert_videos.py` regenerates `ScreenSaver/Videos.json` from OrangeJedi's
  catalog. See `tools/README.md`.
- **Background**: `PORTING-PLAN.md` is the original plan behind this fork's changes.

## License

[MIT License](https://raw.githubusercontent.com/JohnCoates/Aerial/master/LICENSE), as in the
upstream projects. The video catalog is derived from
[OrangeJedi/Aerial](https://github.com/OrangeJedi/Aerial) (MIT). The videos themselves belong to
Apple.
