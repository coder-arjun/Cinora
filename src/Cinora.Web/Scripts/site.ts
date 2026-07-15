// Cinora frontend entry point. esbuild bundles Alpine + HTMX + this module into one hashed,
// minified file (wwwroot/dist/site-<hash>.js) — everything ships locally, no CDN.
//
// Division of labor (see .claude/skills/alpine-htmx-interactivity): Alpine owns local UI state,
// HTMX owns server round-trips. Core reading flows render without JS (progressive enhancement);
// this script only enhances.
import Alpine from "@alpinejs/csp";
import htmx from "htmx.org";
import { registerSearch } from "./components/search";
import { registerWatchlistMenu } from "./components/watchlistMenu";
import { registerCommentCollapse } from "./components/commentCollapse";
import { registerConfirmDialog, requestConfirmation } from "./components/confirmDialog";
import { registerPasswordToggle } from "./components/passwordToggle";
import { registerNavMenu } from "./components/navMenu";
import { registerAccountMenu } from "./components/accountMenu";
import { registerTour } from "./components/tour";
import { registerLanguageSelect } from "./components/languageSelect";

declare global {
  interface Window {
    Alpine: typeof Alpine;
    htmx: typeof htmx;
  }
}

// Progressive enhancement flag: stamp `js` on <html> as early as this module runs so CSS can reveal controls
// that only work with JS (the `.js-only` utility in app.css — e.g. the watchlist "Remove" menu item, whose
// hx-delete has no no-JS fallback). The no-JS DOM never gets the class, so those controls stay hidden there.
document.documentElement.classList.add("js");

// Must match AntiforgeryOptions.HeaderName configured in Program.cs.
const ANTIFORGERY_HEADER = "RequestVerificationToken";
// The layout renders the token into this meta tag from IAntiforgery.GetAndStoreTokens.
const TOKEN_META_SELECTOR = 'meta[name="request-verification-token"]';

interface HtmxConfigRequestDetail {
  headers: Record<string, string>;
}

// Attach the anti-forgery token to every HTMX request so state-changing hx-post/hx-put/etc.
// satisfy the global AutoValidateAntiforgeryToken filter. Real <form>s already post the hidden
// __RequestVerificationToken field automatically; this covers non-form HTMX requests. The
// htmx:configRequest event bubbles to the document.
function attachAntiforgeryHeader(event: Event): void {
  const token = document
    .querySelector<HTMLMetaElement>(TOKEN_META_SELECTOR)
    ?.content;
  if (!token) {
    return;
  }
  const { detail } = event as CustomEvent<HtmxConfigRequestDetail>;
  detail.headers[ANTIFORGERY_HEADER] = token;
}

// Expose both libraries. `window.htmx` is used by components (the search component fires
// window.htmx.ajax); `window.Alpine` is kept for debugging. NOTE: under the @alpinejs/csp build, inline
// `x-data` / `x-on` expressions in markup are FORBIDDEN — components are registered by name (below) and
// referenced by bare name only. HTMX `hx-*` attributes are unaffected (attribute-driven, no eval).
window.Alpine = Alpine;
window.htmx = htmx;

// Defense-in-depth: disable HTMX's eval-family code paths (js: expressions, hx-on, trigger filters). We use
// none of them (all interactivity is bare-name Alpine + attribute-only hx-*), and the strict `script-src
// 'self'` CSP already blocks eval — but pinning this off survives a future CSP regression.
window.htmx.config.allowEval = false;

// Cross-document View Transitions (Styles/app.css `@view-transition`) are a CSS-only platform feature. When a
// navigation transition is interrupted — a second navigation before the first finishes, or the dev hot-reload
// script navigating mid-transition — the browser SKIPS it and surfaces a benign "AbortError: Transition was
// skipped" as an unhandled promise rejection. There is no app-level promise to await, so swallow ONLY that one
// exact rejection (matched by both name and message) to keep the console clean; every other rejection is left
// untouched so real failures still surface.
window.addEventListener("unhandledrejection", (event) => {
  const reason = event.reason as { name?: string; message?: string } | undefined;
  if (reason?.name === "AbortError" && /transition was skipped/i.test(reason.message ?? "")) {
    event.preventDefault();
  }
});

document.addEventListener("htmx:configRequest", attachAntiforgeryHeader);

// --- Search region a11y (Milestone 2.4) ------------------------------------------------------------------
// Two concerns for the #search-results region, both handled in component-body JS (NOT hx-on) so the strict
// CSP is untouched, and both scoped to that region so the 2.3 discovery-rail swaps are never affected (on
// those pages #search-results is absent, so every handler early-returns):
//   1. Politely announce a CONCISE summary of each update to a dedicated #search-status node — instead of
//      making the whole card grid an aria-live region (which would re-read every card on each keystroke).
//   2. Restore focus after a swap that DESTROYED the focused control (e.g. the Retry button), which would
//      otherwise drop focus to <body>. Live search-as-you-type is unaffected: its focus lives in the input,
//      OUTSIDE the swapped region, so the guard below never triggers for it.

// Set in htmx:beforeSwap — was focus inside the subtree about to be replaced?
let searchFocusWasInSwap = false;

function searchResultsRegion(): HTMLElement | null {
  return document.getElementById("search-results");
}

// True (and narrows `target` to Element) when a swap touches the search-results region.
function swapTouchesSearchRegion(target: EventTarget | null): target is Element {
  const region = searchResultsRegion();
  return (
    region !== null &&
    target instanceof Element &&
    (region === target || region.contains(target) || target.contains(region))
  );
}

// A search card carries a watchlist control, so a set/remove swap fires INSIDE #search-results. Those swaps
// are owned by the watchlist handlers below (their own announce + focus); the search handlers must skip them,
// or a watchlist toggle would re-announce the stale "N results" summary and steal focus to the whole region.
function isWatchlistControlSwap(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest("[data-watchlist-control]") !== null;
}

function onSearchBeforeSwap(event: Event): void {
  const target = event.target;
  if (isWatchlistControlSwap(target)) {
    searchFocusWasInSwap = false;
    return;
  }
  const active = document.activeElement;
  searchFocusWasInSwap =
    swapTouchesSearchRegion(target) && active instanceof Element && target.contains(active);
}

function onSearchAfterSwap(event: Event): void {
  if (isWatchlistControlSwap(event.target)) {
    return; // a watchlist set/remove — handled by onWatchlistAfterSwap.
  }
  const region = searchResultsRegion();
  if (region === null || !swapTouchesSearchRegion(event.target)) {
    return;
  }

  // (1) Announce the newest concise summary. Each results partial exposes a short string on the last
  //     [data-search-announce] element ("20 results for dune" / "10 more results loaded" / "End of results").
  const announcements = region.querySelectorAll<HTMLElement>("[data-search-announce]");
  const latest = announcements.item(announcements.length - 1);
  const status = document.getElementById("search-status");
  if (latest !== null && status !== null) {
    status.textContent = latest.getAttribute("data-search-announce") ?? "";
  }

  // (2) Restore focus only if the swap destroyed the focused control and focus fell to <body>.
  const active = document.activeElement;
  if (searchFocusWasInSwap && (active === null || active === document.body)) {
    region.focus({ preventScroll: true });
  }
  searchFocusWasInSwap = false;
}

document.addEventListener("htmx:beforeSwap", onSearchBeforeSwap);
document.addEventListener("htmx:afterSwap", onSearchAfterSwap);

// --- Review region a11y (Milestone 3.1) ------------------------------------------------------------------
// The Details reviews section has two lazy, HTMX-swapped regions: #my-review (your write form / your review
// card / sign-in prompt) and #reviews-list (the public, load-more list). Mirroring the 2.4 search pattern
// (component-body JS, NOT hx-on → CSP-safe; scoped so other pages' swaps early-return), this block:
//   H4  announces a CONCISE status to a single polite #reviews-status node — the regions themselves are NOT
//       aria-live, which would read every ~4000-char body on load/append.
//   H2  restores focus after a swap that destroyed the focused control (create/edit/delete/cancel) — to the
//       swapped-in review card, or to a stable anchor (the section heading) after a delete.
//   M1  clears the lazy skeleton's aria-busy once its partial has swapped in.
//   H3  surfaces a polite inline message in #review-error on a failed review WRITE (htmx:responseError — a
//       429/400/404/500 that HTMX will not swap), instead of a silent no-op.

const REVIEW_REGION_IDS = ["my-review", "reviews-list"];

interface HtmxRequestConfigLike {
  verb?: string;
  path?: string;
}
interface HtmxDetailLike {
  requestConfig?: HtmxRequestConfigLike;
  xhr?: XMLHttpRequest;
}

// The #my-review / #reviews-list region a swap touched, or null when the swap was elsewhere.
function reviewRegionOf(node: EventTarget | null): HTMLElement | null {
  if (!(node instanceof Element)) {
    return null;
  }
  for (const id of REVIEW_REGION_IDS) {
    const region = document.getElementById(id);
    if (region !== null && (region === node || region.contains(node) || node.contains(region))) {
      return region;
    }
  }
  return null;
}

function reviewErrorRegion(): HTMLElement | null {
  return document.getElementById("review-error");
}

function hideReviewError(): void {
  const el = reviewErrorRegion();
  if (el !== null) {
    el.textContent = "";
    el.classList.add("hidden");
  }
}

function showReviewError(message: string): void {
  const el = reviewErrorRegion();
  if (el !== null) {
    // Unhide BEFORE writing text: mutating an assertive role="alert" while it is
    // display:none fails to announce on some SR/browser combos.
    el.classList.remove("hidden");
    el.textContent = message;
  }
}

// Concise polite announcement into the shared #reviews-status node (used by review, like and comment swaps).
function announceReviews(message: string): void {
  const status = document.getElementById("reviews-status");
  if (status !== null && message.length > 0) {
    status.textContent = message;
  }
}

// Classifies a swap that lives INSIDE #reviews-list but is really a like or comment interaction — the review
// handlers must NOT run for these (else an unlike would announce "Review deleted" and steal card focus). Uses
// element markers (works in before/afterSwap); the review handlers early-return when this is non-null.
function socialControlOf(node: EventTarget | null): "like" | "comment" | null {
  if (!(node instanceof Element)) {
    return null;
  }
  if (node.closest("[data-like-button]") !== null) {
    return "like";
  }
  if (node.closest("[data-comment-thread]") !== null) {
    return "comment";
  }
  return null;
}

// The swapped-in review card to focus (create/edit/cancel), or null (delete swaps in no card).
function findReviewCard(target: Element): HTMLElement | null {
  if (target.matches("[data-review-card]")) {
    return target as HTMLElement;
  }
  return (
    target.querySelector<HTMLElement>("[data-review-card]") ??
    target.closest<HTMLElement>("[data-review-card]")
  );
}

// A concise, non-flooding status for the swap: an explicit carrier on the swapped content wins (e.g. the
// load-more count on the reviews list); otherwise derive it from the write verb/path.
function reviewAnnouncement(target: Element, config: HtmxRequestConfigLike | undefined): string {
  const carrier = target.matches("[data-review-announce]")
    ? target
    : target.querySelector("[data-review-announce]");
  if (carrier !== null) {
    return carrier.getAttribute("data-review-announce") ?? "";
  }
  const verb = config?.verb?.toLowerCase() ?? "";
  const path = config?.path ?? "";
  if (verb === "delete") {
    return "Review deleted";
  }
  if (verb === "post") {
    return path === "/reviews" ? "Your review posted" : "Review updated";
  }
  return "";
}

// Set in htmx:beforeSwap — was focus inside the review subtree about to be replaced?
let reviewFocusWasInSwap = false;

