# Phase 3 — Reviews & Social

## Goal
The social core: users write reviews, react to each other, build a friend graph, and see a live activity feed.

## Prerequisites
Phase 2 exit criteria verified (catalog + details pages live).

## In Scope
- Reviews: create/edit/delete own review per title (one per user+movie, enforced by unique index), rating 1–10, rich text body with sanitization.
- Likes on reviews (ReviewLike), comments on reviews (Comment) — HTMX partial updates, optimistic UI where safe.
- Friends: request / accept / decline / remove (Friend entity), user search, profiles showing recent activity.
- Activity feed: friends' reviews/likes/comments, cursor-paginated, cached hot path.
- In-app notifications (Notification entity) over SignalR: friend requests, likes, comments; notification center per app flow.
- Resource-based authorization: only owners edit their content.

## Out of Scope
Push notifications (Phase 6), AI recommendations (Phase 5). **In-app chat appears in the PRD but in no schema or plan — get an explicit product decision from the user before scoping it here.**

## Agents
architecture-agent (feed + notification design first), backend-agent, database-agent (indexes for feed queries), frontend-agent, security-agent (authZ policies, sanitization), performance-agent (feed queries/caching), testing-agent, ux-agent, documentation-agent.

## Skills
cqrs-mediatr, fluent-validation, ef-core-data-access, signalr-realtime, redis-caching, security-hardening, alpine-htmx-interactivity, razor-views, ui-animations, dotnet-performance.

## Review Gates
All five loops: /review-architecture, /review-code, /review-security (authZ + XSS focus), /review-performance (feed), /review-ui.

## Exit Criteria
- Full review lifecycle works with validation (rating 1–10) and ownership enforcement.
- Likes/comments update without full page reload; feed paginates smoothly.
- Friend flow complete; notifications arrive in real time via SignalR and persist in the notification center.
- All tests (including integration tests for authZ) pass; zero Critical/High findings open.
