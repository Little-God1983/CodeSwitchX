using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests;

/// <summary>Runs posted work synchronously; tests never need a WPF dispatcher.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
