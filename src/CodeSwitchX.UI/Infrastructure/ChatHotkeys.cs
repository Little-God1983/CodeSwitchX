using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Yard;
using CodeSwitchX.Hosting.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// The global hotkeys that switch Raven's chat (#121), set in Settings → Shortcuts: chat 0 (the Yard's), chats 1–11, the
/// previous and next chat, and the chat of the next question waiting (#230), which is read out. Like push to talk they work while VS Code has the focus and do not bring the shell up.
/// <see cref="HotkeyService"/> registers them, again after every change, and marks a binding Windows refuses as taken.
/// Ctrl+Alt+digit is not offered: AltGr counts as Ctrl+Alt (<see cref="HotkeyChord.WhyNot"/>).
/// </summary>
public sealed partial class ChatHotkeys : ObservableObject
{
    public const string SettingKey = "raven.chatHotkeys";

    /// <summary>The highest chat number a hotkey is offered for: more windows than this are not expected.</summary>
    public const int HighestChat = 11;

    private readonly ISettingsStore? _store;
    private readonly ILogger _logger;
    private bool _loading;

    public ChatHotkeys(ISettingsStore? store = null, ILogger<ChatHotkeys>? logger = null)
    {
        _store = store;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        var rows = new List<ChatHotkeyRow> { new("chat0", "Chat 0, the Yard", "Ctrl+Alt+F12", new ChatSwitch(0, false, false), 0, this) };
        for (var n = 1; n <= HighestChat; n++)
        {
            rows.Add(new($"chat{n}", $"Chat {n}", $"Ctrl+Alt+F{n}", new ChatSwitch(n, false, false), 0, this));
        }

        rows.Add(new("previous", "Previous chat", "Ctrl+Alt+PageUp", null, -1, this));
        rows.Add(new("next", "Next chat", "Ctrl+Alt+PageDown", null, 1, this));
        rows.Add(new("nextQuestion", "Next question", "Ctrl+Alt+Insert", null, 0, this)); // #230; not End: Remote Desktop takes Ctrl+Alt+End
        Rows = rows;
        Check();
    }

    public IReadOnlyList<ChatHotkeyRow> Rows { get; }

    /// <summary>The bindings changed (a chord set, or loaded), or <see cref="Capturing"/> did: register them again.</summary>
    public event Action? Changed;

    /// <summary>
    /// A chord box in Settings has the keyboard: the chat hotkeys are let go meanwhile, or pressing the chord to set would
    /// switch the chat instead of reaching the box.
    /// </summary>
    [ObservableProperty]
    private bool _capturing;

    partial void OnCapturingChanged(bool value) => Changed?.Invoke();

    /// <summary>The stored chords; rows the store does not name keep their default. Never throws.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var stored = await _store.GetAsync<Dictionary<string, string>>(SettingKey, ct);
            _loading = true;
            foreach (var row in Rows)
            {
                if (stored?.TryGetValue(row.Id, out var chord) == true)
                {
                    row.Chord = chord;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The chat hotkeys could not be read; the defaults are used");
        }
        finally
        {
            _loading = false;
        }

        Check();
        Changed?.Invoke();
    }

    /// <summary>The rows to register, each with its chord: set, valid and not clashing; the problem of every other row is shown.</summary>
    public IReadOnlyList<(ChatHotkeyRow Row, HotkeyChord Chord)> Active => Rows
        .Where(r => r.Problem is null && HotkeyChord.TryParse(r.Chord, out _))
        .Select(r => { HotkeyChord.TryParse(r.Chord, out var chord); return (r, chord); })
        .ToList();

    internal void OnChordChanged()
    {
        if (_loading)
        {
            return;
        }

        Check();
        _saving = SaveAsync(_saving);
        Changed?.Invoke();
    }

    /// <summary>The last save, never faulting: the next waits for it, so the chords stored last are the ones set last.</summary>
    private Task _saving = Task.CompletedTask;

    private const string TakenText = "Taken by another app: pick another chord.";

    /// <summary>
    /// Gives every row its problem, or none: not a chord, one that breaks typing, set twice, one CodeSwitchX uses itself,
    /// or one Windows refused at the last registration (<see cref="MarkTaken"/>), which stays until the next one.
    /// </summary>
    public void Check()
    {
        var fixedChords = HotkeyService.Bindings.ToDictionary(b => (b.Modifiers & ~HotkeyModifiers.NoRepeat, b.VirtualKey), b => b.Label);
        var seen = new Dictionary<HotkeyChord, ChatHotkeyRow>();
        foreach (var row in Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Chord))
            {
                row.Problem = null; // off
                continue;
            }

            if (!HotkeyChord.TryParse(row.Chord, out var chord))
            {
                row.Problem = "Not a key chord: click the box and press the keys.";
            }
            else if (fixedChords.TryGetValue((chord.Modifiers, chord.VirtualKey), out var label))
            {
                row.Problem = $"CodeSwitchX uses {chord.Text} already ({label}).";
            }
            else if (chord.WhyNot is { } why)
            {
                row.Problem = why;
            }
            else if (seen.TryGetValue(chord, out var first))
            {
                row.Problem = $"{first.Label} has {chord.Text} already.";
            }
            else
            {
                seen[chord] = row;
                row.Problem = row.Taken ? TakenText : null;
            }
        }
    }

    /// <summary>Windows refused the binding: another app holds it.</summary>
    public void MarkTaken(ChatHotkeyRow row)
    {
        row.Taken = true;
        row.Problem = TakenText;
    }

    /// <summary>Before a registration: every "taken" is forgotten, so each is tried again (the other app may have let go).</summary>
    public void ForgetTaken()
    {
        foreach (var row in Rows)
        {
            row.Taken = false;
        }

        Check();
    }

    private async Task SaveAsync(Task previous)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            await previous;
            await _store.SetAsync(SettingKey, Rows.ToDictionary(r => r.Id, r => r.Chord));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The chat hotkeys could not be saved");
        }
    }
}

/// <summary>One chat hotkey: its chord ("Ctrl+Alt+F3", empty for none) and why it does not work, if it does not.</summary>
/// <param name="Target">The chat it switches to; null for a step.</param>
/// <param name="Step">-1 or 1: the previous or next chat; 0 for a chat of its own.</param>
public sealed partial class ChatHotkeyRow(string id, string label, string defaultChord, ChatSwitch? target, int step, ChatHotkeys owner)
    : ObservableObject
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string Default { get; } = defaultChord;

    public ChatSwitch? Target { get; } = target;

    public int Step { get; } = step;

    [ObservableProperty]
    private string _chord = defaultChord;

    /// <summary>Why the chord does not work (taken by another app, set twice, …); null when it does or none is set.</summary>
    [ObservableProperty]
    private string? _problem;

    /// <summary>Windows refused the chord at the last registration; forgotten when the chord changes, and before the next one.</summary>
    internal bool Taken { get; set; }

    partial void OnChordChanged(string value)
    {
        Taken = false;
        owner.OnChordChanged();
    }

    [RelayCommand]
    private void Reset() => Chord = Default;

    [RelayCommand]
    private void Clear() => Chord = "";
}
