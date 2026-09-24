namespace CodeSwitchX.Core.Sessions;

/// <summary>Normalised inputs to the state machine. Hook events, transcript facts and monitors map onto these.</summary>
public enum SessionSignal
{
    SessionStart,
    PromptSubmit,
    ToolUse,
    Notification,
    Stop,
    SessionEnd,
    ProcessGone,
    StaleTimeout,

    /// <summary>Claude's idle_prompt notification, sent about 60 s after a turn has finished while the input waits.</summary>
    IdlePrompt,
}
