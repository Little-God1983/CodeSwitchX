using System.Runtime.CompilerServices;
using CodeSwitchX.Conductor;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>A brain that answers each question with <see cref="Answer"/>, at once, or once <see cref="Gate"/> opens.</summary>
internal sealed class FakeBrain : IConductorBrain
{
    public List<string> Asked { get; } = [];

    public int WarmUps { get; private set; }

    /// <summary>What it says to a question; nothing by default.</summary>
    public Func<string, IEnumerable<BrainEvent>> Answer { get; set; } = _ => [];

    /// <summary>While set and not completed, every answer waits for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async IAsyncEnumerable<BrainEvent> AskAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        Asked.Add(text);
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        foreach (var e in Answer(text))
        {
            yield return e;
        }
    }

    public void WarmUp() => WarmUps++;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
