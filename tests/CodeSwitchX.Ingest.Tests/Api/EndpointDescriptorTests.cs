using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.Ingest.Tests.Api;

public class EndpointDescriptorTests : IDisposable
{
    private static readonly EndpointDescriptor Old = new("csx-old", 1, 1, new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
    private static readonly EndpointDescriptor New = new("csx-new", 2, 2, new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csx-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public EndpointDescriptorTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "endpoint.json");
        Old.Write(_file);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Write_waits_for_a_relay_that_still_reads_the_old_file()
    {
        // The relay reads endpoint.json without FileShare.Delete, so replacing the file fails with a sharing violation while it does.
        var reader = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = Task.Run(() => New.Write(_file), TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        reader.Dispose();

        await write.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        EndpointDescriptor.TryRead(_file)!.PipeName.ShouldBe("csx-new");
        Directory.GetFiles(_dir).Select(Path.GetFileName).ShouldBe(["endpoint.json"]);
    }

    [Fact]
    public void A_write_that_cannot_replace_the_file_leaves_no_temporary_file_behind()
    {
        using var holder = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None);

        // Windows reports the sharing violation of a move as access denied.
        Should.Throw<UnauthorizedAccessException>(() => New.Write(_file));

        Directory.GetFiles(_dir).Select(Path.GetFileName).ShouldBe(["endpoint.json"]);
    }
}
