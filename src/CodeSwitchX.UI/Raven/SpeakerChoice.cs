using System.Collections.ObjectModel;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.Voice.Audio;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Raven;

/// <summary>
/// The output Raven speaks on (#172), as the microphone's choice works: Settings keeps the default, which is saved; the
/// panel's picker tries another, which is not. What is heard goes to <see cref="IAudioOutput"/>: a device chosen by id,
/// or none to follow the Windows default as before there was a choice.
/// </summary>
public sealed partial class SpeakerChoice : ObservableObject
{
    private readonly ISpeakerCatalog _catalog;
    private readonly IAudioOutput _output;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SpeakerChoice> _logger;
    private readonly ITimer _deviceRefresh;
    private int _listings;
    private int _appliedListing;

    /// <summary>A list-bound ComboBox writes null into its selection while its list is cleared; that is no pick.</summary>
    private bool _refreshing;

    /// <summary>The default is the device the user chose, found; false while it follows the Windows default (none chosen, or the one chosen is gone).</summary>
    private bool _defaultIsChosen;

    public SpeakerChoice(ISpeakerCatalog catalog, IAudioOutput output, IUiDispatcher dispatcher, TimeProvider time, ILogger<SpeakerChoice> logger)
    {
        _catalog = catalog;
        _output = output;
        _dispatcher = dispatcher;
        _logger = logger;
        _deviceRefresh = time.CreateTimer(_ => ListAndPostDevices(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // A headset plugged in raises a burst of notifications: listed once they are quiet, as the microphones are.
        catalog.DevicesChanged += (_, _) => _deviceRefresh.Change(RavenPanelViewModel.DeviceChangeSettle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The output devices there are (UI thread).</summary>
    public ObservableCollection<SpeakerDevice> Speakers { get; } = [];

    /// <summary>The output Raven speaks on, which the panel's picker shows: one tried there while it is plugged in, the default otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrialNote))]
    private SpeakerDevice? _selectedSpeaker;

    /// <summary>The default as listed, which Settings shows and picks: the one chosen while it is plugged in, the Windows default otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrialNote))]
    private SpeakerDevice? _defaultSpeaker;

    /// <summary>The user's choice of default, stored in the settings; only a pick in Settings changes it, never a fallback. Null: the Windows default.</summary>
    [ObservableProperty]
    private SpeakerDevice? _preferredSpeaker;

    /// <summary>An output picked on the panel to try it: heard until the default is picked again, it is unplugged or the app restarts. Never saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrialNote))]
    private SpeakerDevice? _trialSpeaker;

    /// <summary>Under the panel's picker while an output is tried there: that it is not saved; null otherwise.</summary>
    public string? TrialNote => TrialSpeaker is null || SelectedSpeaker is null ? null
        : DefaultSpeaker is { } chosen ? $"Trying it. Not saved: Raven starts with {chosen.Name}." : "Trying it. Not saved.";

    /// <summary>The last <see cref="RefreshAsync"/>; completed when none ran or it has been applied.</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Lists the outputs off the UI thread and applies the result on it; the shell calls this once the settings are loaded.</summary>
    public Task RefreshAsync() => PendingRefresh = Task.Run(ListAndPostDevices);

    private Task ListAndPostDevices()
    {
        var number = Interlocked.Increment(ref _listings);
        IReadOnlyList<SpeakerDevice> devices;
        SpeakerDevice? windowsDefault;
        Exception? failure = null;
        try
        {
            devices = _catalog.List();
            windowsDefault = _catalog.Default();
        }
        catch (Exception ex)
        {
            (devices, windowsDefault, failure) = ([], null, ex);
        }

        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                Apply(number, devices, windowsDefault, failure);
                applied.TrySetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not apply the output list");
                applied.TrySetException(ex);
            }
        });
        return applied.Task;
    }

    private void Apply(int number, IReadOnlyList<SpeakerDevice> devices, SpeakerDevice? windowsDefault, Exception? failure)
    {
        if (number < _appliedListing)
        {
            return; // a listing that finished after a newer one is older news
        }

        _appliedListing = number;
        if (failure is not null)
        {
            // The microphone's listing says that Windows audio is not available; the choices stay for when it is back.
            _logger.LogWarning(failure, "Could not list the outputs");
        }

        var trialPicked = TrialSpeaker;
        _refreshing = true;
        try
        {
            Speakers.Clear();
            foreach (var device in devices)
            {
                Speakers.Add(device);
            }

            var (chosen, outcome) = MicrophoneChoice.Resolve(devices, PreferredSpeaker, windowsDefault);
            _defaultIsChosen = PreferredSpeaker is not null && outcome is MicrophoneChoiceOutcome.Stored or MicrophoneChoiceOutcome.Relocated;
            SpeakerDevice? trial = null;
            if (trialPicked is not null
                && MicrophoneChoice.Resolve(devices, trialPicked, windowsDefault) is { Outcome: MicrophoneChoiceOutcome.Stored or MicrophoneChoiceOutcome.Relocated } tried)
            {
                trial = tried.Device;
            }

            DefaultSpeaker = chosen;
            SelectedSpeaker = trial ?? chosen;
            // With no devices listed (Windows audio down) the trial is kept for when they are back; gone from a list, it is over.
            TrialSpeaker = devices.Count == 0 ? trialPicked : trial;
        }
        finally
        {
            _refreshing = false;
        }

        UpdateOutput();
    }

    /// <summary>A pick in Settings.</summary>
    partial void OnDefaultSpeakerChanged(SpeakerDevice? value)
    {
        if (!_refreshing && value is not null)
        {
            ChooseDefault(value);
        }
    }

    /// <summary>The new default, saved, and heard from now on, ending a trial: also when it is the default already, as by voice.</summary>
    public void ChooseDefault(SpeakerDevice device)
    {
        DefaultSpeaker = device;
        PreferredSpeaker = device;
        _defaultIsChosen = true;
        TrialSpeaker = null; // also when the output being tried is the one picked: then the selection does not change
        SelectedSpeaker = device;
        UpdateOutput();
    }

    /// <summary>A pick on the panel: tried, not saved. Picking the default there ends the trial.</summary>
    partial void OnSelectedSpeakerChanged(SpeakerDevice? value)
    {
        if (_refreshing || value is null)
        {
            return;
        }

        TrialSpeaker = Equals(value, DefaultSpeaker) ? null : value;
        UpdateOutput();
    }

    /// <summary>The device heard by id: the one tried, or the default the user chose; none while the Windows default is followed.</summary>
    private void UpdateOutput() => _output.DeviceId = TrialSpeaker is { } trial && SelectedSpeaker is not null ? trial.Id
        : _defaultIsChosen ? DefaultSpeaker?.Id : null;
}
