// The `search` Alpine component (Milestone 2.4) — registered by NAME so it works under the @alpinejs/csp
// build (the eval-free distribution required by the strict `script-src 'self'` CSP). Markup references only
// bare names (x-data="search", x-model="query", x-on:input.debounce.300ms="onInput", x-show="loading",
// x-bind:aria-busy="loading"); all logic lives here, in the component body, where ordinary JS is allowed
// (the CSP restriction is on inline MARKUP expressions only, not on registered component code).
//
// Division of labour (see .claude/skills/alpine-htmx-interactivity): Alpine owns the local input state and
// the debounce; the actual server round-trip is fired through HTMX (window.htmx.ajax) so it flows through
// the same request pipeline (and anti-forgery wiring) as the rest of the app — no bespoke fetch, no hx-on.
import type { Alpine } from "@alpinejs/csp";

// Shape of the reactive data object the "search" factory returns. `onInput` is a method (declares its own
// `this`) so the debounced x-on handler can read `query`/set `loading` without an inline expression.
interface SearchData {
  query: string;
  loading: boolean;
  // Alpine magic ($root = the x-data root element), injected onto `this` at runtime by the @alpinejs/csp
  // build. Declared optional + readonly so the factory literal below neither needs nor can provide it.
  readonly $root?: HTMLElement;
  init(this: SearchData): void;
  onInput(this: SearchData): void;
}

// Search-as-you-type threshold: below this we do NOT fire a request. MUST stay in sync with
// DiscoveryController.MinQueryLength (the server guards the same floor before dispatching).
const MIN_LENGTH = 2;

/** Registers the `search` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerSearch(alpine: Alpine): void {
  alpine.data("search", (): SearchData => ({
    query: "",
    loading: false,

    // Seed the box from the server-rendered term on the x-data root (data-query) so a deep link, a shared
    // URL, or a no-JS Enter-reload keeps its query visible. Alpine calls init() as part of x-data setup —
    // BEFORE x-model binds — so x-model then reflects this seeded value straight back onto the input
    // (rather than blanking it, which a "" default would). No request is fired here; a deep-linked page's
    // results are already server-rendered.
    init(this: SearchData) {
      this.query = this.$root?.dataset.query ?? "";
    },

    onInput(this: SearchData) {
      const results = document.getElementById("search-results");
      if (!results) {
        return;
      }

      const term = this.query.trim();
      // Min-length guard: below the threshold we fire nothing — no request, no non-2xx, no spin. (An
      // empty/too-short box is the idle state, not an error; the server mirrors this by returning the
      // 200 prompt if it is ever hit directly.)
      if (term.length < MIN_LENGTH) {
        this.loading = false;
        return;
      }

      // The results region echoes which media the page is scoped to; default to Movie for safety.
      const media = results.getAttribute("data-media") ?? "Movie";
      const url =
        `/discover/search/results?q=${encodeURIComponent(term)}` +
        `&media=${encodeURIComponent(media)}&page=1`;

      this.loading = true;
      // HTMX verb is the lowercase HttpVerb union ("get"); it swaps the fresh results partial into the
      // persistent #search-results region. `.finally` clears the spinner whether the swap succeeds, fails,
      // or the request is superseded.
      void window.htmx
        .ajax("get", url, { target: "#search-results", swap: "innerHTML" })
        .finally(() => {
          this.loading = false;
        });
    },
  }));
}
