namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Marshals bus callbacks onto the UI thread; tests substitute a synchronous implementation.</summary>
public interface IUiDispatcher
{
    void Post(Action action);

    /// <summary>Posts <paramref name="action"/> with its <paramref name="state"/>: for work posted many times a second, a
    /// cached static delegate and the state as a struct, so the caller allocates no closure per post.</summary>
    void Post<T>(Action<T> action, T state);
}
