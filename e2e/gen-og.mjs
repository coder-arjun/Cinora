// One-off dev tool: renders Cinora's 1200×630 social share image (Open Graph / WhatsApp link preview) to
// src/Cinora.Web/wwwroot/og-image.png. Uses the Playwright chromium already installed for e2e — NOT a runtime
// dependency and never called by the app build. Re-run after brand tweaks:  node e2e/gen-og.mjs
//
// The lockup is CENTERED and VERTICAL (mark stacked over the wordmark) and kept inside a central square safe
// zone, so when WhatsApp/iMessage crop the 1.91:1 card to a square thumbnail the full logo still reads — a
// horizontal lockup gets sliced through the wordmark and looks "cropped/unprofessional".
import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "..", "src", "Cinora.Web", "wwwroot", "og-image.png");

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
  * { margin:0; padding:0; box-sizing:border-box; }
  html,body { width:1200px; height:630px; }
  body {
    font-family: Georgia, "Times New Roman", serif;
    background:
      radial-gradient(60% 75% at 50% 40%, rgba(235,189,87,0.16), rgba(235,189,87,0) 62%),
      linear-gradient(160deg, #0d1017 0%, #090b0f 55%, #070809 100%);
    color:#f3efe6;
    display:flex; align-items:center; justify-content:center;
    position:relative; overflow:hidden;
  }
  .frame { position:absolute; inset:26px; border:1px solid rgba(235,189,87,0.20); border-radius:22px; }
  .frame::after { content:""; position:absolute; inset:6px; border:1px solid rgba(235,189,87,0.09); border-radius:16px; }
  /* Everything lives inside a centered ~560px column → survives a center-square crop. */
  .stage { position:relative; display:flex; flex-direction:column; align-items:center; text-align:center; width:560px; }
  .tile { width:132px; height:132px; filter: drop-shadow(0 16px 36px rgba(0,0,0,0.55)); margin-bottom:26px; }
  .wordmark {
    font-size:122px; line-height:0.92; font-weight:600; letter-spacing:-0.015em;
    background:linear-gradient(180deg,#f7dc95 0%,#eabf5c 46%,#cf9526 100%);
    -webkit-background-clip:text; background-clip:text; color:transparent;
  }
  .rule { width:96px; height:1px; margin:26px 0 24px; background:linear-gradient(90deg, rgba(235,189,87,0), rgba(235,189,87,0.75), rgba(235,189,87,0)); }
  .tagline { font-size:33px; color:#d8d2c6; font-weight:400; }
  .tagline b { color:#f3efe6; font-weight:600; }
  .meta {
    margin-top:26px; display:flex; align-items:center; gap:14px;
    font-family: ui-sans-serif, "Segoe UI", Arial, sans-serif;
    font-size:21px; letter-spacing:0.30em; text-transform:uppercase; color:#a49d90; font-weight:600;
  }
  .stars { color:#eabf5c; letter-spacing:4px; font-size:20px; }
</style></head><body>
  <div class="frame"></div>
  <div class="stage">
    <svg class="tile" viewBox="0 0 32 32" aria-hidden="true">
      <defs>
        <linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f4d488"/><stop offset="1" stop-color="#d9a02c"/></linearGradient>
        <linearGradient id="t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1b1f29"/><stop offset="1" stop-color="#0a0c11"/></linearGradient>
        <radialGradient id="glow" cx="0.5" cy="0.46" r="0.55"><stop offset="0" stop-color="#ebbd57" stop-opacity="0.34"/><stop offset="0.7" stop-color="#ebbd57" stop-opacity="0.05"/><stop offset="1" stop-color="#ebbd57" stop-opacity="0"/></radialGradient>
      </defs>
      <rect x="1.25" y="1.25" width="29.5" height="29.5" rx="8" fill="url(#t)" stroke="#ebbd57" stroke-opacity="0.28"/>
      <circle cx="16" cy="15.4" r="10.5" fill="url(#glow)"/>
      <path d="M22.78 20.24 A8 8 0 1 1 22.78 11.76" fill="none" stroke="url(#g)" stroke-width="3.3" stroke-linecap="round"/>
      <path d="M13.5 12.5 L19 16 L13.5 19.5 Z" fill="url(#g)"/>
    </svg>
    <div class="wordmark">Cinora</div>
    <div class="rule"></div>
    <div class="tagline">Social reviews for <b>film &amp; series</b></div>
    <div class="meta"><span class="stars">★★★★★</span><span>Rate · Review · Discover</span></div>
  </div>
</body></html>`;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
await page.setContent(html, { waitUntil: "networkidle" });
await page.screenshot({ path: out, type: "png" });
await browser.close();
console.log("wrote " + out);
