namespace CodeSwitchX.Conductor;

/// <summary>Each Raven chat's own brain: what is said in a chat is only in its conversation.</summary>
public interface IChatBrains
{
    /// <summary>The brain of a window's chat; of chat 0, the Yard, for null.</summary>
    IConductorBrain For(Guid? workspaceId);
}

/// <summary>
/// The chats' brains, made as a chat is first asked, with few processes: the <see cref="Warm"/> brains used last keep
/// theirs, and an older one is rested (its process stopped once its turn is over). A rested brain starts again on its next
/// question and picks its conversation up where it was (<see cref="ClaudeCliBrain"/> resumes it). Thread-safe.
/// </summary>
public sealed class ChatBrains(Func<Guid?, IConductorBrain> create, int warm = ChatBrains.Warm) : IChatBrains, IDisposable, IAsyncDisposable
{
    /// <summary>How many brains keep their process: the chat in use and the one or two used before it.</summary>
    public const int Warm = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PooledBrain> _brains = [];

    /// <summary>Most recently used first.</summary>
    private readonly LinkedList<PooledBrain> _used = [];

    private bool _disposed;

    public IConductorBrain For(Guid? workspaceId)
    {
        var key = workspaceId ?? Guid.Empty;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_brains.TryGetValue(key, out var brain))
            {
                brain = new PooledBrain(this, create(workspaceId));
                _brains[key] = brain;
            }

            return brain;
        }
    }

    /// <summary>The brain is about to be used: it goes first, and the one that falls behind the warm ones is rested.</summary>
    private void Use(PooledBrain brain)
    {
        List<PooledBrain> rest = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (brain.Node.List is not null)
            {
                _used.Remove(brain.Node);
            }

            _used.AddFirst(brain.Node);
            brain.Resting = false;
            for (var node = _used.First; node is not null; node = node.Next)
            {
                if (node.Value != brain && !node.Value.Resting && Position(node) >= warm)
                {
                    node.Value.Resting = true;
                    rest.Add(node.Value);
                }
            }
        }

        foreach (var cold in rest)
        {
            cold.Inner.Rest(); // returns at once; the process goes once its turn is over
        }
    }

    private int Position(LinkedListNode<PooledBrain> node)
    {
        var position = 0;
        for (var at = _used.First; at != node; at = at!.Next)
        {
            position++;
        }

        return position;
    }

    public void Dispose()
    {
        List<PooledBrain> all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = [.. _brains.Values];
        }

        foreach (var brain in all)
        {
            if (brain.Inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                brain.Inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A chat's brain as the pool hands it out: each use marks it used; the pool disposes the brain, not its users.</summary>
    private sealed class PooledBrain : IConductorBrain
    {
        private readonly ChatBrains _pool;

        public PooledBrain(ChatBrains pool, IConductorBrain inner)
        {
            _pool = pool;
            Inner = inner;
            Node = new LinkedListNode<PooledBrain>(this);
        }

        public IConductorBrain Inner { get; }

        public LinkedListNode<PooledBrain> Node { get; }

        /// <summary>Rested since it was last used: not rested again.</summary>
        public bool Resting { get; set; }

        public IAsyncEnumerable<BrainEvent> AskAsync(string text, CancellationToken ct)
        {
            _pool.Use(this);
            return Inner.AskAsync(text, ct);
        }

        public void WarmUp()
        {
            _pool.Use(this);
            Inner.WarmUp();
        }

        public void Rest() => Inner.Rest();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
