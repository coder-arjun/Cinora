---
description: Execute one implementation phase end-to-end with review gates
argument-hint: <phase number 1-6>
---

# Run Phase $ARGUMENTS

Execute phase $ARGUMENTS of the Cinora implementation plan, end to end.

## Steps
1. Read the matching phase file in `.claude/phases/` (phase-$ARGUMENTS-*.md). If $ARGUMENTS is empty, determine the next incomplete phase from `PROGRESS.md` and confirm it with the user before starting.
2. Verify the previous phase's **Exit Criteria** are actually met (run the checks — don't trust the log). If they are not, stop and report what's missing instead of proceeding.
3. Break the phase's deliverables into milestones. Order them by dependency; identify which can run in parallel.
4. For each milestone: have architecture-agent validate the design first when it introduces new structure, then delegate implementation to the specialist agents named in the phase file. Every agent must return its six-section report (Analysis, Recommendations, Implementation, Validation, Risks, Next Steps).
5. After each milestone, validate: `dotnet build` succeeds, all tests pass, acceptance criteria met. Reject and re-delegate on failure.
6. When all deliverables are complete, run the phase's **Review Gates** — the automatic review loops: `/review-architecture`, `/review-code`, `/review-security`, `/review-performance`, and `/review-ui` if the phase touched UI. Fix and loop per each loop's protocol.
7. Confirm every Exit Criterion in the phase file with a real command or observation. List each criterion with its evidence.
8. Update `PROGRESS.md` (phase, milestones, dates, deviations) via documentation-agent.

## Report
End with: Completed Tasks · Files Modified · Reasoning · Potential Improvements · Next Milestone · Current Project Health · Confidence Score.

## Hard Rules
- NEVER run `git commit` or `git push`. The human handles all version control.
- A phase is not complete while any Critical or High review finding remains open.
- Never claim an exit criterion is met without evidence from a real command.
