# Pomodoro — website

Landing page for [Pomodoro](https://github.com/Shimodos/PomodoroTimer), a native Pomodoro timer for Windows and macOS.

Live: https://shimodos.github.io/pomodoro-site/

## Stack

- [Astro](https://astro.build) 7, static output, one page.
- [Tailwind CSS](https://tailwindcss.com) 4 through the Vite plugin.
- Self-hosted Inter (`@fontsource-variable/inter`).
- No other dependencies, no client framework. The only scripts on the page are the theme toggle and a
  `fetch` to the GitHub API that points the download buttons at the latest release.

## Develop

```bash
npm install
npm run dev        # http://localhost:4321/pomodoro-site/
npm run build      # static site in dist/
npm run preview
```

## Deploy

[`.github/workflows/deploy.yml`](.github/workflows/deploy.yml) builds the site and publishes it to GitHub Pages on
every push to `main`. The repository needs **Settings → Pages → Source: GitHub Actions** once.

`site` and `base` live in [`astro.config.mjs`](astro.config.mjs). For a custom domain set `site` to the domain and
remove `base`.

## Screenshots

The app screenshots in `src/assets/shots/` and the `public/og.png` card are rendered, not captured:
[`tools/snapshot`](tools/snapshot) loads the real app (the sibling `../PomodoroTimer` checkout) in-process, puts it
into a few typical states and renders every window through WPF at 2x, so they stay crisp on any display.

```bash
cd tools/snapshot
dotnet run -c Release -- ../../src/assets/shots 2
move ..\..\src\assets\shots\og.png ..\..\public\og.png
```

Requires the .NET 8 SDK and the app repository checked out next to this one.

The macOS screenshots (`src/assets/shots/mac-*.png`) are real window captures of the Mac build
(`macos/build/Pomodoro.app` in the app repository, light and dark theme, `POMODORO_DATA_DIR` pointed at a
scratch folder) taken with `screencapture -l<window id>` at 2x.

## Promo clips

The two clips in `public/` (1080×1350, 30 fps, no sound) are rendered the same way by
[`tools/promo`](tools/promo): the app is captured in a few states and every frame is composed
offline — gradient backdrop, shadow, a slow push-in, a drawn cursor. ffmpeg encodes the frames.

- `promo.mp4` (~9 s): focus 00:04 → 00:01, the break auto-starts at 05:00 in its own color, the cursor
  switches the window to compact mode.
- `promo-settings.mp4` (~13 s): the settings dialog — the focus slider is dragged, the Sound page, the dark
  theme and a violet accent are picked (live preview), "keep window on top" is switched on, Save closes the
  dialog and the main window appears in its new look.

```bash
cd tools/promo
dotnet run -c Release -- ../../public/promo.mp4 -clip main -ffmpeg "C:\path\to\ffmpeg.exe"
dotnet run -c Release -- ../../public/promo-settings.mp4 -clip settings -ffmpeg "C:\path\to\ffmpeg.exe"
```

`-frames <dir>` additionally dumps a PNG every half second for checking. Needs ffmpeg (`winget install Gyan.FFmpeg`).

## macOS promo

[`tools/promo-mac`](tools/promo-mac) renders the macOS announcement images and clip from real window
captures of the Mac build (Swift + AppKit + AVFoundation, no ffmpeg; needs the Command Line Tools):

```bash
cd tools/promo-mac && ./build.sh
./bin/stills <captures-dir> <out-dir>      # 1-hero, 2-colors, 3-settings, 4-installer (1600×900)
./bin/clip <frames-dir> ../../public/promo-mac.mp4   # 1080×1350, 30 fps, ~12 s
```

Captures: `focus-running-light`, `compact-break-dark`, `break-running-dark`, `break-view-light`,
`settings-timer-dark-win-Settings`, `dmg-installer` for the stills; `f5…f1` (focus per second), `b0…b2`
(break), `c0…c2` (compact) for the clip. Taken with `screencapture -l<window id>` while the app runs with
`POMODORO_DATA_DIR` pointed at a scratch folder.

## License

MIT
