# Raven sounds and volume: design

Date: 2026-10-24. Status: approved by the user. Milestone "Raven sounds": #305, #306, #307, #308.

## Why

Today one fixed chime, two rising sine tones made in code (`src/CodeSwitchX.Voice/Audio/ChatChime.cs`), stands for
everything other chats do: finished, failed, a question, a permission prompt. The chat you are in makes no sound at all,
only Raven's spoken line. Neither the chime nor Raven's voice has a volume setting: the chime is fixed at a third of full
loudness, the voice plays at what Windows gives it.

The user wants to tell by ear what happened without looking or waiting for Raven to word it, pick sounds they like
(musical, natural, not electronic beeps), add sounds of their own, and set how loud the sounds and Raven's voice are.

## Goals

- Six sound slots, each with its own picker in Settings: what each event sounds like, or no sound.
- A pool of shipped, musical/natural sounds plus sounds the user adds.
- Sounds for **all** chats, including the chat the user is in.
- No sound is thrown away for coming too soon: a queue plays them one after another.
- One volume slider for all sounds, one for Raven's voice.

## Non-goals

- A volume per sound slot (one slider covers all six).
- Changing Windows' or the device's volume: the sliders scale what CodeSwitchX plays, nothing else.
- Sounds for anything not in the six slots below.
- A volume control on Raven's panel: the sliders are in Settings only.

## Anti-goals (would be a failure even if it works)

- Sounds talking over Raven's voice or the user (except the one below that exists exactly for that).
- A burst of beeps: sounds overlapping, or outdated ones played long after they stopped being true.
- A long user file blocking every sound behind it.
- Two systems doing the same job: the old chime settings must go, not sit beside the new ones.

## The six sounds (user's decision)

