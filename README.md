# Pomodoro — website

Landing page for [Pomodoro](https://github.com/Shimodos/PomodoroTimer), a native Pomodoro timer for Windows.

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

## Promo clip

`public/promo.mp4` (1080×1350, 30 fps, ~9 s, no sound) is rendered the same way by
[`tools/promo`](tools/promo): the app is captured in a few states (focus 00:04 → 00:01, the break
auto-starting at 05:00, compact mode) and every frame is composed offline — gradient backdrop, shadow,
a slow push-in, a drawn cursor that switches the window to compact mode. ffmpeg encodes the frames.

```bash
cd tools/promo
dotnet run -c Release -- ../../public/promo.mp4 -ffmpeg "C:\path\to\ffmpeg.exe"
```

`-frames <dir>` additionally dumps a PNG every half second for checking. Needs ffmpeg (`winget install Gyan.FFmpeg`).

## License

MIT
