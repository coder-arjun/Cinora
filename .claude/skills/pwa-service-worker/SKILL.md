---
name: pwa-service-worker
description: Use when adding or debugging Cinora's PWA layer — web app manifest questions, service worker lifecycle or caching issues, offline support, stale assets after deploy, or rolling out a new service worker version to clients.
---

# PWA & Service Worker

## Overview
Cinora installs like a native app and degrades gracefully offline; the service worker is a versioned, explicit cache proxy with a deliberate strategy per resource type — never a transparent cache-everything layer.

## Quick Reference
| Task | Approach |
|---|---|
| Manifest | `name`, `short_name`, 192/512 icons (+ maskable), `display: "standalone"`, dark `theme_color`/`background_color` |
| Registration | `navigator.serviceWorker.register('/sw.js')` in `_Layout.cshtml`; serve `sw.js` from wwwroot root so scope covers `/` |
| Static assets & posters | Cache-first (immutable, versioned URLs) |
| HTML navigations | Network-first, cache fallback, then `/offline` page |
| Authenticated POSTs | Never cached — bypass the fetch handler entirely |
| Updates | Bump cache version, prompt user, `SKIP_WAITING` message, reload on `controllerchange` |

## Pattern
```js
// wwwroot/sw.js
const VERSION = 'cinora-v42'; // WHY: bump per deploy so activate can purge old caches
const STATIC = `${VERSION}-static`;
const PAGES = `${VERSION}-pages`;

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(STATIC).then((c) =>
    c.addAll(['/offline', '/css/app.css', '/js/app.js', '/img/logo.svg'])));
});

self.addEventListener('activate', (e) => {
  // WHY: delete caches from previous versions or storage grows unbounded
  e.waitUntil(caches.keys().then((keys) => Promise.all(
    keys.filter((k) => !k.startsWith(VERSION)).map((k) => caches.delete(k)))));
});

// WHY: skipWaiting only on user consent — a "New version available" prompt —
// so an in-use page is never hijacked mid-session
self.addEventListener('message', (e) => {
  if (e.data === 'SKIP_WAITING') self.skipWaiting();
});

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return; // WHY: never cache POSTs (reviews, auth)

  if (req.destination === 'image' || req.destination === 'style' || req.destination === 'script') {
    e.respondWith(caches.match(req).then((hit) => hit ?? fetch(req).then((res) => {
      const copy = res.clone();
      caches.open(STATIC).then((c) => c.put(req, copy));
      return res;
    })));
    return;
  }

  if (req.mode === 'navigate') {
    // WHY: network-first keeps server-rendered Razor HTML fresh; cache is fallback
    e.respondWith(fetch(req)
      .then((res) => {
        const copy = res.clone();
        caches.open(PAGES).then((c) => c.put(req, copy));
        return res;
      })
      .catch(async () => (await caches.match(req)) ?? caches.match('/offline')));
  }
});
```

Push handling lives in this same worker — see `.claude/skills/web-push-notifications/SKILL.md`. Build tooling for `sw.js` follows `.claude/skills/typescript-frontend/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Serving `sw.js` from `/js/` | Scope is limited to `/js/`; serve from site root |
| Caching authenticated HTML for all users | Network-first navigations; never cache-first personalized pages |
| Unconditional `skipWaiting()` | Prompt the user, then message the waiting worker |
| No cache cleanup in `activate` | Delete non-current version caches |
| Testing only with "Update on reload" | Verify the real waiting/prompt flow in a normal tab |
