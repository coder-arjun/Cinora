// Animated "aurora" mesh-gradient background (canvas 2D) for the auth / form / landing hero pages. CSP-safe:
// the drawing code ships in the bundled site.js ('self'), touches no inline style/script, and uses no eval.
// A few soft brand-coloured radial blobs drift on sine waves and blend additively over the near-black page
// for a premium shader-like backdrop. Honors prefers-reduced-motion (paints ONE static frame, no loop) and
// pauses the loop while the tab is hidden. Mount point: a <canvas data-aurora> inside the _AuroraBackground
// partial. A missing canvas is a no-op, so calling this on non-aurora pages is safe.

type Rgb = readonly [number, number, number];

interface AuroraOrb {
  readonly baseX: number; // resting position as a fraction of canvas width (0..1)
  readonly baseY: number; // resting position as a fraction of canvas height (0..1)
  readonly radius: number; // radius as a fraction of the smaller canvas dimension
  readonly color: Rgb;
  readonly ampX: number; // horizontal drift amplitude (fraction of width)
  readonly ampY: number; // vertical drift amplitude (fraction of height)
  readonly speedX: number; // radians of phase per ms
  readonly speedY: number;
  readonly phaseX: number;
  readonly phaseY: number;
}

// Brand palette: gold (accent), violet and deep indigo — the "Modern Dark (Cinema Mobile)" ambient-light look.
const ORBS: readonly AuroraOrb[] = [
  { baseX: 0.16, baseY: 0.22, radius: 0.6, color: [235, 189, 87], ampX: 0.06, ampY: 0.05, speedX: 0.00007, speedY: 0.00009, phaseX: 0, phaseY: 1.5 },
  { baseX: 0.82, baseY: 0.74, radius: 0.55, color: [129, 90, 245], ampX: 0.07, ampY: 0.06, speedX: 0.00006, speedY: 0.00008, phaseX: 2, phaseY: 0.5 },
  { baseX: 0.62, baseY: 0.14, radius: 0.45, color: [56, 90, 210], ampX: 0.05, ampY: 0.05, speedX: 0.00009, speedY: 0.00006, phaseX: 4, phaseY: 3 },
  { baseX: 0.34, baseY: 0.86, radius: 0.48, color: [216, 160, 44], ampX: 0.05, ampY: 0.06, speedX: 0.00008, speedY: 0.00007, phaseX: 1, phaseY: 2 },
];

// Render at half resolution — the soft gradients + a CSS blur hide it, and it keeps the per-frame fill cheap.
const RENDER_SCALE = 0.5;

export function initAurora(): void {
  const canvas = document.querySelector<HTMLCanvasElement>("canvas[data-aurora]");
  if (canvas === null) {
    return;
  }
  const ctx = canvas.getContext("2d");
  if (ctx === null) {
    return;
  }

  let width = 0;
  let height = 0;

  const resize = (): void => {
    width = Math.max(1, Math.round((canvas.clientWidth || window.innerWidth) * RENDER_SCALE));
    height = Math.max(1, Math.round((canvas.clientHeight || window.innerHeight) * RENDER_SCALE));
    canvas.width = width;
    canvas.height = height;
  };

  const draw = (time: number): void => {
    ctx.clearRect(0, 0, width, height);
    ctx.globalCompositeOperation = "lighter"; // additive → glowing aurora over the dark page
    const min = Math.min(width, height);
    for (const orb of ORBS) {
      const cx = (orb.baseX + Math.sin(time * orb.speedX + orb.phaseX) * orb.ampX) * width;
      const cy = (orb.baseY + Math.cos(time * orb.speedY + orb.phaseY) * orb.ampY) * height;
      const r = orb.radius * min;
      const gradient = ctx.createRadialGradient(cx, cy, 0, cx, cy, r);
      const [red, green, blue] = orb.color;
      gradient.addColorStop(0, `rgba(${red},${green},${blue},0.5)`);
      gradient.addColorStop(1, `rgba(${red},${green},${blue},0)`);
      ctx.fillStyle = gradient;
      ctx.beginPath();
      ctx.arc(cx, cy, r, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalCompositeOperation = "source-over";
  };

  resize();
  window.addEventListener("resize", resize);

  // Reduced motion: one static frame, no animation loop.
  if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    draw(0);
    return;
  }

  let frame = 0;
  const loop = (time: number): void => {
    draw(time);
    frame = window.requestAnimationFrame(loop);
  };
  frame = window.requestAnimationFrame(loop);

  // Pause while the tab is hidden (don't burn cycles/battery on an off-screen animation).
  document.addEventListener("visibilitychange", () => {
    window.cancelAnimationFrame(frame);
    if (!document.hidden) {
      frame = window.requestAnimationFrame(loop);
    }
  });
}
