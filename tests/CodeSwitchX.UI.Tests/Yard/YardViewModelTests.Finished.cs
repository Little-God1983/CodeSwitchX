using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>A tile whose chat finished a turn shimmers until the user opens it (#287).</summary>
public partial class YardViewModelTests
{
    private async Task<WorkspaceTileViewModel> AppTile()
    {
        _resolver.SetRoots(WorkspaceResolver.RootsOf([_app, _shop]));
        await _yard.InitializeAsync(CancellationToken.None);
        return _yard.FindTile(_app.Id)!;
    }

    private void Prompt(string id) =>
        _engine.Apply(Hook(id, "UserPromptSubmit", SessionSignal.PromptSubmit, @"c:\repo\app") with { Prompt = "fix the build" });

    private void Stop(string id) => _engine.Apply(Hook(id, "Stop", SessionSignal.Stop, @"c:\repo\app"));

    [Fact]
    public async Task A_tile_whose_chat_finishes_a_turn_shows_it_finished()
    {
        var app = await AppTile();
        Prompt("s1");
        app.ShowsFinished.ShouldBeFalse("it works");

        Stop("s1");

        app.IsFinished.ShouldBeTrue();
        app.ShowsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task Opening_the_workspace_clears_the_finished_mark()
    {
        var app = await AppTile();
        Prompt("s1");
        Stop("s1");

        _yard.CabShows(_app.Id);

        app.IsFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_that_finishes_while_the_cab_shows_its_workspace_is_seen()
    {
        var app = await AppTile();
        _yard.CabShows(_app.Id);
        Prompt("s1");

        Stop("s1");

        app.IsFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_that_finishes_after_the_cab_left_its_workspace_is_finished()
    {
        var app = await AppTile();
        _yard.CabShows(_app.Id);
        Prompt("s1");
        _yard.CabShows(null);

        Stop("s1");

        app.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task Showing_another_workspace_leaves_the_finished_mark()
    {
        var app = await AppTile();
        Prompt("s1");
        Stop("s1");

        _yard.CabShows(_shop.Id);

        app.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task A_new_turn_of_the_chat_ends_its_finished_mark()
    {
        var app = await AppTile();
        Prompt("s1");
        Stop("s1");

        Prompt("s1");

        app.IsFinished.ShouldBeFalse();
        app.ShowsFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task Another_chat_working_hides_the_finished_mark_until_it_finishes_too()
    {
        var app = await AppTile();
        Prompt("s1");
        Stop("s1");

        Prompt("s2");
        app.IsFinished.ShouldBeTrue("s1's turn is still unseen");
        app.ShowsFinished.ShouldBeFalse("the running light shows");

        Stop("s2");
        app.ShowsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task A_chat_that_leaves_the_tile_takes_its_finished_mark_with_it()
    {
        var app = await AppTile();
        Prompt("s1");
        Stop("s1");

        app.Remove("s1");

        app.IsFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task A_question_is_no_finished_turn()
    {
        var app = await AppTile();
        Prompt("s1");

        _engine.Apply(Hook("s1", "Notification", SessionSignal.Notification, @"c:\repo\app"));

        app.IsFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task A_question_on_the_tile_shows_instead_of_the_finished_mark_and_it_comes_back_after()
    {
        var app = await AppTile();
        Prompt("s1");
        Prompt("s2");
        _engine.Apply(Hook("s2", "Notification", SessionSignal.Notification, @"c:\repo\app"));

        Stop("s1");
        app.IsFinished.ShouldBeTrue();
        app.NeedsAttention.ShouldBeTrue();
        app.ShowsFinished.ShouldBeFalse("the question shows");

        _engine.Apply(Hook("s2", "PreToolUse", SessionSignal.ToolUse, @"c:\repo\app")); // answered: s2 works on
        app.ShowsFinished.ShouldBeFalse("the running light shows");

        Stop("s2");
        app.ShowsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stop_without_a_turn_is_no_finished_turn()
    {
        var app = await AppTile();
        _engine.Apply(Hook("s1", "SessionStart", SessionSignal.SessionStart, @"c:\repo\app"));

        Stop("s1");

        app.IsFinished.ShouldBeFalse();
    }

    [Fact]
    public async Task An_idle_chat_restored_at_startup_is_not_finished()
    {
        var app = await AppTile();

        _bus.Publish(new SessionChanged(null, Snapshot("s1", _app.Id, SessionState.Idle)));

        app.Chats.ShouldHaveSingleItem();
        app.IsFinished.ShouldBeFalse();
    }
}
