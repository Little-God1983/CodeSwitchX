namespace CodeSwitchX.Core.Tests;

/// <summary>#392: a file another program holds for a moment, as a scanner does one just written, is waited out.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("csx-atomic-").FullName;

    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private string PathOf(string name) => Path.Combine(_root, name);

    /// <summary>
    /// Runs <paramref name="change"/> on a thread of its own while <paramref name="held"/> is open, and lets go 100 ms after
    /// <paramref name="begun"/> says the change has started: well inside the half second of tries, on a busy machine too.
    /// </summary>
    private static void WhileHeldForAMoment(string held, Func<bool> begun, Action change)
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                change();
            }
            catch (Exception ex)
            {
                failed = ex;
            }
        });
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            thread.Start();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!begun() && thread.IsAlive)
            {
                DateTime.UtcNow.ShouldBeLessThan(until, "the change never began");
                Thread.Sleep(1);
            }

            Thread.Sleep(100);
            if (failed is not null)
            {
                throw new ShouldAssertException("the change failed while the file was held", failed);
            }

            thread.IsAlive.ShouldBeTrue("the file is held: the change cannot have gone through yet");
        }

        thread.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        if (failed is not null)
        {
            throw new ShouldAssertException("the change failed after the file was let go", failed);
        }
    }

    [Fact]
    public void A_move_over_a_file_held_for_a_moment_goes_through_once_it_is_let_go()
    {
        var target = PathOf("index.json");
        var source = PathOf("index.json.part");
        File.WriteAllText(target, "old");
        File.WriteAllText(source, "new");

        WhileHeldForAMoment(target, () => true, () => AtomicFile.MoveOver(source, target));

        File.ReadAllText(target).ShouldBe("new");
        File.Exists(source).ShouldBeFalse();
    }

    [Fact]
    public void A_replace_waits_out_a_temporary_file_still_held()
    {
        // A failed write left its temporary file, and a scanner still holds it.
        var target = PathOf("index.json");
        var tmp = target + ".part";
        File.WriteAllText(target, "old");
        File.WriteAllText(tmp, "left");

        WhileHeldForAMoment(tmp, () => true, () => AtomicFile.Replace(target, "new", ".part"));

        File.ReadAllText(target).ShouldBe("new");
        File.Exists(tmp).ShouldBeFalse();
    }

    [Fact]
    public void A_file_held_for_good_fails_after_the_tries_and_leaves_no_temporary_file()
    {
        var target = PathOf("index.json");
        File.WriteAllText(target, "old");

        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Should.Throw<Exception>(() => AtomicFile.Replace(target, "new", ".part"));
            (ex is IOException or UnauthorizedAccessException).ShouldBeTrue(ex.GetType().Name);
        }

        File.ReadAllText(target).ShouldBe("old");
        File.Exists(target + ".part").ShouldBeFalse();
    }

    [Fact]
    public void A_read_only_target_is_refused_at_once()
    {
        var target = PathOf("index.json");
        var source = PathOf("index.json.part");
        File.WriteAllText(target, "old");
        File.WriteAllText(source, "new");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Should.Throw<Exception>(() => AtomicFile.MoveOver(source, target));
            watch.Elapsed.ShouldBeLessThan(AtomicFile.RetryDelay * (AtomicFile.Attempts - 1), "not tried again: it will not change");
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }
}
