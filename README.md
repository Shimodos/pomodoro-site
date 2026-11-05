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

## License

MIT
