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
        var views = Directory.EnumerateFiles(SourceOfUi(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        views.ShouldNotBeEmpty();

        var late = new List<string>();
        foreach (var view in views)
        {
            var xaml = File.ReadAllText(view);
            var defined = Key().Matches(xaml).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Min(m => m.Index));
            foreach (Match use in Use().Matches(xaml))
            {
                if (defined.TryGetValue(use.Groups[1].Value, out var at) && at > use.Index)
                {
                    late.Add($"{Path.GetFileName(view)}: {use.Groups[1].Value} used on line {Line(xaml, use.Index)}, defined on line {Line(xaml, at)}");
                }
            }
        }

        late.ShouldBeEmpty();
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

    [GeneratedRegex("""\{StaticResource\s+([^}\s]+)\s*\}""")]
    private static partial Regex Use();
}
