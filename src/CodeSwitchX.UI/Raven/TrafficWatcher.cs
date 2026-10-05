namespace CodeSwitchX.UI.Raven;

/// <summary>
/// Who may make a sound (#125): the chat the user is in speaks, other chats get a short sound at most, and only when it is
/// quiet. A sound or announcement starts the cooldown, and whatever comes inside it is only marked in the list: nothing
/// is saved up to be said later.
/// </summary>
public sealed class TrafficWatcher(TimeProvider time)
{
    /// <summary>How long Raven and the user must both have been quiet before news is told: it must not step on the user's next sentence.</summary>
    public static readonly TimeSpan NewsGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>The cooldown before Settings changes it.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(10);

    /// <summary>The cooldowns Settings offers, in seconds.</summary>
    public static readonly IReadOnlyList<int> CooldownChoices = [5, 10, 15, 20, 30, 60];

    /// <summary>When the last sound or announcement was, as a timestamp: the system clock set back does not stretch the cooldown.</summary>
    private long? _lastSound;

    /// <summary>How long after an announcement or sound another chat stays silent.</summary>
    public TimeSpan Cooldown { get; set; } = DefaultCooldown;

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
    /// Whether another chat's news or card makes its sound now: the sound is on, the floor free and the cooldown over. The
    /// sound made starts the cooldown again.
    /// </summary>
    public bool TrySound(bool floorFree)
    {
        if (!SoundOn || !floorFree || !CooledDown)
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
