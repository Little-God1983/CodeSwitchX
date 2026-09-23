using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class ScreenRectTests
{
    [Fact]
    public void Width_and_height_derive_from_edges()
    {
        var rect = ScreenRect.FromSize(10, 20, 300, 200);

        rect.Right.ShouldBe(310);
        rect.Bottom.ShouldBe(220);
        rect.Width.ShouldBe(300);
        rect.Height.ShouldBe(200);
        rect.ShouldBe(new ScreenRect(10, 20, 310, 220));
    }
}
