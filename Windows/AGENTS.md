# Windows/ — the plugin's main window

`MainWindow.cs` is a big `partial class MainWindow` split across `MainWindow.*.cs`. **Don't read the
whole file** — it's ~3,600 lines. Find the section's `DrawXxx` method by name (grep), then read from
there. Line numbers drift as code changes; method names are the stable anchors.

## Which file owns what
`MainWindow` is split across six files. Pick the one for your change; grep the `DrawXxx`/method
name inside it, then read that range.

| File | ~Lines | Owns |
|---|---|---|
| `MainWindow.cs` | ~1,350 | Fields, ctor, `Draw`/`PreDraw`, the Video/Watch tab UI, room create/join/host, API key, playback/transport/audio UI, status bar, legacy sync, share-code UI + pickers (`GenerateCode`/`DecodePaste`). |
| `MainWindow.Sync.cs` | ~1,330 | Sync **backend** (no UI): Snowcloak syncshell parsing, watch2gether room + payload creation, receive/broadcast/apply sync, playback-control logic. |
| `MainWindow.Render.cs` | ~970 | `DrawInWindowPreview`, world/screen render surfaces (`DrawWorldSurfaceOverlay`, `DrawScreenSurface`), native capture, renderer-process lifecycle, decode/path helpers, the nested `record struct`s. |
| `MainWindow.ScreenTab.cs` | ~730 | Screen-share tab: capture source, resolution, enhancement/upscaling. |
| `MainWindow.Settings.cs` | ~450 | Settings tab + diagnostics / renderer probe. |
| `MainWindow.StyleTab.cs` | ~190 | Style tab. |

Rule of thumb: **UI draw code → `MainWindow.cs` or a `*Tab.cs`; sync/networking logic → `Sync.cs`;
video-surface/renderer plumbing → `Render.cs`.**

## Extending
It's one `partial class`, so a new file `MainWindow.Foo.cs` (same header:
`public sealed partial class MainWindow { … }`, **no** base list) can add methods that use any field.
To carve another tab out of `MainWindow.cs`, move its contiguous `DrawXxx` methods into a new partial
file the same way — cut only between methods (each is preceded by a blank line; no attributes sit at
those boundaries).

Build/verify: see the repo-root `AGENTS.md` (SDK isn't on PATH). Aim for 0 warnings, 0 errors.