function onReviewBeforeSwap(event: Event): void {
  const target = event.target;
  // Like/comment swaps live inside #reviews-list but are handled by their own plumbing below.
  if (socialControlOf(target) !== null) {
    reviewFocusWasInSwap = false;
    return;
  }
  const active = document.activeElement;
  reviewFocusWasInSwap =
    reviewRegionOf(target) !== null &&
    target instanceof Element &&
    active instanceof Element &&
    target.contains(active);
}

function onReviewAfterSwap(event: Event): void {
  const target = event.target;
  if (socialControlOf(target) !== null) {
    return; // a like/comment swap — handled by onLikeAfterSwap / onCommentAfterSwap.
  }
  const region = reviewRegionOf(target);
  if (region === null || !(target instanceof Element)) {
    return;
  }

  // M1: the lazy skeleton has been replaced — the region is no longer busy.
  region.removeAttribute("aria-busy");
  // A successful review swap clears any prior write-error message.
  hideReviewError();

  // H4: announce a concise status only (never the card bodies).
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  announceReviews(reviewAnnouncement(target, detail.requestConfig));

  // H2: restore focus only if the swap destroyed the focused control, so the initial lazy loads — whose
  // focus is elsewhere — are left alone.
  if (reviewFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const anchor = findReviewCard(target) ?? document.getElementById("cinora-reviews-heading");
      anchor?.focus({ preventScroll: true });
    }
  }
  reviewFocusWasInSwap = false;
}

const TOO_FAST = "You're doing that too fast — try again in a moment.";

function onReviewResponseError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const verb = detail.requestConfig?.verb?.toLowerCase() ?? "";
  const path = detail.requestConfig?.path ?? "";
  // A REVIEW write only — NOT a like (/like) or comment (/comments) write (those get their own messages).
  const isReviewWrite =
    (verb === "post" || verb === "delete") &&
    path.startsWith("/reviews") &&
    !path.includes("/like") &&
    !path.includes("/comments");
  if (!isReviewWrite) {
    return;
  }
  const httpStatus = detail.xhr?.status ?? 0;
  showReviewError(httpStatus === 429 ? TOO_FAST : "Couldn't save your review — please try again.");
}

// --- Like & comment plumbing (Milestone 3.2) -------------------------------------------------------------
// Like/comment controls nest inside #reviews-list, so the review handlers above deliberately skip them
// (socialControlOf) and these dedicated handlers take over — correct SR announcements, focus, count sync and
// error feedback. All component-body JS (NOT hx-on) → CSP-safe; every handler early-returns off its own
// element marker, so nothing else on the page is affected.

// Update the review card's comment-toggle count (M5) after an add (+1) / delete (-1), without a re-render.
function adjustCommentCount(reviewCard: Element, delta: number): void {
  const toggle = reviewCard.querySelector("[data-comment-toggle]");
  if (toggle === null) {
    return;
  }
  const current = Number.parseInt(toggle.getAttribute("data-comment-count") ?? "0", 10);
  const next = Math.max(0, (Number.isNaN(current) ? 0 : current) + delta);
  toggle.setAttribute("data-comment-count", String(next));
  const label = toggle.querySelector("[data-comment-count-label]");
  if (label !== null) {
    label.textContent = `${next} ${next === 1 ? "comment" : "comments"}`;
  }
}

// Toggle the thread's empty-state class (drives the CSS in app.css) — robust to whitespace text nodes that a
// bare :empty would trip on, and it restores "No comments yet" after the last comment is deleted (L10).
function refreshCommentEmptyState(thread: Element): void {
  thread.classList.add("is-loaded");
  const items = thread.querySelector(".comment-items");
  const isEmpty = items === null || items.querySelector("[data-comment-card]") === null;
  thread.classList.toggle("comment-thread-empty", isEmpty);
}

function onLikeAfterSwap(event: Event): void {
  const target = event.target;
  if (!(target instanceof Element)) {
    return;
  }
  const button = target.closest<HTMLElement>("[data-like-button]");
  if (button === null) {
    return; // not a like swap
  }
  hideReviewError();
  // Focus is restored by HTMX itself (the re-rendered button keeps its stable id).
  const liked = button.getAttribute("aria-pressed") === "true";
  const countText = button.getAttribute("data-like-count") ?? "0";
  const likesWord = Number.parseInt(countText, 10) === 1 ? "like" : "likes";
  announceReviews(`${liked ? "Liked" : "Like removed"}, ${countText} ${likesWord}`);
}

// Set in htmx:beforeSwap — was focus inside the comment subtree about to be replaced (a comment delete)?
let commentFocusWasInSwap = false;

function onCommentBeforeSwap(event: Event): void {
  const target = event.target;
  commentFocusWasInSwap =
    target instanceof Element &&
    target.closest("[data-comment-thread]") !== null &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onCommentAfterSwap(event: Event): void {
  const target = event.target;
  if (!(target instanceof Element)) {
    return;
  }
  const thread = target.closest<HTMLElement>("[data-comment-thread]");
  if (thread === null) {
    return; // not a comment swap
  }

  hideReviewError();
  refreshCommentEmptyState(thread);

  const reviewCard = thread.closest<HTMLElement>("[data-review-card]");

  // The initial disclosure load innerHTML-swaps the whole thread region: mark the toggle expanded (M7); do
  // not announce (the user chose to view the thread).
  if (target.matches("[data-comment-thread]")) {
    reviewCard?.querySelector("[data-comment-toggle]")?.setAttribute("aria-expanded", "true");
    commentFocusWasInSwap = false;
    return;
  }

  const verb = (event as CustomEvent<HtmxDetailLike>).detail.requestConfig?.verb?.toLowerCase() ?? "";
  if (verb === "post") {
    announceReviews("Comment added");
    if (reviewCard !== null) {
      adjustCommentCount(reviewCard, 1);
    }
  } else if (verb === "delete") {
    announceReviews("Comment deleted");
    if (reviewCard !== null) {
      adjustCommentCount(reviewCard, -1);
    }
    // The deleted comment card held focus — move it to a sensible anchor (the write textarea, else the thread).
    if (commentFocusWasInSwap) {
      const active = document.activeElement;
      if (active === null || active === document.body) {
        const reviewId = thread.getAttribute("data-review-id") ?? "";
        const anchor = document.getElementById(`comment-body-${reviewId}`) ?? thread;
        anchor.focus({ preventScroll: true });
      }
    }
  }
  // A GET load-more append is intentionally silent.
  commentFocusWasInSwap = false;
}

function onSocialResponseError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const verb = detail.requestConfig?.verb?.toLowerCase() ?? "";
  const path = detail.requestConfig?.path ?? "";
  const httpStatus = detail.xhr?.status ?? 0;
  if (path.includes("/like")) {
    showReviewError(httpStatus === 429 ? TOO_FAST : "Couldn't update the like — please try again.");
  } else if (path.includes("/comments")) {
    if (httpStatus === 429) {
      showReviewError(TOO_FAST);
    } else {
      showReviewError(
        verb === "delete"
          ? "Couldn't delete the comment — please try again."
          : "Couldn't post your comment — please try again.",
      );
    }
  }
}

document.addEventListener("htmx:beforeSwap", onReviewBeforeSwap);
document.addEventListener("htmx:afterSwap", onReviewAfterSwap);
document.addEventListener("htmx:responseError", onReviewResponseError);
document.addEventListener("htmx:beforeSwap", onCommentBeforeSwap);
document.addEventListener("htmx:afterSwap", onLikeAfterSwap);
document.addEventListener("htmx:afterSwap", onCommentAfterSwap);
document.addEventListener("htmx:responseError", onSocialResponseError);

// --- Friends & profiles a11y (Milestone 3.3) -------------------------------------------------------------
// The friends surface (accept/decline/add/remove) had NO site.ts entry, so it inherited none of the
// focus-restore/announce work. This block mirrors the review handlers, scoped to friend/profile swaps by the
// /friends request path (reliable across outerHTML swaps) plus element markers, so it never disturbs
// review/like/comment/search swaps (they early-return here, and the review/like/comment handlers early-return
// for these). Concerns: F2 restore focus (a swapped-away Accept/Remove button drops focus to <body>), F3
// announce a concise message to the persistent #friends-status node, F4 keep the CSS-driven empty-states fresh.

// True when a swap is a friend/profile mutation (scope by path first, element markers as a fallback).
function isFriendSwap(target: EventTarget | null, config: HtmxRequestConfigLike | undefined): boolean {
  if ((config?.path ?? "").startsWith("/friends")) {
    return true;
  }
  return (
    target instanceof Element &&
    (target.closest("[data-friend-card]") !== null ||
      target.closest("[data-pending-request]") !== null ||
      target.closest("#profile-friend-control") !== null)
  );
}

function announceFriends(message: string): void {
  const status = document.getElementById("friends-status");
  if (status !== null && message.length > 0) {
    status.textContent = message;
  }
}

// Concise, deterministic status derived from the write (not read from the visible line, which is unreliable
// to relocate after an outerHTML swap and empty for a remove).
function friendAnnouncement(verb: string, path: string): string {
  if (verb === "delete") {
    return "Friend removed";
  }
  if (path.endsWith("/accept")) {
    return "You are now friends";
  }
  if (path.endsWith("/decline")) {
    return "Request declined";
  }
  if (verb === "post") {
    return "Request sent";
  }
  return "";
}

// F4: toggle each collection's .is-empty class (drives the CSS empty-state in app.css) off REAL card markers,
// so removing the last friend / responding to the last request restores the empty message with no re-render.
function refreshFriendCollections(): void {
  document.querySelectorAll(".friend-collection").forEach((collection) => {
    const items = collection.querySelector(".collection-items");
    const hasCards =
      items !== null && items.querySelector("[data-friend-card], [data-pending-request]") !== null;
    collection.classList.toggle("is-empty", !hasCards);
  });
}

let friendFocusWasInSwap = false;

function onFriendBeforeSwap(event: Event): void {
  const target = event.target;
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  friendFocusWasInSwap =
    isFriendSwap(target, detail.requestConfig) &&
    target instanceof Element &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onFriendAfterSwap(event: Event): void {
  const target = event.target;
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  if (!isFriendSwap(target, detail.requestConfig) || !(target instanceof Element)) {
    return;
  }

  // F3: announce a concise message to the persistent polite node.
  announceFriends(
    friendAnnouncement(
      detail.requestConfig?.verb?.toLowerCase() ?? "",
      detail.requestConfig?.path ?? "",
    ),
  );

  // F4: keep the CSS-driven empty-states fresh.
  refreshFriendCollections();

  // F2: restore focus if the swap destroyed the focused control. Prefer the swapped-in visible status line,
  // then the nearest section heading, then the page heading — never leave focus on <body>.
  if (friendFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const statusLine = target.matches("[data-friend-status-line]")
        ? target
        : target.querySelector("[data-friend-status-line]");
      const heading = target.closest("section, div")?.querySelector("h1, h2") ?? null;
      const anchor = (statusLine ?? heading ?? document.querySelector("main h1, main h2")) as HTMLElement | null;
      if (anchor !== null) {
        if (!anchor.hasAttribute("tabindex")) {
          anchor.setAttribute("tabindex", "-1");
        }
        anchor.focus({ preventScroll: true });
      }
    }
  }
  friendFocusWasInSwap = false;
}

function onFriendResponseError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const path = detail.requestConfig?.path ?? "";
  if (!path.startsWith("/friends")) {
    return;
  }
  const httpStatus = detail.xhr?.status ?? 0;
  announceFriends(httpStatus === 429 ? TOO_FAST : "That didn't work — please try again.");
}

