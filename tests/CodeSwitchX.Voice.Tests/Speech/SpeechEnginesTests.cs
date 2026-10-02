namespace CodeSwitchX.Voice.Tests.Speech;

using System.Runtime.CompilerServices;
using CodeSwitchX.Voice.Speech;

public sealed class SpeechEnginesTests
{
    private readonly SpeechSettings _settings = new();
    private readonly FakeEngine _kokoro = new(SpeechEngine.Kokoro);
    private readonly FakeEngine _qwen = new(SpeechEngine.Qwen);
    private readonly SpeechEngines _engines;
    private readonly List<TextToSpeechStatus> _told = [];
    private readonly List<EngineStatus> _toldOfEach = [];

    public SpeechEnginesTests()
    {
        _engines = new SpeechEngines(_settings, [_kokoro, _qwen]);
        _engines.StatusChanged += (_, status) => _told.Add(status);
        _engines.EngineStatusChanged += (_, status) => _toldOfEach.Add(status);
    }

    [Fact]
    public async Task With_no_engine_picked_Raven_only_writes()
    {
        _engines.Status.ShouldBe(SpeechEngines.NoEngine);
        _engines.Prepare(install: true);

        var error = await Should.ThrowAsync<TextToSpeechNotReadyException>(async () =>
        {
            await foreach (var _ in _engines.SpeakAsync("Hello.", CancellationToken.None))
            {
            }
        });

        error.Status.State.ShouldBe(TextToSpeechState.NoEngine);
        (_kokoro.Prepared, _qwen.Prepared).ShouldBe((0, 0), "nothing is installed in the background");
    }

    [Fact]
    public async Task The_engine_picked_gets_ready_and_speaks()
    {
        _settings.Engine = SpeechEngine.Kokoro;

        _engines.Prepare(install: false);
        var chunks = new List<SpeechChunk>();
        await foreach (var chunk in _engines.SpeakAsync("Hello.", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        _kokoro.Prepared.ShouldBe(1);
        _kokoro.Spoken.ShouldBe(["Hello."]);
        _qwen.Spoken.ShouldBeEmpty();
        chunks.ShouldHaveSingleItem();
    }

    [Fact]
    public void Picking_another_engine_stops_the_one_before_and_tells_where_the_new_one_stands()
    {
        _settings.Engine = SpeechEngine.Qwen;
        _told.ShouldBe([TextToSpeechStatus.Off]);
        _qwen.Report(new TextToSpeechStatus(TextToSpeechState.Ready));
        _kokoro.Report(new TextToSpeechStatus(TextToSpeechState.NotInstalled));

        _settings.Engine = SpeechEngine.Kokoro;

        _qwen.Stops.ShouldBe(1);
        _kokoro.Stops.ShouldBe(1, "the first pick stopped the engine not picked");
        _told.ShouldBe([TextToSpeechStatus.Off, new TextToSpeechStatus(TextToSpeechState.Ready), new TextToSpeechStatus(TextToSpeechState.NotInstalled)]);
        _engines.Status.State.ShouldBe(TextToSpeechState.NotInstalled);
    }

    [Fact]
    public void Only_the_picked_engine_s_changes_are_its_status_but_every_engine_s_are_told()
    {
        _settings.Engine = SpeechEngine.Kokoro;
        _told.Clear();

        _qwen.Report(new TextToSpeechStatus(TextToSpeechState.Loading));
        _kokoro.Report(new TextToSpeechStatus(TextToSpeechState.Ready));

        _told.ShouldBe([new TextToSpeechStatus(TextToSpeechState.Ready)]);
        _toldOfEach.Select(s => s.Engine).ShouldBe([SpeechEngine.Qwen, SpeechEngine.Kokoro]);
        _engines.StatusOf(SpeechEngine.Qwen).State.ShouldBe(TextToSpeechState.Loading);
    }

    [Fact]
    public void The_voice_setup_installs_and_cancels_the_engine_it_names()
    {
        _engines.Install(SpeechEngine.Qwen);
        _engines.Cancel(SpeechEngine.Qwen);
        _engines.CheckInstalls();

        (_qwen.Installs, _qwen.Stops).ShouldBe((1, 1));
        (_kokoro.Installs, _kokoro.Stops).ShouldBe((0, 0));
        (_kokoro.Checks, _qwen.Checks).ShouldBe((1, 1));
    }

    [Fact]
    public void A_hang_restarts_the_picked_engine()
    {
        _settings.Engine = SpeechEngine.Qwen;

        _engines.Recover();

        (_qwen.Recovered, _kokoro.Recovered).ShouldBe((1, 0));
    }

    private sealed class FakeEngine(SpeechEngine engine) : ISpeechEngineVoice
    {
        public SpeechEngine Engine { get; } = engine;

        public TextToSpeechStatus Status { get; private set; } = TextToSpeechStatus.Off;

        public event EventHandler<TextToSpeechStatus>? StatusChanged;

        public bool IsInstalled { get; set; }

        public int Prepared { get; private set; }

        public int Installs { get; private set; }

        public int Stops { get; private set; }

        public int Checks { get; private set; }

        public int Recovered { get; private set; }

        public List<string> Spoken { get; } = [];

        public void Report(TextToSpeechStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }

        public void Prepare(bool install) => Prepared++;

        public void Install() => Installs++;

        public void Stop() => Stops++;

        public void CheckInstall() => Checks++;

        public void Recover() => Recovered++;

        public async IAsyncEnumerable<SpeechChunk> SpeakAsync(string text, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            Spoken.Add(text);
            yield return new SpeechChunk(new byte[2], 24000);
        }
    }
}
