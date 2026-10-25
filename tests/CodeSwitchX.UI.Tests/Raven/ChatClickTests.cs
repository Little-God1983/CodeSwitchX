namespace CodeSwitchX.UI.Tests.Raven;

using CodeSwitchX.UI.Raven;

/// <summary>A click on a row of Raven's chat list opens its window (#311): only a press and release on that same row.</summary>
public sealed class ChatClickTests
{
    private readonly object _a = new();
    private readonly object _b = new();

    [Fact]
    public void A_press_and_release_on_one_row_is_a_click_on_it()
    {
        var click = new ChatClick();

        click.Press(_a);

        click.Release(_a).ShouldBeTrue();
    }

    // Review of #311: a drag from the list's padding that ended on a row opened that row's window.
    [Fact]
    public void A_release_on_a_row_after_a_press_beside_the_rows_is_no_click()
    {
        var click = new ChatClick();

        click.Press(null);

        click.Release(_a).ShouldBeFalse();
    }

    [Fact]
    public void A_drag_from_one_row_to_another_is_no_click()
    {
        var click = new ChatClick();

        click.Press(_a);

        click.Release(_b).ShouldBeFalse();
    }

    [Fact]
    public void A_press_counts_for_one_release_only()
    {
        var click = new ChatClick();
        click.Press(_a);
        click.Release(_a);

        click.Release(_a).ShouldBeFalse("a later release without its own press, at the end of a drag from outside the list");
    }
}
