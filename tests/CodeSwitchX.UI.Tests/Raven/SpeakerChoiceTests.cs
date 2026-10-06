using CodeSwitchX.UI.Raven;
using CodeSwitchX.Voice.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Raven;

/// <summary>
/// The output Raven speaks on (#172): Settings keeps the default, which is saved; the panel tries another, which is not.
/// What is heard goes to the output by id, or nowhere in particular while the Windows default is followed.
/// </summary>
public sealed class SpeakerChoiceTests
{
    private static readonly SpeakerDevice Speakers = new("id-speakers", "Speakers");
    private static readonly SpeakerDevice Headphones = new("id-headphones", "Headphones");
    private static readonly SpeakerDevice Tv = new("id-tv", "TV");

    private readonly ISpeakerCatalog _catalog = Substitute.For<ISpeakerCatalog>();
    private readonly FakeOutput _output = new();
    private readonly FakeTimeProvider _time = new();

    public SpeakerChoiceTests()
    {
        _catalog.List().Returns([Speakers, Headphones, Tv]);
        _catalog.Default().Returns(Speakers);
    }

    private async Task<SpeakerChoice> NewChoiceAsync(SpeakerDevice? preferred = null)
    {
        var choice = new SpeakerChoice(_catalog, _output, new ImmediateDispatcher(), _time, NullLogger<SpeakerChoice>.Instance)
        {
            PreferredSpeaker = preferred,
        };
        await choice.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return choice;
    }

    private void DevicesChange()
    {
        _catalog.DevicesChanged += Raise.Event<EventHandler>(_catalog, EventArgs.Empty);
        _time.Advance(RavenPanelViewModel.DeviceChangeSettle);
    }

    [Fact]
    public async Task With_nothing_chosen_the_windows_default_is_followed()
    {
        var choice = await NewChoiceAsync();

        choice.Speakers.ShouldBe([Speakers, Headphones, Tv]);
        (choice.DefaultSpeaker, choice.SelectedSpeaker).ShouldBe((Speakers, Speakers));
        _output.DeviceId.ShouldBeNull("the Windows default of the moment plays, as before there was a choice");
        choice.TrialNote.ShouldBeNull();
    }

    [Fact]
    public async Task A_stored_default_that_is_plugged_in_is_heard_by_its_id()
    {
        var choice = await NewChoiceAsync(Headphones);

        (choice.DefaultSpeaker, choice.SelectedSpeaker).ShouldBe((Headphones, Headphones));
        _output.DeviceId.ShouldBe(Headphones.Id);
    }

    [Fact]
    public async Task A_stored_default_that_is_gone_follows_the_windows_default_and_comes_back_with_its_device()
    {
        _catalog.List().Returns([Speakers, Tv]);
        var choice = await NewChoiceAsync(Headphones);
        choice.DefaultSpeaker.ShouldBe(Speakers);
        _output.DeviceId.ShouldBeNull();

        _catalog.List().Returns([Speakers, Headphones, Tv]);
        DevicesChange();

        choice.DefaultSpeaker.ShouldBe(Headphones);
        choice.PreferredSpeaker.ShouldBe(Headphones, "a fallback is not a choice");
        _output.DeviceId.ShouldBe(Headphones.Id);
    }

    [Fact]
    public async Task A_pick_on_the_panel_is_heard_but_not_saved()
    {
        var choice = await NewChoiceAsync();

        choice.SelectedSpeaker = Headphones;

        _output.DeviceId.ShouldBe(Headphones.Id);
        choice.PreferredSpeaker.ShouldBeNull();
        choice.DefaultSpeaker.ShouldBe(Speakers);
        choice.TrialSpeaker.ShouldBe(Headphones);
        choice.TrialNote.ShouldBe("Trying it. Not saved: Raven starts with Speakers.");
    }

    [Fact]
    public async Task Picking_the_default_on_the_panel_ends_the_trial()
    {
        var choice = await NewChoiceAsync();
        choice.SelectedSpeaker = Headphones;

        choice.SelectedSpeaker = Speakers;

        choice.TrialSpeaker.ShouldBeNull();
        choice.TrialNote.ShouldBeNull();
        _output.DeviceId.ShouldBeNull();
    }

