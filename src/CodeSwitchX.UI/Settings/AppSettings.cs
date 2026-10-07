using System.Globalization;
using CodeSwitchX.Conductor;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Voice;
using CodeSwitchX.Voice.Dictation;
using CodeSwitchX.Voice.Speech;

namespace CodeSwitchX.UI.Settings;

/// <summary>
/// The settings Raven reads and changes by voice (#126), one list: each with its page, plain name, the values it takes and
/// what it does. A change sets the same property the Settings page binds to (or the panel's, for what the panel owns), so
/// the page, the panel, the stored value and whatever follows (a download, the voice restarting) are as after a click.
/// What deletes data or changes another program's files is not changed by voice: Settings opens at its page instead.
/// </summary>
public sealed class AppSettings : IAppSettings
{
    private const string On = "on", Off = "off";

    private readonly Func<ShellViewModel> _shell;
    private readonly IUiDispatcher _ui;
    private readonly ChatSettings _chats;
    private readonly IReadOnlyList<Entry> _entries;

    /// <param name="chats">The model aliases, which a model is named by, as set_defaults names it.</param>
    public AppSettings(Func<ShellViewModel> shell, IUiDispatcher ui, ChatSettings chats)
    {
        _shell = shell;
        _ui = ui;
        _chats = chats;
        _entries = Entries();
    }

    /// <summary>The list, with the values that change (voices, microphones, model names) as they are now.</summary>
    public Task<IReadOnlyList<AppSetting>> ListAsync(CancellationToken ct) => _ui.InvokeAsync<IReadOnlyList<AppSetting>>(
        () => [.. _entries.Select(e => e.Values?.Invoke() is { } values ? e.Info with { Values = values } : e.Info)], RavenActions.UiTimeout, ct);

    private SettingsViewModel S => _shell().Settings;

    private RavenPanelViewModel R => _shell().Raven;

    public Task<AppSettingValue> GetAsync(string name, CancellationToken ct)
    {
        var entry = Find(name);
        return _ui.InvokeAsync(() => ValueOf(entry), RavenActions.UiTimeout, ct);
    }

    public Task<AppSettingValue> SetAsync(string name, string value, CancellationToken ct)
    {
        var entry = Find(name);
        return _ui.InvokeAsync(() =>
        {
            if (entry.Set is null)
            {
                _shell().BringForward();
                _shell().OpenSettingsAt(entry.Page); // the user does it there
                throw new YardActionException($"{Capital(entry.Info.Name)} is not changed by voice: {entry.Info.NotByVoice} "
                    + $"Settings is open at {Title(entry.Page)}.");
            }

            var note = entry.Set(value.Trim());
            return ValueOf(entry) with { Note = note };
        }, RavenActions.UiTimeout, ct);
    }

    public Task<string> OpenAsync(string? page, CancellationToken ct)
    {
        // "Settings", "the settings": no page named, as none given.
        SettingsPage? target = string.IsNullOrWhiteSpace(page) || PageWords(page).Count == 0 ? null : PageNamed(page);
        return _ui.InvokeAsync(() =>
        {
            _shell().BringForward();
            if (target is { } named)
            {
                _shell().OpenSettingsAt(named);
            }
            else
            {
                _shell().OpenSettings();
            }

            return Title(S.Page);
        }, RavenActions.UiTimeout, ct);
    }

    private AppSettingValue ValueOf(Entry entry) => new(entry.Info.Name, entry.Info.Page, entry.Get(), entry.Values?.Invoke() ?? entry.Info.Values);

    /// <summary>
    /// A page by how the user says it: by a word of its title ("voice", "the listening settings", "privacy"), else by a
    /// setting on it, as the sidebar's search finds it ("the microphone settings" is Listening, "hooks" Claude Code).
    /// </summary>
    internal static SettingsPage PageNamed(string said)
    {
        var words = PageWords(said);
        var found = SettingsPageItem.All.Where(p => Words(p.Title).Any(words.Contains)).ToList();
        if (found.Count == 0)
        {
            found = [.. SettingsPageItem.All.Where(p => words.Any(w => w.Length > 2 && p.Keywords.Any(k => Words(k).Contains(w))))];
        }

        return found switch
        {
            [var one] => one.Page,
            [] => throw new YardActionException($"Settings has no page '{said.Trim()}'. Its pages: "
                + string.Join(", ", SettingsPageItem.All.Select(p => p.Title)) + "."),
            _ => throw new YardActionException($"'{said.Trim()}' could be the {string.Join(" or the ", found.Select(p => p.Title))} page: "
                + "ask the user which."),
        };
    }

