// The `watchlistMenu` Alpine component (Milestone 4.3) — registered by NAME so it works under the
// @alpinejs/csp build (the eval-free distribution the strict `script-src 'self'` CSP requires). Markup
// references only bare names (x-data="watchlistMenu", x-on:click="onTriggerClick",
// x-bind:aria-expanded="open", x-on:keydown="onPanelKeydown"); all logic lives here in the component body,
// where ordinary JS is allowed (the CSP restriction is on inline MARKUP expressions only).
//
// WHY a component at all: the status menu is a native <details>/<summary> disclosure that already works with
// NO JS (progressive enhancement — the real <form> + hidden anti-forgery field still post). This component
// ELEVATES that baseline to a keyboard-complete menu button AND — the load-bearing part — PORTALS the open
// panel to <body> and pins it with position:fixed. Portaling guarantees the panel's fixed containing block is
// ALWAYS the viewport: on the discovery rails the .rail-scroller (overflow-x:auto, which also clips overflow-y)
// can never crop it, and on Details the frosted+animated title panel (backdrop-filter + a filled rise-in
// transform) can no longer establish a containing block that mis-places and clips it (review-gate C1). HTMX
// still owns the writes (each option is an hx-post/hx-delete that swaps the control's own outerHTML); Alpine
// owns only open/close + positioning. Focus/announce after the swap is site.ts's job.
import type { Alpine } from "@alpinejs/csp";

// The reactive data + methods the "watchlistMenu" factory returns. Only `open` is reactive UI state (bound to
// aria-expanded); the underscore fields cache bound listeners so add/removeEventListener pair by identity.
interface WatchlistMenuData {
  open: boolean;
  // Alpine magics injected onto `this` at runtime by the @alpinejs/csp build. Declared optional + readonly so
  // the factory literal neither needs nor can provide them.
  readonly $root?: HTMLDetailsElement;
  readonly $refs?: Record<string, HTMLElement>;

  // Cached, instance-bound listeners (assigned in init) so the window/document listeners added while the menu
  // is open can be removed by identity when it closes.
  onViewportChange?: () => void;
  onDocumentPointer?: (event: Event) => void;
  onBeforeControlSwap?: (event: Event) => void;
  // A delegated click listener the component itself attaches to the panel in openMenu(). It CANNOT be an
  // x-on:click on the status buttons: the panel is portaled to <body> (out of the x-data scope), so Alpine's
  // mutation observer tears any directive off the moved node and the optimistic handler would never fire. A
  // real addEventListener on the panel node survives the move, so this is the portal-safe way to react to a pick.
  onPanelClick?: (event: Event) => void;

  init(this: WatchlistMenuData): void;
  destroy(this: WatchlistMenuData): void;
  onTriggerClick(this: WatchlistMenuData, event: Event): void;
  onTriggerKeydown(this: WatchlistMenuData, event: KeyboardEvent): void;
  onPanelKeydown(this: WatchlistMenuData, event: KeyboardEvent): void;
  openMenu(this: WatchlistMenuData, focusFirst: boolean): void;
  closeMenu(this: WatchlistMenuData, focusTrigger: boolean): void;
  positionPanel(this: WatchlistMenuData): void;
  menuItems(this: WatchlistMenuData): HTMLElement[];
}

// Keeps the fixed panel clear of the viewport edges.
const VIEWPORT_MARGIN = 8;

// Status value → the visible trigger label; kept in lockstep with the server-side statusLabel switch in
// _WatchlistControl.cshtml so the optimistic label matches what the swap re-renders.
const STATUS_LABELS: Record<string, string> = {
  PlanToWatch: "Plan to Watch",
  Watching: "Watching",
  Watched: "Watched",
};

