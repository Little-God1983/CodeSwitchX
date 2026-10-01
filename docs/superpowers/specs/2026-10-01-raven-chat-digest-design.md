# Raven's chat digest (#73)

Raven tells the user what their chats did as conversation, not as a queue of notices. Design approved in chat on
2026-10-01. RAIVEN stays untouched; it may still speak about the same chats until the user switches it off.

## What the user decided

- **A new turn stops the old answer, and Raven keeps the conversation.** When the user talks while Raven still answers, the
  brain's turn is interrupted (not killed): its log entry ends with "(interrupted)", nothing of it is spoken later, and
  "go on" works because the brain remembers what it was saying.
- **The digest has a conversational flow.** Raven's brain words it, so two pieces of news read as one remark, not two
  canned notices. A fixed sentence is only the fallback when the brain cannot answer.

## Facts it rests on

- `SessionEngine` publishes every chat's change as `SessionChanged(Previous, Current)` on the event bus.
- The CLI's stream-json input takes `{"type":"control_request","request_id":…,"request":{"subtype":"interrupt"}}`.
  Verified on CLI 2.1.285: it answers with a `control_response` (success), the turn ends with
  `result`/`error_during_execution` within a second, the process stays, and the next turn knows what the cut-off answer
  said. Today `ClaudeCliBrain` kills the process on cancel, which loses the conversation and costs a cold start.
- `IYardDirectory.ChatsAsync` gives each shown chat's workspace name, title, state and `NeedsYou`.

## 1. ChatNews: one slot per chat

`CodeSwitchX.UI/Raven/ChatNews.cs`, subscribed to `SessionChanged`.

- **What counts**, for chats the Yard shows (`ShowsAsChat`) and with a previous snapshot:
  - Working → Idle: *finished*.
  - Any state → Waiting: *needs you*, with the notification text.
  - Any state → Errored: *failed*.
  - Nothing else counts. That includes Ended, a first snapshot (a chat restored at startup), and inferred-only changes
    whose previous state was not Working.
- **Slots:** each chat has one slot, `ChatNewsItem(SessionId, WorkspaceId, Workspace, Title, Kind, Detail, At)`. Newer news
  replaces the slot's older news. A chat that finishes and then needs you before Raven speaks is told once, as needs you.
- **Taking:** `Take()` empties the slots and returns the items still fresh. News the chat has moved past without new
  news (it needed the user and works again) is dropped. An item older than 2 minutes is not spoken: it
  goes into the log card as a line marked "not spoken (older than 2 minutes)".
- **Raised:** `NewsArrived` lets the floor know there is something to say.
- **Workspace name:** comes from `IYardDirectory` when the news is taken. A chat that has left the board is dropped.

## 2. RavenFloor: one party speaks

The panel's turn handling moves into `CodeSwitchX.UI/Raven/RavenFloor.cs`, which owns who holds the floor.

- **Holders:** the user (talking), an answer to the user, or a digest. Each holder gets a generation number and a
  `CancellationTokenSource`.
- **A mic press** silences Raven at once (`ReplyVoice.Hush`) but interrupts nothing: a press that brings no question
  (a cough, a mis-tap) leaves the answer to be written.
- **A new question** (spoken or typed) takes the floor. In one step it cancels the current holder's token (the brain turn
  and its speech) and hushes `ReplyVoice`. A question asked before it that has not gone to the brain yet is not lost:
  it goes along with the new one, as its first half.
- **When the floor is free**, the next holder is picked in this order: the user's turn, then chat questions (an empty
  source until #74), then news.
- **Free** means nobody talks, no answer is being made or spoken, and Raven has been quiet for 1.5 s (`NewsGrace`), so a
  digest does not step on the user's next sentence.

## 3. The digest turn

- **What the brain gets:** the items taken from `ChatNews`, as one message:

  ```
  [Chat news, not from the user. Tell the user this news the way you would mention it in conversation: one to three short
  spoken sentences, each chat once, no lists, no markdown. Do not call tools.]
  - ContentAutomatorX, chat "Fix the upload retry": finished. It last said: "…the retry now backs off and the test passes."
  - CodeSwitchX, chat "Release notes": needs you: "Claude needs your permission to use Bash".
  ```

  "It last said" is the end of the chat's final reply: the last assistant text in its transcript, about 300 characters,
  read with `TranscriptTailer` from the end of the file.
- **Spoken** through `ReplyVoice` like any answer, and written as a Raven entry below the card.
- **If the brain fails**, or gives no text, the fallback is a fixed sentence: "ContentAutomatorX finished, and CodeSwitchX
  needs you."
- **Interrupted:** a digest that is interrupted is dropped; its chats count as told. The brain has the news in its
  conversation, so "go on" or "what did it change?" works.
- **No actions:** while the digest's brain turn runs, the Yard's actions are refused (`NewsTurnGuard` in front of
  `RavenActions`): the prompt quotes what other chats said, and only the user's own words may act.
- **When:** a digest turn only runs while speech is on (not muted, "Speak chat news" on). Otherwise the card is still
  written and no brain turn is spent.

## 4. ClaudeCliBrain: interrupt instead of kill

- A cancelled turn sends the interrupt control request and keeps reading until the turn's `result`, which it does not
  report as a failure. It does not yield past the cancel.
- **Fallback:** if no `result` comes within 5 s, the process is stopped as today.
- The next turn waits for this drain (the `_turns` lock is held until it is done).

## 5. The log card and the tile

- **The card:** a new `RavenLogKind.News` entry holding `ChatNewsLine`s (workspace, title, what happened).
- **Clicking a line** shows the Yard and spotlights the tile: `IRavenShell.ShowTile(workspaceId)` switches to the Yard,
  brings the tile into view and lights its border for 2 s.

## 6. Setting

- "Speak chat news" (`raven.speakNews`, on by default) in Settings → Raven.
- When it is off, the cards are still written but nothing is spoken.

## Testing

- **ChatNews:** which changes count, newer news replacing older, the 2-minute rule, a chat gone from the board.
- **RavenFloor and the panel** (fake brain, fake voice, fake clock):
  - Three chats finish while the user talks, and one digest follows the answer.
  - A press during the digest drops it, and nothing of it is spoken later.
  - A chat that changes twice is told once, with its latest state.
  - A press during an answer interrupts it, and it ends with "(interrupted)".
  - Order: the user first, news last.
  - Muted or setting off: a card, no brain turn.
- **ClaudeCliBrain** (fake process): cancel sends the interrupt and drains to the `result`; no `result` in 5 s stops the
  process; the next turn runs on the same process.
- **Live checks:** the done-when steps on screen, with the user verifying.