| Slot | Plays when | Through the queue |
|---|---|---|
| Starts working | a chat begins a turn | yes |
| Finishes | a chat's turn ends normally (`ChatNewsKind.Finished`) | yes |
| Asks a question | a chat waits on `AskUserQuestion` (`ChatAskKind.Question`) | yes |
| Asks permission | a chat waits on a permission prompt (`ChatAskKind.Permission`) | yes |
| Fails | a chat's turn ends with an error (`ChatNewsKind.Failed`) | yes |
| Raven hears its name | Open mic heard "Raven" and now listens for the rest (#217) | **no: plays at once** |

A chat that waits on the user while Raven holds no card for it (`ChatNewsKind.NeedsYou`: a chat asked in its VS Code tab,
a "waiting for your input" notification, a card handed over to VS Code) uses the **Asks a question** slot, and is
outdated once the chat no longer waits (Claude's call; today it gets the chime too, it must not go silent).

"Starts working" plays for every chat's turn, including one the user has just typed into and one Raven sent a message to
(the user said all sounds for all chats). Each slot can be set to "None".

## The queue (user's decisions)

- Every event of an unmuted chat puts a message in one queue. A muted chat puts nothing in it. This holds for the chat
  the user is in too.
- The queue plays its sounds **one after another**, never two at once, with a short gap between them (the gap is the
  builder's call; a few hundred milliseconds).
- It **holds while the floor is busy**: the same floor today's chime and news wait for (`TrafficWatcher.IsFree`: Raven
  speaking or telling, the user talking or holding push-to-talk, a card or proposal open, Raven waiting for the words
  after its name). When the floor frees, it plays from the front.
  Once the floor frees, the queue also waits `TrafficWatcher.NewsGrace` (1.5 s of quiet), as news does, so a sound
  does not step on the user's next sentence. Sounds do **not** wait "Pause between messages", and a sound does not
  restart it for the next sound: that pause is for Raven's speech only (Claude's calls).
- **Mutes** (Claude's calls, following the user's "yes for muted" and the rule that what the user asks for gets
  through): Raven's panel mute (`RavenPanelViewModel.IsMuted`) silences every queued sound, as it silences today's
  chime. A chat's own mute (`RavenChat.IsMuted`) keeps that chat's events out of the queue. "Speak chat news" off does
  **not** silence sounds: the sounds are the way to follow chats without speech.
- **No cooldown**: nothing is dropped for coming soon after another sound.
- **Outdated messages are dropped** before they play. A message is outdated when what it announces is no longer true:
  a question or permission prompt that was answered (in Raven or in VS Code) or withdrawn; a "starts working" of a chat
  that has since finished, failed or asked something; a "finishes"/"fails" of a chat that has since started a new turn.
  The exact rule per slot is the builder's call within that meaning; it must be covered by tests.
- **The chat the user is in:** its sound plays, and Raven's spoken line for the same event (news, a question read out)
  follows straight after it. "Pause between messages" does not apply between a chat's own sound and its own line.
  Everything else Raven says on its own still waits the pause after a sound, as today.
- **Raven hears its name** skips the queue and plays immediately, even though the floor is busy (the user is talking).
  It belongs to no chat, so no chat mute applies, and it answers the user calling Raven, so the panel mute does not
  silence it either. Only its picker set to None or the sounds slider at 0 silences it.
  It should be short and quiet so it does not get in the way of the user's sentence or the speech-to-text.

### Order: the chat you are in goes first (Claude's recommendation, played back to the user without objection)

When the queue and Raven's own speech are both ready, **the chat the user is in goes first**: its sound waits until its
spoken line is worded, then sound and line play back to back, ahead of other chats' queued sounds. The catch-up on
switching chats also goes before other chats' queued sounds, so a stream of sounds can never hold it back. Other chats'
sounds follow in their order.

### Raven mentions the other waiting chats (user's idea; issue 4)

When Raven speaks the news of the chat the user is in and other chats are waiting at that moment, it adds a short
remark about them after that chat's line. Example (wording is only an example):

> "Chat CodeSwitchX finished: the tests pass. By the way, one chat has a question and another has finished."

With nothing else waiting, Raven says only the chat's line, as today.

- **Worded by Raven's model, not built by the app (user's decision):** nothing in the remark is hard-coded, not "BTW",
  not a template. The teller is given every other chat that waits (all of them, no cap), with what each waits for
  (question, permission, finished, failed, needs you) and its workspace name, and decides itself how to say it: names,
  counts or both, short. The app only decides *whether* there is a remark (something else waits) and that it comes
  after the chat's own line.
- **The sounds of the chats it mentions are dropped (user's decision):** what Raven has just said in words is not
  beeped afterwards. Every chat handed to the teller for the remark counts as mentioned once the remark is spoken. A
  chat whose event comes in after the remark was worded is not in it and keeps its sound.
- The same muting applies as for the sounds: a muted chat is not mentioned.
- The remark never stands alone: other chats' news is still never spoken on its own (today's rule), only alongside the
  news of the chat the user is in. (Whether the catch-up on switching also gets the remark is the builder's call; the
  catch-up already tells what happened in the chat switched to.)

## Volume (user's decision: two sliders; values are Claude's calls)

