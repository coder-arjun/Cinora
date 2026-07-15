---
name: web-push-notifications
description: Use when implementing or debugging web push in Cinora — VAPID keys, PushManager subscription flow, permission prompts, storing subscriptions on the Device entity, sending pushes from Notification events, expired-subscription cleanup, or notification click deep links.
---

# Web Push Notifications

## Overview
Push is opt-in and contextual: subscribe only after a deliberate user gesture, persist one subscription per Device, send from the server with VAPID via the WebPush library, and prune dead endpoints aggressively.

## Quick Reference
| Task | Approach |
|---|---|
| VAPID keys | Generate once; public key to the client, private key in App Service settings / user secrets |
| Permission UX | Ask after an explicit action ("Notify me" on watchlist) — never on landing or page load |
| Subscribe | `registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey })` |
| Persist | POST endpoint, `p256dh`, `auth` to the server; store on the `Device` entity per user |
| Send | WebPush NuGet library, triggered by `Notification` events (queued via Hangfire) |
| Dead subscriptions | `WebPushException` with 404/410 → delete that Device's subscription |
| Click handling | `notificationclick` opens/focuses the deep link from `data.url` |

## Pattern
```js
// wwwroot/js/push.js — call ONLY from a user gesture (e.g. "Enable notifications")
export async function enablePush(vapidPublicKey) {
  const registration = await navigator.serviceWorker.ready;

  // WHY: permission is requested here, in context, after the user clicked —
  // asking on page load gets denials that permanently block the origin
  const permission = await Notification.requestPermission();
  if (permission !== 'granted') return false;

  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true, // WHY: required by Chrome; every push must show a notification
    applicationServerKey: urlBase64ToUint8Array(vapidPublicKey),
  });

  // WHY: server stores endpoint + keys on the Device entity; without
  // persistence the subscription is lost on the next visit
  await fetch('/api/devices/push-subscription', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(subscription),
  });
  return true;
}

function urlBase64ToUint8Array(base64) {
  // WHY: applicationServerKey must be a Uint8Array, not the raw base64url string
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  const raw = atob(padded.replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}
```

The `push` and `notificationclick` handlers live in the same worker described in `.claude/skills/pwa-service-worker/SKILL.md`. Server sends run as background jobs — see `.claude/skills/hangfire-background-jobs/SKILL.md`; use SignalR for in-app realtime and push only for out-of-app reach, per `.claude/skills/signalr-realtime/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| Prompting for permission on landing | Prompt only after a user action that implies interest |
| Passing the base64 VAPID key directly | Decode to `Uint8Array` first |
| Ignoring 404/410 send failures | Delete the subscription from the Device row immediately |
| Sending from the request thread | Enqueue via Hangfire; push endpoints are slow and flaky |
| `notificationclick` always opening a new tab | `clients.matchAll` and focus an existing window first |
| One subscription per user | Store per Device — users have phones and desktops |
