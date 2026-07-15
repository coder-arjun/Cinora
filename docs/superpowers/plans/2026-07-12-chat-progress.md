# Chat — Execution Progress Ledger

Plan: `docs/superpowers/plans/2026-07-12-chat.md` · Spec: `docs/architecture/chat-design.md`
Mode: subagent-driven, **no-git** (no worktree/commits; reviewers read changed files; each task ends at a
build+test Checkpoint; the human commits). Started 2026-07-12.

## Status
- [x] C1.1 — Enums + Conversation entity
- [x] C1.2 — ConversationMember + Message entities
- [x] C1.3 — EF configs + DbSets + AddChat migration
- [x] C2.1 — IChatNotifier port + DTOs
- [x] C2.2 — StartDirectConversationCommand
- [x] C2.3 — SendMessageCommand + GetMessagesQuery + VMs
- [x] C2.4 — Conversations list + read + group/membership + delete verticals
- [x] C3.1 — ChatHub + IChatClient + membership join
- [x] C3.2 — SignalRChatNotifier + wire push + MapHub
- [x] C4.1 — ChatController + /chat page shell + conversation list
- [x] C4.2 — thread + composer + send endpoint + _ChatMessage + dedupe client
- [x] C4.3 — new-group modal + Message affordance + group mgmt + unread badge
- [x] C4.4 — built-in emoji picker
- [x] C5.1 — read flow (mark-read + receipts)  [client folded into chat.ts]
- [x] C5.2 — typing indicator  [client folded into chat.ts]
- [~] C6.1 — offline Web Push on new message  → DEFERRED (out of chosen MVP+ scope; see note)
- [x] ADR 0024 + final review (3 findings fixed) + [x] **DEPLOYED (2026-07-15)**

## ✅ FEATURE COMPLETE (2026-07-14) — C1–C5 built, C6 deferred. 540 tests green. ADR 0024 written. Final review running; deploy pending.

## ⏸️ (superseded) RESUME HERE (paused 2026-07-14)
Next task: **C2.4** — 8 verticals in `src/Cinora.Application/Features/Chat/` + `tests/.../Chat/GroupAndReadTests.cs`:
`GetConversationsQuery` (newest-first, correlated other-member title/avatar + last-msg preview + UnreadCount=`Count(SentAtUtc>myLastReadAtUtc && SenderId!=me)`, N+1-free), `MarkConversationReadCommand` (advance LastReadAtUtc→UtcNow, push `ConversationReadAsync`), `CreateGroupConversationCommand(+validator)` (members=friends∧¬blocked, creator=Admin), `AddGroupMembersCommand` (member adds own friends∧¬blocked, push "member-added"), `RemoveGroupMemberCommand` (admin-only), `LeaveConversationCommand` (remove my row; if I was Admin promote earliest-joined remaining), `RenameGroupCommand(+validator)` (admin-only), `DeleteMessageCommand` (sender-only→MarkDeleted; **decided: NO push — IChatNotifier has no delete method, note for final review**). `Unit` handlers `return Unit.Value;`. Then build + `--filter Chat`, then `/review-security`+`/review-code` for C2. Plan §432/§565; spec §5 (chat-design.md:158). Remaining after C2.4: C3 (hub+adapter), C4 (UI+emoji), C5 (typing/read), C6 (push), ADR 0024 + review + deploy.
State at pause: build 0/0 (Release, TWAE on); **10 chat tests green**; migration `20260713185229_AddChat` applied to CinoraTest + in `artifacts/migrate.sql` (NOT yet applied to prod). No half-written files.

