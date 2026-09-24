namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Marshals bus callbacks onto the UI thread; tests substitute a synchronous implementation.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
