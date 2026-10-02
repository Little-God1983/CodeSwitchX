using System.Security.Cryptography;

namespace CodeSwitchX.Voice.Listening;

/// <summary>One model file Open mic needs: where it comes from, and what it must be once here.</summary>
public sealed record ListeningModel(string FileName, Uri Source, long Length, string Sha256);

/// <summary>
/// Open mic's two models, in a folder of their own beside the Whisper model. Each is pinned (a release tag, a Hugging
/// Face commit) and checked by length and SHA-256: a download goes to a ".partial" file, and only one that matches is
/// moved into place, so a cut or changed file is never loaded. Some 11 MB together, fetched the first time the user
/// switches to Open mic.
/// </summary>
public sealed class ListeningModelStore(string folder, HttpClient http)
{
    /// <summary>Silero VAD v6.2.3 (MIT): is this 32 ms of audio speech?</summary>
    public static readonly ListeningModel Silero = new("silero_vad.onnx",
        new Uri("https://raw.githubusercontent.com/snakers4/silero-vad/v6.2.3/src/silero_vad/data/silero_vad.onnx"),
        2_327_524, "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3");

    /// <summary>Pipecat's Smart Turn v3.2, int8 for the CPU (BSD-2-Clause): has the user finished their turn?</summary>
    public static readonly ListeningModel SmartTurn = new("smart-turn-v3.2-cpu.onnx",
        new Uri("https://huggingface.co/pipecat-ai/smart-turn-v3/resolve/f766f81d3cfdf7737ac64aad813d91bbfd56bf93/smart-turn-v3.2-cpu.onnx"),
        8_679_182, "2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f");

    /// <summary>The models this store keeps; the tests hand in their own.</summary>
    internal IReadOnlyList<ListeningModel> Models { get; init; } = [Silero, SmartTurn];

    /// <summary>How long a download may go without a byte before it counts as stalled; the tests shorten it.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string PathOf(ListeningModel model) => Path.Combine(folder, model.FileName);

    /// <summary>Every model is in place at its pinned length (the hash was checked when it arrived).</summary>
    public bool IsPresent => Models.All(Present);

    /// <summary>Downloads the models not yet in place. Progress is 0..1 over all their bytes. Throws on a failed or
    /// mismatching download, which leaves no file behind, and on one that stalls (an <see cref="IOException"/> after
    /// <see cref="StallTimeout"/> without a byte): the body is read without the client's timeout, so nothing else ends it.</summary>
    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var missing = Models.Where(m => !Present(m)).ToList();
        var total = (double)Math.Max(1, missing.Sum(m => m.Length));
        long done = 0;
        foreach (var model in missing)
        {
            var partial = PathOf(model) + ".partial";
            try
            {
                using var response = await http.GetAsync(model.Source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long read = 0;
                var target = File.Create(partial);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[81_920];
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    int n;
                    while ((n = await ReadAsync(source, buffer, stall, model, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, n);
                        read += n;
                        progress?.Report(Math.Min((done + read) / total, 0.99));
                    }
                }

                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (read != model.Length || sha != model.Sha256)
                {
                    throw new IOException($"{model.FileName} did not arrive whole ({read:N0} of {model.Length:N0} bytes, or another file)");
                }

                File.Move(partial, PathOf(model), overwrite: true);
                done += model.Length;
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
        }

        progress?.Report(1.0);
    }

    /// <summary>One read of the body, given <see cref="StallTimeout"/> afresh.</summary>
    private async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationTokenSource stall, ListeningModel model, CancellationToken ct)
    {
        stall.CancelAfter(StallTimeout);
        try
        {
            return await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{model.FileName}'s download stalled: nothing came for {StallTimeout.TotalSeconds:0.#} s");
        }
    }

    /// <summary>Deletes a model that would not load, so the next switch to Open mic downloads it again.</summary>
    public void Forget(ListeningModel model)
    {
        if (File.Exists(PathOf(model)))
        {
            File.Delete(PathOf(model));
        }
    }

    private bool Present(ListeningModel model) => new FileInfo(PathOf(model)) is { Exists: true } file && file.Length == model.Length;
}
