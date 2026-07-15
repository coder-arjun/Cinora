// The `accountMenu` Alpine component — the signed-in user's avatar dropdown in the top bar. It groups the user's
// name, Settings and Log out into one professional menu instead of three loose top-bar controls. CSP-safe under
// the @alpinejs/csp build: the markup references only bare names (x-on:click="toggle", x-bind:aria-expanded="open",
// x-show="open"), and all logic lives here. The panel is absolutely positioned within the trigger's relative
// wrapper (no portal needed), closes on outside-click and Escape.
import type { Alpine } from "@alpinejs/csp";

// The reactive state + methods the "accountMenu" factory returns. Only `open` is reactive UI state.
interface AccountMenuData {
  open: boolean;
  toggle(this: AccountMenuData): void;
  close(this: AccountMenuData): void;
}

/** Registers the `accountMenu` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerAccountMenu(alpine: Alpine): void {
  alpine.data("accountMenu", (): AccountMenuData => ({
    open: false,

    toggle(this: AccountMenuData) {
      this.open = !this.open;
    },

    close(this: AccountMenuData) {
      this.open = false;
    },
  }));
}