document.addEventListener("htmx:beforeSwap", onFriendBeforeSwap);
document.addEventListener("htmx:afterSwap", onFriendAfterSwap);
document.addEventListener("htmx:responseError", onFriendResponseError);

// --- Activity feed a11y (Milestone 3.4) ------------------------------------------------------------------
// The home feed shipped with NO site.ts entry, so it inherited none of the focus-restore/announce work. This
// block mirrors the 2.4 search handlers, scoped to the /home/feed request path (fallback: the #feed-items
// region) so it early-returns for every other swap (search/review/like/comment/friend), and they for it.
//   H1  restore focus after a swap that destroyed the focused control (the _FeedError Retry button, which has
//       no id) — to the persistent, focusable #feed-items list.
//   M4  copy the newest concise carrier (data-feed-announce on the load-more sentinel / terminal marker) into
//       the polite #feed-status node ("N more items loaded" / "You're all caught up").

function feedRegion(): HTMLElement | null {
  return document.getElementById("feed-items");
}

function isFeedSwap(target: EventTarget | null, config: HtmxRequestConfigLike | undefined): boolean {
  if ((config?.path ?? "").startsWith("/home/feed")) {
    return true;
  }
  const region = feedRegion();
  return (
    region !== null &&
    target instanceof Element &&
    (region === target || region.contains(target) || target.contains(region))
  );
}

let feedFocusWasInSwap = false;

function onFeedBeforeSwap(event: Event): void {
  const target = event.target;
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  feedFocusWasInSwap =
    isFeedSwap(target, detail.requestConfig) &&
    target instanceof Element &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onFeedAfterSwap(event: Event): void {
  const target = event.target;
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  if (!isFeedSwap(target, detail.requestConfig) || !(target instanceof Element)) {
    return;
  }
  const region = feedRegion();

  // M4: announce the newest concise carrier from the swapped-in sentinel / terminal.
  const scope = region ?? target;
  const carriers = scope.querySelectorAll<HTMLElement>("[data-feed-announce]");
  const latest = carriers.item(carriers.length - 1);
  const status = document.getElementById("feed-status");
  if (latest !== null && status !== null) {
    status.textContent = latest.getAttribute("data-feed-announce") ?? "";
  }

  // H1: restore focus only if the swap destroyed the focused control (Retry) and focus fell to <body>.
  if (feedFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const anchor = region ?? (target instanceof HTMLElement ? target : null);
      anchor?.focus({ preventScroll: true });
    }
  }
  feedFocusWasInSwap = false;
}

document.addEventListener("htmx:beforeSwap", onFeedBeforeSwap);
document.addEventListener("htmx:afterSwap", onFeedAfterSwap);

// --- Realtime notifications (Milestone 3.5, ADR 0011) ----------------------------------------------------
// Signed-in users only: the backend renders #notification-unread-badge ONLY when authenticated, so its
// presence is the auth guard. We open a SignalR connection to the [Authorize] hub at /hubs/notifications
// (same-origin wss://, allowed by connect-src 'self'; SignalR uses fetch/WebSocket — no eval, so the strict
// CSP is untouched), push on-brand toasts, and keep the bell badge live.
//
// XSS (CRITICAL): dto.Message / dto.ActorDisplayName carry the ACTOR's user-controlled display name, so ALL
// notification text is written with textContent — NEVER innerHTML. A start failure degrades gracefully to
// the server's no-JS unread-count polling; automatic reconnect retries transient drops.

interface NotificationDto {
  Id?: string;
  Type?: string;
  Message?: string;
  ActorDisplayName?: string;
  TargetId?: string;
  CreatedAtUtc?: string;
}

const TOAST_TIMEOUT_MS = 6000;
const MAX_TOASTS = 3; // M4: cap concurrent transient toasts so a burst can't overflow the mobile viewport.
const toastTimers = new WeakMap<HTMLElement, number>();

function clearToastTimer(toast: HTMLElement): void {
  const timer = toastTimers.get(toast);
  if (timer !== undefined) {
    window.clearTimeout(timer);
    toastTimers.delete(toast);
  }
}

// M4: remove a toast; if it held focus, move focus to a sibling toast (else the bell) so a focused toast
// can never vanish and strand focus.
function dismissToast(toast: HTMLElement): void {
  clearToastTimer(toast);
  const movesFocus = toast.contains(document.activeElement);
  const sibling = toast.nextElementSibling ?? toast.previousElementSibling;
  toast.remove();
  if (movesFocus) {
    const fallback =
      sibling instanceof HTMLElement ? sibling : document.getElementById("notification-bell");
    fallback?.focus({ preventScroll: true });
  }
}

function scheduleToastDismiss(toast: HTMLElement): void {
  clearToastTimer(toast);
  toastTimers.set(
    toast,
    window.setTimeout(() => {
      dismissToast(toast);
    }, TOAST_TIMEOUT_MS),
  );
}

// The transient notification toasts currently in the host — the evictable set. Excludes the persistent
// connection-lost hint, the persistent SW update toast AND the offline-write notice (FIX 4), so a burst of
// ≥ MAX_TOASTS notifications can never evict the "new version available" prompt (its Refresh action must
// survive) nor the "your change wasn't saved" notice.
function transientToasts(host: HTMLElement): HTMLElement[] {
  return Array.from(host.querySelectorAll<HTMLElement>(".toast")).filter(
    (toast) =>
      toast.id !== "notif-connection-lost" &&
      toast.id !== "sw-update-toast" &&
      toast.id !== "htmx-offline-toast",
  );
}

// Build + show an on-brand toast. Text is set via textContent (XSS-safe); the .toast CSS (app.css) is
// reduced-motion safe via the global prefers-reduced-motion rule. Auto-dismisses (paused while hovered/
// focused); a dismiss button clears it early; concurrent toasts are capped (M4).
function showNotificationToast(message: string): void {
  const host = document.getElementById("toast-host");
  if (host === null || message.length === 0) {
    return;
  }

  // M4: keep at most MAX_TOASTS by dropping the oldest toast the user is NOT reading/interacting with. If
  // every toast is hovered/focused, allow overflow — the host's max-height + overflow-y scroll covers it.
  for (let list = transientToasts(host); list.length >= MAX_TOASTS; list = transientToasts(host)) {
    const removable = list.find(
      (toast) => !toast.matches(":hover") && !toast.contains(document.activeElement),
    );
    if (removable === undefined) {
      break;
    }
    dismissToast(removable);
  }

  const toast = document.createElement("div");
  toast.className = "toast";
  toast.tabIndex = -1; // focusable so dismiss can move focus here from a removed sibling.

  const text = document.createElement("p");
  text.className = "toast-text";
  text.textContent = message; // NEVER innerHTML — the message contains a user-controlled display name.

  const link = document.createElement("a");
  link.className = "toast-link";
  link.href = "/notifications"; // safe default deep link (per-type coords aren't available client-side)
  link.textContent = "View";

  const dismiss = document.createElement("button");
  dismiss.type = "button";
  dismiss.className = "toast-dismiss";
  dismiss.setAttribute("aria-label", "Dismiss notification");
  dismiss.textContent = "×"; // ×
  dismiss.addEventListener("click", () => {
    dismissToast(toast);
  });

  // M4: pause the auto-dismiss while the toast is hovered or holds focus; resume on leave/blur.
  toast.addEventListener("pointerenter", () => {
    clearToastTimer(toast);
  });
  toast.addEventListener("focusin", () => {
    clearToastTimer(toast);
  });
  toast.addEventListener("pointerleave", () => {
    scheduleToastDismiss(toast);
  });
  toast.addEventListener("focusout", (event) => {
    const next = (event as FocusEvent).relatedTarget;
    if (next instanceof Node && toast.contains(next)) {
      return; // focus merely moved between the toast's own controls
    }
    scheduleToastDismiss(toast);
  });

  toast.append(text, link, dismiss);
  host.append(toast);
  scheduleToastDismiss(toast);
}

// Update the bell's unread badge: refresh data-unread-count + visible text (cap "9+"), hide at zero, and keep
// the bell's accessible name in sync (M1). Uses the real count in the aria-label (like the server) but caps
// the visible badge text.
function updateUnreadBadge(count: number): void {
  const safe = Number.isFinite(count) ? Math.max(0, Math.trunc(count)) : 0;

  const badge = document.getElementById("notification-unread-badge");
  if (badge !== null) {
    badge.setAttribute("data-unread-count", String(safe));
    badge.textContent = safe > 9 ? "9+" : String(safe);
    // H1: toggle the SAME `hidden` CLASS the server + Tailwind govern visibility with — the DOM `hidden`
    // PROPERTY was overridden by the badge's own `.inline-flex` utility, so it never actually hid/showed it.
    badge.classList.toggle("hidden", safe === 0);
  }

  // M1: the server-set aria-label ("Notifications, N unread") was never refreshed live.
  const bell = document.getElementById("notification-bell");
  if (bell !== null) {
    bell.setAttribute("aria-label", safe > 0 ? `Notifications, ${safe} unread` : "Notifications");
  }
}

// One-shot, unobtrusive "live updates paused" hint when the connection is permanently closed (automatic
// reconnect has already been retrying between onreconnecting/onreconnected — onclose means it gave up).
function showConnectionLostHint(): void {
  const host = document.getElementById("toast-host");
  if (host === null || document.getElementById("notif-connection-lost") !== null) {
    return;
  }
  const hint = document.createElement("div");
  hint.id = "notif-connection-lost";
  hint.className = "toast toast-muted";

  const text = document.createElement("p");
  text.className = "toast-text";
  text.textContent = "Live updates paused — refresh the page to reconnect.";

  hint.append(text);
  host.append(hint);
}

async function connectNotifications(): Promise<void> {
  // Auth guard: the badge is rendered only for signed-in users, so no badge → not authenticated → no hub.
  if (document.getElementById("notification-unread-badge") === null) {
    return;
  }

  // Milestone 6.4 (§6.5, backlog 3.5): lazy-load @microsoft/signalr (~56 KB) ONLY here, AFTER the authed-only
  // badge gate, via a dynamic import() so esbuild emits it as a separate content-hashed /dist chunk. Anonymous
  // pages never reach this line, so they never download it — the shared site-*.js bundle shrinks accordingly.
  // CSP-safe: the chunk is same-origin under /dist (script-src 'self' covers it); SignalR uses fetch/WebSocket,
  // no eval. A load failure degrades to the server's no-JS unread-count polling.
  let signalr: typeof import("@microsoft/signalr");
  try {
    signalr = await import("@microsoft/signalr");
  } catch (error: unknown) {
    console.warn("Cinora notifications: realtime client failed to load; falling back to polling.", error);
    return;
  }

  const connection = new signalr.HubConnectionBuilder()
    .withUrl("/hubs/notifications")
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .build();

  connection.on("ReceiveNotification", (dto: NotificationDto) => {
    const actor = typeof dto.ActorDisplayName === "string" ? dto.ActorDisplayName : "";
    const message =
      typeof dto.Message === "string" && dto.Message.length > 0
        ? dto.Message
        : actor.length > 0
          ? `${actor} sent you a notification`
          : "You have a new notification";
    showNotificationToast(message); // the authoritative badge count arrives via UnreadCountChanged below
  });

  connection.on("UnreadCountChanged", (count: number) => {
    updateUnreadBadge(count);
  });

  connection.onclose(() => {
    showConnectionLostHint();
  });

  connection.start().catch((error: unknown) => {
    // Never throw from startup — the server's no-JS unread-count polling keeps the badge fresh.
    console.warn("Cinora notifications: realtime connection failed to start; falling back to polling.", error);
  });
}

