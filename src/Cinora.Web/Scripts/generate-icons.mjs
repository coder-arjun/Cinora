// Cinora PWA icon generator (Milestone 6.1, ADR 0019 §1). A ONE-OFF, dependency-free dev tool — it is NOT
// part of the runtime build (build.mjs never calls it) and ships no runtime dependency. It renders the three
// installable PNG icons committed under wwwroot/icons/ using only Node's built-in `zlib` (a hand-rolled PNG
// encoder), so no image library (ImageSharp/sharp/svg2png) is required.
//
//   node Scripts/generate-icons.mjs        (re-run to regenerate the committed PNGs)
//
// On-brand: the dark surface-0 background + the gold accent are converted from the exact oklch @theme tokens
// in Styles/app.css, so the mark matches the luxury UI. The mark is a gold ring with an opening on the right
// (a stylized "C" for Cinora). The maskable variant keeps the mark inside the center-80% safe zone.
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const iconsDir = join(root, "wwwroot", "icons");

// ---- oklch → sRGB (the same conversion the design uses to pick manifest theme_color) --------------------
function oklchToRgb(L, C, hDeg) {
  const h = (hDeg * Math.PI) / 180;
  const a = C * Math.cos(h);
  const b = C * Math.sin(h);
  const l_ = L + 0.3963377774 * a + 0.2158037573 * b;
  const m_ = L - 0.1055613458 * a - 0.0638541728 * b;
  const s_ = L - 0.0894841775 * a - 1.291485548 * b;
  const l = l_ ** 3;
  const m = m_ ** 3;
  const s = s_ ** 3;
  const lin = [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
  return lin.map((c) => {
    const v = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055;
    return Math.round(Math.min(1, Math.max(0, v)) * 255);
  });
}

// @theme tokens (Styles/app.css): --color-surface-0 and --color-accent.
const SURFACE = oklchToRgb(0.15, 0.008, 265); // page background
const ACCENT = oklchToRgb(0.82, 0.13, 85); // warm gold

// ---- minimal PNG encoder (8-bit RGBA, filter 0) ---------------------------------------------------------
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buffer) {
  let crc = 0xffffffff;
  for (let i = 0; i < buffer.length; i += 1) {
    crc = CRC_TABLE[(crc ^ buffer[i]) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const typeBytes = Buffer.from(type, "ascii");
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBytes, data])), 0);
  return Buffer.concat([length, typeBytes, data, crc]);
}