    /// <summary>The words of a page as said, without "open", "the", "settings" and the like.</summary>
    private static List<string> PageWords(string said) =>
        [.. Words(said).Where(w => w is not ("settings" or "setting" or "page" or "the" or "my" or "open" or "show" or "raven" or "codeswitchx"))];

    /// <summary>
    /// A setting by its name or another way of saying it. Otherwise by whole words: the name whose words are all in what was
    /// said, the most of them winning ("open mic mode" is open mic, not the microphone's "mic"), or the names that hold all
    /// the words said ("model" is any of the models: the user is asked which).
    /// </summary>
    private Entry Find(string name)
    {
        var key = Normal(name);
        if (_entries.Where(e => e.Names.Contains(key)).ToList() is [var exact])
        {
            return exact;
        }

        var said = Words(name).Where(w => w is not ("the" or "setting" or "settings")).ToHashSet();
        var scored = _entries.Select(e => (Entry: e, Score: e.Names.Select(n => Words(n)).Max(n =>
                n.All(said.Contains) ? n.Length * 2 : said.Count > 0 && said.All(n.Contains) ? said.Count * 2 - 1 : 0)))
            .Where(x => x.Score > 0).ToList();
        var best = scored.Count == 0 ? [] : scored.Where(x => x.Score == scored.Max(y => y.Score)).Select(x => x.Entry).ToList();
        return best is [var one] ? one
            : throw new YardActionException(best.Count > 1
                ? $"'{name.Trim()}' could be {string.Join(" or ", best.Select(e => e.Info.Name))}: ask the user which."
                : $"There is no setting '{name.Trim()}'. list_settings lists them.");
    }