// --- Notifications inbox load-more a11y (Milestone 3.5) ---------------------------------------------------
// The /notifications inbox is a keyset load-more list (#notification-items, tabindex="-1") with a polite
// #notification-status node and data-notification-announce carriers that nothing wrote. This mirrors the feed
// handler, scoped to the /notifications/feed path (fallback: the #notification-items region) so it
// early-returns for every other swap: (M2) announce the newest carrier; (M3) restore focus after the
// error-retry outerHTML swap drops it to <body>.

function notificationItemsRegion(): HTMLElement | null {
  return document.getElementById("notification-items");
}

function isNotificationSwap(target: EventTarget | null, config: HtmxRequestConfigLike | undefined): boolean {
  if ((config?.path ?? "").startsWith("/notifications/feed")) {
    return true;
  }
  const region = notificationItemsRegion();
  return (
    region !== null &&
    target instanceof Element &&
    (region === target || region.contains(target) || target.contains(region))
  );
}

let notificationFocusWasInSwap = false;

function onNotificationBeforeSwap(event: Event): void {
  const target = event.target;
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  notificationFocusWasInSwap =
    isNotificationSwap(target, detail.requestConfig) &&
    target instanceof Element &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onNotificationAfterSwap(event: Event): void {
  const target = event.target;
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  if (!isNotificationSwap(target, detail.requestConfig) || !(target instanceof Element)) {
    return;
  }
  const region = notificationItemsRegion();

  // M2: announce the newest concise carrier from the swapped-in sentinel / terminal.
  const scope = region ?? target;
  const carriers = scope.querySelectorAll<HTMLElement>("[data-notification-announce]");
  const latest = carriers.item(carriers.length - 1);
  const status = document.getElementById("notification-status");
  if (latest !== null && status !== null) {
    status.textContent = latest.getAttribute("data-notification-announce") ?? "";
  }

  // M3: restore focus if the swap (e.g. the error-retry) destroyed the focused control and it fell to <body>.
  if (notificationFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const anchor = region ?? (target instanceof HTMLElement ? target : null);
      anchor?.focus({ preventScroll: true });
    }
  }
  notificationFocusWasInSwap = false;
}

document.addEventListener("htmx:beforeSwap", onNotificationBeforeSwap);
document.addEventListener("htmx:afterSwap", onNotificationAfterSwap);

// --- Settings (profile + avatar) region a11y (Milestone 4.2 Step C) --------------------------------------
// The owner-only settings page has two HTMX-swapped regions: #current-avatar (the avatar upload/replace/remove
// fragment) and #profile-form (the display-name + privacy form). Mirroring the 3.1 review pattern (component-body
// JS, NOT hx-on → CSP-safe; scoped so other pages' swaps early-return, and this early-returns off theirs), this
// block:
//   * announces a CONCISE status to the persistent polite #settings-status node (never the form contents);
//   * restores focus after a swap that destroyed the focused control (the profile Save button) → to the swapped
//     region (both carry tabindex="-1"), so keyboard/SR focus is never dropped to <body>;
//   * THE KEY STEP-C GAP: renders a concise inline role="alert" into #settings-error on a non-2xx HTMX response —
//     a bad-type/oversize avatar or an invalid profile update returns a 400 ProblemDetails that HTMX will NOT swap,
//     so the user would otherwise see nothing. Never renders the raw ProblemDetails body.

const SETTINGS_REGION_IDS = ["current-avatar", "profile-form", "notification-prefs-form"];

// The #current-avatar / #profile-form region a swap touched, or null when the swap was elsewhere.
function settingsRegionOf(node: EventTarget | null): HTMLElement | null {
  if (!(node instanceof Element)) {
    return null;
  }
  for (const id of SETTINGS_REGION_IDS) {
    const region = document.getElementById(id);
    if (region !== null && (region === node || region.contains(node) || node.contains(region))) {
      return region;
    }
  }
  return null;
}

function settingsErrorRegion(): HTMLElement | null {
  return document.getElementById("settings-error");
}

function hideSettingsError(): void {
  const el = settingsErrorRegion();
  if (el !== null) {
    el.textContent = "";
    el.classList.add("hidden");
  }
}

function showSettingsError(message: string): void {
  const el = settingsErrorRegion();
  if (el !== null) {
    // Unhide BEFORE writing text: mutating an assertive role="alert" while it is
    // display:none fails to announce on some SR/browser combos.
    el.classList.remove("hidden");
    el.textContent = message;
  }
}

function announceSettings(message: string): void {
  const status = document.getElementById("settings-status");
  if (status !== null && message.length > 0) {
    status.textContent = message;
  }
}

// A concise, deterministic status derived from the settings write (not read from the swapped-in content).
function settingsAnnouncement(verb: string, path: string): string {
  if (path.includes("/settings/profile/avatar")) {
    return verb === "delete" ? "Avatar removed" : "Avatar updated";
  }
  if (path.startsWith("/settings/notifications")) {
    return "Notification preferences saved";
  }
  if (path.startsWith("/settings/profile")) {
    return "Profile saved";
  }
  return "";
}

let settingsFocusWasInSwap = false;

function onSettingsBeforeSwap(event: Event): void {
  const target = event.target;
  settingsFocusWasInSwap =
    settingsRegionOf(target) !== null &&
    target instanceof Element &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onSettingsAfterSwap(event: Event): void {
  const target = event.target;
  const region = settingsRegionOf(target);
  if (region === null || !(target instanceof Element)) {
    return;
  }

  // A successful settings swap clears any prior inline error.
  hideSettingsError();

  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  announceSettings(
    settingsAnnouncement(
      detail.requestConfig?.verb?.toLowerCase() ?? "",
      detail.requestConfig?.path ?? "",
    ),
  );

  // Restore focus only if the swap destroyed the focused control (e.g. the profile Save button), so focus never
  // falls to <body>. The avatar Upload / Remove buttons live OUTSIDE #current-avatar, so uploads keep their focus
  // and this is a no-op for them.
  if (settingsFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      region.focus({ preventScroll: true });
    }
  }
  settingsFocusWasInSwap = false;
}

function onSettingsResponseError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const verb = detail.requestConfig?.verb?.toLowerCase() ?? "";
  const path = detail.requestConfig?.path ?? "";
  if (path.startsWith("/settings/notifications")) {
    const status = detail.xhr?.status ?? 0;
    showSettingsError(
      status === 429 ? TOO_FAST : "Couldn't save your notification preferences — please try again.",
    );
    return;
  }
  if (!path.startsWith("/settings/profile")) {
    return;
  }
  const httpStatus = detail.xhr?.status ?? 0;
  if (httpStatus === 429) {
    showSettingsError(TOO_FAST);
    return;
  }
  if (path.includes("/settings/profile/avatar")) {
    if (verb === "delete") {
      showSettingsError("Couldn't remove your photo — please try again.");
    } else if (httpStatus === 413) {
      showSettingsError("That image is too large — choose a JPEG, PNG or WebP within the size limit.");
    } else {
      showSettingsError("Couldn't upload that image — choose a JPEG, PNG or WebP within the size limit.");
    }
    return;
  }
  // The profile display-name + privacy update.
  showSettingsError("Couldn't save your profile — please check your display name and try again.");
}

document.addEventListener("htmx:beforeSwap", onSettingsBeforeSwap);
document.addEventListener("htmx:afterSwap", onSettingsAfterSwap);
document.addEventListener("htmx:responseError", onSettingsResponseError);

// --- Watchlist control a11y (Milestone 4.3) --------------------------------------------------------------
// The reusable _WatchlistControl (Details, rails, search cards) swaps its OWN outerHTML on a set/remove.
// Mirroring the established region pattern (component-body JS, NOT hx-on → CSP-safe; scoped to /watchlist
// WRITES so every other swap early-returns here, and the search/review/etc. handlers early-return for these),
// this block:
//   * announces a CONCISE status to the shared polite #watchlist-status node, read from a data-wl-announce
//     carrier the swapped-in control renders (the phrase lives once, in Razor — the control itself is NOT
//     aria-live, which would re-read on every navigation);
//   * restores focus after the outerHTML swap destroys the focused trigger/menu-item → to the re-rendered
//     control's [data-wl-trigger], so focus never falls to <body>;
//   * surfaces a concise inline role="alert" in the shared #watchlist-error node on a failed write
//     (htmx:responseError — a 429/500 HTMX won't swap), cleared on the next successful swap.

function watchlistErrorRegion(): HTMLElement | null {
  return document.getElementById("watchlist-error");
}

function hideWatchlistError(): void {
  const el = watchlistErrorRegion();
  if (el !== null) {
    el.textContent = "";
    el.classList.add("hidden");
  }
}

function showWatchlistError(message: string): void {
  const el = watchlistErrorRegion();
  if (el !== null) {
    // Unhide BEFORE writing text: mutating an assertive role="alert" while it is display:none fails to
    // announce on some SR/browser combos.
    el.classList.remove("hidden");
    el.textContent = message;
  }
}

function announceWatchlist(message: string): void {
  const status = document.getElementById("watchlist-status");
  if (status !== null && message.length > 0) {
    status.textContent = message;
  }
}

// A watchlist WRITE (set/remove) — not the GET /watchlist/control lazy-hydrate (announcing on a hydrate, or on
// the initial page render, would be wrong). Scoped by the request path so it is reliable across outerHTML swaps.
function isWatchlistWrite(config: HtmxRequestConfigLike | undefined): boolean {
  const verb = config?.verb?.toLowerCase() ?? "";
  const path = config?.path ?? "";
  return (verb === "post" || verb === "delete") && path.startsWith("/watchlist");
}

// The re-rendered control the swap produced (the target itself, a descendant, or its wrapper).
function watchlistControlOf(target: Element): HTMLElement | null {
  if (target.matches("[data-watchlist-control]")) {
    return target as HTMLElement;
  }
  return (
    target.querySelector<HTMLElement>("[data-watchlist-control]") ??
    target.closest<HTMLElement>("[data-watchlist-control]")
  );
}

let watchlistFocusWasInSwap = false;

function onWatchlistBeforeSwap(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const target = event.target;
  watchlistFocusWasInSwap =
    isWatchlistWrite(detail.requestConfig) &&
    target instanceof Element &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onWatchlistAfterSwap(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const target = event.target;
  if (!isWatchlistWrite(detail.requestConfig) || !(target instanceof Element)) {
    return;
  }

  const control = watchlistControlOf(target);

  // A successful write clears any prior inline error (on every surface the control appears on).
  hideWatchlistError();

  // On the /watchlist PAGE an in-place remove is owned by the 4.4 page block below (it removes the whole card
  // <li>, decrements the filter-chip counts, announces to #watchlist-page-status and moves focus). Defer to it
  // here so the two never double-announce or double-focus; the inline error was already cleared above.
  if (isWatchlistPageRemoval(target, detail.requestConfig)) {
    watchlistFocusWasInSwap = false;
    return;
  }

  // Announce the new state from the swapped-in control's carrier.
  announceWatchlist(control?.getAttribute("data-wl-announce") ?? "");

  // On the /watchlist PAGE an in-place STATUS CHANGE is finished by the 4.4 page block, which OWNS FOCUS: it
  // re-buckets the filter-chip counts, rewrites the card's data-watchlist-status, and — if the new status leaves
  // the active filter — removes the card <li> and re-homes focus. Keep the announce above (the status-change
  // phrase lives once, on the control's data-wl-announce carrier), but DEFER focus so we never restore it to a
  // trigger the 4.4 block is about to destroy. Off the page isWatchlistPageStatusChange is false → focus stays.
  if (isWatchlistPageStatusChange(target, detail.requestConfig)) {
    watchlistFocusWasInSwap = false;
    return;
  }

  // Restore focus if the swap destroyed the focused trigger/menu-item and it fell to <body>.
  if (watchlistFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const trigger = control?.querySelector<HTMLElement>("[data-wl-trigger]") ?? control;
      trigger?.focus({ preventScroll: true });
    }
  }
  watchlistFocusWasInSwap = false;
}

