using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

/// <summary>The changed files behind a tile's "N changed" pill (#444), read from the status the pill is counted from.</summary>
public class GitInspectorChangedFilesTests : IDisposable
{
    private const string Top = @"E:\Repo";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-git-changed-" + Guid.NewGuid().ToString("N"));

    public GitInspectorChangedFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("1 .M N... 100644 100644 100644 aaaa aaaa src/a b.cs", "src/a b.cs", @"E:\Repo\src\a b.cs", ChangeKind.Edited)]
    [InlineData("1 M. N... 100644 100644 100644 aaaa bbbb a.cs", "a.cs", @"E:\Repo\a.cs", ChangeKind.Edited)]
    [InlineData("1 A. N... 000000 100644 100644 0000 aaaa new.cs", "new.cs", @"E:\Repo\new.cs", ChangeKind.New)]
    [InlineData("1 .D N... 100644 100644 000000 aaaa aaaa gone.cs", "gone.cs", @"E:\Repo\gone.cs", ChangeKind.Deleted)]
    [InlineData("1 D. N... 100644 000000 000000 aaaa 0000 gone.cs", "gone.cs", @"E:\Repo\gone.cs", ChangeKind.Deleted)]
    [InlineData("u UU N... 100644 100644 100644 100644 aaaa bbbb cccc both.cs", "both.cs", @"E:\Repo\both.cs", ChangeKind.Conflict)]
    [InlineData("? docs/new/", "docs/new/", @"E:\Repo\docs\new", ChangeKind.New)]
    [InlineData("? notes.md", "notes.md", @"E:\Repo\notes.md", ChangeKind.New)]
    [InlineData("? \"docs/\\303\\244 \\\"q\\\".md\"", "docs/ä \"q\".md", @"E:\Repo\docs\ä ""q"".md", ChangeKind.New)]
    public void A_status_line_becomes_a_file(string line, string path, string fullPath, ChangeKind kind)
    {
        var files = GitInspector.ChangedFilesIn([line], Top).Files;

        files.Count.ShouldBe(1);
        files[0].ShouldBe(new ChangedFile(path, fullPath, kind));
    }

    [Theory]
    [InlineData("2 R. N... 100644 100644 100644 aaaa aaaa R100 src/new.cs\tsrc/old.cs", "src/new.cs", "src/old.cs")]
    [InlineData("2 C. N... 100644 100644 100644 aaaa aaaa C75 copy.cs\torig.cs", "copy.cs", "orig.cs")]
    [InlineData("2 R. N... 100644 100644 100644 aaaa aaaa R100 \"\\303\\244 neu.cs\"\t\"\\303\\244 alt.cs\"", "ä neu.cs", "ä alt.cs")]
    public void A_rename_keeps_the_new_path_and_the_old_one(string line, string path, string oldPath)
    {
        var file = GitInspector.ChangedFilesIn([line], Top).Files.ShouldHaveSingleItem();

        file.Path.ShouldBe(path);
        file.OldPath.ShouldBe(oldPath);
        file.Kind.ShouldBe(ChangeKind.Renamed);
        file.FullPath.ShouldBe(Path.Combine(Top, path.Replace('/', '\\')));
    }

    [Theory]
    [InlineData("# branch.oid defa35d0aa11defa35d0aa11defa35d0aa11defa")]
    [InlineData("# branch.head main")]
    [InlineData(" M a.cs")]
    [InlineData("1 .M")]
    public void Headers_and_lines_of_no_known_form_are_no_files(string line)
    {
        GitInspector.ChangedFilesIn([line], Top).Files.ShouldBeEmpty();
    }

    [Fact]
    public void A_new_folder_entry_is_a_folder()
    {
        var file = GitInspector.ChangedFilesIn(["? docs/new/"], Top).Files.ShouldHaveSingleItem();

        file.IsFolder.ShouldBeTrue();
        GitInspector.ChangedFilesIn(["? docs/new.md"], Top).Files[0].IsFolder.ShouldBeFalse();
    }

    [Fact]
    public void Two_lists_of_the_same_files_are_equal_whatever_their_order()
    {
        var a = new ChangedFile("b.cs", @"E:\Repo\b.cs", ChangeKind.Edited);
        var b = new ChangedFile("A.cs", @"E:\Repo\A.cs", ChangeKind.New);

        var one = new ChangedFileList([a, b]);
        var two = new ChangedFileList([b, a]);

        one.ShouldBe(two);
        (one == two).ShouldBeTrue();
        one.GetHashCode().ShouldBe(two.GetHashCode());
        one.Files.Select(f => f.Path).ShouldBe(["A.cs", "b.cs"], "sorted by path, case set aside");
        one.ShouldNotBe(new ChangedFileList([a]));
    }

    [Fact]
    public async Task Inspect_lists_the_files_it_counts()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(
            "# branch.oid defa35d0aa11defa35d0aa11defa35d0aa11defa\n# branch.head main\n1 .M N... 100644 100644 100644 92cd 92cd a.cs\r\n? b.cs\n"));

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        var files = info.ChangedFiles.ShouldNotBeNull().Files;
        files.Count.ShouldBe(info.DirtyCount!.Value, "the tab lists what the pill counts");
        files.Select(f => f.FullPath).ShouldBe([Path.Combine(_root, "a.cs"), Path.Combine(_root, "b.cs")]);
    }

    [Fact]
    public async Task A_git_that_gives_no_answer_lists_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(null));

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        info.ChangedFiles.ShouldBeNull("no list is made up where git did not answer");
    }
}
