namespace CodeSwitchX.UI.Raven;

/// <summary>
/// Who may make a sound (#125): the chat the user is in speaks, other chats get a short sound at most, and only when it is
/// quiet. A sound or announcement starts the cooldown, and what other chats bring inside it is only marked in the list:
/// nothing is saved up to be said later. The catch-up of a chat the user switches to is theirs, asked for by the switch:
/// the cooldown does not hold it (#143), the pause does (#152).
/// </summary>
public sealed class TrafficWatcher(TimeProvider time)
{
    /// <summary>How long Raven and the user must both have been quiet before news is told: it must not step on the user's next sentence.</summary>
    public static readonly TimeSpan NewsGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>The cooldown before Settings changes it.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(10);

    /// <summary>The cooldowns Settings offers, in seconds.</summary>
    public static readonly IReadOnlyList<int> CooldownChoices = [5, 10, 15, 20, 30, 60];

    /// <summary>The pause between messages before Settings changes it (#152).</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(3);

    /// <summary>The pauses Settings offers, in seconds.</summary>
    public static readonly IReadOnlyList<int> PauseChoices = [2, 3, 5, 10, 15, 20, 30];

    /// <summary>When the last sound or announcement was, as a timestamp: the system clock set back does not stretch the cooldown.</summary>
    private long? _lastSound;

    /// <summary>How long after an announcement or sound another chat stays silent.</summary>
    public TimeSpan Cooldown { get; set; } = DefaultCooldown;

    /// <summary>
    /// How long after Raven last spoke or made a sound whatever it says on its own waits (#152): news, a catch-up, a card read
    /// out. A chat's sound inside it is left out, as inside the cooldown. Its answers to the user never wait: the user waits
    /// for them.
    /// </summary>
    public TimeSpan Pause
    {
        get => _pause;
        set
        {
            if (_pause == value)
            {
                return; // set again with the other traffic settings: what waits keeps its wait
            }

            _pause = value;
            PauseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private TimeSpan _pause = DefaultPause;

    /// <summary>The pause was set: what waits for it is told by the new one.</summary>
    public event EventHandler? PauseChanged;

    /// <summary>How much of the pause is left; zero once it has passed, or when Raven has said nothing yet.</summary>
    public TimeSpan PauseLeft => _lastSound is { } last && Pause - time.GetElapsedTime(last) is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    /// <summary>How long what Raven says on its own waits once the floor is free: the grace, or what is left of the pause if longer.</summary>
    public TimeSpan WaitBeforeTelling => PauseLeft is var left && left > NewsGrace ? left : NewsGrace;

    /// <summary>Other chats get their short sound; off, they are only marked in the list.</summary>
    public bool SoundOn { get; set; } = true;

    /// <summary>The selected chat's own news waits for the cooldown too; its cards and Raven's answers never do.</summary>
    public bool OwnNewsWaits { get; set; }

    /// <summary>
    /// Nobody talks: no recording, no clip or question unanswered, nothing being said, and no allow waiting for the user's
    /// yes. The user's next words answer the read-back, so nothing else is said or written to them in between: a yes or
    /// an okay to another chat's question or news would allow the prompt.
    /// </summary>
    public static bool IsFree(Floor floor) => floor is
    {
        Capturing: false, Holding: false, Pending: 0, Asking: 0, Telling: false, Speaking: false, OpenSpeech: false, AwaitingYes: false,
    };

    /// <summary>Raven said something, or stopped saying it: the cooldown runs from now.</summary>
    public void Announced() => _lastSound = time.GetTimestamp();

    /// <summary>No announcement or sound within the cooldown.</summary>
    public bool CooledDown => _lastSound is not { } last || time.GetElapsedTime(last) >= Cooldown;

    /// <summary>
    /// Whether another chat's news or card makes its sound now: the sound is on, the floor free, and the cooldown and the
    /// pause over. The sound made starts them again.
    /// </summary>
    public bool TrySound(bool floorFree)
    {
        if (!SoundOn || !floorFree || !CooledDown || PauseLeft > TimeSpan.Zero)
        {
            return false;
        }

        Announced();
        return true;
    }

    /// <summary>Whether the selected chat's own news is spoken, rather than only shown.</summary>
    public bool MaySpeakOwnNews => !OwnNewsWaits || CooledDown;

    /// <summary>What the floor is made of, from the panel's state.</summary>
    public readonly record struct Floor(bool Capturing, bool Holding, int Pending, int Asking, bool Telling, bool Speaking, bool OpenSpeech,
        bool AwaitingYes);
}