function onWatchlistResponseError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  if (!isWatchlistWrite(detail.requestConfig)) {
    return;
  }
  const httpStatus = detail.xhr?.status ?? 0;
  showWatchlistError(httpStatus === 429 ? TOO_FAST : "Couldn't update your watchlist — please try again.");
}

document.addEventListener("htmx:beforeSwap", onWatchlistBeforeSwap);
document.addEventListener("htmx:afterSwap", onWatchlistAfterSwap);
document.addEventListener("htmx:responseError", onWatchlistResponseError);

// --- Watchlist page a11y (Milestone 4.4) -----------------------------------------------------------------
// The /watchlist page is a keyset load-more grid (#watchlist-items, tabindex="-1") with a polite
// #watchlist-page-status node, and each card carries the reusable _WatchlistControl. Scoped to that grid + the
// /watchlist/page request path (every other swap early-returns here; the 4.3 control handlers defer to this for
// the removal and status-change cases), this block owns THREE page-specific concerns the 4.3 control block does not:
//   (A) LOAD-MORE / ERROR-RETRY (GET /watchlist/page): copy the newest data-watchlist-announce carrier into
//       #watchlist-page-status ("N more titles loaded" / "You've reached the end"), and restore focus after an
//       error-retry swap that would otherwise drop the destroyed Retry button's focus to <body>.
//   (B) LIVE REMOVAL (DELETE /watchlist from a card ON THIS PAGE): the swap returns the null-status "Add"
//       control, which reads as confusing on a "my watchlist" surface — so instead remove the whole card <li>,
//       decrement the matching filter-chip count + the "All" total, announce it politely, move focus to the
//       next card (else the grid / the revealed empty state), and re-evaluate the empty state.
//   (C) LIVE STATUS CHANGE (POST /watchlist from a card ON THIS PAGE): the control swaps itself to the new
//       status, but the card's data-watchlist-status, the filter-chip counts and (in a filtered view) its
//       membership go stale. Re-sync them silently (the 4.3 block announces the change once); if the new status
//       leaves the active filter, drop the card <li> and re-home focus via the same helpers as (B).
// All component-body JS (NOT hx-on) → the strict CSP is untouched.

function watchlistGrid(): HTMLElement | null {
  return document.getElementById("watchlist-items");
}

// (B) A successful in-place REMOVE (DELETE /watchlist) from a card inside the page grid. The 4.3 control
// handlers early-return on this so only this block acts: a load-more is a GET and a status set is a POST (so
// neither matches), and a remove from Details/rails/search has no #watchlist-items ancestor (grid.contains is
// false), so it is skipped there too. This is a function declaration so the 4.3 guard above can call it (hoisted).
function isWatchlistPageRemoval(
  target: EventTarget | null,
  config: HtmxRequestConfigLike | undefined,
): boolean {
  const verb = config?.verb?.toLowerCase() ?? "";
  const path = config?.path ?? "";
  const grid = watchlistGrid();
  return (
    verb === "delete" &&
    path.startsWith("/watchlist") &&
    grid !== null &&
    target instanceof Element &&
    grid.contains(target)
  );
}

// (C) A successful in-place STATUS CHANGE (POST /watchlist) from a card inside the page grid. Mutually exclusive
// with the removal (DELETE) and load-more (GET /watchlist/page) predicates. Off the /watchlist page there is no
// #watchlist-items ancestor, so this is false and the 4.3 control handler keeps its own announce + focus. A
// function declaration so the 4.3 handler (above) can call it (hoisted, like isWatchlistPageRemoval).
function isWatchlistPageStatusChange(
  target: EventTarget | null,
  config: HtmxRequestConfigLike | undefined,
): boolean {
  const verb = config?.verb?.toLowerCase() ?? "";
  const path = config?.path ?? "";
  const grid = watchlistGrid();
  return (
    verb === "post" &&
    path.startsWith("/watchlist") &&
    !path.startsWith("/watchlist/page") &&
    grid !== null &&
    target instanceof Element &&
    grid.contains(target)
  );
}

// (A) A load-more / error-retry page read. Scoped by the /watchlist/page path so it is reliable across the
// outerHTML sentinel swaps (mirrors the feed's /home/feed scoping); the set/remove writes hit /watchlist with
// no /page segment, so they never match here.
function isWatchlistPageLoadMore(config: HtmxRequestConfigLike | undefined): boolean {
  const verb = config?.verb?.toLowerCase() ?? "";
  return verb === "get" && (config?.path ?? "").startsWith("/watchlist/page");
}

function announceWatchlistPage(message: string): void {
  const status = document.getElementById("watchlist-page-status");
  if (status !== null && message.length > 0) {
    status.textContent = message;
  }
}

// Adjust a filter-chip count in place (no re-render). Keys are the enum names the chips carry
// (data-watchlist-count="PlanToWatch|Watching|Watched|All").
function adjustWatchlistCount(key: string, delta: number): void {
  const el = document.querySelector<HTMLElement>(`[data-watchlist-count="${key}"]`);
  if (el === null) {
    return;
  }
  const current = Number.parseInt(el.textContent ?? "0", 10);
  const next = Math.max(0, (Number.isNaN(current) ? 0 : current) + delta);
  el.textContent = String(next);
}

// The nearest sibling <li> that still holds a real card (skips the load-more sentinel / terminal <li>), for
// moving focus after a removal — next first, then previous.
function adjacentWatchlistCardLi(li: HTMLElement): HTMLElement | null {
  for (let sib = li.nextElementSibling; sib !== null; sib = sib.nextElementSibling) {
    if (sib instanceof HTMLElement && sib.querySelector("[data-watchlist-card]") !== null) {
      return sib;
    }
  }
  for (let sib = li.previousElementSibling; sib !== null; sib = sib.previousElementSibling) {
    if (sib instanceof HTMLElement && sib.querySelector("[data-watchlist-card]") !== null) {
      return sib;
    }
  }
  return null;
}

// The grid is truly empty only when it holds no card AND no pending load-more sentinel (a sentinel carries the
// only hx-get inside the grid; the terminal "end" marker does not). A grid with a pending sentinel will refill,
// so the empty state must not flash.
function watchlistGridIsEmpty(grid: HTMLElement): boolean {
  return (
    grid.querySelector("[data-watchlist-card]") === null && grid.querySelector("[hx-get]") === null
  );
}

// The active status filter on the /watchlist page ("PlanToWatch" | "Watching" | "Watched"), or null for the "All"
// view. Read from the aria-current chip's own count KEY so it is the canonical enum name (robust to any URL-casing
// quirk); the chips are always present whenever the grid is. The key matches the values data-watchlist-status /
// data-wl-status / data-watchlist-count all share.
function activeWatchlistFilter(): string | null {
  const key = document
    .querySelector<HTMLElement>('[aria-current="page"] [data-watchlist-count]')
    ?.getAttribute("data-watchlist-count");
  return key != null && key.length > 0 && key !== "All" ? key : null;
}

// Reveal the hidden live-empty block and (L1) strip any leftover non-card marker <li> — the terminal "You've
// reached the end" sentinel — so it can't sit beside the revealed empty state. Only ever called once the grid
// holds no card and no pending sentinel, so every remaining <li> is a spent marker. Returns the block (or null).
function revealWatchlistEmptyState(grid: HTMLElement): HTMLElement | null {
  grid.querySelectorAll<HTMLElement>(":scope > li").forEach((li) => {
    if (li.querySelector("[data-watchlist-card]") === null) {
      li.remove();
    }
  });
  const emptyBlock = document.querySelector<HTMLElement>("[data-watchlist-live-empty]");
  if (emptyBlock !== null) {
    emptyBlock.hidden = false;
  }
  return emptyBlock;
}

// Optional polite announcements for a card drop — omitted when the caller already announced (a status change is
// announced once by the 4.3 control handler, so the page drop stays silent).
interface WatchlistDropMessages {
  removed?: string;
  empty?: string;
}

// Shared by the live REMOVE (B) and the filtered-out STATUS CHANGE (C): drop a card <li>, then reveal the empty
// state (L1-clean) or move focus to the nearest surviving card. Count re-bucketing and any re-announce are the
// caller's job (they differ between a remove and a status change); this owns only the DOM removal, focus, and the
// optional empty/removed announcement.
function dropWatchlistCardLi(cardLi: HTMLElement, messages: WatchlistDropMessages): void {
  const grid = watchlistGrid();
  if (grid === null) {
    return;
  }
  const neighbor = adjacentWatchlistCardLi(cardLi);
  cardLi.remove();

  if (watchlistGridIsEmpty(grid)) {
    const emptyBlock = revealWatchlistEmptyState(grid);
    announceWatchlistPage(messages.empty ?? "");
    (emptyBlock ?? grid).focus({ preventScroll: true });
    return;
  }

  announceWatchlistPage(messages.removed ?? "");
  const focusTarget = neighbor?.querySelector<HTMLElement>("a") ?? grid;
  focusTarget.focus({ preventScroll: true });
}

// Perform the live removal (B): decrement the counts for the removed title, then drop its card <li> (which
// re-homes focus + reveals the empty state). The card root survived the control-only swap, so it still names which
// chip to decrement (the swapped-in control is now the null-status "Add" state and no longer knows).
function handleWatchlistRemoval(target: Element): void {
  const cardRoot =
    target.closest<HTMLElement>("[data-watchlist-card]") ??
    target.querySelector<HTMLElement>("[data-watchlist-card]");
  const cardLi = cardRoot?.closest("li") ?? null;
  if (cardRoot === null || cardLi === null) {
    return;
  }

  const status = cardRoot.getAttribute("data-watchlist-status") ?? "";
  if (status.length > 0) {
    adjustWatchlistCount(status, -1);
  }
  adjustWatchlistCount("All", -1);

  dropWatchlistCardLi(cardLi, {
    removed: "Removed from your watchlist.",
    empty: "Removed from your watchlist. Nothing left in this view.",
  });
}

// Set in htmx:beforeSwap — was focus inside the subtree the load-more / status-change swap is about to replace?
// (The removal path (B) manages its own focus and does not use this flag.)
let watchlistPageFocusWasInSwap = false;