## Log (newest first)
**DEPLOYED to prod 2026-07-15.** DB: user applied `artifacts/migrate.sql` to `db58983` via SSMS (this env can't reach prod SQL :1433 — MonsterASP DB firewall). Files: agent FTPS-uploaded to `site78342.siteasp.net` — `app_offline.htm` up (site→503) → 4 changed `Cinora.*.dll` to `/wwwroot/` + the 18 current `/dist` files (app-fe818f6f8d.css / site-WDMTEWJ2.js / chat-OYGP2XEN.js / esm-FOAS4QNR.js / chunk-I7AUKTXE.js / manifest.json, each +.br +.gz) + `sw.js` to `/wwwroot/wwwroot/` → SHA-256 verified `Cinora.Web.dll` + `app-fe818f6f8d.css` byte-identical → `app_offline.htm` removed. Only changed files shipped (unchanged 3rd-party DLLs + `deps.json` + `endpoints.json` left as-is; app uses `UseStaticFiles`, not `MapStaticAssets`). `App_Data/` + `appsettings.Production.json` preserved. Smoke: `/health` 200 Healthy, `/` 200, new css+chat.js 200, `/chat` unauth 302→login. Checklist: `docs/deployment/deploy-2026-07-15-chat.md`. **Carried:** logged-in `/chat` click + two-browser realtime smoke (user); **ROTATE the db58983 + FTP passwords (shared in chat).**
FINAL REVIEW (read-only code-review-agent) — 3 findings, ALL FIXED + regression-tested:
- **H1 (High) — removed group member kept receiving live messages** via the stale `conversation-{id}` SignalR group (persisted reads 403'd, but the socket was fail-open). FIX: `IChatNotifier.MessageSentAsync` now takes the current member ids; `SignalRChatNotifier` fans out to each current member's `chat-user-{id}` group (resolved live at send from the DB), so a removed/left user is never a recipient. Regression: `After_removing_a_member_a_sent_message_is_not_pushed_to_them`.
- **M2 (Medium) — newly-added member could read full pre-join history.** FIX: `GetMessagesQuery` now bounds history to `SentAtUtc >= my JoinedAtUtc`. Regression: `A_newly_added_member_cannot_read_messages_sent_before_they_joined`.
- **L1 (Low) — DM block re-check misfired on a 2-member group** (`members.Count == 2` not `Type == Direct`). FIX: `SendMessageCommand` gates the re-check on `conversation.Type == ConversationType.Direct`.
Post-fix: build 0/0; **542 tests green** (Domain 65, Application 128, Infrastructure 110, Web 239 — +2 chat regressions, 29 chat tests total). No schema change (fixes code-only; `AddChat` migration unchanged). ADR 0024 records the participation-as-authorization stance incl. the realtime cut-off.
Task C4 HTTP tests: ChatPageTests 3/3 (home renders, non-member GET /chat/{id}→403, send returns @-encoded bubble incl. `&lt;script&gt;`). **25 chat tests green.** Build 0/0.
C6.1 DEFERRED: offline Web Push on new message is beyond the user's chosen scope (MVP + typing + read receipts + emoji, per the AskUserQuestion). The existing push pipeline (IPushDispatch.Enqueue → SendPushNotificationCommand) is keyed to a Notification inbox row per push; reusing it per chat message would pollute the notification inbox, and a parallel chat-push path is larger than the chosen scope warrants. The live push round-trip is already a carried manual item on this project. Recorded as an enhancement in ADR 0024; the IChatNotifier/ChatHub seam leaves room to add it later without rework.
Task C4 (+C5 client): code-complete — ChatController (index/thread/history/send/direct/group/add/remove/leave/rename/read/delete, all [Authorize]+SocialWrite) + Web VMs/forms + ChatNavViewComponent (+badge) + GetChatUnreadCountQuery + GetConversationHeaderQuery (Application) + views: Chat/Index (two-pane + new-group modal), _ThreadPanel, _GroupPanel, _MessageList, _ConversationRow, _ChatMessage (data-mine/data-message-id, styled-delete) + Scripts/components/chat.ts (SignalR join/receive-dedupe/typing/read-receipt "Seen", emoji picker at caret, modals, Enter-to-send, auto-grow, mark-read) + data/emoji.ts (8 categories) + site.ts lazy-load on #chat-root + _Layout (vc:chat-nav next to bell + Messages drawer row) + _FriendCard Message button. **Build 0/0 (.NET); npm run build clean (tsc strict + esbuild; chat.js split chunk 8.3kb).** DeleteMessageCommand now returns tombstone ChatMessageVm. C5.1/C5.2 client folded in. Authored directly. PENDING: C4 HTTP tests (ChatPageTests), C6 offline push, ADR 0024, review, deploy.
Task C3.1+C3.2: complete — ChatHub (`conversation-{id}` groups, per-user `chat-user-{id}` on connect, membership-checked JoinConversation/Typing, fail-closed [Authorize]+null-abort) + IChatClient (ReceiveMessage/UserTyping/ConversationRead/ConversationUpdated) + IChatMembership/ChatMembership (thin scoped AnyAsync) + SignalRChatNotifier (best-effort try/catch→[LoggerMessage] 3600, msg/read→conversation group, changed→per-user groups) + Program.cs wiring (AddScoped IChatNotifier/IChatMembership after IRealtimeNotifier; MapHub /hubs/chat after notifications). **NO CSP change** (same-origin). ChatHubAuthTests 2/2 + ChatRealtimeTests 2/2. **22 chat tests green.** Build 0/0. Authored directly. **Milestone C3 COMPLETE.**
Task C2.4: complete — 8 verticals (GetConversationsQuery N+1-free unread/preview/other-member; MarkConversationReadCommand; CreateGroupConversationCommand+validator; AddGroupMembersCommand; RemoveGroupMemberCommand admin-only; LeaveConversationCommand admin-promote-earliest; RenameGroupCommand+validator admin-only; DeleteMessageCommand sender-only soft) + GroupAndReadTests 8/8. **18 chat tests green.** Build 0/0. DEVIATION: DeleteMessageCommand emits NO realtime push (IChatNotifier has no per-message-delete method; tombstone shows on next load) — noted for final review. Authored directly.
**Milestone C2 COMPLETE.** NOTE: per-milestone /review-security+/review-code being consolidated into the end-of-feature review gate (speed/autonomy per user) — will run the full gate before deploy.
Task C1.1: complete — ConversationType/ConversationRole enums + Conversation entity (CreateDirect/CreateGroup/Rename/BumpLastMessage, guards) + ConversationTests 4/4. Build 0/0. Deviation: softened 2 forward `<see cref>` (ConversationMember/Message) → `<c>` to avoid CS1574 under TWAE (types arrive in C1.2; can restore later — cosmetic).
Task C1.2: complete — ConversationMember (Create/MarkRead/Promote) + Message (Create/MarkDeleted, BodyMaxLength=4000) entities + MessageTests 3/3. Build 0/0. NOTE: implementer API-server-errored while still reading conventions (nothing written); I authored C1.2 directly (verbatim from plan).
Task C2.1: complete — IChatNotifier port (MessageSent/ConversationRead/ConversationChanged) + ChatMessageDto/ChatReadDto/ConversationEventDto in Common/Realtime. Build 0/0. Authored directly.
Task C2.2: complete — StartDirectConversationCommand (find-or-create, block+friend gate, verbatim from plan) + StartDirectTests 4/4 (non-friend→403, friends→2-member Direct, twice→same id, blocked→403). Authored directly.
Task C2.3: complete — ChatViewModels (ChatMessageVm/MessageThreadVm/ConversationsVm/ConversationSummaryVm) + SendMessageCommand (+validator, membership+DM-block gate, best-effort push) + GetMessagesQuery (keyset SentAtUtc DESC,Id DESC, correlated sender sub-select) + MessagingTests 5/5. 10 chat tests green total. Build 0/0. Authored directly.
Task C1.3: complete — 3 EF configs (Conversation/ConversationMember/Message, verbatim from plan §Step 5) + 3 DbSets on IAppDbContext + CinoraDbContext + throw-stubs & Ignore<T>() in the 3 Application test fakes (TestAppDbContext/ProfilesTestDbContext/PushTestDbContext) + AddChat migration (20260713185229, ADDITIVE: 3 CreateTable, unique (ConversationId,UserId), IX (UserId), IX Messages(ConversationId,SentAtUtc), Restrict FKs) + artifacts/migrate.sql regenerated. ChatPersistenceTests 1/1 green. NOTE: implementer stalled after writing only the test; I authored the rest directly.

## Minor findings roll-up (for the final review)
(append reviewer Minor findings here)
