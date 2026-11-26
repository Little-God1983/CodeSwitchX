using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Tests.Infrastructure;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.UI.Tests.Yard;

/// <summary>The panel's Changes tab (#444): the files behind the line's "N changed" pill.</summary>
public partial class GitHubListViewModelTests
{
    private const string Checkout = @"C:\app";

    private static ChangedFile Edited(string path, string top = Checkout) =>
        new(path, Path.Combine(top, path.TrimEnd('/').Replace('/', '\\')), ChangeKind.Edited);

    private static ChangedFileList Files(params ChangedFile[] files) => new(files);

    /// <summary>A panel opened from a line in <paramref name="path"/> with <paramref name="files"/> changed, on the Changes tab or not.</summary>
    private async Task<GitHubPanelViewModel> OpenWithChanges(ChangedFileList? files, bool showChanges = true, Action? readGit = null, string path = Checkout,
        Func<ProcessStartInfo, bool>? start = null)
    {
        var (activity, pill) = await PillAsync();
        return new GitHubPanelViewModel(pill.Activity, GitHubItemKind.Issue, new GitHubReader(_gh), activity, Browsers(), _clipboard, new ImmediateDispatcher(), _time,
            NullLogger.Instance, _closedLists.Add, new PanelCheckout(path, "main", () => false, NewJobs(), ReadGit: readGit, Files: files), showChanges, start);
    }

    [Fact]
    public async Task Opened_on_changes_shows_the_files_and_reads_no_list()
    {
        var model = await OpenWithChanges(Files(Edited("b.cs"), Edited("a.cs")));
        var asked = _gh.Asked.Count;

        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.IsChangesShown.ShouldBeTrue();
        var changes = model.Changes.ShouldNotBeNull();
        changes.IsShown.ShouldBeTrue();
        model.PullRequests.IsShown.ShouldBeFalse();
        model.Issues.IsShown.ShouldBeFalse();
        changes.Rows.Select(r => r.DisplayPath).ShouldBe(["a.cs", "b.cs"]);
        changes.CountText.ShouldBe("2");
        _gh.Asked.Count.ShouldBe(asked, "a tab that is not looked at costs no read (#406)");
        _gh.ListAsked.IsSet.ShouldBeFalse();
    }

    [Fact]
    public async Task Show_switches_between_changes_and_a_list()
    {
        var reads = 0;
        var model = await OpenWithChanges(Files(Edited("a.cs")), showChanges: false, readGit: () => reads++);
        model.IsChangesShown.ShouldBeFalse();
        model.Issues.IsShown.ShouldBeTrue();

        model.ShowChangesCommand.Execute(null);

        model.IsChangesShown.ShouldBeTrue();
        model.Changes!.IsShown.ShouldBeTrue();
        model.Issues.IsShown.ShouldBeFalse();
        model.PullRequests.IsShown.ShouldBeFalse();
        reads.ShouldBe(1, "the list is read again, not left up to 30 s old");

        model.ShowCommand.Execute(GitHubItemKind.Issue);

        model.IsChangesShown.ShouldBeFalse();
        model.Changes.IsShown.ShouldBeFalse();
        model.Issues.IsShown.ShouldBeTrue("the list in front before is shown again");
        model.PullRequests.IsShown.ShouldBeFalse();
    }

    [Fact]
    public async Task No_checkout_no_changes_tab()
    {
        var (activity, pill) = await PillAsync();

        var model = Open(activity, pill);

        model.Changes.ShouldBeNull();
        model.IsChangesShown.ShouldBeFalse();
    }

    [Fact]
    public async Task Copy_all_paths_puts_full_paths_one_to_a_line()
    {
        var model = await OpenWithChanges(Files(Edited("src/b.cs"), Edited("a.cs")));

        model.CopyAllPathsCommand.Execute(null);

        _clipboard.Text.ShouldBe(@"C:\app\a.cs" + Environment.NewLine + @"C:\app\src\b.cs");
        model.CopyMessage.ShouldBe("Copied 2 paths.");

        model.FollowChanges(Files(Edited("a.cs")));
        model.CopyAllPathsCommand.Execute(null);

        _clipboard.Text.ShouldBe(@"C:\app\a.cs");
        model.CopyMessage.ShouldBe("Copied 1 path.");
    }

