// The `commentCollapse` component (Milestone 6.5, backlog 3.2) — a one-way "Show more" for a long comment body.
// Registered by NAME for the @alpinejs/csp build: markup references only bare names (x-data="commentCollapse",
// x-ref="body", x-ref="toggle", x-on:click="expand") — all logic lives here in the component body.
//
// Progressive enhancement: the clamp CSS (.comment-body.is-clamped) is JS-GATED (html.js in app.css), so a
// reader with no JS always sees the FULL body — the toggle cannot work without JS, so it must never hide text.
// The toggle button ships hidden; init() reveals it ONLY when the clamped body is genuinely truncated on this
// viewport (a short-but-long-char comment that already fits keeps no control). expand() drops the clamp for good
// and moves focus onto the now-full text. Reduced-motion safe by construction (the reveal is instant — no
// height animation to collapse).
import type { Alpine } from "@alpinejs/csp";

interface CommentCollapseData {
  // Alpine $refs (body/toggle), injected at runtime by the @alpinejs/csp build.
  readonly $refs?: Record<string, HTMLElement>;
  init(this: CommentCollapseData): void;
  expand(this: CommentCollapseData): void;
}

// Announce a concise, polite cue into the established scoped status node — the same #reviews-status site.ts writes
// its comment add/delete announcements to (with the home-feed's #feed-status as the fallback wherever comments
// render outside the reviews section). textContent only (CSP-safe); a no-op when neither node is present.
function announceComment(message: string): void {
  const status =
    document.getElementById("reviews-status") ?? document.getElementById("feed-status");
  if (status !== null && message.length > 0) {
    status.textContent = message;
  }
}

/** Registers the `commentCollapse` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerCommentCollapse(alpine: Alpine): void {
  alpine.data("commentCollapse", (): CommentCollapseData => ({
    init(this: CommentCollapseData) {
      const body = this.$refs?.body;
      if (body === undefined) {
        return;
      }
      // If the clamped body is not actually overflowing on this viewport, there is nothing to reveal — drop the
      // clamp and keep the control hidden so "Show more" never appears on a comment that already fits.
      if (body.scrollHeight <= body.clientHeight + 1) {
        body.classList.remove("is-clamped");
        return;
      }
      this.$refs?.toggle?.classList.remove("hidden");
    },

    expand(this: CommentCollapseData) {
      const body = this.$refs?.body;
      const toggle = this.$refs?.toggle;
      if (body !== undefined) {
        body.classList.remove("is-clamped");
        body.setAttribute("tabindex", "-1");
        body.focus({ preventScroll: true }); // land SR/keyboard focus on the now-full text
      }
      if (toggle !== undefined) {
        toggle.setAttribute("aria-expanded", "true");
        toggle.classList.add("hidden"); // one-way: the control is spent
      }
      // Belt-and-suspenders (UI L2): unlike a comment add/delete, an expand only moves focus onto the now-full
      // <p> — so on the minority of SRs that don't announce a focused paragraph it would otherwise be silent. A
      // brief polite cue makes every action announce consistently. Keeps the focus-to-paragraph behaviour above.
      announceComment("Comment expanded");
    },
  }));
}