// Perform the in-place STATUS CHANGE (C / fix H1): the control swapped itself to the new status, but the card
// root's data-watchlist-status, the filter-chip counts, and (in a filtered view) the card's membership are all
// stale. Re-sync them SILENTLY — the 4.3 control handler already announced the change and deferred focus to this
// block.
function handleWatchlistStatusChange(target: Element): void {
  const control = watchlistControlOf(target);
  const cardRoot =
    control?.closest<HTMLElement>("[data-watchlist-card]") ??
    target.closest<HTMLElement>("[data-watchlist-card]");
  if (cardRoot === null) {
    watchlistPageFocusWasInSwap = false;
    return;
  }

  const oldStatus = cardRoot.getAttribute("data-watchlist-status") ?? "";
  // The new status is read straight off the swapped-in control root: data-wl-status mirrors the resulting
  // WatchlistStatus and is present after every set POST. Do nothing if it is unreadable or unchanged.
  const newStatus = control?.getAttribute("data-wl-status") ?? "";
  if (newStatus.length === 0 || newStatus === oldStatus) {
    watchlistPageFocusWasInSwap = false;
    return;
  }

  // Re-bucket the filter-chip counts (oldStatus → newStatus). The "All" total is UNCHANGED by a status change (the
  // entry stays in the watchlist), and the card root now carries the new status so a later removal decrements the
  // right chip.
  cardRoot.setAttribute("data-watchlist-status", newStatus);
  if (oldStatus.length > 0) {
    adjustWatchlistCount(oldStatus, -1);
  }
  adjustWatchlistCount(newStatus, 1);

  // In a filtered view, a card whose new status no longer matches the active filter must leave this view. Reuse
  // the removal path's empty-state + focus logic, silently (the 4.3 handler owns the announce; this block owns
  // focus for every page status change, so dropping the card re-homes focus correctly).
  const filter = activeWatchlistFilter();
  const cardLi = cardRoot.closest("li");
  if (filter !== null && newStatus !== filter && cardLi !== null) {
    dropWatchlistCardLi(cardLi, {});
    watchlistPageFocusWasInSwap = false;
    return;
  }

  // The card stays. Restore focus to the re-rendered trigger if the swap dropped it to <body>.
  if (watchlistPageFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const trigger = control?.querySelector<HTMLElement>("[data-wl-trigger]") ?? control;
      trigger?.focus({ preventScroll: true });
    }
  }
  watchlistPageFocusWasInSwap = false;
}

function onWatchlistPageBeforeSwap(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const target = event.target;
  // Both the load-more / error-retry read AND the in-place status change can destroy the focused control and drop
  // focus to <body>; THIS block owns re-homing it in both cases. (The removal path (B) manages its own focus.)
  const managesFocus =
    isWatchlistPageLoadMore(detail.requestConfig) ||
    isWatchlistPageStatusChange(target, detail.requestConfig);
  watchlistPageFocusWasInSwap =
    managesFocus &&
    target instanceof Element &&
    document.activeElement instanceof Element &&
    target.contains(document.activeElement);
}

function onWatchlistPageAfterSwap(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const target = event.target;
  if (!(target instanceof Element)) {
    return;
  }

  // (B) live removal — owned here (the 4.3 control block defers to this).
  if (isWatchlistPageRemoval(target, detail.requestConfig)) {
    handleWatchlistRemoval(target);
    return;
  }

  // (C / fix H1) in-place status change — re-sync the card's status attribute, the filter-chip counts and the
  // filtered membership; own focus (the 4.3 control block announced the change and deferred focus to this).
  if (isWatchlistPageStatusChange(target, detail.requestConfig)) {
    handleWatchlistStatusChange(target);
    return;
  }

  // (A) load-more / error-retry — announce the newest carrier, restore lost focus.
  if (!isWatchlistPageLoadMore(detail.requestConfig)) {
    return;
  }
  const grid = watchlistGrid();
  const scope = grid ?? target;
  const carriers = scope.querySelectorAll<HTMLElement>("[data-watchlist-announce]");
  const latest = carriers.item(carriers.length - 1);
  if (latest !== null) {
    announceWatchlistPage(latest.getAttribute("data-watchlist-announce") ?? "");
  }

  if (watchlistPageFocusWasInSwap) {
    const active = document.activeElement;
    if (active === null || active === document.body) {
      const anchor = grid ?? (target instanceof HTMLElement ? target : null);
      anchor?.focus({ preventScroll: true });
    }
  }
  watchlistPageFocusWasInSwap = false;
}

document.addEventListener("htmx:beforeSwap", onWatchlistPageBeforeSwap);
document.addEventListener("htmx:afterSwap", onWatchlistPageAfterSwap);

// --- Recommendations "For You" dismiss a11y (Milestone 5.4) -----------------------------------------------
// Each _RecommendationCard's dismiss button hx-post's /recommendations/{id}/dismiss and swaps its OWN <li>
// outerHTML with an empty 200 — the card is removed (durable "not interested" is deferred, ADR 0018 §11).
// Mirroring the established region pattern (component-body JS, NOT hx-on → CSP-safe; scoped to /recommendations
// WRITES so every other swap early-returns here, and the search/review/watchlist handlers early-return for this):
//   * announce a concise "Dismissed {title}" (from the removed card's data-rec-announce carrier) into the shared
//     polite #recommendations-status node — read in beforeSwap while the card still exists;
//   * restore focus to an adjacent card (else the rail/page heading) when the removed card held focus and it fell
//     to <body>, so keyboard/SR focus is never stranded;
//   * surface the 429 too-fast copy (else a generic retry line) on a failed dismiss (htmx:responseError — a
//     429/500 HTMX will not swap, so the card stays with no feedback otherwise).

function isRecommendationDismiss(config: HtmxRequestConfigLike | undefined): boolean {
  const verb = config?.verb?.toLowerCase() ?? "";
  return verb === "post" && (config?.path ?? "").startsWith("/recommendations");
}

// The nearest sibling <li> that still holds a recommendation card (next first, then previous), for re-homing
// focus after a dismissal. Rec lists have no load-more sentinel, so every [data-rec-item] <li> is a real card.
function adjacentRecommendationCardLi(li: HTMLElement): HTMLElement | null {
  for (let sib = li.nextElementSibling; sib !== null; sib = sib.nextElementSibling) {
    if (sib instanceof HTMLElement && sib.matches("[data-rec-item]")) {
      return sib;
    }
  }
  for (let sib = li.previousElementSibling; sib !== null; sib = sib.previousElementSibling) {
    if (sib instanceof HTMLElement && sib.matches("[data-rec-item]")) {
      return sib;
    }
  }
  return null;
}

// Captured in htmx:beforeSwap (while the dismissed card still exists) — where to send focus if the removal
// strands it on <body>: an adjacent card's link, else the enclosing rec region's heading.
let recDismissFocusAnchor: HTMLElement | null = null;

function onRecommendationBeforeSwap(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const target = event.target;
  recDismissFocusAnchor = null;
  if (!isRecommendationDismiss(detail.requestConfig) || !(target instanceof Element)) {
    return;
  }

  // Announce NOW — the card's carrier is about to be removed with its <li>.
  const carrier = target.matches("[data-rec-announce]")
    ? target
    : target.querySelector("[data-rec-announce]");
  const status = document.getElementById("recommendations-status");
  if (carrier !== null && status !== null) {
    status.textContent = carrier.getAttribute("data-rec-announce") ?? "";
  }

  // Only bother capturing a focus anchor when focus is inside the card being removed.
  const li = target.closest<HTMLElement>("[data-rec-item]");
  const active = document.activeElement;
  if (li !== null && active instanceof Element && li.contains(active)) {
    const neighbor = adjacentRecommendationCardLi(li);
    recDismissFocusAnchor =
      neighbor?.querySelector<HTMLElement>("a") ??
      li.closest("[data-rec-region]")?.querySelector<HTMLElement>("h1, h2") ??
      null;
  }
}

// Fix 5 (UI L4): on the /recommendations PAGE, dismissing the LAST card empties the grid while the page <h1>
// still promises picks. Reveal the server-rendered-but-hidden honest empty stub ([data-rec-live-empty]) so the
// surface stays honest, and announce it politely. Scoped by the presence of BOTH the grid and the stub — the
// /home rail has neither, so this is a no-op there. Attribute/text toggle only (the stub's copy is server
// -rendered and trusted); never innerHTML.
function revealRecommendationsEmptyIfDrained(): void {
  const grid = document.getElementById("recommendations-grid");
  const liveEmpty = document.querySelector<HTMLElement>("[data-rec-live-empty]");
  if (grid === null || liveEmpty === null || !liveEmpty.hidden) {
    return;
  }
  if (grid.querySelector("[data-rec-item]") !== null) {
    return; // cards remain — nothing to reveal.
  }
  liveEmpty.hidden = false;
  const status = document.getElementById("recommendations-status");
  if (status !== null) {
    status.textContent = "That's all your picks for now.";
  }
}

function onRecommendationAfterSwap(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  if (!isRecommendationDismiss(detail.requestConfig)) {
    return;
  }

  // Runs regardless of whether the dismissed card held focus (so the empty state appears even for a mouse
  // dismiss). No-op unless this is the /recommendations page and the grid is now empty.
  revealRecommendationsEmptyIfDrained();

  const anchor = recDismissFocusAnchor;
  recDismissFocusAnchor = null;
  if (anchor === null) {
    return; // the removed card did not hold focus — leave focus where it is.
  }

  const active = document.activeElement;
  if (active === null || active === document.body) {
    if (!anchor.hasAttribute("tabindex")) {
      anchor.setAttribute("tabindex", "-1");
    }
    anchor.focus({ preventScroll: true });
  }
}

function onRecommendationResponseError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  if (!isRecommendationDismiss(detail.requestConfig)) {
    return;
  }
  const httpStatus = detail.xhr?.status ?? 0;
  const status = document.getElementById("recommendations-status");
  if (status !== null) {
    status.textContent = httpStatus === 429 ? TOO_FAST : "Couldn't dismiss that — please try again.";
  }
}

document.addEventListener("htmx:beforeSwap", onRecommendationBeforeSwap);
document.addEventListener("htmx:afterSwap", onRecommendationAfterSwap);
document.addEventListener("htmx:responseError", onRecommendationResponseError);

// --- Styled delete-confirm (Milestone 6.5, backlog 3.1) --------------------------------------------------
// Replace the native window.confirm / hx-confirm on destructive actions with the CSP-safe dark-glass dialog
// (confirmDialog component + _ConfirmDialog.cshtml). HTMX fires htmx:confirm before EVERY request; when the
// triggering control carries the structured data-confirm-* attributes we DEFER the request (preventDefault),
// show the styled dialog, and only on confirm re-issue the SAME request via detail.issueRequest(true) — the
// exact hx-delete/hx-post + anti-forgery flow is unchanged (the confirm only gates it). Controls WITHOUT the
// data attributes proceed normally (there is no hx-confirm on them, so htmx never shows a native confirm).

interface HtmxConfirmDetail {
  elt: Element;
  issueRequest(skipConfirmation: boolean): void;
  question?: string | null;
}

function onHtmxConfirm(event: Event): void {
  const detail = (event as CustomEvent<HtmxConfirmDetail>).detail;
  const trigger = detail.elt.closest<HTMLElement>("[data-confirm-title]");
  if (trigger === null) {
    return; // no styled confirmation requested — let HTMX proceed (no native confirm on these elements).
  }
  event.preventDefault(); // defer; issueRequest(true) runs the identical request iff the user confirms.
  void requestConfirmation({
    title: trigger.getAttribute("data-confirm-title") ?? "Are you sure?",
    message: trigger.getAttribute("data-confirm-message") ?? "",
    action: trigger.getAttribute("data-confirm-action") ?? "Confirm",
    tone: trigger.getAttribute("data-confirm-tone") ?? "danger",
    trigger,
  }).then((confirmed) => {
    if (confirmed) {
      detail.issueRequest(true);
    }
  });
}

document.addEventListener("htmx:confirm", onHtmxConfirm);

// --- Offline write affordance (Milestone 6.5, §7 — closes a 6.3-backlog gap) ------------------------------
// Every region handler above catches htmx:responseError (a completed non-2xx response) but NOT htmx:sendError —
// the event HTMX fires when the request never reaches the server (a network/offline transport failure). Without
// this, an offline WRITE swaps nothing and announces nothing. This shared handler surfaces ONE polite,
// non-blocking toast (reusing the #toast-host grammar; textContent only — never innerHTML) telling the user the
// change wasn't saved. Singleton (id-guarded) so a burst of failed writes can't stack toasts (it never double
// -fires); scoped to WRITES (non-GET) so an offline read's load-more failure stays quiet.