    private static string Title(SettingsPage page) => SettingsPageItem.All.Single(p => p.Page == page).Title;

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string[] Words(string text) =>
        Normal(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lower case, the apostrophes gone and any other punctuation a space: "Brain & chats" is "brain chats".</summary>
    internal static string Normal(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Where(c => c is not ('\'' or '’')).Select(c => char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(" ", new string([.. chars]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"on", "yes", "true", "enabled" and the like; anything else is not allowed.</summary>
    private static bool Switch(string name, string value) => Normal(value) switch
    {
        "on" or "yes" or "true" or "enabled" or "enable" or "1" => true,
        "off" or "no" or "false" or "disabled" or "disable" or "0" => false,
        _ => throw new YardActionException($"{Capital(name)} is on or off, not '{value}'. Nothing was changed."),
    };

    private static string OnOff(bool value) => value ? On : Off;

    /// <summary>The one of <paramref name="values"/> the user means: its name, or the start of one.</summary>
    private static string OneOf(string name, string value, IReadOnlyList<string> values)
    {
        var key = Normal(value);
        var found = values.Where(v => Normal(v) == key).ToList();
        if (found.Count == 0)
        {
            found = [.. values.Where(v => key.Length > 1 && Normal(v).Contains(key))];
        }

        return found is [var one] ? one
            : throw new YardActionException($"{Capital(name)} is one of {string.Join(", ", values)}, not '{value}'. Nothing was changed.");
    }

    /// <summary>
    /// The count in what was said, with its scale: "20", "20 seconds", "1.5 million", "200k", "2,000,000", "1.500.000".
    /// Groups of three digits after a comma, or after more than one point, are thousands; otherwise the comma or point is
    /// the decimal point. Null when there is none, it is negative, or it is too large.
    /// </summary>
    internal static long? Number(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value.ToLowerInvariant(),
            @"(-)?(\d{1,3}(?:,\d{3})+|\d{1,3}(?:\.\d{3}){2,}|\d+)(?:[.,](\d+))?\s*(k\b|thousand|m\b|million|mio)?");
        if (!match.Success || match.Groups[1].Success || match.Groups[2].Value.Length > 24)
        {
            return null;
        }

        try
        {
            var number = decimal.Parse(match.Groups[2].Value.Replace(",", "").Replace(".", ""), CultureInfo.InvariantCulture);
            if (match.Groups[3].Success)
            {
                number += decimal.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture);
            }

            number *= match.Groups[4].Value switch
            {
                "k" or "thousand" => 1_000,
                "m" or "million" or "mio" => 1_000_000,
                _ => 1,
            };
            return number <= long.MaxValue ? (long)Math.Round(number) : null;
        }
        catch (OverflowException)
        {
            return null; // more than any count
        }
    }

    /// <summary>The Whisper models by the names their Listening rows show (#141): one added later is listed too.</summary>
    private static readonly IReadOnlyList<(WhisperModel Model, string Name)> WhisperNames =
        [.. Enum.GetValues<WhisperModel>().Select(m => (m, ModelLamp.RowNameOf(m)))];

    /// <summary>The engines by the names the Voice page shows, as stored, and None.</summary>
    private static readonly IReadOnlyList<(string Stored, string Name)> EngineNames =
        [(SettingsViewModel.NoEngine, "None"), .. Enum.GetValues<SpeechEngine>().Select(e => (e.ToString(), ModelLamp.NameOf(e)))];

    private static readonly IReadOnlyList<(SpeechModel Model, string Name)> VoiceModelNames = [(SpeechModel.Small, "0.6B"), (SpeechModel.Large, "1.7B")];

    private IReadOnlyList<Entry> Entries()
    {
        IReadOnlyList<string> onOff = [On, Off];
        Entry Toggle(string name, SettingsPage page, string description, Func<bool> get, Action<bool> set, params string[] aliases) =>
            new(new(name, Title(page), description, onOff), page, aliases, () => OnOff(get()), v =>
            {
                set(Switch(name, v));
                return null;
            });

        // A count of seconds the Voice page offers in a box: "20", "20 seconds".
        Entry Seconds(string name, string description, string[] aliases, IReadOnlyList<int> choices, Func<int> get, Action<int> set) =>
            new(new(name, Title(SettingsPage.Voice), description, [.. choices.Select(c => $"{c} seconds")]), SettingsPage.Voice, aliases,
                () => $"{get()} seconds", v =>
                {
                    set(Number(v) is { } n && choices.Contains((int)Math.Min(n, int.MaxValue)) ? (int)n
                        : throw new YardActionException($"{Capital(name)} is one of {string.Join(", ", choices)} seconds, not '{v}'. Nothing was changed."));
                    return null;
                });

        // A time the Yard page offers in a box, worded as there, or none: "10", "10 minutes", "off", "0". Said with a
        // unit, it is that long: "1 hour" is no "1 minute", and 60 minutes are one hour.
        Entry Time(string name, string description, string[] aliases, Func<int, string> worded, int unitMinutes, IReadOnlyList<int> choices, Func<int> get,
            Action<int> set)
        {
            string Text(int count) => worded(count).ToLowerInvariant();
            return new(new(name, Title(SettingsPage.Yard), description, [.. choices.Select(Text)]), SettingsPage.Yard, aliases, () => Text(get()), v =>
            {
                set(Normal(v) is "off" or "none" or "never" ? 0
                    : Minutes(Normal(v), unitMinutes) is { } minutes && minutes % unitMinutes == 0 && choices.Contains((int)(minutes / unitMinutes))
                        ? (int)(minutes / unitMinutes)
                    : throw new YardActionException($"{Capital(name)} is one of {string.Join(", ", choices.Select(Text))}, not '{v}'. Nothing was changed."));
                return null;
            });
        }

        // The minutes in "10", "10 minutes", "2 hours": a bare number is in the setting's own unit. Null for anything else.
        static long? Minutes(string said, int unitMinutes) =>
            System.Text.RegularExpressions.Regex.Match(said, @"^(\d{1,6})(?: (minutes?|mins?|hours?|hrs?))?$") is { Success: true } match
                ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
                    * (match.Groups[2].Value is { Length: > 0 } unit ? unit[0] == 'h' ? 60 : 1 : unitMinutes)
                : null;

        return
        [
            // Voice
            new(new("voice engine", Title(SettingsPage.Voice), "What Raven speaks with: None (text only), Kokoro (small, on the CPU) or "
                    + "Qwen3-TTS (more natural, needs an NVIDIA graphics card).", [.. EngineNames.Select(e => e.Name)]), SettingsPage.Voice,
                ["engine", "speech engine", "text to speech", "tts"],
                () => EngineNames.FirstOrDefault(e => e.Stored == S.RavenVoiceEngine).Name ?? S.RavenVoiceEngine,
                v =>
                {
                    var name = OneOf("the voice engine", v, [.. EngineNames.Select(e => e.Name)]);
                    var stored = EngineNames.Single(e => e.Name == name).Stored;
                    if (S.RavenVoiceEngine == stored)
                    {
                        return null; // nothing changes
                    }

                    S.RavenVoiceEngine = stored;
                    return name == "None" ? "Raven answers in text only now." : "A voice not on this PC yet is installed the first time Raven speaks.";
                }),
            new(new("voice", Title(SettingsPage.Voice), "The voice Raven speaks with, one of the engine's voices.", null), SettingsPage.Voice,
                ["voices"], // "speaker" is the output Raven speaks on (#172)
                () => CurrentVoices() is { } voices ? voices.FirstOrDefault(x => x.Id == CurrentVoiceId())?.Name ?? CurrentVoiceId() : "none: Raven speaks with no engine",
                v =>
                {
                    // The engine's own voices first; a voice of the other engine switches to it, as picking it on the page does.
                    var engines = CurrentVoices() is null ? [] : new[] { CurrentEngine() };
                    var everyVoice = engines.Concat([SpeechEngine.Kokoro, SpeechEngine.Qwen]).Distinct()
                        .SelectMany(e => SpeechSettings.VoicesOf(e).Select(x => (Engine: e, Voice: x))).ToList();
                    if (engines.Length == 0 && Normal(v) is var key && everyVoice.All(x => Normal(x.Voice.Name) != key))
                    {
                        throw new YardActionException("No voice engine is picked, so Raven has no voice: set the voice engine to Kokoro or "
                            + "Qwen3-TTS first. Nothing was changed.");
                    }

                    var own = engines.Length == 0 ? [] : SpeechSettings.VoicesOf(engines[0]).Select(x => x.Name).ToList();
                    var name = own.Count > 0 && own.Any(n => Normal(n) == Normal(v) || Normal(n).Contains(Normal(v)))
                        ? OneOf("the voice", v, own)
                        : OneOf("the voice", v, [.. everyVoice.Select(x => x.Voice.Name).Distinct()]);
                    var picked = everyVoice.First(x => x.Voice.Name == name);
                    S.PickVoice(picked.Engine, picked.Voice.Id);
                    return null;
                },
                () => CurrentVoices()?.Select(x => x.Name).ToList()),
            new(new("voice model", Title(SettingsPage.Voice), "Qwen3-TTS's model: 0.6B is faster, 1.7B sounds better.",
                    [.. VoiceModelNames.Select(m => m.Name)]), SettingsPage.Voice, ["qwen model", "tts model"],
                () => VoiceModelNames.Single(m => m.Model == S.RavenVoiceModel).Name,
                v =>
                {
                    var name = OneOf("the voice model", v, [.. VoiceModelNames.Select(m => m.Name)]);
                    var model = VoiceModelNames.Single(m => m.Name == name).Model;
                    var changed = S.RavenVoiceModel != model;
                    S.RavenVoiceModel = model;
                    // Only Qwen3-TTS speaks with it: with Kokoro, or none, it waits for Qwen3-TTS to be picked.
                    return !changed || S.RavenVoiceEngine != nameof(SpeechEngine.Qwen) ? null
                        : "The voice restarts with it; 1.7B downloads (3.5 GB) the first time Raven speaks with it.";
                }),
            Toggle("speak chat news", SettingsPage.Voice, "Raven says when the chat the user is in finishes, fails or needs them; off, it is "
                + "only written.", () => S.RavenSpeakNews, v => S.RavenSpeakNews = v, "chat news", "news"),
            Toggle("sound for other chats", SettingsPage.Voice, "Other chats make a short sound, when it is quiet, instead of being spoken; "
                + "off, they are only marked in the list.", () => S.RavenChatSound, v => S.RavenChatSound = v, "chime", "sound", "other chats"),
            Seconds("cooldown", "Seconds other chats stay silent after Raven speaks or a chat makes its sound.", ["cool down", "quiet time"],
                TrafficWatcher.CooldownChoices, () => S.RavenCooldownSeconds, v => S.RavenCooldownSeconds = v),
            Seconds("pause between messages", "Seconds what Raven says on its own (news, a catch-up, a question read out) waits after Raven "
                    + "last spoke or a chat made its sound; a chat's sound inside it is left out. Raven's answers to the user never wait.",
                ["pause", "gap", "gap between messages", "pause between news", "time between messages"],
                TrafficWatcher.PauseChoices, () => S.RavenPauseSeconds, v => S.RavenPauseSeconds = v),
            Toggle("the chat I'm in also waits for the cooldown", SettingsPage.Voice, "On, the news of the chat the user is in is only shown "
                + "if Raven spoke or a chat made its sound within the cooldown; the catch-up on switching is still said.", () => S.RavenOwnNewsWaits, v => S.RavenOwnNewsWaits = v,
                "own news waits", "my chat waits", "chat I'm in waits", "chat I'm in waits for the cooldown"),
            Toggle("catch-up when I switch chats", SettingsPage.Voice, "On, switching to a chat with something new makes Raven say in a "
                + "sentence or two what happened there while the user was away, a pause after Raven last spoke or a chat made its sound. "
                + "Its own switch: it is said with chat news only written too.", () => S.RavenCatchUp, v => S.RavenCatchUp = v,
                "catch-up", "catch up", "catchup"),
            Toggle("muted", SettingsPage.Voice, "Raven writes its answers without speaking them (the speaker button on Raven's panel).",
                () => R.IsMuted, v => R.IsMuted = v, "mute", "silent"),

            // Listening
            new(new("open mic", Title(SettingsPage.Listening), "On, Raven listens all the time and hears when the user speaks; off, it is "
                    + "push to talk: the user holds a key or the mic button. Also set as \"open mic\" or \"push to talk\"; \"push to talk\" "
                    + "is also a setting of its own, the other way round.", onOff),
                SettingsPage.Listening, ["mic mode", "always listening", "listening mode"],
                () => R.MicMode == MicMode.OpenMic ? On
                    : R.PreferredMicMode == MicMode.OpenMic ? "off: it is switched on, but fell back to push to talk after a failure" : Off,
                v =>
                {
                    var open = Normal(v) switch
                    {
                        "open mic" or "open" => true,
                        "push to talk" or "push" => false,
                        _ => Switch("open mic", v),
                    };
                    R.ChooseMicModeCommand.Execute(open ? MicMode.OpenMic : MicMode.PushToTalk);
                    return null;
                }),
            Toggle("push to talk", SettingsPage.Listening, "On, the user holds a key or the mic button to talk; off, open mic: Raven "
                    + "listens all the time.", () => R.MicMode != MicMode.OpenMic,
                v => R.ChooseMicModeCommand.Execute(v ? MicMode.PushToTalk : MicMode.OpenMic), "push-to-talk"),
            new(new("microphone", Title(SettingsPage.Listening), "The microphone Raven starts with and hears, unless another is being "
                    + "tried on Raven's panel. Setting it also ends that trial.", null), SettingsPage.Listening, ["mic", "input"],
                () => R.DefaultMicrophone?.Name ?? "none",
                v =>
                {
                    if (R.Microphones.Count == 0)
                    {
                        throw new YardActionException("No microphone is listed right now: Windows reports none. Nothing was changed.");
                    }

                    var name = OneOf("the microphone", v, [.. R.Microphones.Select(m => m.Name)]);
                    R.ChooseDefaultMicrophone(R.Microphones.First(m => m.Name == name)); // the same default again still ends a trial
                    return null;
                },
                () => [.. R.Microphones.Select(m => m.Name)]),
            new(new("speaker", Title(SettingsPage.Voice), "The output Raven speaks on, unless another is being tried on Raven's panel. "
                    + "Setting it also ends that trial.", null), SettingsPage.Voice, ["speakers", "audio output", "output", "headphones"],
                () => R.Speakers?.DefaultSpeaker?.Name ?? "none",
                v =>
                {
                    if (R.Speakers is not { Speakers.Count: > 0 } speakers)
                    {
                        throw new YardActionException("No output is listed right now: Windows reports none. Nothing was changed.");
                    }

                    var name = OneOf("the speaker", v, [.. speakers.Speakers.Select(s => s.Name)]);
                    speakers.ChooseDefault(speakers.Speakers.First(s => s.Name == name)); // the same default again still ends a trial
                    return null;
                },
                () => [.. R.Speakers?.Speakers.Select(s => s.Name) ?? []]),
            Toggle("talk over Raven", SettingsPage.Listening, "In open mic, the user talking over Raven stops it; off, open mic ignores "
                + "speech while Raven speaks (Raven heard on speakers).", () => S.RavenBargeIn, v => S.RavenBargeIn = v, "barge in", "interrupt"),
            new(new("speech to text model", Title(SettingsPage.Listening), "The Whisper model that writes down what the user says: Tiny, "
                    + "Base and Small hear English only and are smaller and faster; Large v3 Turbo also hears other languages.", [.. WhisperNames.Select(w => w.Name)]),
                SettingsPage.Listening, ["whisper", "whisper model", "dictation model", "speech recognition"],
                () => ModelLamp.RowNameOf(S.RavenWhisperModel),
                v =>
                {
                    // "Whisper Tiny", "tiny English", "base.en": the row's name is the model's.
                    var said = string.Join(" ", Words(v).Where(w => w is not ("whisper" or "english" or "en" or "model")));
                    var name = OneOf("the speech-to-text model", said.Length > 0 ? said : v, [.. WhisperNames.Select(w => w.Name)]);
                    S.RavenWhisperModel = WhisperNames.Single(w => w.Name == name).Model;
                    return "A model not on this PC downloads with the next dictation, or from its row on the Listening page.";
                }),

            // Brain & chats
            new(new("Raven's model", Title(SettingsPage.Brain), "The model Raven's window chats think with; the next question starts a new "
                    + "conversation with it.", SettingsViewModel.KnownBrainModels), SettingsPage.Brain, ["brain model", "raven model", "your model"],
                () => S.RavenBrainModel, v => SetModel(v, m => S.RavenBrainModel = m)),
            new(new("chat 0's model", Title(SettingsPage.Brain), "The model chat 0, the overview, answers with and words its chat summaries with.",
                    SettingsViewModel.KnownBrainModels), SettingsPage.Brain, ["overview model", "chat zero model", "yard model"],
                () => S.RavenOverviewModel, v => SetModel(v, m => S.RavenOverviewModel = m)),
            new(new("Raven's effort", Title(SettingsPage.Brain), "How hard all of Raven's chats think before they answer (\"default\": Claude "
                    + "Code's own); higher follows its rules more reliably and answers later. The conversation carries on.",
                    SettingsViewModel.EffortChoices), SettingsPage.Brain, ["your effort", "raven effort", "brain effort", "thinking effort"],
                () => S.RavenEffort,
                v =>
                {
                    S.RavenEffort = ChatSettings.DefaultEffortOf(v) ?? SettingsViewModel.ClaudeDefault;
                    return null;
                }),
            new(new("new chats' model", Title(SettingsPage.Brain), "The model a Claude Code chat Raven starts runs with (\"default\": Claude "
                    + "Code's own).", null), SettingsPage.Brain, ["chat model", "model for new chats", "new chat model"],
                () => S.RavenChatModel,
                v =>
                {
                    S.SetChatDefaults(_chats.Defaults with { Model = _chats.DefaultModelOf(v) }); // as set_defaults takes it
                    return null;
                },
                () => S.ChatModelChoices),
            new(new("new chats' effort", Title(SettingsPage.Brain), "The effort a chat Raven starts runs at (\"default\": Claude Code's own).",
                    SettingsViewModel.EffortChoices), SettingsPage.Brain, ["effort", "chat effort", "effort for new chats"],
                () => S.RavenChatEffort,
                v =>
                {
                    S.SetChatDefaults(_chats.Defaults with { Effort = ChatSettings.DefaultEffortOf(v) }); // as set_defaults takes it
                    return null;
                }),

            // Yard
            Time("keep a closed chat", "How long a chat stays on its tile, greyed, after its tab was closed; \"off\" for not at all.",
                ["keep closed chats", "closed chats", "closed chat", "keep ended chats"], SettingsViewModel.KeepClosedText, 1, Yard.YardViewModel.KeepClosedMinutesChoices,
                () => S.YardKeepClosedMinutes, v => S.YardKeepClosedMinutes = v),
            Time("hide an idle chat", "How long a chat may do nothing before its tile hides it, though its tab is open; \"never\" to show "
                    + "every open chat tab.",
                ["hide idle chats", "idle chats", "idle chat", "hide a chat that has been idle"], SettingsViewModel.HideIdleText, 60, Yard.YardViewModel.HideIdleHoursChoices,
                () => S.YardHideIdleHours, v => S.YardHideIdleHours = v),

            // Usage
            new(new("5-hour budget", Title(SettingsPage.Usage), "The tokens of the 5-hour window the bottom bar measures against; \"off\" for "
                    + "none.", null), SettingsPage.Usage, ["budget", "token budget", "five hour budget"],
                () => S.FiveHourBudgetTokens is { } tokens ? tokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens" : Off,
                v =>
                {
                    S.FiveHourBudgetTokens = Normal(v) is "off" or "none" ? null
                        : Number(v) is { } n && n > 0 ? n
                        : throw new YardActionException($"The 5-hour budget is a number of tokens or off, not '{v}'. Nothing was changed.");
                    return null;
                }),

            // Privacy & data
            Toggle("store payloads", SettingsPage.Privacy, "Keeps the full hook payloads of the chats in the database, for debugging.",
                () => S.StorePayloads, v => S.StorePayloads = v, "payloads", "keep payloads"),

            // Not by voice
            new(new("shortcuts", Title(SettingsPage.Shortcuts), "The keys that switch Raven's chat.", null,
                    NotByVoice: "a shortcut is set by pressing it in its box."), SettingsPage.Shortcuts, ["hotkeys", "hotkey", "keys", "shortcut"],
                () => "set on the Shortcuts page", null),
            new(new("hooks", Title(SettingsPage.ClaudeCode), "The hooks that let every Claude Code chat report its state to CodeSwitchX.", null,
                    NotByVoice: "installing or removing them changes Claude Code's own settings file, so the user does it on the page."),
                SettingsPage.ClaudeCode, ["claude code hooks", "install hooks", "remove hooks"], () => S.HookStatusText, null),
            new(new("relay path", Title(SettingsPage.ClaudeCode), "The hook relay program Claude Code runs for every hook.", null,
                    NotByVoice: "every Claude Code chat runs it, so the user changes it on the page."), SettingsPage.ClaudeCode,
                ["relay", "hook relay", "relay executable"], () => S.RelayExecutable, null),
            new(new("data", Title(SettingsPage.Privacy), "What CodeSwitchX keeps, and where: its data folder and logs.", null,
                    NotByVoice: "Raven never deletes or moves data."), SettingsPage.Privacy,
                ["delete data", "clear data", "data folder", "logs", "database", "delete logs"], () => S.DataFolder, null),
        ];
    }

    private IReadOnlyList<SpeechVoice>? CurrentVoices() => S.RavenVoiceEngine switch
    {
        nameof(SpeechEngine.Kokoro) => SpeechSettings.KokoroVoices,
        nameof(SpeechEngine.Qwen) => SpeechSettings.QwenVoices,
        _ => null,
    };

    private SpeechEngine CurrentEngine() => S.RavenVoiceEngine == nameof(SpeechEngine.Kokoro) ? SpeechEngine.Kokoro : SpeechEngine.Qwen;

    private string CurrentVoiceId() => S.RavenVoiceEngine == nameof(SpeechEngine.Kokoro) ? S.RavenKokoroVoice : S.RavenQwenVoice;

    /// <summary>
    /// The model id a name means for Raven's own brain: an alias, an alias with its version, or the id of one of them or of a
    /// model the Brain page offers. Another id is refused: one Claude does not know would leave Raven unable to answer, and so
    /// unable to set it back by voice; it is typed on the page.
    /// </summary>
    private string? SetModel(string value, Action<string> set)
    {
        var id = _chats.ModelIdOf(value);
        if (!_chats.Aliases.Any(a => a.Id == id) && !SettingsViewModel.KnownBrainModels.Contains(id))
        {
            throw new YardActionException($"{id} is not a model Raven knows, and Raven could not answer at all with a wrong one: type it on "
                + $"the {Title(SettingsPage.Brain)} page if it is right. Nothing was changed.");
        }

        set(id);
        return null;
    }

    /// <param name="Aliases">Other ways the user may name it.</param>
    /// <param name="Set">Sets it from what was said and returns a note; null for a setting not changed by voice.</param>
    /// <param name="Values">The values it takes now, for a list that changes (voices, microphones); null for the listed ones.</param>
    private sealed record Entry(AppSetting Info, SettingsPage Page, string[] Aliases, Func<string> Get, Func<string, string?>? Set,
        Func<IReadOnlyList<string>?>? Values = null)
    {
        public IReadOnlyList<string> Names { get; } = [Normal(Info.Name), .. Aliases.Select(Normal)];
    }
}