function encodePng(width, height, rgba) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // colour type: RGBA
  ihdr[10] = 0; // compression
  ihdr[11] = 0; // filter
  ihdr[12] = 0; // interlace

  // Prefix each scanline with filter byte 0 (None).
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y += 1) {
    raw[y * (stride + 1)] = 0;
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, y * stride + stride);
  }
  const idat = deflateSync(raw, { level: 9 });

  return Buffer.concat([
    signature,
    chunk("IHDR", ihdr),
    chunk("IDAT", idat),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

// ---- point-in-triangle (barycentric sign test) — punches the "play" triangle into the mark --------------
function edgeSign(px, py, a, b) {
  return (px - b.x) * (a.y - b.y) - (a.x - b.x) * (py - b.y);
}
function inTriangle(px, py, a, b, c) {
  const d1 = edgeSign(px, py, a, b);
  const d2 = edgeSign(px, py, b, c);
  const d3 = edgeSign(px, py, c, a);
  const hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
  const hasPos = d1 > 0 || d2 > 0 || d3 > 0;
  return !(hasNeg && hasPos);
}

// ---- the mark: a gold ring (a stylized "C") cradling a play triangle (film + monogram) ------------------
// `markScale` shrinks the ring for the maskable variant so it fits inside the center-80% safe zone.
function renderIcon(size, markScale, monochrome = false) {
  const rgba = Buffer.alloc(size * size * 4);
  const cx = size / 2 - 0.5;
  const cy = size / 2 - 0.5;

  const rOuter = size * 0.34 * markScale;
  const thickness = size * 0.11 * markScale;
  const rInner = rOuter - thickness;
  const rMid = (rOuter + rInner) / 2;
  const capR = thickness / 2;

  // Opening centred on +x (to the right), a ±32° gap — reads as a "C".
  const openHalf = (32 * Math.PI) / 180;
  // Rounded caps at the two ends of the opening.
  const capA = { x: cx + rMid * Math.cos(openHalf), y: cy + rMid * Math.sin(openHalf) };
  const capB = { x: cx + rMid * Math.cos(-openHalf), y: cy + rMid * Math.sin(-openHalf) };

  // Play triangle centred in the ring (the "watch" cue). Offsets mirror wwwroot/favicon.svg, scaled by the mark
  // size; omitted from the monochrome badge, which reads better as a plain ring at status-bar size.
  const triSpan = size * markScale;
  const triTip = { x: cx + 0.094 * triSpan, y: cy };
  const triTop = { x: cx - 0.078 * triSpan, y: cy - 0.109 * triSpan };
  const triBot = { x: cx - 0.078 * triSpan, y: cy + 0.109 * triSpan };

  const SS = 4; // 4×4 supersampling for anti-aliased edges

  for (let y = 0; y < size; y += 1) {
    for (let x = 0; x < size; x += 1) {
      let hits = 0;
      for (let sy = 0; sy < SS; sy += 1) {
        for (let sx = 0; sx < SS; sx += 1) {
          const px = x + (sx + 0.5) / SS;
          const py = y + (sy + 0.5) / SS;
          const dx = px - cx;
          const dy = py - cy;
          const dist = Math.hypot(dx, dy);

          let inMark = false;
          if (dist >= rInner && dist <= rOuter) {
            // Inside the annulus — excluded only within the opening wedge.
            const angle = Math.atan2(dy, dx); // -π..π; opening is around 0
            if (Math.abs(angle) > openHalf) {
              inMark = true;
            }
          }
          if (!inMark) {
            // Rounded caps close the ring ends cleanly.
            if (Math.hypot(px - capA.x, py - capA.y) <= capR) inMark = true;
            else if (Math.hypot(px - capB.x, py - capB.y) <= capR) inMark = true;
          }
          // Play triangle (colour icons only; the badge stays a clean ring at tiny sizes).
          if (!inMark && !monochrome && inTriangle(px, py, triTip, triTop, triBot)) {
            inMark = true;
          }
          if (inMark) hits += 1;
        }
      }

      const coverage = hits / (SS * SS);
      const i = (y * size + x) * 4;
      if (monochrome) {
        // Notification badge: a WHITE silhouette on a TRANSPARENT background — the platform tints it (Android
        // renders a colour PNG as a grey blob), so only the alpha (coverage) carries the mark.
        rgba[i] = 255;
        rgba[i + 1] = 255;
        rgba[i + 2] = 255;
        rgba[i + 3] = Math.round(coverage * 255);
      } else {
        // Composite gold over the surface background by coverage (opaque icon; alpha stays 255).
        rgba[i] = Math.round(SURFACE[0] + (ACCENT[0] - SURFACE[0]) * coverage);
        rgba[i + 1] = Math.round(SURFACE[1] + (ACCENT[1] - SURFACE[1]) * coverage);
        rgba[i + 2] = Math.round(SURFACE[2] + (ACCENT[2] - SURFACE[2]) * coverage);
        rgba[i + 3] = 255;
      }
    }
  }

  return encodePng(size, size, rgba);
}

mkdirSync(iconsDir, { recursive: true });
writeFileSync(join(iconsDir, "icon-192.png"), renderIcon(192, 1));
writeFileSync(join(iconsDir, "icon-512.png"), renderIcon(512, 1));
// Maskable: shrink the mark to ~0.78 so it stays within the center-80% safe zone under a circle/squircle mask.
writeFileSync(join(iconsDir, "icon-maskable-512.png"), renderIcon(512, 0.78));
// Monochrome notification badge (Android status bar): a white "C" silhouette on transparent, platform-tinted.
writeFileSync(join(iconsDir, "badge.png"), renderIcon(96, 1, true));

console.log(
  `Cinora PWA icons written to wwwroot/icons/ (surface #${SURFACE.map((c) => c.toString(16).padStart(2, "0")).join("")}, ` +
    `accent #${ACCENT.map((c) => c.toString(16).padStart(2, "0")).join("")}): icon-192.png, icon-512.png, icon-maskable-512.png, badge.png`,
);
