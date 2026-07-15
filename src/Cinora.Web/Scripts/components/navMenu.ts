// The `navMenu` component — the responsive top-bar SIDE DRAWER (a right-side slide-over) for small screens,
// modelled on the DailyPilot / ExpenseTracker mobile drawer. CSP-safe for the @alpinejs/csp build: the markup uses
// only bare names (x-data="navMenu", x-show="open", x-on:click="toggle"/"close", x-on:keydown.escape.window="close",
// x-bind:aria-expanded="expanded"). x-show toggles the panel + backdrop (the reliable, proven directive in this
// build); x-transition slides/fades them. This component only holds the drawer's open state and locks body scroll
// while it is open.
//
// Progressive enhancement: the desktop nav is plain CSS (md:flex) and always works; with no JS the hamburger does
// nothing and the drawer stays hidden (x-cloak).
import type { Alpine } from "@alpinejs/csp";

interface NavMenuData {
  open: boolean;
  // aria-expanded is a string attribute ("true"/"false"), bound via x-bind so it stays in sync with `open`.
  expanded: string;
  toggle(this: NavMenuData): void;
  close(this: NavMenuData): void;
}

/** Registers the `navMenu` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerNavMenu(alpine: Alpine): void {
  alpine.data("navMenu", (): NavMenuData => ({
    open: false,
    expanded: "false",
    toggle(this: NavMenuData) {
      this.open = !this.open;
      this.expanded = this.open ? "true" : "false";
      // Lock body scroll while the slide-over is open (matches the drawer pattern).
      document.body.classList.toggle("overflow-hidden", this.open);
    },
    close(this: NavMenuData) {
      this.open = false;
      this.expanded = "false";
      document.body.classList.remove("overflow-hidden");
    },
  }));
}
