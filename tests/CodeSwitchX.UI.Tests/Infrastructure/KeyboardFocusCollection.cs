namespace CodeSwitchX.UI.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KeyboardFocusCollection
{
    /// <summary>Tests that read where the keyboard is: another test's window taking the activation would take it from theirs.</summary>
    public const string Name = "Keyboard focus";
}
