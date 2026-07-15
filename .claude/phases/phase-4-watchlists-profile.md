# Phase 4 — Watchlists & Profile

## Goal
Personal curation: watchlists that work everywhere in the app, and a polished profile with avatars.

## Prerequisites
Phase 3 exit criteria verified (social core live).

## In Scope
- Watchlist: add/remove from any title surface (details, rails, search results), statuses (Plan to Watch / Watching / Watched), watchlist page with filtering and sorting.
- Watched ↔ review nudge: marking Watched offers the review flow.
- Profile: avatar upload to Azure Blob Storage (content-type/size validation, SAS delivery), profile editing, public profile view (reviews, watchlist highlights, friends).
- Account settings: notification preferences (feeds Phase 6 push opt-in), privacy basics (public/friends-only profile).
- Empty states and counts everywhere watchlist status appears.

## Out of Scope
AI recommendations, push notifications, offline.

## Agents
backend-agent, database-agent (watchlist indexes/status enum), frontend-agent, security-agent (upload safety), testing-agent, ux-agent, documentation-agent.

## Skills
azure-blob-storage, ef-core-data-access, cqrs-mediatr, fluent-validation, alpine-htmx-interactivity, razor-views, premium-ui-design, responsive-accessibility, security-hardening.

## Review Gates
/review-architecture, /review-code, /review-security (upload path), /review-performance (watchlist queries), /review-ui.

## Exit Criteria
- Watchlist toggling works from every title surface with instant UI feedback and correct persistence.
- Avatar upload validates type/size, stores in Blob (Azurite locally), and renders via time-limited URLs.
- Profile and settings pages complete and responsive; privacy setting respected in queries.
- All tests pass; zero Critical/High findings open.
