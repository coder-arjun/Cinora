// §6/§7/§8 — the chat client: realtime message append (deduped), the built-in emoji picker, the new-group /
// group panel toggles, composer send-clear + Enter-to-send + auto-grow, typing indicator, and read receipts.
// All CSP-safe: no inline JS (wired via addEventListener off #chat-root), and message text is set with
// textContent, never innerHTML (§13). Lazy-loaded by site.ts only when #chat-root is present.

import { EMOJI_GROUPS } from "../data/emoji";

interface ChatMessagePush {
  readonly Id: string;
  readonly ConversationId: string;
  readonly SenderId: string;
  readonly SenderDisplayName: string;
  readonly Body: string;
  readonly SentAtUtc: string;
  readonly SharedMovieTmdbId?: number | null;
  readonly SharedMovieMediaType?: string | null;
  readonly SharedMovieTitle?: string | null;
  readonly SharedMoviePosterPath?: string | null;
}

// Mirror the server TmdbImageUrlBuilder: route the poster through the free weserv proxy (TMDB's CDN is blocked
// on some networks). Used only for the realtime shared-movie card; the server-rendered card uses the tag helper.
function tmdbPosterUrl(path: string | null | undefined): string | null {
  if (path === null || path === undefined || path.length === 0) {
    return null;
  }
  const normalized = path.startsWith("/") ? path : `/${path}`;
  return `https://images.weserv.nl/?url=image.tmdb.org/t/p/w185${normalized}`;
}

const ANTIFORGERY_HEADER = "RequestVerificationToken";

function antiforgeryToken(): string | null {
  return (
    document.querySelector<HTMLMetaElement>('meta[name="request-verification-token"]')?.content ?? null
  );
}

function firstRune(value: string): string {
  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return "?";
  }
  return [...trimmed][0]?.toUpperCase() ?? "?";
}

// The server renders the stored UTC instant WITHOUT a trailing 'Z' (EF loads SQL datetime2 as Unspecified, so
// ToString omits the zone). new Date() would then parse it as LOCAL and skip the conversion entirely — the
// reported "time is still wrong". Append 'Z' when there is no zone designator so it parses as UTC, then format
// to the viewer's local time. Timestamps that already carry a zone (the realtime DTO) are used as-is.
function toUtcDate(iso: string): Date {
  const hasZone = /[zZ]$/.test(iso) || /[+-]\d\d:?\d\d$/.test(iso);
  return new Date(hasZone ? iso : `${iso}Z`);
}

