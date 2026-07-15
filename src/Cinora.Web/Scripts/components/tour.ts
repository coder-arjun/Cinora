// The `tour` component — the fullscreen feature slideshow reached from the landing page's "Explore Cinora"
// button (Views/Tour/Index.cshtml). Registered by NAME for the @alpinejs/csp build: the markup references only
// bare names (x-data="tour", x-ref="track"/"dots"/"prev"/"next", x-on:click="prev"/"next",
// x-on:keydown.window="onKeydown", x-on:touchstart="onTouchStart", x-on:touchend="onTouchEnd"). ALL slideshow
// logic (which slide shows, dot state, arrow enablement, swipe/keyboard) lives here — the csp build forbids
// expressions in markup, so nothing like `x-show="index === 0"` is used; the component drives a CSS transform on
// the track instead and toggles classes/attributes imperatively.
//
// Progressive enhancement: with no JS the track shows the first slide (transform 0) and the slides simply stack
// horizontally clipped — the page is still readable and the CTA/Skip links still work.
import type { Alpine } from "@alpinejs/csp";

interface TourData {
  index: number;
  count: number;
  touchStartX: number | null;
  readonly $refs?: Record<string, HTMLElement>;
  init(this: TourData): void;
  next(this: TourData): void;
  prev(this: TourData): void;
  onKeydown(this: TourData, event: KeyboardEvent): void;
  onTouchStart(this: TourData, event: TouchEvent): void;
  onTouchEnd(this: TourData, event: TouchEvent): void;
  apply(this: TourData): void;
}

const SWIPE_THRESHOLD_PX = 40;

/** Registers the `tour` component on the given Alpine instance. Call BEFORE Alpine.start(). */
export function registerTour(alpine: Alpine): void {
  alpine.data("tour", (): TourData => ({
    index: 0,
    count: 0,
    touchStartX: null,

    init(this: TourData) {
      const track = this.$refs?.track;
      this.count = track === undefined ? 0 : track.children.length;

      // Dots are wired here (not in markup) so the csp build never needs a per-dot expression: each jumps to its
      // own slide. Captured index `i` is stable per iteration.
      const dots = this.$refs?.dots;
      if (dots !== undefined) {
        Array.from(dots.querySelectorAll<HTMLButtonElement>("[data-dot]")).forEach((dot, i) => {
          dot.addEventListener("click", () => {
            this.index = i;
            this.apply();
          });
        });
      }

      this.apply();
    },

    next(this: TourData) {
      if (this.index < this.count - 1) {
        this.index += 1;
        this.apply();
      }
    },

    prev(this: TourData) {
      if (this.index > 0) {
        this.index -= 1;
        this.apply();
      }
    },

    onKeydown(this: TourData, event: KeyboardEvent) {
      if (event.key === "ArrowRight") {
        event.preventDefault();
        this.next();
      } else if (event.key === "ArrowLeft") {
        event.preventDefault();
        this.prev();
      }
    },

    onTouchStart(this: TourData, event: TouchEvent) {
      this.touchStartX = event.changedTouches[0]?.clientX ?? null;
    },

    onTouchEnd(this: TourData, event: TouchEvent) {
      if (this.touchStartX === null) {
        return;
      }
      const endX = event.changedTouches[0]?.clientX ?? this.touchStartX;
      const delta = endX - this.touchStartX;
      this.touchStartX = null;
      if (Math.abs(delta) < SWIPE_THRESHOLD_PX) {
        return;
      }
      if (delta < 0) {
        this.next();
      } else {
        this.prev();
      }
    },

    apply(this: TourData) {
      const track = this.$refs?.track;
      if (track !== undefined) {
        track.style.transform = `translateX(-${this.index * 100}%)`;
      }

      const dots = this.$refs?.dots;
      if (dots !== undefined) {
        Array.from(dots.querySelectorAll<HTMLButtonElement>("[data-dot]")).forEach((dot, i) => {
          const active = i === this.index;
          dot.setAttribute("aria-current", active ? "true" : "false");
          dot.classList.toggle("bg-accent", active);
          dot.classList.toggle("bg-white/25", !active);
        });
      }

      const prev = this.$refs?.prev;
      if (prev !== undefined) {
        (prev as HTMLButtonElement).disabled = this.index === 0;
      }
      const next = this.$refs?.next;
      if (next !== undefined) {
        (next as HTMLButtonElement).disabled = this.index >= this.count - 1;
      }
    },
  }));
}
