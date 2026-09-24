namespace CodeSwitchX.Core.Sessions;

public readonly record struct UsageDelta(string Model, DateTimeOffset At, TokenUsage Tokens);
