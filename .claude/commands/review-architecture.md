---
description: Agentic loop — architecture review until no Critical/High findings remain
---

# Architecture Review Loop

Run an iterative architecture review of the Cinora solution until it is clean.

## Scope
Clean Architecture layering (Domain ← Application ← Infrastructure/Web — dependencies must point inward only), project references, CQRS usage (no query logic in commands, no MediatR ceremony where a plain service is simpler), DI registrations and lifetimes, Options Pattern usage, domain logic leaking into controllers or views, duplication across layers.

## Loop Protocol
1. Dispatch **architecture-agent** (read-only for this loop) to review the current solution against the scope above. It must return findings rated **Critical / High / Medium / Low**, each with file:line and a concrete recommended fix.
2. If there are Critical or High findings: delegate fixes to the owning specialist (backend-agent, database-agent, frontend-agent). Medium findings: fix if the change is cheap and local; otherwise record in `REVIEW_BACKLOG.md`. Low: record only.
3. Verify fixes: `dotnet build` and the test suite must pass after every fix round.
4. Re-run the review on affected areas.
5. Exit when an iteration produces **zero Critical and zero High** findings, or after **3 iterations** — in that case report remaining findings honestly and stop; do not loop forever.

## Report
Findings table per iteration (severity, location, status), what was fixed, what was deferred to `REVIEW_BACKLOG.md`, final verdict.

## Hard Rules
- NEVER run `git commit` or `git push`.
- Reviewer never edits files; fixes go through the owning specialist agents.
