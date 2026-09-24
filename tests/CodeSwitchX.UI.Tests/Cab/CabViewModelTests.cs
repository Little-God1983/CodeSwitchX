using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Cab;

public class CabViewModelTests
{
    [Fact]
    public void SetActive_lists_every_other_tile_as_a_pip()
    {
        var cab = new CabViewModel();
        var yard = ShellTestHarness.CreateYardWithoutInit();
        var a = new WorkspaceTileViewModel(new Workspace { Name = "A", RootPath = @"c:\a" }, yard);
        var b = new WorkspaceTileViewModel(new Workspace { Name = "B", RootPath = @"c:\b" }, yard);
        var c = new WorkspaceTileViewModel(new Workspace { Name = "C", RootPath = @"c:\c" }, yard);

        cab.SetActive(b, [a, b, c]);

        cab.ActiveTile.ShouldBe(b);
        cab.Pips.ShouldBe([a, c]);
    }

    [Fact]
    public void Back_and_pip_selection_raise_events()
    {
        var cab = new CabViewModel();
        var yard = ShellTestHarness.CreateYardWithoutInit();
        var a = new WorkspaceTileViewModel(new Workspace { Name = "A", RootPath = @"c:\a" }, yard);
        var back = false;
        Guid? switched = null;
        cab.BackRequested += () => back = true;
        cab.SwitchRequested += id => switched = id;

        cab.BackCommand.Execute(null);
        cab.SelectPipCommand.Execute(a);

        back.ShouldBeTrue();
        switched.ShouldBe(a.Id);
    }
}
