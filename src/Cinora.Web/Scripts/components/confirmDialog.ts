// The `confirmDialog` component (Milestone 6.5, backlog 3.1) — the CSP-safe styled replacement for the native
// window.confirm / hx-confirm on destructive actions (delete review, delete comment, remove friend, remove
// avatar). Registered by NAME so it works under the @alpinejs/csp build (the eval-free distribution the strict
// `script-src 'self'` CSP requires): markup references only bare names (x-data="confirmDialog", x-ref="panel",
// x-on:click="cancel", x-on:keydown="onKeydown") — all logic lives here in the component body, where ordinary
// JS is allowed (the CSP restriction is on inline MARKUP expressions only).
//
// WHY a component + a module function: the dialog is a SINGLETON rendered once in _Layout (_ConfirmDialog.cshtml).
// Scripts/site.ts intercepts the HTMX `htmx:confirm` event and calls requestConfirmation(...) — the confirm merely
// GATES the exact same hx-delete/hx-post + anti-forgery request (issued via event.detail.issueRequest on confirm),
// so the endpoint/token flow is never touched. If the dialog is unavailable (partial absent / Alpine failed to
// boot), requestConfirmation falls back to window.confirm so a destructive request is NEVER issued unconfirmed.
import type { Alpine } from "@alpinejs/csp";

/** Options for a single styled confirmation, read from the triggering element's data-confirm-* attributes. */
export interface ConfirmOptions {
  title: string;
  message: string;
  action: string;
  /** "danger" (default → red confirm button) or "primary". */
  tone: string;
  /** The control that opened the dialog; focus returns to it on close. */
  trigger: HTMLElement | null;
}

interface ConfirmDialogData {
  // Alpine $refs (panel/title/message/cancel/confirm), injected at runtime by the @alpinejs/csp build.
  readonly $refs?: Record<string, HTMLElement>;

  // Per-request state — a singleton dialog holds at most one pending confirmation at a time.
  _resolve: ((confirmed: boolean) => void) | undefined;
  _trigger: HTMLElement | null;

  init(this: ConfirmDialogData): void;
  request(this: ConfirmDialogData, options: ConfirmOptions): Promise<boolean>;
  confirm(this: ConfirmDialogData): void;
  cancel(this: ConfirmDialogData): void;
  onKeydown(this: ConfirmDialogData, event: KeyboardEvent): void;
  settle(this: ConfirmDialogData, confirmed: boolean): void;
  focusables(this: ConfirmDialogData): HTMLElement[];
}

// Module-singleton reference to the initialized dialog so site.ts's htmx:confirm handler can drive it via
// requestConfirmation() without reaching into Alpine internals. Null until the component inits (or if the
// partial is absent) → requestConfirmation then falls back to window.confirm.
let instance: ConfirmDialogData | null = null;

const OVERLAY_ID = "confirm-dialog";

// A11y defense-in-depth (both gates) atop aria-modal + the Tab-trap: while the dialog is open, make the app's
// background content `inert` so an AT virtual cursor can't browse the page behind the modal. We inert the layout
// header + main only — the dialog mount (#confirm-dialog), the #toast-host and the sr-only polite status regions
// are SIBLINGS of <main> (not inside it), so they stay perceivable/announceable. Feature-guarded: `inert` is
// widely supported, but the check keeps old browsers safe (they retain the existing aria-modal + focus-trap).
const INERT_SUPPORTED = typeof HTMLElement !== "undefined" && "inert" in HTMLElement.prototype;

// The background elements to toggle `inert` on: the layout header (a direct child of <body>, so page-level
// <header>s inside <main> are never matched) and #main-content. Queried fresh each time (both are stable layout
// nodes) so set and unset always operate on the same live elements.
function backgroundRegions(): HTMLElement[] {
  const regions: HTMLElement[] = [];
  const header = document.querySelector<HTMLElement>("body > header");
  if (header !== null) {
    regions.push(header);
  }
  const main = document.getElementById("main-content");
  if (main !== null) {
    regions.push(main);
  }
  return regions;
}

function setBackgroundInert(inert: boolean): void {
  if (!INERT_SUPPORTED) {
    return;
  }
  for (const region of backgroundRegions()) {
    region.inert = inert;
  }
}

function fallbackText(options: ConfirmOptions): string {
  return options.message.length > 0 ? `${options.title}\n\n${options.message}` : options.title;
}