- **Sounds**: 0–100 %, default about 60 % (near today's chime, which is fixed at a third of full scale; the builder sets
  the default so the new default sounds about as loud as today's chime).
- **Raven's voice**: 0–100 %, default 100 % = today's loudness, so nothing changes until the user moves it.
- Both apply at once, also to sound already playing or queued. Releasing the sounds slider plays a sample; releasing the
  voice slider has Raven say a short line, so the user hears the level they set.
- The voice slider covers everything spoken in Raven's voice: answers, news, catch-ups, questions read out, and the voice
  samples on the Voice page. The sounds slider covers the six sounds and their play buttons.
- At 0 the sound or voice is silent but everything else behaves as if it played (the queue still waits for it, news is
  still marked).
- Both are persisted like the other Raven settings (`SettingKeys`, `SettingsViewModel`).

## Sounds shipped and added

- **Shipped pool (Claude's call within the user's "musical, natural"):** about 10–12 short sounds: marimba, kalimba,
  soft bells, wooden knocks, plucks. All CC0 (or an equally free licence that needs no credit in the app); the source and
  licence of each are recorded in the repo next to them. Each slot gets its own default from the pool. The user listens
  to the defaults on screen before the PR that ships them is merged.
- **User's own sounds:** an "Add sound…" button opens a file picker for WAV or MP3. The file is **copied** into
  CodeSwitchX's data folder, so moving or deleting the original breaks nothing. Added sounds join the pool for all six
  slots; each has a play and a remove button. Removing a sound in use sets the slots that used it back to their default.
- **Long files (user's decision):** a file longer than 3 s is accepted with the notice "This sound is 12 s long. Only the
  first 3 s will be used." On playing, it is cut at 3 s with a short fade-out, not a hard stop. (3 s is Claude's call.)
- A file that cannot be read is refused with a message; a sound file that has gone missing plays the slot's default.

## Settings layout (user's choice: layout A)

A new page **Sounds** in the Settings sidebar under Raven, right after Voice. From top to bottom:

1. **Volume**: the two sliders, "Sounds" and "Raven's voice", with their percentage.
2. **When a chat…**: the six slots, each a row with its name, a picker and a play button. "Raven hears its name" notes
   that it plays at once.
3. **Your sounds**: the added files with length, play and remove; the "Add sound…" button and the 3 s note.

The page gets its entry in `SettingsPage`/`SettingsPageItem.All` with search keywords (sound, beep, chime, volume, loud,
quiet, …). The mockup is in `.superpowers/brainstorm/` (layout A).

## Old settings removed (user's decision)

From the Voice page's "While you work", delete with their keys, view-model properties, rows and code paths:

- **Sound for other chats** (`TrafficWatcher.SoundOn`): replaced by the six pickers' "None".
- **Cooldown** (`raven.cooldownSeconds`, `TrafficWatcher.Cooldown`, `CooledDown`, `TrySound`'s cooldown check).
- **The chat I'm in also waits for the cooldown** (`raven.ownNewsWaits`, `TrafficWatcher.OwnNewsWaits`,
  `MaySpeakOwnNews`).

**Pause between messages** stays (with the exception above). The search keywords and help texts that mention the removed
settings or "other chats are never spoken… a short sound" are updated. Stored values of the removed keys are ignored
(deleting them from the database is the builder's call).

The user asked explicitly: after removing them, check **on screen** that news, sounds and Raven's speech still behave
cleanly: the chat you are in still speaks its news, questions are still read out, the catch-up still comes, nothing
talks over anything.

## Delivery

A milestone "Raven sounds" with four issues, one PR each, each usable on screen on its own (the user's rule: one issue,
one PR; split the issue, never the PR):

1. #305 **Sounds page with both volume sliders.** The new page with the Volume section only; today's chime follows the sounds
   slider, Raven's voice the voice slider.
2. #306 **Six sounds through a queue.** The shipped pool, the six pickers with play buttons, the queue for all chats with the
   floor rule and outdated drops, "Raven hears its name" at once, and the three old settings removed with the on-screen
   check. Today's chime is retired (or becomes one of the pool, builder's call).
3. #307 **Your own sounds.** Add, copy, play, remove, the 3 s notice and cut.
4. #308 **Raven mentions the other waiting chats.** The remark after the chat's own line, worded by the teller, and the
   mentioned chats' sounds dropped.


## Left to the builder

- Where the queue lives (`TrafficWatcher`, a new class beside it, or the Voice project) and how it learns of events.
- How "Starts working" is detected (e.g. the chat's state turning to working, `RavenChat.IsWorking`, or a
  `PromptSubmit` signal) as long as it fires once per turn.
- How volume is applied (sample scaling in the player or `IWavePlayer.Volume`), as long as it does not change the
  device's or Windows' volume and changes apply live.
- The gap between queued sounds, the exact defaults, and which pool sound is which slot's default.
- Mp3 decoding (NAudio's `Mp3FileReader`/MediaFoundation) and converting files to the output's format.
