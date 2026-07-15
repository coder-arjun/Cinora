---
description: Agentic loop — code review until no Critical/High findings remain
---

# Code Review Loop

Run an iterative code-quality review of recently changed Cinora code until it is clean.

## Scope
Correctness bugs, SOLID/DRY violations, Microsoft naming conventions, async correctness (sync-over-async, missing `CancellationToken` propagation, unawaited tasks), missing FluentValidation coverage, missing error handling, entities passed to views instead of view models, dead code and duplication.

## Loop Protocol
1. Determine the review target: files changed in the current milestone (or the area the user names).
2. Dispatch **code-review-agent** (read-only) on the target. It must return findings rated **Critical / High / Medium / Low**, each with file:line, a one-line rationale, and a concrete fix.
3. If there are Critical or High findings: delegate fixes to the owning specialist (backend-agent, frontend-agent, database-agent). Medium: fix if cheap and local, else record in `REVIEW_BACKLOG.md`. Low: record only.
4. Verify: `dotnet build` and all tests pass after each fix round.
5. Re-run the review on the changed files. Exit when an iteration yields **zero Critical and zero High** findings, or after **3 iterations** (report remaining findings honestly).

## Report
Findings table per iteration (severity, location, status), fixes applied, deferred items, final verdict.

## Hard Rules
- NEVER run `git commit` or `git push`.
- code-review-agent never edits files — findings only.
