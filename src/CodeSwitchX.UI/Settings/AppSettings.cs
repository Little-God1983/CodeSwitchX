using CodeSwitchX.Core.Yard;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Raven;
using CodeSwitchX.UI.Shell;
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
    private readonly IReadOnlyList<Entry> _entries;

    public AppSettings(Func<ShellViewModel> shell, IUiDispatcher ui)
    {
        _shell = shell;
        _ui = ui;
        _entries = Entries();
        Settings = [.. _entries.Select(e => e.Info)];
    }

    public IReadOnlyList<AppSetting> Settings { get; }

    public IReadOnlyList<string> Pages { get; } = [.. SettingsPageItem.All.Select(p => p.Title)];

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
        SettingsPage? target = string.IsNullOrWhiteSpace(page) ? null : PageNamed(page);
        return _ui.InvokeAsync(() =>
        {
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

    /// <summary>A page by how the user says it: "voice", "the listening settings", "brain", "privacy".</summary>
    internal static SettingsPage PageNamed(string said)
    {
        var words = Words(said).Where(w => w is not ("settings" or "setting" or "page" or "the" or "my")).ToList();
        var key = string.Join(" ", words);
        var found = SettingsPageItem.All.Where(p => Words(p.Title).Any(t => words.Contains(t)) || Normal(p.Title) == key).ToList();
        return found switch
        {
            [var one] => one.Page,
            _ => throw new YardActionException($"Settings has no page '{said.Trim()}'. Its pages: "
                + string.Join(", ", SettingsPageItem.All.Select(p => p.Title)) + "."),
        };
    }

    /// <summary>A setting by its name or another way of saying it; a name that is part of only one also finds it.</summary>
    private Entry Find(string name)
    {
        var key = Normal(name);
        var exact = _entries.Where(e => e.Names.Contains(key)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = _entries.Where(e => e.Names.Any(n => key.Length > 2 && (n.Contains(key) || key.Contains(n)))).ToList();
        return partial.Count == 1 ? partial[0]
            : throw new YardActionException(partial.Count > 1
                ? $"'{name.Trim()}' could be {string.Join(" or ", partial.Select(e => e.Info.Name))}: ask the user which."
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
            found = [.. values.Where(v => key.Length > 1 && (Normal(v).StartsWith(key, StringComparison.Ordinal) || Normal(v).Contains(key)))];
        }

        return found is [var one] ? one
            : throw new YardActionException($"{Capital(name)} is one of {string.Join(", ", values)}, not '{value}'. Nothing was changed.");
    }

    /// <summary>The first whole number in what was said: "20", "20 seconds".</summary>
    private static long? Number(string value) =>
        new string([.. value.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c is ',' or '.').Where(char.IsDigit)]) is { Length: > 0 } digits
            && long.TryParse(digits, out var n) ? n : null;

    private static readonly IReadOnlyList<(WhisperModel Model, string Name)> WhisperNames =
    [
        (WhisperModel.TinyEnglish, "Tiny English"),
        (WhisperModel.BaseEnglish, "Base English"),
        (WhisperModel.SmallEnglish, "Small English"),
        (WhisperModel.LargeV3Turbo, "Large v3 Turbo"),
    ];

    private static readonly IReadOnlyList<(string Stored, string Name)> EngineNames =
    [
        (SettingsViewModel.NoEngine, "None"),
        (nameof(SpeechEngine.Kokoro), "Kokoro"),
        (nameof(SpeechEngine.Qwen), "Qwen3-TTS"),
    ];

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
                    S.RavenVoiceEngine = EngineNames.Single(e => e.Name == name).Stored;
                    return name == "None" ? "Raven answers in text only now." : "A voice not on this PC yet is installed the first time Raven speaks.";
                }),
            new(new("voice", Title(SettingsPage.Voice), "The voice Raven speaks with, one of the engine's voices.", null), SettingsPage.Voice,
                ["voices", "speaker"],
                () => CurrentVoices() is { } voices ? voices.FirstOrDefault(x => x.Id == CurrentVoiceId())?.Name ?? CurrentVoiceId() : "none: Raven speaks with no engine",
                v =>
                {
                    var voices = CurrentVoices() ?? throw new YardActionException("No voice engine is picked, so Raven has no voice: set the "
                        + "voice engine to Kokoro or Qwen3-TTS first. Nothing was changed.");
                    var name = OneOf("the voice", v, [.. voices.Select(x => x.Name)]);
                    var id = voices.Single(x => x.Name == name).Id;
                    if (S.RavenVoiceEngine == nameof(SpeechEngine.Kokoro))
                    {
                        S.RavenKokoroVoice = id;
                    }
                    else
                    {
                        S.RavenQwenVoice = id;
                    }

                    return null;
                },
                () => CurrentVoices()?.Select(x => x.Name).ToList()),
            new(new("voice model", Title(SettingsPage.Voice), "Qwen3-TTS's model: 0.6B is faster, 1.7B sounds better.",
                    [.. VoiceModelNames.Select(m => m.Name)]), SettingsPage.Voice, ["qwen model", "tts model"],
                () => VoiceModelNames.Single(m => m.Model == S.RavenVoiceModel).Name,
                v =>
                {
                    var name = OneOf("the voice model", v, [.. VoiceModelNames.Select(m => m.Name)]);
                    S.RavenVoiceModel = VoiceModelNames.Single(m => m.Name == name).Model;
                    return "The voice restarts with it; 1.7B downloads (3.5 GB) the first time Raven speaks with it.";
                }),
            Toggle("speak chat news", SettingsPage.Voice, "Raven says when the chat the user is in finishes, fails or needs them; off, it is "
                + "only written.", () => S.RavenSpeakNews, v => S.RavenSpeakNews = v, "chat news", "news"),
            Toggle("sound for other chats", SettingsPage.Voice, "Other chats make a short sound, when it is quiet, instead of being spoken; "
                + "off, they are only marked in the list.", () => S.RavenChatSound, v => S.RavenChatSound = v, "chime", "sound", "other chats"),
            new(new("cooldown", Title(SettingsPage.Voice), "Seconds other chats stay silent after Raven speaks or a chat makes its sound.",
                    [.. S0CooldownChoices().Select(c => $"{c} seconds")]), SettingsPage.Voice, ["cool down", "quiet time"],
                () => $"{S.RavenCooldownSeconds} seconds",
                v =>
                {
                    var seconds = Number(v) is { } n && S0CooldownChoices().Contains((int)Math.Min(n, int.MaxValue)) ? (int)n
                        : throw new YardActionException($"The cooldown is one of {string.Join(", ", S0CooldownChoices())} seconds, not '{v}'. "
                            + "Nothing was changed.");
                    S.RavenCooldownSeconds = seconds;
                    return null;
                }),
            Toggle("the chat I'm in also waits for the cooldown", SettingsPage.Voice, "On, the news of the chat the user is in is only shown "
                + "if Raven spoke or a chat made its sound within the cooldown.", () => S.RavenOwnNewsWaits, v => S.RavenOwnNewsWaits = v,
                "own news waits", "my chat waits", "chat i m in waits"),
            Toggle("muted", SettingsPage.Voice, "Raven writes its answers without speaking them (the speaker button on Raven's panel).",
                () => R.IsMuted, v => R.IsMuted = v, "mute", "silent"),

            // Listening
            new(new("open mic", Title(SettingsPage.Listening), "On, Raven listens all the time and hears when the user speaks; off, it is "
                    + "push to talk: the user holds a key or the mic button. Also set as \"open mic\" or \"push to talk\".", onOff),
                SettingsPage.Listening, ["mic mode", "always listening", "listening mode"],
                () => OnOff(R.PreferredMicMode == MicMode.OpenMic),
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
            new(new("microphone", Title(SettingsPage.Listening), "The microphone Raven hears.", null), SettingsPage.Listening, ["mic", "input"],
                () => R.SelectedMicrophone?.Name ?? "none",
                v =>
                {
                    var name = OneOf("the microphone", v, [.. R.Microphones.Select(m => m.Name)]);
                    R.SelectedMicrophone = R.Microphones.First(m => m.Name == name);
                    return null;
                },
                () => [.. R.Microphones.Select(m => m.Name)]),
            Toggle("talk over Raven", SettingsPage.Listening, "In open mic, the user talking over Raven stops it; off, open mic ignores "
                + "speech while Raven speaks (Raven heard on speakers).", () => S.RavenBargeIn, v => S.RavenBargeIn = v, "barge in", "interrupt"),
            new(new("speech to text model", Title(SettingsPage.Listening), "The Whisper model that writes down what the user says: the "
                    + "English ones are smaller and faster, Large v3 Turbo also hears other languages.", [.. WhisperNames.Select(w => w.Name)]),
                SettingsPage.Listening, ["whisper", "whisper model", "dictation model", "speech recognition"],
                () => WhisperNames.Single(w => w.Model == S.RavenWhisperModel).Name,
                v =>
                {
                    var name = OneOf("the speech-to-text model", v, [.. WhisperNames.Select(w => w.Name)]);
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
            new(new("new chats' model", Title(SettingsPage.Brain), "The model a Claude Code chat Raven starts runs with (\"default\": Claude "
                    + "Code's own).", null), SettingsPage.Brain, ["chat model", "model for new chats", "new chat model"],
                () => S.RavenChatModel,
                v =>
                {
                    S.RavenChatModel = OneOf("the new chats' model", v, S.ChatModelChoices);
                    return null;
                },
                () => S.ChatModelChoices),
            new(new("new chats' effort", Title(SettingsPage.Brain), "The effort a chat Raven starts runs at (\"default\": Claude Code's own).",
                    SettingsViewModel.EffortChoices), SettingsPage.Brain, ["effort", "chat effort", "effort for new chats"],
                () => S.RavenChatEffort,
                v =>
                {
                    S.RavenChatEffort = OneOf("the new chats' effort", v, SettingsViewModel.EffortChoices);
                    return null;
                }),

            // Usage
            new(new("5-hour budget", Title(SettingsPage.Usage), "The tokens of the 5-hour window the bottom bar measures against; \"off\" for "
                    + "none.", null), SettingsPage.Usage, ["budget", "token budget", "five hour budget"],
                () => S.FiveHourBudgetTokens is { } tokens ? $"{tokens:N0} tokens" : Off,
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
            new(new("shortcuts", Title(SettingsPage.Shortcuts), "The keys that switch Raven's chat.", null, ByVoice: false,
                    NotByVoice: "a shortcut is set by pressing it in its box."), SettingsPage.Shortcuts, ["hotkeys", "hotkey", "keys", "shortcut"],
                () => "set on the Shortcuts page", null),
            new(new("hooks", Title(SettingsPage.ClaudeCode), "The hooks that let every Claude Code chat report its state to CodeSwitchX.", null,
                    ByVoice: false, NotByVoice: "installing or removing them changes Claude Code's own settings file, so the user does it on the page."),
                SettingsPage.ClaudeCode, ["claude code hooks", "install hooks", "remove hooks"], () => S.HookStatusText, null),
            new(new("relay path", Title(SettingsPage.ClaudeCode), "The hook relay program Claude Code runs for every hook.", null, ByVoice: false,
                    NotByVoice: "every Claude Code chat runs it, so the user changes it on the page."), SettingsPage.ClaudeCode,
                ["relay", "hook relay", "relay executable"], () => S.RelayExecutable, null),
            new(new("data", Title(SettingsPage.Privacy), "What CodeSwitchX keeps, and where: its data folder and logs.", null, ByVoice: false,
                    NotByVoice: "Raven never deletes or moves data."), SettingsPage.Privacy,
                ["delete data", "clear data", "data folder", "logs", "database", "delete logs"], () => S.DataFolder, null),
        ];
    }

    private static IReadOnlyList<int> S0CooldownChoices() => TrafficWatcher.CooldownChoices;

    private IReadOnlyList<SpeechVoice>? CurrentVoices() => S.RavenVoiceEngine switch
    {
        nameof(SpeechEngine.Kokoro) => SpeechSettings.KokoroVoices,
        nameof(SpeechEngine.Qwen) => SpeechSettings.QwenVoices,
        _ => null,
    };

    private string CurrentVoiceId() => S.RavenVoiceEngine == nameof(SpeechEngine.Kokoro) ? S.RavenKokoroVoice : S.RavenQwenVoice;

    /// <summary>An alias the table knows, or a model id as said; never blank.</summary>
    private static string? SetModel(string value, Action<string> set)
    {
        var known = SettingsViewModel.KnownBrainModels;
        var model = known.FirstOrDefault(m => Normal(m) == Normal(value))
            ?? known.Where(m => Normal(m).Contains(Normal(value))).ToList() switch { [var one] => one, _ => null }
            ?? (value.Trim() is { Length: > 0 } id && !id.Contains(' ') ? id
                : throw new YardActionException($"'{value}' is no model: say one of {string.Join(", ", known)}, or a full model id. Nothing was changed."));
        set(model);
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
