using System.Runtime.CompilerServices;
using CodeSwitchX.Conductor;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>A brain that answers each question with <see cref="Answer"/>, at once, or once <see cref="Gate"/> opens.</summary>
internal sealed class FakeBrain : IConductorBrain
{
    public List<string> Asked { get; } = [];

    public int WarmUps => Volatile.Read(ref _warmUps);

    /// <summary>What it says to a question; nothing by default.</summary>
    public Func<string, IEnumerable<BrainEvent>> Answer { get; set; } = _ => [];

    /// <summary>While set and not completed, every answer waits for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async IAsyncEnumerable<BrainEvent> AskAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        Asked.Add(text);
        if (BeforeSent is { } before)
        {
            await before.Task.WaitAsync(ct); // cancelled here, the question never went in, as with the real one
        }

        if (FailsBeforeSent)
        {
            yield return new BrainFailed("Claude Code is not installed, so Raven cannot answer. Install it (claude.ai/code) and ask again.");
            yield break;
        }

        Sent.Add(text);
        yield return new BrainQuestionSent();
        if (Gate is { } gate)
        {
            await (IgnoresCancel ? gate.Task : gate.Task.WaitAsync(ct)); // a cancelled turn ends at once, as the real one is interrupted
        }

        var first = true;
        foreach (var e in Answer(text))
        {
            ct.ThrowIfCancellationRequested();
            if (!first && Pause is { } pause)
            {
                await pause.Task.WaitAsync(ct);
            }

            first = false;
            yield return e;
        }
    }

    /// <summary>Every turn fails before its question goes in (no Claude Code, a process that will not start).</summary>
    public bool FailsBeforeSent { get; set; }

    /// <summary>The questions that went in (<see cref="BrainQuestionSent"/>), in order.</summary>
    public List<string> Sent { get; } = [];

    /// <summary>While set and not completed, a question waits for it before it goes in (a cold start, a busy turn lock).</summary>
    public TaskCompletionSource? BeforeSent { get; set; }

    /// <summary>The gate holds a cancelled turn too: one that takes a while to end.</summary>
    public bool IgnoresCancel { get; set; }

    /// <summary>While set and not completed, an answer stops after its first event and waits for it.</summary>
    public TaskCompletionSource? Pause { get; set; }

    public void WarmUp() => Interlocked.Increment(ref _warmUps);

    private int _warmUps;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
