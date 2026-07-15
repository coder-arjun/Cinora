// Cinora service worker (Milestone 6.1, ADR 0019). Authored in TypeScript and bundled by build.mjs into
// wwwroot/sw.js — served from the WEB ROOT so its scope is "/" (the whole origin), with a STABLE, non-hashed
// filename so the browser's byte-comparison update check can find it.
//
// The two build-time constants below are injected by esbuild `define`s (see build.mjs), computed from the
// content-hashed wwwroot/dist/manifest.json:
//   __SW_VERSION__ — a short SHA-256 of the manifest JSON. Any asset-hash change ⇒ a new version ⇒ a new
//                    sw.js byte image ⇒ the browser detects an update and purges the old caches on activate.
//   __PRECACHE__   — the EXACT hashed asset URLs (+ icons) this build produced, precached on install.
//
// CSP: this worker and every fetch it makes are same-origin or a poster origin already admitted by the strict
// policy (img-src https://image.tmdb.org) — NO CSP change is needed (design §4). See docs/adr/0019.
export {};

// The WebWorker lib (ServiceWorkerGlobalScope, ExtendableEvent, FetchEvent, Clients…) is supplied by
// tsconfig.sw.json (`"lib": ["ES2022", "WebWorker"]`); no triple-slash reference is needed here (and one
// placed after `export {};` would be ignored by TS anyway).
declare const __SW_VERSION__: string;
declare const __PRECACHE__: readonly string[];

// `self` in a service worker is a ServiceWorkerGlobalScope; cast once (redeclaring `self` conflicts with the
// WebWorker lib's own declaration, so we alias it instead).
const sw = self as unknown as ServiceWorkerGlobalScope;

const VERSION = __SW_VERSION__;
const STATIC_CACHE = `${VERSION}-static`; // app shell + hashed assets (precached on install)
const PAGES_CACHE = `${VERSION}-pages`; // public navigations seen at runtime (network-first)
const POSTER_CACHE = `${VERSION}-posters`; // TMDB poster art (cache-first, bounded LRU)

const OFFLINE_URL = "/offline";
const POSTER_ORIGIN = "https://image.tmdb.org";
const POSTER_CACHE_LIMIT = 60; // bound the poster cache; evict oldest-first (design §2.2)

// install → precache the designed offline fallback + the exact hashed assets this build produced.
sw.addEventListener("install", (event: ExtendableEvent) => {
  event.waitUntil(
    caches.open(STATIC_CACHE).then((cache) => cache.addAll([OFFLINE_URL, ...__PRECACHE__])),
  );
});

// activate → delete every cache from a previous VERSION (purge stale deploys), then take control of open pages.
sw.addEventListener("activate", (event: ExtendableEvent) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(keys.filter((key) => !key.startsWith(VERSION)).map((key) => caches.delete(key))),
      )
      .then(() => sw.clients.claim()),
  );
});

// message → adopt a WAITING worker only on explicit user consent (site.ts posts this from the update toast).
// NEVER an unconditional skipWaiting() — an in-use page (a half-typed review) is never hijacked mid-session.
sw.addEventListener("message", (event: ExtendableMessageEvent) => {
  if (event.data === "SKIP_WAITING") {
    void sw.skipWaiting();
  }
});

// The JSON shape SendPushNotificationCommand serializes (title/body/url/tag). All fields are optional on the
// wire; the handlers below default them defensively.
interface PushMessage {
  title?: string;
  body?: string;
  url?: string;
  tag?: string;
}

// showNotification's options plus the widely-supported mobile extras the DOM lib's NotificationOptions omits
// (vibrate, renotify). Declared here so strict tsc accepts them without loosening the whole type; it stays
// assignable to NotificationOptions when passed to showNotification.
interface ExtendedNotificationOptions extends NotificationOptions {
  vibrate?: readonly number[];
  renotify?: boolean;
}

// push → show the notification (Milestone 6.2, ADR 0020 §3.5). Text is set via showNotification's title/body
// OPTIONS (PLAIN TEXT) — NEVER innerHTML: data.body carries the server-composed message, which contains a
// user-controlled display name (XSS stance §3). `tag` coalesces repeats; `data.url` rides for the click handler.
sw.addEventListener("push", (event: PushEvent) => {
  const data = (event.data?.json() ?? {}) as PushMessage;
  const title = typeof data.title === "string" && data.title.length > 0 ? data.title : "Cinora";
  const options: ExtendedNotificationOptions = {
    body: typeof data.body === "string" ? data.body : "",
    icon: "/icons/icon-192.png",
    badge: "/icons/badge.png", // a dedicated MONOCHROME badge — a colour icon renders as a grey blob in the status bar
    requireInteraction: true, // stay until dismissed; easy to miss on mobile otherwise
    vibrate: [200, 100, 200], // a short haptic buzz where supported
    data: { url: typeof data.url === "string" ? data.url : "/" },
  };
  if (typeof data.tag === "string" && data.tag.length > 0) {
    options.tag = data.tag;
    options.renotify = true; // re-alert when a newer notification replaces one carrying the same tag
  }
  event.waitUntil(sw.registration.showNotification(title, options));
});

