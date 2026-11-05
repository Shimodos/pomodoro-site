// @ts-check
import { defineConfig } from 'astro/config';
import tailwindcss from '@tailwindcss/vite';

// Project page on GitHub Pages: https://shimodos.github.io/pomodoro-site
// For a custom domain later: set `site` to the domain and remove `base`.
export default defineConfig({
  site: 'https://shimodos.github.io',
  base: '/pomodoro-site',
  compressHTML: true,
  vite: {
    plugins: [tailwindcss()],
  },
});