function timeLabel(iso: string): string {
  const date = toUtcDate(iso);
  if (Number.isNaN(date.getTime())) {
    return "";
  }
  return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

// The server renders timestamps in UTC (the stored instant). Convert every [data-chat-time] element to the
// viewer's LOCAL timezone so message + conversation-row times match the user's clock (their reported "timezone
// is different"). The source instant is the <time datetime> attribute or a data-datetime attribute. Idempotent
// via data-localized so re-running after an HTMX swap is cheap.
function localizeTimestamps(root: ParentNode): void {
  root.querySelectorAll<HTMLElement>("[data-chat-time]").forEach((el) => {
    if (el.dataset.localized === "true") {
      return;
    }
    const iso = el.getAttribute("datetime") ?? el.dataset.datetime ?? "";
    const label = timeLabel(iso);
    if (label.length > 0) {
      el.textContent = label;
      el.dataset.localized = "true";
    }
  });
}

function scrollToBottom(list: HTMLElement): void {
  list.scrollTop = list.scrollHeight;
}

/** Build a CSP-safe "theirs" message bubble (all text via textContent) for a realtime-arriving message. */
function createIncomingBubble(dto: ChatMessagePush): HTMLElement {
  const row = document.createElement("div");
  row.id = `msg-${dto.Id}`;
  row.dataset.messageId = dto.Id;
  row.dataset.mine = "false";
  row.className = "flex items-end gap-2 flex-row";

  const avatar = document.createElement("span");
  avatar.className =
    "flex h-9 w-9 shrink-0 items-center justify-center rounded-pill border border-white/10 bg-surface-2/70 text-sm font-semibold text-ink";
  avatar.setAttribute("aria-hidden", "true");
  avatar.textContent = firstRune(dto.SenderDisplayName);

  const column = document.createElement("div");
  column.className = "group flex max-w-[78%] flex-col items-start";

  const name = document.createElement("span");
  name.className = "mb-0.5 px-1 text-[0.7rem] font-medium text-ink-subtle";
  name.textContent = dto.SenderDisplayName;
  column.append(name);

  // Shared-movie card (if the message carries one).
  const tmdbId = dto.SharedMovieTmdbId ?? 0;
  if (tmdbId > 0) {
    const card = document.createElement("a");
    const media = (dto.SharedMovieMediaType ?? "Movie").toLowerCase();
    card.href = `/discover/title/${media}/${tmdbId}`;
    card.className =
      "flex w-64 max-w-full items-stretch gap-3 overflow-hidden rounded-2xl rounded-bl-sm border border-white/10 bg-surface-2/80 transition-colors hover:border-accent/40";

    const poster = tmdbPosterUrl(dto.SharedMoviePosterPath);
    if (poster !== null) {
      const img = document.createElement("img");
      img.src = poster;
      img.alt = dto.SharedMovieTitle ?? "";
      img.loading = "lazy";
      img.className = "h-24 w-16 shrink-0 object-cover";
      card.append(img);
    }

    const info = document.createElement("span");
    info.className = "flex min-w-0 flex-col justify-center py-2 pr-3";
    const label = document.createElement("span");
    label.className = "text-[0.62rem] font-semibold uppercase tracking-wide text-accent";
    label.textContent = `🎬 ${dto.SharedMovieMediaType === "Series" ? "Series" : "Movie"}`;
    const cardTitle = document.createElement("span");
    cardTitle.className = "mt-0.5 line-clamp-3 text-sm font-semibold text-ink";
    cardTitle.textContent = dto.SharedMovieTitle ?? "";
    info.append(label, cardTitle);
    card.append(info);
    column.append(card);
  }

  // Text / caption bubble (only when there is body text — a bare movie share has none).
  if (dto.Body.length > 0) {
    const bubble = document.createElement("div");
    bubble.className =
      "mt-1 rounded-2xl rounded-bl-sm bg-surface-2 px-3.5 py-2 text-sm leading-relaxed text-ink shadow-sm";
    const body = document.createElement("span");
    body.className = "whitespace-pre-wrap break-words";
    body.textContent = dto.Body;
    bubble.append(body);
    column.append(bubble);
  }

  const time = document.createElement("time");
  time.className = "mt-0.5 px-1 text-[0.62rem] text-ink-subtle";
  time.dateTime = dto.SentAtUtc;
  time.textContent = timeLabel(dto.SentAtUtc);
  column.append(time);

  row.append(avatar, column);
  return row;
}

function wireEmojiPicker(): void {
  const toggle = document.getElementById("emoji-toggle");
  const popover = document.getElementById("emoji-popover");
  const input = document.getElementById("chat-message-input") as HTMLTextAreaElement | null;
  if (toggle === null || popover === null || input === null) {
    return;
  }

  const openPopover = (): void => {
    popover.classList.remove("hidden");
    toggle.setAttribute("aria-expanded", "true");
  };
  const closePopover = (): void => {
    popover.classList.add("hidden");
    toggle.setAttribute("aria-expanded", "false");
  };

  // Build a modern, categorized picker ONCE: a sticky tab bar of category icons + a scrollable body of labelled
  // emoji grids. All CSP-safe (DOM built here, addEventListener only). Picking an emoji keeps the picker open for
  // multi-emoji entry, like WhatsApp/Instagram.
  popover.classList.add("flex", "flex-col", "overflow-hidden");

  const tabBar = document.createElement("div");
  tabBar.className = "flex items-center gap-0.5 border-b border-white/10 px-1.5 py-1.5";
  const body = document.createElement("div");
  body.className = "max-h-60 overflow-y-auto px-1.5 py-1.5";

  const tabButtons: HTMLButtonElement[] = [];
  const setActiveTab = (active: number): void => {
    tabButtons.forEach((tab, i) => {
      tab.classList.toggle("bg-accent/20", i === active);
      tab.classList.toggle("text-accent", i === active);
    });
  };

  EMOJI_GROUPS.forEach((group, index) => {
    const section = document.createElement("section");
    const heading = document.createElement("p");
    heading.className =
      "sticky top-0 z-10 bg-surface-1/95 px-0.5 py-1 text-[0.6rem] font-semibold uppercase tracking-wider text-ink-subtle backdrop-blur-sm";
    heading.textContent = group.label;

    const grid = document.createElement("div");
    grid.className = "grid grid-cols-8 gap-0.5 pb-2";
    for (const emoji of group.emojis) {
      const button = document.createElement("button");
      button.type = "button";
      button.className =
        "flex h-8 w-8 items-center justify-center rounded-md text-xl leading-none transition-transform duration-100 hover:scale-125 hover:bg-white/10";
      button.textContent = emoji;
      button.title = emoji;
      button.addEventListener("click", () => insertAtCaret(input, emoji));
      grid.append(button);
    }
    section.append(heading, grid);
    body.append(section);

    const tab = document.createElement("button");
    tab.type = "button";
    tab.className = "flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-lg transition-colors hover:bg-white/10";
    tab.textContent = group.icon;
    tab.setAttribute("aria-label", group.label);
    tab.title = group.label;
    tab.addEventListener("click", () => {
      section.scrollIntoView({ behavior: "smooth", block: "start" });
      setActiveTab(index);
    });
    tabBar.append(tab);
    tabButtons.push(tab);
  });

  setActiveTab(0);
  popover.append(tabBar, body);

  toggle.addEventListener("click", (event) => {
    event.stopPropagation();
    if (popover.classList.contains("hidden")) {
      openPopover();
    } else {
      closePopover();
    }
  });

  // Click-away + Escape close.
  document.addEventListener("click", (event) => {
    if (!popover.classList.contains("hidden") && event.target instanceof Node
      && !popover.contains(event.target) && event.target !== toggle) {
      closePopover();
    }
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closePopover();
    }
  });
}