    [Fact]
    public async Task Copy_folder_path_copies_the_folder()
    {
        var model = await OpenWithChanges(Files(Edited("src/b.cs"), Edited("docs/new/")));
        var rows = model.Changes!.Rows;

        model.CopyFolderPathCommand.Execute(rows.Single(r => r.File.Path == "src/b.cs"));

        _clipboard.Text.ShouldBe(@"C:\app\src");
        model.CopyMessage.ShouldBe(@"Copied C:\app\src.");

        model.CopyFolderPathCommand.Execute(rows.Single(r => r.File.IsFolder));

        _clipboard.Text.ShouldBe(@"C:\app\docs\new", "a new folder git lists whole is its own folder");
    }

    [Fact]
    public async Task Open_folder_selects_an_existing_file()
    {
        var top = TempCheckout();
        try
        {
            Directory.CreateDirectory(Path.Combine(top, "src"));
            File.WriteAllText(Path.Combine(top, "src", "a.cs"), "x");
            Directory.CreateDirectory(Path.Combine(top, "docs", "new"));
            var model = await OpenWithChanges(Files(Edited("src/a.cs", top), Edited("docs/new/", top)), path: top, start: Started);

            await model.OpenFolderCommand.ExecuteAsync(model.Changes!.Rows.Single(r => !r.File.IsFolder));
            await model.OpenFolderCommand.ExecuteAsync(model.Changes.Rows.Single(r => r.File.IsFolder));

            _started.Select(s => (s.FileName, s.Arguments)).ShouldBe(
            [
                ("explorer.exe", $"\"{Path.Combine(top, "docs", "new")}\""),
                ("explorer.exe", $"/select,\"{Path.Combine(top, "src", "a.cs")}\""),
            ], ignoreOrder: true);
            model.OpenMessage.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public async Task Open_folder_of_a_deleted_file_opens_the_nearest_folder_there_is()
    {
        var top = TempCheckout();
        try
        {
            Directory.CreateDirectory(Path.Combine(top, "sub"));
            var model = await OpenWithChanges(Files(Edited("sub/gone/x.cs", top)), path: top, start: Started);

            await model.OpenFolderCommand.ExecuteAsync(model.Changes!.Rows[0]);

            var start = _started.ShouldHaveSingleItem();
            start.FileName.ShouldBe("explorer.exe");
            start.Arguments.ShouldBe($"\"{Path.Combine(top, "sub")}\"");
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public async Task Open_folder_says_so_when_nothing_opens()
    {
        var top = TempCheckout();
        try
        {
            var model = await OpenWithChanges(Files(Edited("a.cs", top)), path: top, start: _ => false);

            await model.OpenFolderCommand.ExecuteAsync(model.Changes!.Rows[0]);

            model.OpenMessage.ShouldBe($"Explorer did not open {top}.");
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }

        var gone = await OpenWithChanges(Files(Edited("x/y.cs", @"Q:\csx-no-such-drive\app")), path: @"Q:\csx-no-such-drive\app", start: Started);

        await gone.OpenFolderCommand.ExecuteAsync(gone.Changes!.Rows[0]);

        gone.OpenMessage.ShouldBe("Can't open a folder for x/y.cs: it is gone.");
        _started.ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_folder_selects_a_changed_submodule_in_its_parent()
    {
        // #446: a submodule is a folder git lists like a file ("1 .M S..."); it is selected, not passed over for its parent.
        var top = TempCheckout();
        try
        {
            Directory.CreateDirectory(Path.Combine(top, "libs", "sub"));
            var model = await OpenWithChanges(Files(Edited("libs/sub", top)), path: top, start: Started);

            await model.OpenFolderCommand.ExecuteAsync(model.Changes!.Rows[0]);

            _started.ShouldHaveSingleItem().Arguments.ShouldBe($"/select,\"{Path.Combine(top, "libs", "sub")}\"");
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public async Task Open_folder_looks_at_the_disk_off_the_calling_thread()
    {
        // #446: a checkout on an offline share blocks a look at the disk for about 20 s, which must not hold up the window.
        var top = TempCheckout();
        try
        {
            File.WriteAllText(Path.Combine(top, "a.cs"), "x");
            var caller = Environment.CurrentManagedThreadId;
            int? starter = null;
            var model = await OpenWithChanges(Files(Edited("a.cs", top)), path: top, start: info =>
            {
                starter = Environment.CurrentManagedThreadId;
                return Started(info);
            });

            await model.OpenFolderCommand.ExecuteAsync(model.Changes!.Rows[0]);

            starter.ShouldNotBeNull().ShouldNotBe(caller);
            _started.ShouldHaveSingleItem();
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public async Task Switching_tabs_clears_the_failure_line()
    {
        // #446: a failure says what went wrong on the tab it happened on.
        var model = await OpenWithChanges(Files(Edited("x/y.cs", @"Q:\csx-no-such-drive\app")), path: @"Q:\csx-no-such-drive\app", start: Started);
        await model.OpenFolderCommand.ExecuteAsync(model.Changes!.Rows[0]);
        model.OpenMessage.ShouldNotBeNull();

        model.ShowCommand.Execute(GitHubItemKind.Issue);

        model.OpenMessage.ShouldBeNull();

        model.OpenMessage = "Couldn't open the row.";
        model.ShowChangesCommand.Execute(null);

        model.OpenMessage.ShouldBeNull();
    }

    [Theory]
    [InlineData(1, "Changes, 1 file, shown")]
    [InlineData(2, "Changes, 2 files, shown")]
    [InlineData(0, "Changes, 0 files, shown")]
    public async Task The_tab_s_name_for_a_screen_reader_counts_files_rightly(int count, string expected)
    {
        var model = await OpenWithChanges(Files([.. Enumerable.Range(0, count).Select(i => Edited($"f{i}.cs"))]));

        model.Changes!.TabAutomationName.ShouldBe(expected);
    }

    [Fact]
    public async Task Follow_with_the_same_files_keeps_the_rows()
    {
        var model = await OpenWithChanges(Files(Edited("a.cs")));
        var rows = model.Changes!.Rows;

        model.FollowChanges(Files(Edited("a.cs")));

        model.Changes.Rows.ShouldBeSameAs(rows, "the same files read again leave the list where it was scrolled");

        model.FollowChanges(Files(Edited("a.cs"), Edited("b.cs")));

        model.Changes.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_changes_say_so()
    {
        var model = await OpenWithChanges(null);
        var changes = model.Changes.ShouldNotBeNull();

        changes.IsUnknown.ShouldBeTrue();
        changes.EmptyText.ShouldBe("Git could not tell what changed.");
        changes.CountText.ShouldBe(string.Empty);

        model.FollowChanges(ChangedFileList.Empty);

        changes.IsUnknown.ShouldBeFalse();
        changes.EmptyText.ShouldBe("No changed files.");
        changes.CountText.ShouldBe("0");

        model.FollowChanges(Files(Edited("a.cs")));

        changes.EmptyText.ShouldBeNull();
    }

    [Fact]
    public void A_row_says_what_happened_in_a_word()
    {
        new ChangedFileRow(new ChangedFile("a.cs", @"C:\app\a.cs", ChangeKind.Edited)).StateText.ShouldBe("edited");
        new ChangedFileRow(new ChangedFile("a.cs", @"C:\app\a.cs", ChangeKind.New)).StateText.ShouldBe("new");
        new ChangedFileRow(new ChangedFile("a.cs", @"C:\app\a.cs", ChangeKind.Deleted)).StateText.ShouldBe("deleted");
        new ChangedFileRow(new ChangedFile("a.cs", @"C:\app\a.cs", ChangeKind.Conflict)).StateText.ShouldBe("conflict");
        var renamed = new ChangedFileRow(new ChangedFile("b.cs", @"C:\app\b.cs", ChangeKind.Renamed, "a.cs"));
        renamed.StateText.ShouldBe("renamed");
        renamed.DisplayPath.ShouldBe("b.cs ← a.cs");
        renamed.OpenFolderName.ShouldBe("Open the folder of b.cs");
        renamed.CopyFolderName.ShouldBe("Copy the folder path of b.cs");
    }

    [Fact]
    public async Task Changes_tab_renders_rows_with_both_buttons()
    {
        var model = await OpenWithChanges(Files(Edited("src/a.cs")));

        await StaThread.RunAsync(() =>
        {
            var panel = PanelView(model);
            var shown = Descendants(panel).OfType<Button>().Where(Shown).Select(AutomationProperties.GetName).ToList();

            shown.ShouldContain("Open the folder of src/a.cs");
            shown.ShouldContain("Copy the folder path of src/a.cs");
            shown.ShouldContain("Copy all paths");
            shown.ShouldNotContain("Copy all links", "the links are the GitHub tabs' to copy");
            shown.ShouldContain("Changes, 1 file, shown");
            Descendants(panel).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ShouldContain("src/a.cs");
            Descendants(panel).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ShouldContain("edited");

            model.ShowCommand.Execute(GitHubItemKind.Issue);
            panel.UpdateLayout();
            shown = Descendants(panel).OfType<Button>().Where(Shown).Select(AutomationProperties.GetName).ToList();

            shown.ShouldContain("Copy all links");
            shown.ShouldNotContain("Copy all paths");
            shown.ShouldNotContain("Open the folder of src/a.cs");
        });
    }

    [Fact]
    public async Task Copy_all_paths_is_off_with_nothing_to_copy()
    {
        // An empty copy would wipe what the user has on the clipboard (review of #444).
        var model = await OpenWithChanges(ChangedFileList.Empty);
        _clipboard.TrySetText("kept");

        model.CopyAllPathsCommand.CanExecute(null).ShouldBeFalse();
        model.FollowChanges(Files(Edited("a.cs")));
        model.CopyAllPathsCommand.CanExecute(null).ShouldBeTrue();
        model.FollowChanges(null);
        model.CopyAllPathsCommand.CanExecute(null).ShouldBeFalse();
        _clipboard.Text.ShouldBe("kept");
    }

    [Fact]
    public async Task The_empty_line_shows_only_while_the_tab_has_no_rows()
    {
        var model = await OpenWithChanges(Files(Edited("a.cs")));

        await StaThread.RunAsync(() =>
        {
            var panel = PanelView(model);
            var empty = Descendants(panel).OfType<TextBlock>().Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Changes.EmptyText");

            Shown(empty).ShouldBeFalse("no blank line above the rows");

            model.FollowChanges(ChangedFileList.Empty);
            panel.UpdateLayout();

            Shown(empty).ShouldBeTrue();
            empty.Text.ShouldBe("No changed files.");
        });
    }

    [Fact]
    public async Task A_panel_without_a_checkout_has_no_changes_tab_on_screen()
    {
        var (activity, pill) = await PillAsync();
        var model = Open(activity, pill);

        await StaThread.RunAsync(() =>
            Descendants(PanelView(model)).OfType<Button>().Where(Shown).Select(AutomationProperties.GetName)
                .ShouldNotContain(n => n.StartsWith("Changes", StringComparison.Ordinal)));
    }

    /// <summary>Nothing up the tree collapses it. Not IsVisible: the panel hangs in no window.</summary>
    private static bool Shown(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private bool Started(ProcessStartInfo info)
    {
        _started.Add(info);
        return true;
    }

    private static string TempCheckout()
    {
        var top = Path.Combine(Path.GetTempPath(), "csx-changes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(top);
        return top;
    }
}
