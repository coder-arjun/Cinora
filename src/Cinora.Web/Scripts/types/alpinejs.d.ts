// Minimal ambient declaration for the Alpine.js CSP build (@alpinejs/csp), which ships no type
// definitions (verified: no `types`/`exports` field). Covers the surface Cinora uses (start + the
// registration APIs its components need) without resorting to `any`, so `strict` type-checking stays
// honest. The CSP build is the eval-free distribution required under the strict `script-src 'self'`
// CSP — see docs/architecture/phase-2-discovery-design.md §11.2.
declare module "@alpinejs/csp" {
  // Exported so components (Scripts/components/*.ts) can type their `Alpine` parameter.
  export interface Alpine {
    /** Boots Alpine: walks the DOM and initializes every `x-data` root. Call once, after registration. */
    start(): void;
    /**
     * Registers a reusable component factory, referenced by NAME from markup as `x-data="name"`. Under the
     * CSP build the factory returns the component's data/methods object; returning `object` (rather than
     * `Record<string, unknown>`) lets a component return a typed interface without an index signature under
     * `strict` + `exactOptionalPropertyTypes`.
     */
    data(name: string, callback: (...args: unknown[]) => object): void;
    /** Registers (or reads) a global reactive store. */
    store(name: string, value?: unknown): unknown;
    /** Registers a custom directive. */
    directive(name: string, callback: (...args: unknown[]) => void): void;
    /** Registers a custom `$magic` property. */
    magic(name: string, callback: (...args: unknown[]) => unknown): void;
    /** Installs an Alpine plugin. */
    plugin(plugin: unknown): void;
  }

  const Alpine: Alpine;
  export default Alpine;
}