function insertAtCaret(input: HTMLTextAreaElement, text: string): void {
  const start = input.selectionStart ?? input.value.length;
  const end = input.selectionEnd ?? input.value.length;
  input.value = input.value.slice(0, start) + text + input.value.slice(end);
  const caret = start + text.length;
  input.selectionStart = caret;
  input.selectionEnd = caret;
  input.focus();
}

function wireModalsAndPanel(): void {
  const newGroupModal = document.getElementById("new-group-modal");

  document.addEventListener("click", (event) => {
    const trigger = (event.target instanceof Element ? event.target : null)?.closest<HTMLElement>("[data-chat-action]");
    if (trigger === null || trigger === undefined) {
      return;
    }
    const action = trigger.dataset.chatAction;
    if (action === "open-new-group" && newGroupModal !== null) {
      newGroupModal.classList.remove("hidden");
      newGroupModal.classList.add("flex");
    } else if (action === "close-new-group" && newGroupModal !== null) {
      newGroupModal.classList.add("hidden");
      newGroupModal.classList.remove("flex");
    } else if (action === "toggle-group-panel") {
      document.getElementById("group-panel")?.classList.toggle("hidden");
    }
  });
}

function wireComposer(list: HTMLElement): void {
  const form = document.getElementById("chat-composer") as HTMLFormElement | null;
  const input = document.getElementById("chat-message-input") as HTMLTextAreaElement | null;
  if (form === null || input === null) {
    return;
  }

  // Auto-grow the textarea.
  const grow = (): void => {
    input.style.height = "auto";
    input.style.height = `${Math.min(input.scrollHeight, 128)}px`;
  };
  input.addEventListener("input", grow);

  // Enter sends; Shift+Enter inserts a newline.
  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      if (input.value.trim().length > 0) {
        form.requestSubmit();
      }
    }
  });

  // After HTMX appends the sent bubble, clear the composer and scroll.
  form.addEventListener("htmx:afterRequest", (event) => {
    const detail = (event as CustomEvent<{ successful?: boolean }>).detail;
    if (detail?.successful === true) {
      input.value = "";
      grow();
      scrollToBottom(list);
    }
  });
}

async function markRead(conversationId: string): Promise<void> {
  const token = antiforgeryToken();
  const headers: Record<string, string> = {};
  if (token !== null) {
    headers[ANTIFORGERY_HEADER] = token;
  }
  try {
    await fetch(`/chat/${conversationId}/read`, { method: "POST", headers });
  } catch {
    // Best-effort: the unread count reconciles on the next navigation.
  }
}

// Advance the delivery/read ticks on MY sent messages (WhatsApp-style). "delivered" → double grey ✓✓ (never
// downgrades a read tick); "read" → double blue ✓✓. CSP-safe: textContent + class toggles only.
function setChatTicks(list: HTMLElement, state: "delivered" | "read"): void {
  list.querySelectorAll<HTMLElement>('[data-mine="true"] [data-tick]').forEach((tick) => {
    const current = tick.dataset.tickState ?? "sent";
    if (state === "read") {
      tick.dataset.tickState = "read";
      tick.textContent = "✓✓";
      tick.classList.remove("text-ink-subtle");
      tick.classList.add("text-sky-400");
      tick.title = "Read";
    } else if (current !== "read") {
      tick.dataset.tickState = "delivered";
      tick.textContent = "✓✓";
      tick.title = "Delivered";
    }
  });
}

