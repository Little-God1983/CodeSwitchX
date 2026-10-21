using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>A tile's rows (#295): the latest turn on top, as many as the user allows, the rest a "+N more" line.</summary>
public partial class YardViewModelTests
{
    private void Tool(string id) =>
        _engine.Apply(Hook(id, "PreToolUse", SessionSignal.ToolUse, @"c:\repo\app") with { ToolName = "Bash" });

    private static IReadOnlyList<string> Rows(WorkspaceTileViewModel tile) => tile.Chats.Select(c => c.SessionId).ToList();

    [Fact]
    public async Task The_chat_whose_turn_started_last_is_on_top()
    {
        var app = await AppTile();
        Prompt("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        Prompt("b");

        Rows(app).ShouldBe(["b", "a"]);

        Stop("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        Prompt("a");

        Rows(app).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_chat_whose_turn_ended_last_is_on_top()
    {
        var app = await AppTile();
        Prompt("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        Prompt("b");
        Stop("b");
        _time.Advance(TimeSpan.FromMinutes(1));
        Tool("a");
        Stop("a");

        Rows(app).ShouldBe(["a", "b"], "a answered after b did");
    }

    [Fact]
    public async Task A_tool_call_does_not_move_a_working_chat()
    {
        var app = await AppTile();
        Prompt("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        Prompt("b");
        _time.Advance(TimeSpan.FromMinutes(1));

        Tool("a");

        Rows(app).ShouldBe(["b", "a"], "two working chats keep their places");
    }

    [Fact]
    public async Task The_rows_keep_their_places_while_the_pointer_is_over_the_board()
    {
        var app = await AppTile();
        Prompt("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        Prompt("b");
        Stop("a");
        _time.Advance(TimeSpan.FromMinutes(1));

        _yard.HoldOrder();
        Prompt("a");

        Rows(app).ShouldBe(["b", "a"]);

        _yard.ReleaseOrder();

        Rows(app).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_tile_shows_at_most_the_chats_allowed_and_counts_the_rest()
    {
        var app = await AppTile();
        _yard.MaxChats = 4;
        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" })
        {
            Prompt(id);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        app.ShownChats.Select(c => c.SessionId).ShouldBe(["f", "e", "d", "c"]);
        app.MoreChats.ShouldBe(2);

        _yard.MaxChats = 10;

        app.ShownChats.Count.ShouldBe(6);
        app.MoreChats.ShouldBe(0);
    }

    [Fact]
    public async Task A_chat_leaving_the_tile_lets_a_hidden_one_show()
    {
        var app = await AppTile();
        _yard.MaxChats = 4;
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            Prompt(id);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        app.Remove("e");

        app.ShownChats.Select(c => c.SessionId).ShouldBe(["d", "c", "b", "a"]);
        app.MoreChats.ShouldBe(0);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(1, YardViewModel.MinMaxChats)]
    [InlineData(99, YardViewModel.MaxMaxChats)]
    public void The_most_chats_on_a_tile_stays_in_its_range(int set, int kept)
    {
        _yard.MaxChats = set;

        _yard.MaxChats.ShouldBe(kept);
    }

    [Theory]
    [InlineData(1.25, 1.25)]
    [InlineData(0.1, YardViewModel.MinFontScale)]
    [InlineData(9.0, YardViewModel.MaxFontScale)]
    [InlineData(double.NaN, 1)]
    [InlineData(0.9000000000000001, 0.9)]
    public void The_font_size_stays_in_the_sliders_range_and_to_two_decimals(double set, double kept)
    {
        _yard.FontScale = set;

        _yard.FontScale.ShouldBe(kept);
    }

    [Fact]
    public void The_tile_size_sizes_the_tile_and_not_its_text()
    {
        _yard.TileScale = 1.5;

        _yard.TileWidth.ShouldBe(YardViewModel.TileBaseWidth * 1.5);
        _yard.TileMinHeight.ShouldBe(YardViewModel.TileBaseMinHeight * 1.5);
        _yard.FontScale.ShouldBe(1);
    }
}
