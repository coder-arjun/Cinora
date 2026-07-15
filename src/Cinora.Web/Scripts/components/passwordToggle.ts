// The `passwordToggle` component — a CSP-safe show/hide control for a password <input> on the auth forms.
// Registered by NAME for the @alpinejs/csp build: the markup references only bare names
// (x-data="passwordToggle", x-bind:type="inputType", x-on:click="toggle", x-bind:aria-label="label",
// x-bind:aria-pressed="pressed", x-show="concealed"/"revealed"). All logic lives here in the component body.
//
// Progressive enhancement: the field is server-rendered as type="password", so a no-JS user still has a fully
// working password field — this only ADDS a reveal control. Two mirrored booleans (concealed/revealed) drive the
// two icons because the @alpinejs/csp build evaluates only bare identifiers (no "!expr"), keeping bindings eval-free.
import type { Alpine } from "@alpinejs/csp";

interface PasswordToggleData {
  concealed: boolean;
  revealed: boolean;
  inputType: string;
  label: string;
  pressed: string;
  toggle(this: PasswordToggleData): void;
}

/** Registers the `passwordToggle` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerPasswordToggle(alpine: Alpine): void {
  alpine.data("passwordToggle", (): PasswordToggleData => ({
    concealed: true,
    revealed: false,
    inputType: "password",
    label: "Show password",
    pressed: "false",
    toggle(this: PasswordToggleData) {
      this.revealed = !this.revealed;
      this.concealed = !this.revealed;
      this.inputType = this.revealed ? "text" : "password";
      this.label = this.revealed ? "Hide password" : "Show password";
      this.pressed = this.revealed ? "true" : "false";
    },
  }));
}