async function connectRealtime(conversationId: string, myId: string, list: HTMLElement): Promise<void> {
  let signalr: typeof import("@microsoft/signalr");
  try {
    signalr = await import("@microsoft/signalr");
  } catch (error: unknown) {
    console.warn("Cinora chat: realtime client failed to load.", error);
    return;
  }

  const connection = new signalr.HubConnectionBuilder()
    .withUrl("/hubs/chat")
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .build();

  const typingNode = document.getElementById("chat-typing");
  let typingTimer: number | undefined;

  connection.on("ReceiveMessage", (dto: ChatMessagePush) => {
    if (dto.ConversationId !== conversationId) {
      return;
    }
    // Dedupe against the sender's own optimistic HTMX append (and any duplicate delivery).
    if (list.querySelector(`[data-message-id="${dto.Id}"]`) !== null) {
      return;
    }
    list.append(createIncomingBubble(dto));
    scrollToBottom(list);
    // Acknowledge delivery to the sender (double grey tick), always. Mark READ (blue tick for the sender) only
    // when this tab is actually visible — a background tab is "delivered", not "read".
    void connection.invoke("MarkDelivered", conversationId).catch(() => undefined);
    if (document.visibilityState === "visible") {
      void markRead(conversationId);
    }
  });

  connection.on("UserTyping", (convId: string, _userId: string, name: string) => {
    if (convId !== conversationId || typingNode === null) {
      return;
    }
    typingNode.textContent = `${name} is typing…`;
    if (typingTimer !== undefined) {
      window.clearTimeout(typingTimer);
    }
    typingTimer = window.setTimeout(() => {
      typingNode.textContent = "";
    }, 3000);
  });

  // Someone ELSE read the conversation → my sent messages flip to the blue double-tick.
  connection.on("ConversationRead", (convId: string, readerUserId: string, _at: string) => {
    if (convId === conversationId && readerUserId !== myId) {
      setChatTicks(list, "read");
    }
  });

  // Someone ELSE's device received the messages → my sent messages flip to the grey double-tick (delivered).
  connection.on("MessageDelivered", (convId: string, userId: string) => {
    if (convId === conversationId && userId !== myId) {
      setChatTicks(list, "delivered");
    }
  });

  connection.onreconnected(() => {
    void connection.invoke("JoinConversation", conversationId).catch(() => undefined);
  });

  try {
    await connection.start();
    await connection.invoke("JoinConversation", conversationId);
  } catch (error: unknown) {
    console.warn("Cinora chat: realtime connection failed to start.", error);
    return;
  }

  // Typing broadcast (debounced ~1.5s) as the user writes.
  const input = document.getElementById("chat-message-input") as HTMLTextAreaElement | null;
  if (input !== null) {
    let lastSent = 0;
    input.addEventListener("input", () => {
      const now = Date.now();
      if (now - lastSent > 1500 && input.value.trim().length > 0) {
        lastSent = now;
        void connection.invoke("Typing", conversationId).catch(() => undefined);
      }
    });
  }
}

export function initChat(): void {
  const root = document.getElementById("chat-root");
  if (root === null) {
    return;
  }

  wireEmojiPicker();
  wireModalsAndPanel();

  // Timezone (server renders UTC → show the viewer's local time), for the initial page and after any HTMX swap
  // that injects more timestamps (send appends a bubble; load-more prepends an older page).
  localizeTimestamps(document);
  document.addEventListener("htmx:afterSwap", (event) => {
    const target = (event as CustomEvent<{ target?: Element }>).detail?.target ?? event.target;
    if (target instanceof Element) {
      localizeTimestamps(target);
    }
  });

  const list = document.getElementById("message-list");
  if (list !== null) {
    wireComposer(list);
    scrollToBottom(list);
  }

  const conversationId = root.dataset.activeConversationId ?? "";
  const myId = root.dataset.currentUserId ?? "";
  if (conversationId.length > 0 && list !== null) {
    // Mark read only when the thread is actually visible on open (so delivered ≠ read).
    if (document.visibilityState === "visible") {
      void markRead(conversationId);
    }
    // Returning to the tab marks the open thread read — a message delivered while backgrounded becomes "read".
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") {
        void markRead(conversationId);
      }
    });
    void connectRealtime(conversationId, myId, list);
  }
}