/** Registers the `confirmDialog` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerConfirmDialog(alpine: Alpine): void {
  alpine.data("confirmDialog", (): ConfirmDialogData => ({
    _resolve: undefined,
    _trigger: null,

    init(this: ConfirmDialogData) {
      instance = this;
    },

    request(this: ConfirmDialogData, options: ConfirmOptions): Promise<boolean> {
      const refs = this.$refs;
      const overlay = document.getElementById(OVERLAY_ID);
      if (refs === undefined || overlay === null) {
        // The styled dialog is unavailable — never proceed with a destructive request unconfirmed.
        return Promise.resolve(window.confirm(fallbackText(options)));
      }

      // Clear any stale pending confirmation (resolves it false) before starting a new one.
      this.settle(false);
      this._trigger = options.trigger;

      if (refs.title !== undefined) {
        refs.title.textContent = options.title; // textContent only — the message may carry a user display name.
      }
      if (refs.message !== undefined) {
        refs.message.textContent = options.message;
        refs.message.classList.toggle("hidden", options.message.length === 0);
      }
      if (refs.confirm !== undefined) {
        refs.confirm.textContent = options.action;
        const danger = options.tone !== "primary";
        refs.confirm.classList.toggle("btn-danger", danger);
        refs.confirm.classList.toggle("btn-primary", !danger);
      }

      overlay.classList.add("is-open");
      // Trap AT virtual-cursor traversal behind the modal (removed again in settle(), both confirm + cancel paths).
      setBackgroundInert(true);

      const promise = new Promise<boolean>((resolve) => {
        this._resolve = resolve;
      });
      // Focus the SAFE (Cancel) action: a stray Space keyup from the triggering keypress can then only cancel,
      // never confirm. Confirm is one Tab away and Esc always cancels.
      refs.cancel?.focus({ preventScroll: true });
      return promise;
    },

    confirm(this: ConfirmDialogData) {
      this.settle(true);
    },

    cancel(this: ConfirmDialogData) {
      this.settle(false);
    },

    onKeydown(this: ConfirmDialogData, event: KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        this.settle(false);
        return;
      }
      if (event.key !== "Tab") {
        return;
      }
      // Focus trap: keep Tab / Shift+Tab cycling within the dialog's focusable controls.
      const panel = this.$refs?.panel;
      const items = this.focusables();
      const first = items[0];
      const last = items[items.length - 1];
      if (panel === undefined || first === undefined || last === undefined) {
        return;
      }
      const active = document.activeElement;
      const inside = active instanceof Node && panel.contains(active);
      if (event.shiftKey) {
        if (!inside || active === first) {
          event.preventDefault();
          last.focus({ preventScroll: true });
        }
      } else if (!inside || active === last) {
        event.preventDefault();
        first.focus({ preventScroll: true });
      }
    },

    settle(this: ConfirmDialogData, confirmed: boolean) {
      const resolve = this._resolve;
      if (resolve === undefined) {
        return; // nothing pending
      }
      this._resolve = undefined;
      document.getElementById(OVERLAY_ID)?.classList.remove("is-open");
      // Un-inert the background before returning focus / issuing the confirmed request (both confirm + cancel run
      // through here). settle(false) is also the stale-clear path in request(), which then re-inerts for the new
      // dialog — so a rapid re-open nets out to inert=true.
      setBackgroundInert(false);
      // Restore focus to the control that opened the dialog (the Delete button). On confirm the ensuing HTMX
      // swap may replace it — site.ts's region focus handlers then re-home focus after that swap.
      const trigger = this._trigger;
      this._trigger = null;
      trigger?.focus({ preventScroll: true });
      resolve(confirmed);
    },

    focusables(this: ConfirmDialogData): HTMLElement[] {
      const panel = this.$refs?.panel;
      if (panel === undefined) {
        return [];
      }
      return Array.from(panel.querySelectorAll<HTMLElement>("button:not([disabled])"));
    },
  }));
}

/**
 * Opens the styled confirmation dialog and resolves true (confirm) / false (cancel/Esc/backdrop). Falls back to a
 * native window.confirm when the dialog component is unavailable, so a destructive request is never issued
 * unconfirmed.
 */
export function requestConfirmation(options: ConfirmOptions): Promise<boolean> {
  if (instance !== null) {
    return instance.request(options);
  }
  return Promise.resolve(window.confirm(fallbackText(options)));
}
