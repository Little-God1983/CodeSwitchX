using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Yard;

namespace CodeSwitchX.Ingest.Tests.Mcp;

/// <summary>A Yard of two workspaces: CodeSwitchX with a working and a waiting chat, Diffusion-Full with a DiffusionNexus folder and an idle chat.</summary>
internal sealed class FakeYard : IYardDirectory
{
    public static readonly Guid CodeSwitchXId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DiffusionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Since = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    public List<YardWorkspace> Workspaces { get; } =
    [
        new(CodeSwitchXId, "CodeSwitchX", "Tools", @"E:\Repos\CodeSwitchX", [new("CodeSwitchX", @"E:\Repos\CodeSwitchX")],
            [new(null, "main", "clean")], Number: 1),
        new(DiffusionId, "Diffusion-Full", "Apps", @"E:\Repos\DiffusionNexus.Installer.SDK",
            [new("DiffusionNexus.Installer.SDK", @"E:\Repos\DiffusionNexus.Installer.SDK"), new("DiffusionNexus", @"E:\Repos\DiffusionNexus")],
            [new("DiffusionNexus.Installer.SDK", "develop", "3 changed"), new("DiffusionNexus", "main", null)], Number: 4),
    ];

    public List<YardChat> Chats { get; } =
    [
        new("aaaaaaaa-0001", "Speech gate", CodeSwitchXId, "CodeSwitchX", SessionState.Working, false, Since, "2m", "claude-opus-5-5", "Edit", 0.42,
            null, @"E:\Repos\CodeSwitchX"),
        new("bbbbbbbb-0002", "Raven brain", CodeSwitchXId, "CodeSwitchX", SessionState.Waiting, true, Since, "5m", "claude-sonnet-5-5", "AskUserQuestion",
            0.1, "Claude needs your permission to use Bash", @"E:\Repos\CodeSwitchX"),
        new("cccccccc-0003", "Installer icons", DiffusionId, "Diffusion-Full", SessionState.Idle, false, Since, "1h 02m", "claude-sonnet-5-5", "Read",
            0.9, null, @"E:\Repos\DiffusionNexus"),
    ];

    public Task<IReadOnlyList<YardWorkspace>> WorkspacesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<YardWorkspace>>(Workspaces);

    public Task<IReadOnlyList<YardChat>> ChatsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<YardChat>>(Chats);
}
