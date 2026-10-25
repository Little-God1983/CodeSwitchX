namespace CodeSwitchX.UI.Raven;

/// <summary>
/// When Raven may speak on its own (#125): the chat the user is in speaks, other chats are only marked in the list and make
/// their sound through the queue (<see cref="SoundQueue"/>, #306). What Raven says on its own waits for a free floor and
/// the pause after it last spoke or a sound played (#152); the catch-up of a chat the user switches to too.
/// </summary>
public sealed class TrafficWatcher(TimeProvider time)
{
    /// <summary>How long Raven and the user must both have been quiet before news is told: it must not step on the user's next sentence.</summary>
    public static readonly TimeSpan NewsGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>The pause between messages before Settings changes it (#152).</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(3);

    /// <summary>The pauses Settings offers, in seconds.</summary>
    public static readonly IReadOnlyList<int> PauseChoices = [2, 3, 5, 10, 15, 20, 30];

    /// <summary>When Raven last spoke or a sound played, as a timestamp: the system clock set back does not stretch the pause.</summary>
    private long? _lastSound;

    /// <summary>
    /// How long after Raven last spoke or a chat's sound played whatever it says on its own waits (#152): news, a catch-up, a
    /// card read out. The sounds themselves never wait for it (#306), nor do Raven's answers to the user: the user waits for
    /// them.
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

    /// <summary>
    /// Nobody talks: no recording, no clip or question unanswered, nothing being said or sent to be said, and no allow waiting for the user's
    /// yes. The user's next words answer the read-back, so nothing else is said or written to them in between: a yes or
    /// an okay to another chat's question or news would allow the prompt.
    /// </summary>
    public static bool IsFree(Floor floor) => floor is
    {
        Capturing: false, Holding: false, Pending: 0, Asking: 0, Telling: false, Speaking: false, OpenSpeech: false, AwaitingYes: false,
        Due: false, Sounding: false,
    };

    /// <summary>Raven said something, stopped saying it, or a chat's sound began: the pause runs from now.</summary>
    public void Announced() => _lastSound = time.GetTimestamp();

    /// <summary>What the floor is made of, from the panel's state.</summary>
    /// <param name="Due">The voice has something to say not heard yet: a reply sent, before its first audio (#292).</param>
    /// <param name="Sounding">A chat's sound plays (#306).</param>
    public readonly record struct Floor(bool Capturing, bool Holding, int Pending, int Asking, bool Telling, bool Speaking, bool OpenSpeech,
        bool AwaitingYes, bool Due, bool Sounding = false);
}