    [Fact]
    public async Task A_pick_in_settings_is_saved_heard_and_ends_the_trial()
    {
        var choice = await NewChoiceAsync();
        choice.SelectedSpeaker = Tv;

        choice.DefaultSpeaker = Headphones;

        choice.PreferredSpeaker.ShouldBe(Headphones);
        choice.SelectedSpeaker.ShouldBe(Headphones);
        choice.TrialSpeaker.ShouldBeNull();
        _output.DeviceId.ShouldBe(Headphones.Id);
    }

    [Fact]
    public async Task Choosing_the_default_it_already_is_still_ends_the_trial()
    {
        var choice = await NewChoiceAsync(Headphones);
        choice.SelectedSpeaker = Tv;

        choice.ChooseDefault(Headphones);

        choice.TrialSpeaker.ShouldBeNull();
        choice.SelectedSpeaker.ShouldBe(Headphones);
        _output.DeviceId.ShouldBe(Headphones.Id);
    }

    [Fact]
    public async Task A_trial_ends_when_its_output_is_unplugged_and_does_not_come_back_with_it()
    {
        var choice = await NewChoiceAsync();
        choice.SelectedSpeaker = Tv;

        _catalog.List().Returns([Speakers, Headphones]);
        DevicesChange();
        choice.SelectedSpeaker.ShouldBe(Speakers);
        choice.TrialSpeaker.ShouldBeNull();
        _output.DeviceId.ShouldBeNull();

        _catalog.List().Returns([Speakers, Headphones, Tv]);
        DevicesChange();
        choice.SelectedSpeaker.ShouldBe(Speakers, "the trial is over");
    }

    [Fact]
    public async Task A_trial_of_the_windows_default_outlives_the_stored_default_falling_back_onto_it()
    {
        var choice = await NewChoiceAsync(Headphones);
        choice.SelectedSpeaker = Speakers;
        _output.DeviceId.ShouldBe(Speakers.Id);

        _catalog.List().Returns([Speakers, Tv]);
        DevicesChange();
        choice.TrialNote.ShouldBe("Trying it. Not saved: Raven starts with Headphones.");
        _catalog.List().Returns([Speakers, Headphones, Tv]);
        DevicesChange();

        choice.SelectedSpeaker.ShouldBe(Speakers, "only the user, or its output going, ends a trial");
        choice.DefaultSpeaker.ShouldBe(Headphones);
        _output.DeviceId.ShouldBe(Speakers.Id);
    }

    [Fact]
    public async Task While_windows_audio_is_down_the_trial_waits_without_a_note_and_is_heard_again_after()
    {
        Exception? failure = null;
        _catalog.List().Returns(_ => failure is null ? [Speakers, Headphones, Tv] : throw failure);
        var choice = await NewChoiceAsync();
        choice.SelectedSpeaker = Tv;

        failure = new System.Runtime.InteropServices.COMException("The audio service is not running.");
        DevicesChange();
        choice.Speakers.ShouldBeEmpty();
        choice.TrialNote.ShouldBeNull("nothing is heard, so nothing is being tried");
        _output.DeviceId.ShouldBeNull();

        failure = null;
        DevicesChange();
        choice.SelectedSpeaker.ShouldBe(Tv);
        _output.DeviceId.ShouldBe(Tv.Id);
    }

    [Fact]
    public async Task A_list_bound_control_clearing_the_selection_during_a_refresh_changes_no_choice()
    {
        var choice = await NewChoiceAsync(Headphones);
        choice.SelectedSpeaker = Tv;
        choice.Speakers.CollectionChanged += (_, _) =>
        {
            choice.SelectedSpeaker = null;
            choice.DefaultSpeaker = null;
        };

        await choice.RefreshAsync();

        (choice.SelectedSpeaker, choice.DefaultSpeaker, choice.PreferredSpeaker, choice.TrialSpeaker).ShouldBe((Tv, Headphones, Headphones, Tv));
    }

    private sealed class FakeOutput : IAudioOutput
    {
        public string? DeviceId { get; set; }

#pragma warning disable CS0067 // the choice only sets the device; the players listen
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public NAudio.Wave.IWavePlayer Open(NAudio.Wave.IWaveProvider source, int latencyMs, out string? playsOn) => throw new NotSupportedException();
    }
}