/** Registers the `watchlistMenu` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerWatchlistMenu(alpine: Alpine): void {
  alpine.data("watchlistMenu", (): WatchlistMenuData => ({
    open: false,

    init(this: WatchlistMenuData) {
      this.open = false;
      // Bind once so the open-time listeners can be removed by identity on close.
      this.onViewportChange = () => {
        // Auto-close on scroll/resize. If a menu item still holds focus, close THROUGH the trigger so focus is
        // never dropped to <body> when the panel disappears (review-gate: Code-Low2).
        const panel = this.$refs?.panel;
        const focusWasInside = panel !== undefined && panel.contains(document.activeElement);
        this.closeMenu(focusWasInside);
      };
      this.onDocumentPointer = (event: Event) => {
        const root = this.$root;
        const panel = this.$refs?.panel;
        if (root === undefined || !this.open) {
          return;
        }
        // A pointer inside the control (opening the trigger) OR inside the portaled panel (choosing an item)
        // must NOT close it first — the panel now lives in <body>, so root.contains() alone no longer covers it.
        if (
          event.target instanceof Node &&
          (root.contains(event.target) || (panel !== undefined && panel.contains(event.target)))
        ) {
          return;
        }
        this.closeMenu(false);
      };
      this.onBeforeControlSwap = (event: Event) => {
        // The set/remove write swaps THIS control's own outerHTML. Because the panel (and its <form>) is
        // portaled to <body> while open, restore it back into the control NOW — capture phase, before HTMX
        // replaces the control and before site.ts's bubble-phase focus bookkeeping reads document.activeElement
        // — so the panel is removed WITH the control (never orphaned in <body>) and the focused menu item is
        // seen inside the control (so focus is correctly restored to the re-rendered trigger).
        const root = this.$root;
        const target = event.target;
        if (root === undefined || !this.open || !(target instanceof Node)) {
          return;
        }
        const control = root.closest("[data-watchlist-control]");
        if (control !== null && (target === control || target.contains(control) || control.contains(target))) {
          this.closeMenu(false);
        }
      };
      this.onPanelClick = (event: Event) => {
        // OPTIMISTIC UI: a status pick paints the trigger gold (+ label) and closes the menu IMMEDIATELY, before
        // the hx-post round-trip returns. WHY: the write can carry a first-touch TMDB EnsureTitleCached fetch
        // (hundreds of ms) during which the control would otherwise look untouched, so the set feels like it did
        // nothing and users refresh to confirm. We do NOT preventDefault — the form still submits to HTMX, whose
        // outerHTML swap re-asserts the authoritative state a moment later. Delegated on the panel so it also
        // covers the portaled-to-<body> case; the Remove button (no name="Status") is ignored here.
        const target = event.target;
        const button = target instanceof Element ? target.closest<HTMLButtonElement>('button[name="Status"]') : null;
        if (button === null) {
          return;
        }
        // Resolve the trigger off $root (the <details>), NOT $refs — $root is stable, and the trigger/summary is
        // never portaled, so it is always found here even while the panel lives in <body>.
        const trigger = this.$root?.querySelector<HTMLElement>("[data-wl-trigger]") ?? null;
        if (trigger !== null) {
          trigger.classList.add("is-active");
          trigger.querySelector(".wl-bookmark")?.setAttribute("fill", "currentColor");
          const labelSpan = trigger.querySelector("span"); // present only on the Details surface (icon-only on cards)
          const label = STATUS_LABELS[button.value];
          if (labelSpan !== null && label !== undefined) {
            labelSpan.textContent = label;
          }
        }
        // Reveal the gold trigger at once by HIDING the panel — but do NOT un-portal it here. Un-portaling
        // (appendChild) mid-click detaches this button as the form's submitter, so HTMX would post with no Status
        // and the server would default it to PlanToWatch. Hiding leaves the form+submitter in place, so the
        // pending submit still carries the chosen value; the real close/un-portal runs in onBeforeControlSwap
        // (kept armed because we leave `open` true) right before the write's outerHTML swap replaces the control.
        const panel = this.$refs?.panel;
        if (panel !== undefined) {
          panel.style.display = "none";
        }
      };
    },

    // Alpine calls destroy() when the root element is removed (e.g. the control's own outerHTML swap while
    // open). closeMenu() detaches every open-time listener AND returns the portaled panel into the (now
    // detached) root, so nothing leaks onto window/document and no panel is left orphaned in <body>.
    destroy(this: WatchlistMenuData) {
      this.closeMenu(false);
    },

    onTriggerClick(this: WatchlistMenuData, event: Event) {
      // Suppress the native <details> toggle — we drive open/close ourselves so we can also portal the panel out
      // of any clipping/containing-block ancestor. Keyboard activation (Enter/Space) reports detail === 0, so
      // only then jump focus into the menu; a mouse click leaves focus on the trigger.
      event.preventDefault();
      if (this.open) {
        this.closeMenu(false);
        return;
      }
      this.openMenu((event as MouseEvent).detail === 0);
    },

    onTriggerKeydown(this: WatchlistMenuData, event: KeyboardEvent) {
      switch (event.key) {
        case "ArrowDown":
          event.preventDefault();
          if (this.open) {
            this.menuItems()[0]?.focus({ preventScroll: true });
          } else {
            this.openMenu(true);
          }
          break;
        case "ArrowUp": {
          event.preventDefault();
          this.openMenu(false);
          const items = this.menuItems();
          items[items.length - 1]?.focus({ preventScroll: true });
          break;
        }
        case "Escape":
          if (this.open) {
            event.preventDefault();
            this.closeMenu(true);
          }
          break;
        default:
          break;
      }
      // Enter / Space fall through to the native activation → onTriggerClick toggles.
    },

    onPanelKeydown(this: WatchlistMenuData, event: KeyboardEvent) {
      const items = this.menuItems();
      if (items.length === 0) {
        return;
      }
      const index = items.findIndex((item) => item === document.activeElement);
      switch (event.key) {
        case "ArrowDown":
          event.preventDefault();
          items[(index + 1) % items.length]?.focus({ preventScroll: true });
          break;
        case "ArrowUp":
          event.preventDefault();
          items[(index - 1 + items.length) % items.length]?.focus({ preventScroll: true });
          break;
        case "Home":
          event.preventDefault();
          items[0]?.focus({ preventScroll: true });
          break;
        case "End":
          event.preventDefault();
          items[items.length - 1]?.focus({ preventScroll: true });
          break;
        case "Escape":
          event.preventDefault();
          this.closeMenu(true);
          break;
        case "Tab":
          // Return focus to the trigger and collapse (the menu-button pattern) rather than synchronously hiding
          // the panel out from under a focused item — the latter drops focus to <body> before the native Tab
          // default runs (review-gate: UI-L4). preventDefault keeps focus deterministically on the trigger; the
          // user tabs onward from there.
          event.preventDefault();
          this.closeMenu(true);
          break;
        default:
          break;
      }
    },

    openMenu(this: WatchlistMenuData, focusFirst: boolean) {
      const root = this.$root;
      const panel = this.$refs?.panel;
      if (root === undefined || panel === undefined) {
        return;
      }
      this.open = true;
      root.open = true; // native <details> reveals the panel; then we portal + place it.
      // PORTAL to <body>: the panel's position:fixed containing block becomes the viewport on EVERY surface,
      // so positionPanel()'s viewport-coordinate math is correct even on Details (whose frosted/animated title
      // panel would otherwise be the containing block) and no overflow:hidden ancestor can clip it.
      document.body.appendChild(panel);
      this.positionPanel();
      window.addEventListener("scroll", this.onViewportChange!, { passive: true, capture: true }); // capture → also catches the rail scroller
      window.addEventListener("resize", this.onViewportChange!, { passive: true });
      document.addEventListener("pointerdown", this.onDocumentPointer!, true);
      document.addEventListener("htmx:beforeSwap", this.onBeforeControlSwap!, true);
      panel.addEventListener("click", this.onPanelClick!); // optimistic status paint — survives the portal to <body>
      if (focusFirst) {
        this.menuItems()[0]?.focus({ preventScroll: true });
      }
    },

    closeMenu(this: WatchlistMenuData, focusTrigger: boolean) {
      const root = this.$root;
      const panel = this.$refs?.panel;
      this.open = false;
      if (root !== undefined) {
        root.open = false;
      }
      if (panel !== undefined) {
        // UN-PORTAL: return the panel to its place inside the <details> root (last child, after the <summary>)
        // so the native disclosure governs its visibility again and it is never left orphaned in <body> — this
        // also runs from destroy() during the control's own outerHTML swap, moving the panel into the detached
        // root (i.e. out of <body>).
        if (root !== undefined && panel.parentNode !== root) {
          root.appendChild(panel);
        }
        // Drop the viewport-fixed placement so the no-JS absolute fallback styles govern again.
        panel.style.position = "";
        panel.style.left = "";
        panel.style.top = "";
        panel.style.margin = "";
        panel.style.display = ""; // clear any optimistic-pick hide so a reused panel is never stuck hidden
        panel.removeEventListener("click", this.onPanelClick!); // pair the openMenu() optimistic-paint listener by identity
      }
      window.removeEventListener("scroll", this.onViewportChange!, true);
      window.removeEventListener("resize", this.onViewportChange!);
      document.removeEventListener("pointerdown", this.onDocumentPointer!, true);
      document.removeEventListener("htmx:beforeSwap", this.onBeforeControlSwap!, true);
      if (focusTrigger) {
        this.$refs?.trigger?.focus({ preventScroll: true });
      }
    },

    // Pin the panel to the viewport (its containing block after the portal): below the trigger if it fits, else
    // above; left-aligned to the trigger, flipped/clamped to stay fully on-screen.
    positionPanel(this: WatchlistMenuData) {
      const trigger = this.$refs?.trigger ?? this.$root;
      const panel = this.$refs?.panel;
      if (trigger === undefined || panel === undefined) {
        return;
      }
      panel.style.position = "fixed";
      panel.style.margin = "0";

      const rect = trigger.getBoundingClientRect();
      const panelWidth = panel.offsetWidth;
      const panelHeight = panel.offsetHeight;
      const viewportWidth = document.documentElement.clientWidth;
      const viewportHeight = document.documentElement.clientHeight;

      let left = rect.left;
      if (left + panelWidth + VIEWPORT_MARGIN > viewportWidth) {
        left = rect.right - panelWidth; // right-align to the trigger instead
      }
      left = Math.max(VIEWPORT_MARGIN, Math.min(left, viewportWidth - panelWidth - VIEWPORT_MARGIN));

      let top = rect.bottom + VIEWPORT_MARGIN;
      if (top + panelHeight + VIEWPORT_MARGIN > viewportHeight && rect.top - panelHeight - VIEWPORT_MARGIN >= VIEWPORT_MARGIN) {
        top = rect.top - panelHeight - VIEWPORT_MARGIN; // flip above when there is no room below
      }
      top = Math.max(VIEWPORT_MARGIN, Math.min(top, viewportHeight - panelHeight - VIEWPORT_MARGIN));

      panel.style.left = `${Math.round(left)}px`;
      panel.style.top = `${Math.round(top)}px`;
    },

    menuItems(this: WatchlistMenuData): HTMLElement[] {
      const panel = this.$refs?.panel;
      if (panel === undefined) {
        return [];
      }
      // The three mutually-exclusive status options are role="menuitemradio" (single-select semantics); the
      // Remove option is a plain role="menuitem". Both are arrow-key navigable.
      return Array.from(panel.querySelectorAll<HTMLElement>('[role="menuitem"], [role="menuitemradio"]'));
    },
  }));
}
