using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Shouldly;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>#335: GitLineTemplate is shared with Raven's chat header, which never shows the issue and pull request pills.</summary>
public sealed class GitPillsXamlTests
{
    private static GitHubPill Pill(GitHubItemKind kind) =>
        new(new RepoActivity(new GitHubRepo("acme", "SomeRepo"), "me", 3, 3, [], []), kind);

    [Fact]
    public Task The_chat_header_never_shows_pills() => StaThread.RunAsync(() =>
    {
        var line = new GitLine(null, "main", 0, Issues: Pill(GitHubItemKind.Issue), PullRequests: Pill(GitHubItemKind.PullRequest));

        var header = PillButtons(Render(line, tile: false));
        header.Count.ShouldBe(2); // they are in the template, only collapsed
        header.ShouldAllBe(b => !IsShown(b));
        PillButtons(Render(line, tile: true)).Count(IsShown).ShouldBe(2);
    });

    [Fact]
    public Task A_line_without_pills_shows_none_even_on_a_tile() => StaThread.RunAsync(() =>
        PillButtons(Render(new GitLine(null, "main", 0), tile: true)).ShouldAllBe(b => !IsShown(b)));

    [Fact]
    public Task A_line_with_one_kind_shows_that_pill_only() => StaThread.RunAsync(() =>
        PillButtons(Render(new GitLine(null, "main", 0, PullRequests: Pill(GitHubItemKind.PullRequest)), tile: true)).Count(IsShown).ShouldBe(1));

    [Fact]
    public Task A_narrow_line_keeps_its_pills_whole_and_the_names_give_way() => StaThread.RunAsync(() =>
    {
        var line = new GitLine("AVeryLongFolderNameForARepository", "feature/a-very-long-branch-name", 3,
            Issues: Pill(GitHubItemKind.Issue), PullRequests: Pill(GitHubItemKind.PullRequest));
        var presenter = Render(line, tile: true, width: 260);

        var right = presenter.ActualWidth;
        var pills = PillButtons(presenter);
        pills.Count.ShouldBe(2);
        foreach (var pill in pills)
        {
            pill.TranslatePoint(new Point(pill.ActualWidth, 0), presenter).X.ShouldBeLessThanOrEqualTo(right + 0.5);
        }

        var state = Descendants<TextBlock>(presenter).Single(t => t.Text == "3 changed");
        state.TranslatePoint(new Point(state.ActualWidth, 0), presenter).X.ShouldBeLessThanOrEqualTo(right + 0.5);
    });

    [Fact]
    public Task No_tooltip_sits_above_a_pill_but_the_names_and_the_state_pill_keep_theirs() => StaThread.RunAsync(() =>
    {
        var line = new GitLine("Folder", "main", 3, "/work/Folder", Pill(GitHubItemKind.Issue), Pill(GitHubItemKind.PullRequest));
        var presenter = Render(line, tile: true);

        var pills = PillButtons(presenter);
        pills.Count.ShouldBe(2);
        foreach (var pill in pills)
        {
            for (DependencyObject? node = pill; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                (node as FrameworkElement)?.ToolTip.ShouldBeNull();
            }
        }

        Descendants<DockPanel>(presenter).Single().ToolTip.ShouldNotBeNull();
        Descendants<Border>(presenter).Single(b => Descendants<TextBlock>(b).Any(t => t.Text == "3 changed")).ToolTip.ShouldNotBeNull();
    });

    [Theory]
    [InlineData(1, GitHubItemKind.Issue, "1 open issue in SomeRepo")]
    [InlineData(2, GitHubItemKind.Issue, "2 open issues in SomeRepo")]
    [InlineData(1, GitHubItemKind.PullRequest, "1 open pull request in SomeRepo")]
    [InlineData(2, GitHubItemKind.PullRequest, "2 open pull requests in SomeRepo")]
    public void The_pill_name_is_singular_at_one(int count, GitHubItemKind kind, string expected) =>
        new GitHubPill(new RepoActivity(new GitHubRepo("acme", "SomeRepo"), "me", count, count, [], []), kind).AutomationName.ShouldBe(expected);

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Descendants<T>(child));
        }

        return found;
    }

    /// <summary>App.xaml's resources are not reachable without the application, so they are parsed from the file, as a dictionary (the namespaces name the assembly, as the XAML compiler would).</summary>
    private static ResourceDictionary AppResources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "CodeSwitchX.UI", "App.xaml")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var app = XDocument.Load(Path.Combine(dir.FullName, "src", "CodeSwitchX.UI", "App.xaml")).Root!;
        var xaml = new XElement(wpf + "ResourceDictionary", app.Attributes().Where(a => a.IsNamespaceDeclaration), app.Element(wpf + "Application.Resources")!.Nodes());
        return (ResourceDictionary)XamlReader.Parse(xaml.ToString().Replace("\"clr-namespace:CodeSwitchX.UI.Infrastructure\"", "\"clr-namespace:CodeSwitchX.UI.Infrastructure;assembly=CodeSwitchX\"")
            .Replace("\"clr-namespace:CodeSwitchX.UI.Yard\"", "\"clr-namespace:CodeSwitchX.UI.Yard;assembly=CodeSwitchX\""));
    }

    private static ContentPresenter Render(GitLine line, bool tile, double width = 400)
    {
        var resources = AppResources();
        // a bare presenter: an ItemsControl has no template without the application's theme
        var items = new ContentPresenter { Content = line, ContentTemplate = (DataTemplate)resources["GitLineTemplate"], Width = width };
        items.Resources.MergedDictionaries.Add(resources);
        if (tile)
        {
            GitPills.SetIsShown(items, true);
        }

        items.Measure(new Size(width, 200));
        items.Arrange(new Rect(0, 0, width, 200));
        items.UpdateLayout();
        return items;
    }

    private static List<Button> PillButtons(DependencyObject root)
    {
        var found = new List<Button>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                found.Add(button);
            }

            found.AddRange(PillButtons(child));
        }

        return found;
    }

    /// <summary>Visible as far as the template decides: no window holds the control, so IsVisible is no help.</summary>
    private static bool IsShown(Button button)
    {
        for (DependencyObject? node = button; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }
}
