// The `languageSelect` component — reloads the Discover shell for the chosen movie language. CSP-safe for the
// @alpinejs/csp build: the markup references only the bare name (x-data="languageSelect", x-on:change="navigate").
//
// Progressive enhancement: the control lives in a real GET <form> with a <noscript> Apply button, so a no-JS user
// changes the select and submits the form. With JS, changing the select navigates immediately. Setting language
// EXPLICITLY (even to "" = Global) means the server treats it as a deliberate choice and does not fall back to the
// user's saved default.
import type { Alpine } from "@alpinejs/csp";

interface LanguageSelectData {
  navigate(this: LanguageSelectData, event: Event): void;
}

/** Registers the `languageSelect` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerLanguageSelect(alpine: Alpine): void {
  alpine.data("languageSelect", (): LanguageSelectData => ({
    navigate(this: LanguageSelectData, event: Event) {
      const select = event.target as HTMLSelectElement | null;
      if (select === null) {
        return;
      }
      const url = new URL(window.location.href);
      url.searchParams.set("language", select.value);
      window.location.assign(`${url.pathname}?${url.searchParams.toString()}`);
    },
  }));
}
