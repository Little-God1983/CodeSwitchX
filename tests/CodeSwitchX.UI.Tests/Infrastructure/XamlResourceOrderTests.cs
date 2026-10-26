using System.Text.RegularExpressions;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Infrastructure;

/// <summary>
/// #321: a <c>StaticResource</c> finds only what is defined above it. One that a view defines itself further down is left
/// unset, without an error as the view loads: the Push to talk / Open mic switch's focus ring was, and WPF threw as the
/// switch got keyboard focus.
/// </summary>
public sealed partial class XamlResourceOrderTests
{
    [Fact]
    public void Every_resource_a_view_defines_itself_is_defined_above_its_first_use()
    {
        var ui = SourceOfUi();
        var views = Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(ui, f).Split(Path.DirectorySeparatorChar) is not ["bin" or "obj", ..])
            .ToList();
        views.ShouldNotBeEmpty();

        var late = views.SelectMany(view => Late(File.ReadAllText(view)).Select(found => $"{Path.GetFileName(view)}: {found}")).ToList();

        late.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("<Style x:Key=\"Pill\"><Setter Value=\"{StaticResource Ring}\" /></Style>\n<Style x:Key=\"Ring\" />", "Ring used on line 1, defined on line 2")]
    [InlineData("<Style x:Key=\"Pill\"><Setter Value=\"{StaticResource ResourceKey=Ring}\" /></Style>\n<Style x:Key=\"Ring\" />",
        "Ring used on line 1, defined on line 2")]
    [InlineData("<Setter.Value><StaticResource ResourceKey=\"Ring\" /></Setter.Value>\n<Style x:Key=\"Ring\" />", "Ring used on line 1, defined on line 2")]
    [InlineData("<!-- once: x:Key=\"Ring\" -->\n<Setter Value=\"{StaticResource Ring}\" />\n<Style x:Key=\"Ring\" />", "Ring used on line 2, defined on line 3")]
    [InlineData("<!-- the pills take {StaticResource Ring} -->\n<Style x:Key=\"Ring\" />\n<Setter Value=\"{StaticResource Ring}\" />", null)]
    [InlineData("<Style x:Key=\"Ring\" />\n<Setter Value=\"{StaticResource Ring}\" />\n<Setter Value=\"{StaticResource FromTheApp}\" />", null)]
    public void A_late_resource_is_found_in_every_way_it_is_written_and_not_in_a_comment(string xaml, string? late)
    {
        var found = Late(xaml);

        if (late is null)
        {
            found.ShouldBeEmpty();
        }
        else
        {
            found.ShouldBe([late]);
        }
    }

    /// <summary>Each resource the XAML defines itself and uses above its definition; comments are no XAML.</summary>
    internal static List<string> Late(string xaml)
    {
        // Blanked, not cut: the lines stay where they are.
        var text = Comment().Replace(xaml, c => NotNewLine().Replace(c.Value, " "));
        var defined = Key().Matches(text).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Min(m => m.Index));
        return [.. Use().Matches(text)
            .Select(use => (Key: use.Groups["key"].Value, use.Index))
            .Where(use => defined.TryGetValue(use.Key, out var at) && at > use.Index)
            .Select(use => $"{use.Key} used on line {Line(text, use.Index)}, defined on line {Line(text, defined[use.Key])}")];
    }

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>The UI project's sources, found from where the tests run (tests/…/bin/…).</summary>
    private static string SourceOfUi()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var ui = Path.Combine(folder.FullName, "src", "CodeSwitchX.UI");
            if (Directory.Exists(ui))
            {
                return ui;
            }
        }

        throw new DirectoryNotFoundException("No src/CodeSwitchX.UI above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex Key();

    /// <summary>The short form, <c>{StaticResource Ring}</c>; the long one, <c>{StaticResource ResourceKey=Ring}</c>; the element, <c>&lt;StaticResource ResourceKey="Ring" /&gt;</c>.</summary>
    [GeneratedRegex("\\{StaticResource\\s+(?:ResourceKey\\s*=\\s*)?(?<key>[^}\\s,]+)[^}]*\\}|<StaticResource\\s+ResourceKey=\"(?<key>[^\"]+)\"")]
    private static partial Regex Use();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("[^\\n]")]
    private static partial Regex NotNewLine();
}
