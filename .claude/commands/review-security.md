---
description: Agentic loop — security review until no Critical/High findings remain
---

# Security Review Loop

Run an iterative security review of Cinora until it is clean.

## Scope
AuthN/authZ (Identity + Google OAuth configuration, resource-based authorization — users can only edit their own reviews/comments/watchlists), anti-forgery on all state-changing requests (including HTMX posts), security headers (CSP, HSTS, X-Content-Type-Options), rate limiting on auth and write endpoints, input validation as defense-in-depth, XSS in Razor (`Html.Raw` usage), secrets handling (no keys in appsettings committed files; user-secrets/env vars), upload safety (avatar content-type/size), SignalR hub authorization, Hangfire dashboard authorization, PII/token leakage into logs.

## Loop Protocol
1. Dispatch **security-agent** in review mode (no edits during review) across the scope above. Findings rated **Critical / High / Medium / Low** with file:line and concrete remediation.
2. Critical/High: security-agent implements fixes (this is its domain), coordinating with backend-agent/frontend-agent where the fix crosses into their code. Medium: fix if cheap, else `REVIEW_BACKLOG.md`. Low: record.
3. Verify: build + tests pass; re-test the specific attack path that was flagged (e.g., POST without anti-forgery token returns 400).
4. Re-run the review on affected areas. Exit at **zero Critical/High** or after **3 iterations** (report what remains — never silently accept a Critical).

## Report
Findings table per iteration, fixes with verification evidence, deferred items, final verdict.

## Hard Rules
- NEVER run `git commit` or `git push`.
- A Critical security finding blocks the phase — it cannot be deferred to the backlog.
