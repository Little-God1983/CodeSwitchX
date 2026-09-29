using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class MouseBackButtonHookTests
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    private const uint Back = 0x0001u << 16;
    private const uint Forward = 0x0002u << 16;

    [Fact]
    public void A_back_press_over_the_docked_window_goes_back_and_its_release_is_taken_with_it()
    {
        // VS Code gets neither half, so it does not also go back in its own editor history.
        var pending = false;

        MouseBackButtonHook.Decide(WmXButtonDown, Back, () => true, ref pending).ShouldBe(BackButtonAction.GoBack);
        MouseBackButtonHook.Decide(WmXButtonUp, Back, () => false, ref pending).ShouldBe(BackButtonAction.Swallow,
            "the release belongs to the press, wherever the cursor is by then");
        pending.ShouldBeFalse();
    }

    [Fact]
    public void A_back_press_anywhere_else_is_passed_on_and_so_is_its_release()
    {
        var pending = false;

        MouseBackButtonHook.Decide(WmXButtonDown, Back, () => false, ref pending).ShouldBe(BackButtonAction.Pass);
        MouseBackButtonHook.Decide(WmXButtonUp, Back, () => true, ref pending).ShouldBe(BackButtonAction.Pass,
            "the window that got the press gets its release");
    }

    [Fact]
    public void The_forward_button_and_other_mouse_input_are_passed_on_without_asking_where_the_cursor_is()
    {
        var pending = false;
        var asked = 0;
        bool Over() { asked++; return true; }

        MouseBackButtonHook.Decide(WmXButtonDown, Forward, Over, ref pending).ShouldBe(BackButtonAction.Pass);
        MouseBackButtonHook.Decide(WmXButtonUp, Forward, Over, ref pending).ShouldBe(BackButtonAction.Pass);
        MouseBackButtonHook.Decide(WmMouseMove, 0, Over, ref pending).ShouldBe(BackButtonAction.Pass);

        asked.ShouldBe(0, "every mouse move of the whole desktop passes through the hook, and must pass quickly");
    }
}
