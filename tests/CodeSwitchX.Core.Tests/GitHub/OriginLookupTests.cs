using System.Diagnostics;
using CodeSwitchX.Core.GitHub;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.GitHub;

public class OriginLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-origin-" + Guid.NewGuid().ToString("N"));

    public OriginLookupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git's object files are read-only
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void An_answer_is_the_origin() =>
        OriginLookup.From(new ProcessRunResult("https://github.com/acme/tool.git\n", false, 7, 0)).ShouldBe(new OriginLookup("https://github.com/acme/tool.git\n"));

    [Fact]
    public void Exit_code_1_is_a_folder_without_an_origin() =>
        OriginLookup.From(new ProcessRunResult(null, false, 7, 1)).ShouldBe(OriginLookup.NoOrigin);

    [Theory]
    [InlineData(true, 7, null)] // timed out
    [InlineData(false, null, null)] // did not start
    [InlineData(false, 7, null)] // cancelled, or a pipe broke: it never reported an exit
    [InlineData(false, 7, 128)] // git itself gave up: a broken config, say
    public void Anything_else_without_an_answer_is_a_failure(bool timedOut, int? pid, int? exitCode) =>
        OriginLookup.From(new ProcessRunResult(null, timedOut, pid, exitCode)).ShouldBe(OriginLookup.Failure);

    [Fact]
    public async Task Real_git_tells_a_folder_without_an_origin_from_one_with()
    {
        await Git("init");

        (await OriginLookup.ReadAsync(_root, TestContext.Current.CancellationToken)).ShouldBe(OriginLookup.NoOrigin);

        await Git("remote add origin https://github.com/acme/tool.git");

        (await OriginLookup.ReadAsync(_root, TestContext.Current.CancellationToken)).Url!.Trim().ShouldBe("https://github.com/acme/tool.git");

        await Git("remote remove origin");

        (await OriginLookup.ReadAsync(_root, TestContext.Current.CancellationToken)).ShouldBe(OriginLookup.NoOrigin);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_reached_is_a_failure() =>
        (await OriginLookup.ReadAsync(Path.Combine(_root, "not-there"), TestContext.Current.CancellationToken)).ShouldBe(OriginLookup.Failure);

    [Fact]
    public async Task A_cancelled_lookup_is_a_failure()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        (await OriginLookup.ReadAsync(_root, stop.Token)).ShouldBe(OriginLookup.Failure);
    }

    private async Task Git(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
    }
}