const OFFLINE_WRITE_MESSAGE = "You appear to be offline — your change wasn't saved. Try again.";
const OFFLINE_TOAST_ID = "htmx-offline-toast";

function showOfflineWriteToast(): void {
  const host = document.getElementById("toast-host");
  if (host === null || document.getElementById(OFFLINE_TOAST_ID) !== null) {
    return; // no host, or the offline notice is already showing (singleton — never double-fires).
  }
  const toast = document.createElement("div");
  toast.id = OFFLINE_TOAST_ID;
  toast.className = "toast toast-muted";
  toast.tabIndex = -1; // focusable so dismissToast can restore focus (to a sibling toast / the bell) from here.

  const text = document.createElement("p");
  text.className = "toast-text";
  text.textContent = OFFLINE_WRITE_MESSAGE;

  const dismiss = document.createElement("button");
  dismiss.type = "button";
  dismiss.className = "toast-dismiss";
  dismiss.setAttribute("aria-label", "Dismiss offline notice");
  dismiss.textContent = "×";
  dismiss.addEventListener("click", () => {
    dismissToast(toast); // FIX 3: the same dismissal machinery as the notification toasts — restores focus, not <body>.
  });

  // FIX 3: pause the auto-dismiss while the toast is hovered or holds focus; resume on leave/blur — so the timer
  // can't remove() the toast out from under a hovering/keyboard user and drop focus to <body> (WCAG 2.4.3).
  toast.addEventListener("pointerenter", () => {
    clearToastTimer(toast);
  });
  toast.addEventListener("focusin", () => {
    clearToastTimer(toast);
  });
  toast.addEventListener("pointerleave", () => {
    scheduleToastDismiss(toast);
  });
  toast.addEventListener("focusout", (event) => {
    const next = (event as FocusEvent).relatedTarget;
    if (next instanceof Node && toast.contains(next)) {
      return; // focus merely moved between the toast's own controls
    }
    scheduleToastDismiss(toast);
  });

  toast.append(text, dismiss);
  host.append(toast);
  // FIX 3: the shared pause-on-hover/focus + focus-restoring auto-dismiss (was a bare setTimeout(... toast.remove())).
  scheduleToastDismiss(toast);
}

function onHtmxSendError(event: Event): void {
  const detail = (event as CustomEvent<HtmxDetailLike>).detail;
  const verb = detail.requestConfig?.verb?.toLowerCase() ?? "";
  // Writes only: an offline read (a GET load-more) simply doesn't advance; the write affordance is the ask.
  if (verb === "" || verb === "get") {
    return;
  }
  showOfflineWriteToast();
}

document.addEventListener("htmx:sendError", onHtmxSendError);

// Register every Alpine component BEFORE start(): x-data="search" / x-data="watchlistMenu" resolve names
// registered via Alpine.data, which must exist by the time Alpine walks the DOM.
registerSearch(Alpine);
registerWatchlistMenu(Alpine);
registerConfirmDialog(Alpine);
registerCommentCollapse(Alpine);
registerPasswordToggle(Alpine);
registerNavMenu(Alpine);
registerAccountMenu(Alpine);
registerTour(Alpine);
registerLanguageSelect(Alpine);

Alpine.start();

void connectNotifications();

// In-app chat (§6): lazy-load the chat client ONLY on the /chat surface (gated on #chat-root), mirroring the
// notifications lazy-load. esbuild code-splitting emits it as a separate content-hashed /dist chunk, so every
// other page's bundle is unaffected. A load failure degrades to the no-JS server-rendered thread (forms still
// post; only realtime/emoji/typing are lost).
if (document.getElementById("chat-root") !== null) {
  void import("./components/chat")
    .then((module) => {
      module.initChat();
    })
    .catch((error: unknown) => {
      console.warn("Cinora chat: client failed to load.", error);
    });
}

// --- PWA service worker: registration + versioned-update toast (Milestone 6.1, ADR 0019) ------------------
// Progressive enhancement: guarded by feature detection, so a browser without service workers runs fully
// online-only (nothing on the page depends on the SW). A new deploy rotates the SW __SW_VERSION__ (build.mjs),
// which the browser installs as the WAITING worker; we surface a CSP-safe "new version" toast (reusing the
// Phase-3 #toast-host grammar, built via DOM — no inline JS) and only adopt it on the user's explicit click,
// so an in-use page (a half-typed review) is never hijacked mid-session.

// Show the update toast at most once per page (a re-fired statechange must not stack toasts).
let swUpdateToastShown = false;

// Set true ONLY when the user clicks "Refresh" (consents to an update). The controllerchange reload is
// gated on it, so the first-ever install's clients.claim() (which fires controllerchange on an uncontrolled
// page) never reloads an in-use page unprompted (design §2.4) — only a consented update reloads, once.
let updateAccepted = false;

function showServiceWorkerUpdateToast(worker: ServiceWorker): void {
  const host = document.getElementById("toast-host");
  if (host === null || swUpdateToastShown) {
    return;
  }
  swUpdateToastShown = true;

  const toast = document.createElement("div");
  toast.id = "sw-update-toast";
  toast.className = "toast";
  // No role="status" here: #toast-host is already aria-live="polite", so a nested live region would
  // double-announce. Rely on the host (consistent with the notification toasts).

  const text = document.createElement("p");
  text.className = "toast-text";
  text.textContent = "A new version of Cinora is available.";

  const refresh = document.createElement("button");
  refresh.type = "button";
  refresh.className = "toast-link";
  refresh.textContent = "Refresh";
  refresh.addEventListener("click", () => {
    refresh.disabled = true;
    refresh.textContent = "Updating…";
    // Consent recorded: only now may the controllerchange listener reload the page (once). This single flag
    // covers both update paths (the already-waiting-on-load worker reuses this same handler).
    updateAccepted = true;
    // The SW's message handler calls skipWaiting(); the controllerchange listener below then reloads once.
    worker.postMessage("SKIP_WAITING");
  });

  const dismiss = document.createElement("button");
  dismiss.type = "button";
  dismiss.className = "toast-dismiss";
  dismiss.setAttribute("aria-label", "Dismiss update notice");
  dismiss.textContent = "×";
  dismiss.addEventListener("click", () => {
    toast.remove();
  });

  toast.append(text, refresh, dismiss);
  host.append(toast);
}

function registerServiceWorker(): void {
  if (!("serviceWorker" in navigator)) {
    return; // no SW support — the app runs online-only (progressive enhancement).
  }

  // The layout renders <meta name="cinora-sw" content="enabled"> ONLY outside Development. In Development the
  // flag is absent: never register the cache-first SW (it would serve stale /dist/* bundles and mask hot
  // edits), and actively unregister any SW left over from a prior Release run so it stops serving stale caches.
  if (document.querySelector('meta[name="cinora-sw"]') === null) {
    void navigator.serviceWorker.getRegistrations().then((registrations) => {
      for (const registration of registrations) {
        void registration.unregister();
      }
    });
    return;
  }

  // Reload exactly once when the freshly-activated worker takes control — but ONLY after the user consented to
  // an update (updateAccepted, set by the Refresh click). Without this gate a first-ever visit loads
  // uncontrolled, the SW then clients.claim()s it, and the resulting controllerchange would reload the page
  // unprompted for every new visitor (design §2.4 — never hijack an in-use page).
  let reloading = false;
  navigator.serviceWorker.addEventListener("controllerchange", () => {
    if (!updateAccepted || reloading) {
      return;
    }
    reloading = true;
    window.location.reload();
  });

  navigator.serviceWorker
    .register("/sw.js")
    .then((registration) => {
      // A worker already installed-and-waiting when the page loaded (updated on a prior visit).
      if (registration.waiting !== null && navigator.serviceWorker.controller !== null) {
        showServiceWorkerUpdateToast(registration.waiting);
      }

      registration.addEventListener("updatefound", () => {
        const installing = registration.installing;
        if (installing === null) {
          return;
        }
        installing.addEventListener("statechange", () => {
          // Prompt only when a controller already exists — otherwise this is the FIRST install (no prior
          // version to replace), which must not show an "update available" toast.
          if (installing.state === "installed" && navigator.serviceWorker.controller !== null) {
            showServiceWorkerUpdateToast(installing);
          }
        });
      });
    })
    .catch((error: unknown) => {
      // Never throw from startup — the app is fully functional without the SW.
      console.warn("Cinora: service worker registration failed; running without offline support.", error);
    });
}

// The SW-served /offline fallback (PwaController) carries a [data-offline-retry] control; wire it to reload
// the page (CSP-safe — bundled, not inline). A no-op on every other page.
function wireOfflineRetry(): void {
  const retry = document.querySelector<HTMLButtonElement>("[data-offline-retry]");
  retry?.addEventListener("click", () => {
    window.location.reload();
  });
}

// --- Web Push subscribe (Milestone 6.2, ADR 0020 §3.1) ---------------------------------------------------
// Progressive enhancement: the "Enable notifications" control in /settings/profile is server-rendered HIDDEN and
// revealed here ONLY when the browser supports service workers + the Push API. On an explicit click it requests
// permission (NEVER on page load — a page-load prompt earns a permanent origin denial), fetches the VAPID public
// key from /push/public-key (404 when push is unconfigured → stay quiet), subscribes, and POSTs the browser
// subscription to /push/subscribe with the anti-forgery token in the RequestVerificationToken header (the raw
// fetch must attach it itself — site.ts's HTMX hook doesn't cover it). CSP-safe (bundled, no inline). Success /
// permission-denied announce through the shared #settings-status polite node (announceSettings, hoisted above).

// The VAPID applicationServerKey must be a Uint8Array, not the raw base64url string. Backed by a concrete
// ArrayBuffer (not the default ArrayBufferLike) so it satisfies the BufferSource the Push API expects.
function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = "=".repeat((4 - (base64.length % 4)) % 4);
  const normalized = (base64 + padding).replace(/-/g, "+").replace(/_/g, "/");
  const raw = window.atob(normalized);
  const output = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i += 1) {
    output[i] = raw.charCodeAt(i);
  }
  return output;
}

// The VISIBLE push-subscribe outcome line ([data-push-status] in the "Push on this device" card). WHY (WCAG
// 4.1.3): a sighted user who denies/blocks push otherwise gets NO on-screen cue — the SR path rides the sr-only
// #settings-status (announceSettings). This mirrors the same outcome to a visible, aria-hidden node via textContent
// only (CSP-safe — never innerHTML). "error" tone tints text-danger (7:1 on the card), "info" text-ink-muted
// (10:1) — both clear AA. An empty message re-hides the line.
function setPushStatus(message: string, tone: "info" | "error"): void {
  const el = document.querySelector<HTMLElement>("[data-push-status]");
  if (el === null) {
    return;
  }
  el.textContent = message;
  el.hidden = message.length === 0;
  el.classList.remove("text-ink-muted", "text-danger");
  el.classList.add(tone === "error" ? "text-danger" : "text-ink-muted");
}

// Report an outcome down BOTH channels: the polite sr-only #settings-status (SR) and the visible [data-push-status]
// line (sighted). Kept in lockstep so the two never drift.
function reportPushOutcome(message: string, tone: "info" | "error"): void {
  announceSettings(message);
  setPushStatus(message, tone);
}

// --- Web Push: shared subscription helpers (Milestone 6.2 + mobile hardening) ----------------------------------
const PUSH_SUPPORTED =
  "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
