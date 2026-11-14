using System.Diagnostics;

namespace CodeSwitchX.Core.Tests.Workspaces;

/// <summary>Real git for the tests of #407 and #435: an origin (bare), the user's checkout, and a second clone that pushes to the origin.</summary>
public abstract class RealGitRepos : IDisposable
{
    protected readonly string _root = Path.Combine(Path.GetTempPath(), "csx-pull-" + Guid.NewGuid().ToString("N"));

    protected string Origin => Path.Combine(_root, "origin.git");

    protected string Work => Path.Combine(_root, "work");

    protected string Other => Path.Combine(_root, "other");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // git marks its object files read-only.
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    protected static async Task<string> Git(string folder, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. arguments])
        {
            info.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("git is not installed");
            throw;
        }

        using var running = process ?? throw new InvalidOperationException("git did not start");
        var output = running.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = running.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await running.WaitForExitAsync(TestContext.Current.CancellationToken);
        running.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)}: {await error}");
        return (await output).Trim();
    }

    protected static async Task Commit(string folder, string file, string text)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, file), text, TestContext.Current.CancellationToken);
        await Git(folder, "add", file);
        await Git(folder, "commit", "-q", "-m", $"{file}: {text}");
    }

    /// <summary>An origin with one commit on main, the checkout on a feature branch with a commit of its own, and the other clone.</summary>
    protected async Task Arrange()
    {
        Directory.CreateDirectory(_root);
        await Git(_root, "init", "-q", "--bare", "-b", "main", Origin);
        await Git(_root, "clone", "-q", Origin, Work);
        await Commit(Work, "a.txt", "one");
        await Git(Work, "push", "-q", "origin", "main");
        await Git(Work, "switch", "-q", "-c", "feature/x");
        await Commit(Work, "f.txt", "feature");
        await Git(_root, "clone", "-q", Origin, Other);
    }

    protected async Task PushToMain(int commits)
    {
        for (var i = 0; i < commits; i++)
        {
            await Commit(Other, "b.txt", $"change {i}");
        }

        await Git(Other, "push", "-q", "origin", "main");
    }
}