// notificationclick → focus an existing Cinora window at the deep link, else open one (ADR 0020 §3.5). data.url
// is a same-origin RELATIVE path from NotificationDeepLink; never blindly opens a new tab.
sw.addEventListener("notificationclick", (event: NotificationEvent) => {
  event.notification.close();
  const raw = (event.notification.data as { url?: unknown } | null)?.url;
  const url = typeof raw === "string" && raw.length > 0 ? raw : "/";
  event.waitUntil(focusOrOpenWindow(url));
});

/** Focus the first open Cinora window and navigate it to the deep link; otherwise open a new one. */
async function focusOrOpenWindow(url: string): Promise<void> {
  const target = new URL(url, sw.location.origin).href;
  const clientList = await sw.clients.matchAll({ type: "window", includeUncontrolled: true });
  for (const client of clientList) {
    const windowClient = client as WindowClient;
    await windowClient.focus();
    try {
      await windowClient.navigate(target);
    } catch {
      // Some browsers reject navigate() on an uncontrolled client; focusing it is still the right outcome.
    }
    return;
  }
  await sw.clients.openWindow(target);
}

// fetch → a deliberate strategy per resource class (design §2.2 table).
sw.addEventListener("fetch", (event: FetchEvent) => {
  const req = event.request;

  // Never touch writes (reviews, likes, /push/*, auth) or anti-forgery; a WebSocket upgrade (SignalR) is not a
  // GET and passes straight through. `return` (not respondWith) lets the browser handle it normally.
  if (req.method !== "GET") {
    return;
  }

  const url = new URL(req.url);

  // TMDB posters — cache-first runtime with a bounded LRU. Caching the cross-origin (opaque) response adds NO
  // page-level CSP origin (posters already load under img-src https://image.tmdb.org).
  if (url.origin === POSTER_ORIGIN) {
    event.respondWith(posterCacheFirst(req));
    return;
  }

  // Same-origin hashed static assets + icons/fonts — cache-first (content-hashed URLs are immutable).
  if (url.origin === sw.location.origin && isHashedAsset(url.pathname)) {
    event.respondWith(cacheFirst(req, STATIC_CACHE));
    return;
  }

  // Navigations — network-first → cache fallback → /offline. Personalized (no-store) responses are refused by
  // the cache (privacy), so offline they fall through to /offline rather than leaking a prior user's page.
  if (req.mode === "navigate") {
    event.respondWith(navigateNetworkFirst(req));
    return;
  }

  // Everything else (same-origin non-navigation GETs not matched above): pass through to the network untouched.
});

/** True for the immutable, content-hashed assets and same-origin icons/fonts. */
function isHashedAsset(pathname: string): boolean {
  return (
    pathname.startsWith("/dist/") ||
    pathname.startsWith("/icons/") ||
    pathname.endsWith(".woff2") ||
    pathname.endsWith(".woff")
  );
}

/** Cache-first: serve a cached copy if present, else fetch and cache a successful response. */
async function cacheFirst(req: Request, cacheName: string): Promise<Response> {
  const cached = await caches.match(req);
  if (cached !== undefined) {
    return cached;
  }
  const response = await fetch(req);
  if (response.ok) {
    const cache = await caches.open(cacheName);
    await cache.put(req, response.clone());
  }
  return response;
}

/** Cache-first for TMDB posters, capped to POSTER_CACHE_LIMIT entries (oldest evicted first). */
async function posterCacheFirst(req: Request): Promise<Response> {
  const cache = await caches.open(POSTER_CACHE);
  const cached = await cache.match(req);
  if (cached !== undefined) {
    return cached;
  }
  const response = await fetch(req);
  // Poster <img> requests are no-cors ⇒ opaque responses (status 0, ok=false); cache those too.
  if (response.ok || response.type === "opaque") {
    await cache.put(req, response.clone());
    await trimCache(cache, POSTER_CACHE_LIMIT);
  }
  return response;
}

/** Evict the oldest entries so `cache` holds at most `limit` (Cache Storage preserves insertion order). */
async function trimCache(cache: Cache, limit: number): Promise<void> {
  const keys = await cache.keys();
  const excess = keys.length - limit;
  for (let i = 0; i < excess; i += 1) {
    const key = keys[i];
    if (key !== undefined) {
      await cache.delete(key);
    }
  }
}

/** Network-first navigation: keep server-rendered Razor fresh; cache only non-personalized public pages. */
async function navigateNetworkFirst(req: Request): Promise<Response> {
  try {
    const response = await fetch(req);
    // Privacy (design §2.3): never cache a personalized page (the app emits Cache-Control: no-store on
    // authenticated responses) so a shared-device second user can't read it offline.
    const cacheControl = response.headers.get("Cache-Control") ?? "";
    if (response.ok && !cacheControl.includes("no-store")) {
      const cache = await caches.open(PAGES_CACHE);
      await cache.put(req, response.clone());
    }
    return response;
  } catch {
    const cached = await caches.match(req);
    if (cached !== undefined) {
      return cached;
    }
    const offline = await caches.match(OFFLINE_URL);
    return offline ?? Response.error();
  }
}