const PUSH_DISMISS_KEY = "cinora-push-optin-dismissed";

function isIos(): boolean {
  return /iPad|iPhone|iPod/.test(navigator.userAgent);
}

// True when running as an installed home-screen PWA (iOS exposes Push/Notification ONLY in this mode).
function isStandalone(): boolean {
  const nav = navigator as Navigator & { standalone?: boolean };
  return nav.standalone === true || window.matchMedia("(display-mode: standalone)").matches;
}

async function fetchVapidKey(): Promise<string | null> {
  const response = await fetch("/push/public-key");
  if (!response.ok) {
    return null;
  }
  const key = (await response.text()).trim();
  return key.length > 0 ? key : null;
}

// POST a browser subscription to the server (attaching the anti-forgery token the raw fetch must carry itself).
async function postSubscription(subscription: PushSubscription): Promise<boolean> {
  const token = document.querySelector<HTMLMetaElement>(TOKEN_META_SELECTOR)?.content ?? "";
  const response = await fetch("/push/subscribe", {
    method: "POST",
    headers: { "Content-Type": "application/json", [ANTIFORGERY_HEADER]: token },
    body: JSON.stringify(subscription),
  });
  return response.ok;
}

// Reuse an existing browser subscription (re-POST to keep the server row fresh) or create one, then register it.
// The caller must have already ensured Notification.permission === "granted" (subscribe() would otherwise prompt).
async function ensureSubscription(): Promise<boolean> {
  const vapidKey = await fetchVapidKey();
  if (vapidKey === null) {
    return false;
  }
  const registration = await navigator.serviceWorker.ready;
  const existing = await registration.pushManager.getSubscription();
  const subscription =
    existing ??
    (await registration.pushManager.subscribe({
      userVisibleOnly: true, // WHY: required by Chrome — every push must show a notification.
      applicationServerKey: urlBase64ToUint8Array(vapidKey),
    }));
  return postSubscription(subscription);
}

// --- Web Push: turn OFF on this device (the in-app disable toggle) ----------------------------------------------
// The live browser subscription (or null). Used both to decide whether the settings control reads "Enable" or
// "Turn off" and to recover the endpoint to remove server-side. PUSH_SUPPORTED-guarded so it never touches an
// absent API, and only awaited when a subscription can exist (see refreshPushControlState) so it can't hang where
// no service worker is registered (development).
async function currentPushSubscription(): Promise<PushSubscription | null> {
  if (!PUSH_SUPPORTED) {
    return null;
  }
  const registration = await navigator.serviceWorker.ready;
  return registration.pushManager.getSubscription();
}

// Toggle the two settings controls: exactly one of Enable / Turn-off is shown at a time.
function setPushControlState(subscribed: boolean): void {
  const enableButton = document.querySelector<HTMLButtonElement>("[data-push-enable-button]");
  const disableButton = document.querySelector<HTMLButtonElement>("[data-push-disable-button]");
  if (enableButton !== null) {
    enableButton.hidden = subscribed;
  }
  if (disableButton !== null) {
    disableButton.hidden = !subscribed;
  }
}

// Reflect the live subscription state onto the controls. Consults the Push API only when permission is granted (a
// subscription can only exist then) — which also avoids awaiting serviceWorker.ready where no SW is registered.
async function refreshPushControlState(): Promise<void> {
  if (Notification.permission !== "granted") {
    setPushControlState(false);
    return;
  }
  setPushControlState((await currentPushSubscription()) !== null);
}

// POST the endpoint to /push/unsubscribe (idempotent server-side), attaching the anti-forgery token the raw fetch
// must carry itself (the HTMX hook doesn't cover a plain fetch).
async function postUnsubscribe(endpoint: string): Promise<boolean> {
  const token = document.querySelector<HTMLMetaElement>(TOKEN_META_SELECTOR)?.content ?? "";
  const response = await fetch("/push/unsubscribe", {
    method: "POST",
    headers: { "Content-Type": "application/json", [ANTIFORGERY_HEADER]: token },
    body: JSON.stringify({ endpoint }),
  });
  return response.ok;
}

// Turn push OFF on this device: unsubscribe the browser PushSubscription AND remove the server device row so no
// stale endpoint lingers on either side. Idempotent — with no live subscription it still flips the control to
// "Enable" and reports the off state. In-app inbox notifications are unaffected.
async function unsubscribeFromPush(button: HTMLButtonElement): Promise<void> {
  const originalLabel = button.textContent;
  button.disabled = true;
  button.textContent = "Turning off…";
  try {
    const subscription = await currentPushSubscription();
    if (subscription === null) {
      setPushControlState(false);
      reportPushOutcome("Push notifications are off for this device.", "info");
      return;
    }
    const { endpoint } = subscription;
    await subscription.unsubscribe(); // stop the browser delivering pushes to this endpoint.
    const ok = await postUnsubscribe(endpoint); // remove the server row (best-effort; the send path also prunes).
    setPushControlState(false);
    reportPushOutcome(
      ok
        ? "Push notifications are off for this device."
        : "Turned off on this device; the server row will be cleaned up automatically.",
      "info",
    );
  } catch (error: unknown) {
    console.warn("Cinora: turning off push notifications failed.", error);
    reportPushOutcome("Couldn't turn off push notifications — please try again.", "error");
  } finally {
    button.disabled = false;
    button.textContent = originalLabel;
  }
}

async function subscribeToPush(button: HTMLButtonElement): Promise<void> {
  // Visible busy affordance (WHY: the subscribe round-trip is async — a static, reduced-motion-safe "Enabling…"
  // label + the .btn:disabled dim tells the user the click registered). Restored in finally.
  const originalLabel = button.textContent;
  button.disabled = true;
  button.textContent = "Enabling…";
  try {
    // Permission is requested HERE, in context, after the user clicked (WHY: a page-load prompt gets denials
    // that permanently block the origin).
    const permission = await Notification.requestPermission();
    if (permission !== "granted") {
      reportPushOutcome(
        "Notifications are blocked — allow them in your browser settings, then try again.",
        "error",
      );
      return;
    }

    // Reuse-or-create the subscription and register it (getSubscription-first avoids re-subscribe churn).
    const ok = await ensureSubscription();
    if (ok) {
      setPushControlState(true); // flip the settings control to "Turn off" now that this device is subscribed.
    }
    reportPushOutcome(
      ok
        ? "Push notifications are on for this device."
        : "Couldn't enable push notifications — please try again.",
      ok ? "info" : "error",
    );
  } catch (error: unknown) {
    console.warn("Cinora: enabling push notifications failed.", error);
    reportPushOutcome("Couldn't enable push notifications — please try again.", "error");
  } finally {
    button.disabled = false;
    button.textContent = originalLabel;
  }
}

function wirePushSubscribe(): void {
  const wrapper = document.querySelector<HTMLElement>("[data-push-enable]");
  if (wrapper === null) {
    return; // not on the settings page.
  }
  if (!("serviceWorker" in navigator) || !("PushManager" in window)) {
    return; // unsupported — leave the control hidden; in-app notifications still work.
  }

  const enableButton = wrapper.querySelector<HTMLButtonElement>("[data-push-enable-button]");
  if (enableButton === null) {
    return;
  }
  const disableButton = wrapper.querySelector<HTMLButtonElement>("[data-push-disable-button]");

  wrapper.hidden = false; // reveal now that push is known to be supported.
  enableButton.addEventListener("click", () => {
    void subscribeToPush(enableButton);
  });
  disableButton?.addEventListener("click", () => {
    void unsubscribeFromPush(disableButton);
  });

  // Reflect the current subscription: show "Turn off" when already subscribed on this device, else "Enable".
  void refreshPushControlState();
}

registerServiceWorker();
wireOfflineRetry();
// Reveal the dismissible opt-in bar (rendered in _Layout for authed + push-configured pages) and wire its actions.
function wirePushOptIn(): void {
  const bar = document.querySelector<HTMLElement>("[data-push-optin]");
  if (bar === null) {
    return; // not rendered (anonymous, or push unconfigured).
  }

  const prompt = bar.querySelector<HTMLElement>("[data-push-optin-prompt]");
  const iosHint = bar.querySelector<HTMLElement>("[data-push-ios-hint]");
  const enableBtn = bar.querySelector<HTMLButtonElement>("[data-push-optin-enable]");
  const testBtn = bar.querySelector<HTMLButtonElement>("[data-push-optin-test]");
  const dismissBtn = bar.querySelector<HTMLButtonElement>("[data-push-optin-dismiss]");
  const status = bar.querySelector<HTMLElement>("[data-push-optin-status]");
  const setStatus = (message: string): void => {
    if (status !== null) {
      status.textContent = message;
    }
  };

  // iOS Safari NOT installed as a PWA: Push/Notification are absent → guide to "Add to Home Screen" (the #1
  // iPhone blocker), never a dead Enable button.
  if (!PUSH_SUPPORTED) {
    if (isIos() && !isStandalone() && prompt !== null && iosHint !== null) {
      prompt.hidden = true;
      iosHint.hidden = false;
      enableBtn?.classList.add("hidden");
      testBtn?.classList.add("hidden");
      bar.hidden = false;
    }
    return;
  }

  // Already granted (refreshPushSubscriptionOnLoad keeps it fresh), denied (can't re-prompt), or dismissed this
  // session → keep the bar hidden. Only "default" gets the invitation.
  if (Notification.permission !== "default" || sessionStorage.getItem(PUSH_DISMISS_KEY) === "1") {
    return;
  }

  bar.hidden = false;

  enableBtn?.addEventListener("click", () => {
    if (enableBtn === null) {
      return;
    }
    void (async () => {
      enableBtn.disabled = true;
      const original = enableBtn.textContent;
      enableBtn.textContent = "Enabling…";
      try {
        const permission = await Notification.requestPermission();
        if (permission !== "granted") {
          setStatus("Notifications are blocked — allow them in your browser settings.");
          return;
        }
        const ok = await ensureSubscription();
        setStatus(
          ok ? "Notifications are on for this device." : "Couldn't enable notifications — please try again.",
        );
        if (ok) {
          window.setTimeout(() => {
            bar.hidden = true;
          }, 1500);
        }
      } catch {
        setStatus("Couldn't enable notifications — please try again.");
      } finally {
        enableBtn.disabled = false;
        enableBtn.textContent = original;
      }
    })();
  });

  testBtn?.addEventListener("click", () => {
    void (async () => {
      setStatus("Sending a test…");
      const token = document.querySelector<HTMLMetaElement>(TOKEN_META_SELECTOR)?.content ?? "";
      try {
        const response = await fetch("/push/test", { method: "POST", headers: { [ANTIFORGERY_HEADER]: token } });
        const payload = (await response.json()) as { message?: string };
        setStatus(typeof payload.message === "string" ? payload.message : "Test sent.");
      } catch {
        setStatus("Couldn't send a test right now.");
      }
    })();
  });

  dismissBtn?.addEventListener("click", () => {
    sessionStorage.setItem(PUSH_DISMISS_KEY, "1");
    bar.hidden = true;
  });
}

// On every load, if the user already granted permission, silently ensure the subscription is registered/fresh so
// the server row survives browser subscription rotation. Best-effort; a background refresh never surfaces errors.
function refreshPushSubscriptionOnLoad(): void {
  if (!PUSH_SUPPORTED || Notification.permission !== "granted") {
    return;
  }
  void ensureSubscription().catch(() => {
    /* silent — a background refresh must never surface an error to the user. */
  });
}

wirePushSubscribe();
wirePushOptIn();
refreshPushSubscriptionOnLoad();
