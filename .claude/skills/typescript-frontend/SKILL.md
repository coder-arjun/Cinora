---
name: typescript-frontend
description: Use when writing or bundling TypeScript for Cinora — esbuild setup, tsconfig strictness, per-page module wiring, DOM typing errors, source map handling, or integrating scripts with Razor pages and Alpine.
---

# TypeScript Frontend

## Overview
Small, strict TypeScript modules bundled by esbuild into `wwwroot/js` and initialized per page — progressive enhancement over server-rendered Razor, never an SPA.

## Quick Reference
| Task | Approach |
|---|---|
| tsconfig | `"strict": true`, `"noUncheckedIndexedAccess": true`, `"module": "ESNext"` |
| Bundling | esbuild → `wwwroot/js/app.js`; no webpack, no framework CLI |
| Dev script | `esbuild src/main.ts --bundle --outdir=wwwroot/js --sourcemap --watch` |
| Publish script | Same command with `--minify` and without `--sourcemap` |
| Page code | One module per page, dispatched from `data-page` on `<body>` |
| DOM lookups | `querySelector<HTMLButtonElement>(...)` + null check; never `!` |
| Alpine interop | Register components via `Alpine.data(...)` in one entry file |
| jQuery | Never — `fetch`, `closest()`, `dataset` cover everything |

## Pattern
```typescript
// src/main.ts — tiny page router: each page runs only the init it needs.
import { initSearch } from "./pages/search";
import { initMovieDetails } from "./pages/movie-details";
import { initFeed } from "./pages/feed";

// WHY: Razor renders <body data-page="search">, so routing stays
// server-driven and no page module executes where it doesn't belong.
const pages: Record<string, () => void> = {
  search: initSearch,
  "movie-details": initMovieDetails,
  feed: initFeed,
};

document.addEventListener("DOMContentLoaded", () => {
  const page = document.body.dataset.page; // WHY: typed string | undefined — the compiler forces the guard
  if (page) pages[page]?.(); // WHY: noUncheckedIndexedAccess makes the lookup possibly undefined — ?. handles it
});
```

## Common Mistakes
| Mistake | Fix |
|---|---|
| `document.querySelector(...)!` non-null assertions | Guard for null — elements legitimately differ per page |
| One growing `site.ts` for everything | Module per page plus a shared `lib/` for utilities |
| Source maps shipped to production | Keep `--sourcemap` in the dev script only |
| `any` for fetched JSON | Declare response interfaces; validate at the boundary |
| Re-implementing dropdown/modal state in TS | Alpine owns local UI state; TS handles the rest |
| Pulling in jQuery for one helper | Native DOM APIs |

For the Alpine/HTMX division of labor see `.claude/skills/alpine-htmx-interactivity/SKILL.md`; service worker registration belongs with `.claude/skills/pwa-service-worker/SKILL.md`.
