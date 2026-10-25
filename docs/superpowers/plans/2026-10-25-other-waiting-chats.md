# Raven mentions the other waiting chats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When Raven speaks the news of the chat the user is in and other chats wait, the teller words one short remark about them, said after the chat's line; the sounds of the chats it mentions are dropped once it is heard (#308).

**Architecture:** `TellNewsAsync` gathers what waits elsewhere after it writes the news (unread news lines not heard and open cards, of every chat but the one the user is in, muted ones left out, one per session, each mentioned once). `TellLinesAsync` takes that list: the teller is asked for the summaries and, after them, one line marked `R.`; the remark is said last, one sentence. Once the reply is heard whole, the mentioned items are remembered and their chats' queued sound cues from before the remark was worded are taken out of the queue.

**Tech Stack:** .NET 10 WPF, xUnit v3, Shouldly, FakeTimeProvider.

**Spec:** `docs/superpowers/specs/2026-10-24-raven-sounds-design.md`, section "Raven mentions the other waiting chats"; issue #308.

## Global Constraints

- Nothing of the remark is hard-coded: no "BTW", no template, no fallback wording. The app decides only whether there is a remark and that it comes after the chat's own line. A teller that gives no remark: none is said.
- The teller is given every other waiting chat, no cap, with its workspace name and what it waits for (question, permission, finished, failed, needs you). Facts only, never what a chat said.
- Muted chats are not mentioned. The remark never stands alone: only with spoken news of the chat the user is in.
- Mentioned chats' sounds are dropped once the remark is heard; a cue that came after the remark was worded keeps its sound.

## Rulings (builder's calls, per the spec's "left to the builder")

- What "waits": the same as whats_new (#243): a news line not heard in a chat's unread entries, while the chat is still where the news left it (`ChatNews.StillHolds`), and an open card. One entry per session: its open card, else its newest line.
- Each waiting item is mentioned once: after a remark is heard, the same line or card is not mentioned again; the chat's next news is (raven-dont-overwhelm).
- The catch-up gets no remark: it already tells what happened in the chat switched to.
- The remark takes one of the reply's sentences: with others waiting, the chat's own lines get one fewer before "And N more.".
- A remark is one sentence (`FirstSentence`), said after "And N more.".

## Review Focus

- The teller answers the remark without the `R.` mark when it was asked for the remark alone: its one line is taken as the remark.
- The reply is hushed before the remark is heard: no item counts as mentioned, the sounds stay.
- A chat whose news came while the teller worded: not in the remark, keeps its sound.
- A chat that works again after its news: not mentioned.
- The user is in chat 0: every window's chat is "other".

---

### Task 1: The prompt and the remark line

**Files:**
- Modify: `src/CodeSwitchX.UI/Raven/RavenPanelViewModel.cs` (SummaryPrompt, new `WaitingChat`, `Remarked`)
- Modify: `src/CodeSwitchX.UI/Raven/ChatNews.cs` (`StillHolds` internal)
- Test: `tests/CodeSwitchX.UI.Tests/Raven/ChatNewsTests.cs`

**Interfaces:**
- Produces: `internal sealed record WaitingChat(string SessionId, string Workspace, string What, object Item)`;
  `internal static string SummaryPrompt(string kind, IReadOnlyList<ChatNewsLine> lines, IReadOnlyList<WaitingChat> others)` (the two-argument form unchanged);
  `internal static string? Remarked(string line)` (the text after an `R.`/`R:`/`R)` mark, null for none).

- [ ] Tests: the two-argument prompt is unchanged with no others; with others, the prompt lists `- <Workspace>: <What>` for each and asks for the summaries then one line starting with `R. `; with no lines, it asks for the remark line alone; `Remarked("R. Two more chats finished.")` is `"Two more chats finished."`, `Remarked("1. the tests pass")` is null, `Remarked("R. ")` is null.
- [ ] Run: `CodeSwitchX.UI.Tests.exe --filter-class "*ChatNewsTests"` → FAIL (no overload / no Remarked).
- [ ] Implement; run → PASS. Commit.

### Task 2: Raven says the remark, and the mentioned chats' sounds are dropped

**Files:**
- Modify: `src/CodeSwitchX.UI/Raven/RavenPanelViewModel.cs` (TellNewsAsync, TellLinesAsync, `WaitingElsewhereAsync`, `MentionOnceHeardAsync`, `_mentioned`)
- Test: `tests/CodeSwitchX.UI.Tests/Raven/RavenPanelViewModelTests.Traffic.cs`

**Interfaces:**
- Consumes: Task 1's `WaitingChat`, `SummaryPrompt(kind, lines, others)`, `Remarked`.
- Produces: `TellLinesAsync(..., IReadOnlyList<WaitingChat>? others = null)` returning `Remark` (the remark said, or null) as a sixth element.

Tests (TrafficVmAsync, the user in chat 1 "b"; the teller answers `1. the tests pass` and `R. <remark>`):
- [ ] `Raven_mentions_the_other_waiting_chats_after_the_chat_s_line`: a, c finish and d asks permission, then b finishes; the teller's prompt lists ContentAutomatorX finished, DiffusionNexus finished, RawCutX asks for permission; the speech is the chat's line, then the remark.
- [ ] `The_mentioned_chats_sounds_are_dropped_once_the_remark_is_heard`: after the voice is quiet and the sound grace and gaps, only chat 1's prelude sound played.
- [ ] `A_chat_whose_news_comes_while_the_remark_is_worded_keeps_its_sound`: e finishes inside the teller's answer; it is not in the prompt, and its sound plays afterwards.
- [ ] `With_nothing_else_waiting_raven_says_only_the_chat_s_line`: the prompt has no `R. `.
- [ ] `A_muted_chat_is_not_mentioned`.
- [ ] `A_chat_working_again_is_not_mentioned`.
- [ ] `A_waiting_chat_is_mentioned_once`: b's second news asks for no remark about a.
- [ ] `A_chat_that_needs_you_gets_the_remark_from_the_teller_alone`: b waits (no summary): the line is said, the teller is asked for the remark alone, its unmarked line is said.
- [ ] `A_remark_hushed_before_it_is_heard_leaves_the_sounds`: a press during the reply: the others' sounds still play.
- [ ] Run each RED, implement, run GREEN; then the UI suite without HotkeyServiceTests. Commit and push; open the PR.
