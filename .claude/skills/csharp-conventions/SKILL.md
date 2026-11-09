---
name: csharp-conventions
description: Use when writing, changing or reviewing C#, .NET, WPF/XAML (.xaml views, converters, styles), CommunityToolkit.Mvvm ViewModels, Generic Host DI registrations, EF Core/SQLite migrations, NuGet package references, or xUnit v3 tests in the CodeSwitchX repo.
---

# CodeSwitchX C# conventions

## Overview

The detailed form of the rules in the repo's `CLAUDE.md`. Every example below is taken from, or trimmed down from,
this codebase; copy the pattern, not a generic template. When this skill and the code disagree, the code wins:
match the nearest existing file and tell the user about the gap.

The build fails on any warning (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`), so style is not optional:
a block-scoped namespace or an `if` without braces breaks the build.

## Quick reference

| Topic | Rule |
|---|---|
| Target | `net10.0-windows`, C# 14 (`LangVersion latest`), nullable on, implicit usings on |
| Namespaces | File-scoped, equal to the folder (`CodeSwitchX.UI.Yard`) |
| Braces | Always, also around one-line bodies |
| Fields | `private readonly T _camelCase;` |
| Collections | Collection expressions: `[]`, `[a, b]`, `[.. source]` |
| Data types | `record` / `sealed record` for messages, snapshots, results; `sealed class` for services and ViewModels |
| Constructors | Services and ViewModels: explicit constructor assigning `readonly` fields. Primary constructors only for small types (nested helpers, simple services) |
| MVVM | CommunityToolkit.Mvvm 8.4: `[ObservableProperty]` on `_fields`, `[RelayCommand]` on methods, class `partial` |
| DI | Generic Host in `App.xaml.cs`, `AddCodeSwitchX<Layer>()` per library, singletons by default |
| UI thread | `IUiDispatcher.Post`, never `Dispatcher`/`Application.Current` in ViewModels or services |
| Time | Inject `TimeProvider`; `FakeTimeProvider` in tests |
| Async | `CancellationToken` passed through; `ConfigureAwait(false)` in code that does not touch UI state after the await |
| Logging | `ILogger<T>` with message templates; Serilog only configured in `App` |
| Tests | xUnit v3 + Shouldly + NSubstitute; test names are sentences with underscores |
| Packages | Central versions in `Directory.Packages.props`; ask before adding one |

## ViewModel

From `src/CodeSwitchX.UI/Workspaces/AddWorkspaceViewModel.cs`:

```csharp
namespace CodeSwitchX.UI.Workspaces;

public sealed partial class AddWorkspaceViewModel : ObservableObject
{
    private readonly WorkspaceProbe _probe;
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceRegistry _registry;
    private readonly ILogger<AddWorkspaceViewModel> _logger;

    [ObservableProperty] private string _inputPath = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private string _name = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] [NotifyCanExecuteChangedFor(nameof(ProbeCommand))] private bool _isBusy;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isProbed;
    [ObservableProperty] private string? _errorMessage;

    public AddWorkspaceViewModel(WorkspaceProbe probe, IWorkspaceStore store, WorkspaceRegistry registry, ILogger<AddWorkspaceViewModel> logger)
    {
        _probe = probe;
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    public ObservableCollection<Track> Tracks { get; } = [];

    public event Action? Closed;   // the view closes itself on this; the ViewModel knows no Window

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ProbeAsync() { /* ... */ }      // generates ProbeCommand (IAsyncRelayCommand)

    private bool CanProbe() => !IsBusy;

    /// <summary>Why, not what: a changed path must be detected again before Add registers anything.</summary>
    partial void OnInputPathChanged(string value) => IsProbed = false;
}
```

Points to keep:
- `sealed partial`; the generator needs `partial`, `sealed` is the repo default.
- Use the generated property (`IsBusy = true`), never the field, outside the constructor.
- Commands are named after the method without `Async`: `ProbeAsync` → `ProbeCommand`.
- React to a change with the generated `partial void OnXChanged(T value)`, not by subscribing to `PropertyChanged`.
- Long-running or startup work goes in `public async Task InitializeAsync(CancellationToken ct)` / `LoadAsync`,
  called by the owner, not in the constructor.
- Bus subscriptions are kept as `IDisposable` and disposed (`PerformanceBarViewModel` keeps `List<IDisposable> _subscriptions`).

## View ↔ ViewModel wiring

There is no ViewModel locator. The root window gets its ViewModel from DI; every child view gets its
DataContext by binding to a property of the parent ViewModel; list items get an implicit `DataTemplate`.

`MainWindow.xaml(.cs)`:

```csharp
public MainWindow(ShellViewModel shell, HotkeyService hotkeys, /* ... */)
{
    InitializeComponent();
    _shell = shell;
    DataContext = shell;
}
```

```xml
<Window ... d:DataContext="{d:DesignInstance shell:ShellViewModel}" Title="{Binding Title}">
  <yard:YardView DataContext="{Binding Yard}"
                 Visibility="{Binding DataContext.Mode, RelativeSource={RelativeSource AncestorType=Window},
                              Converter={StaticResource EnumVisible}, ConverterParameter=Yard}" />
```

Item template (`Cab/CabView.xaml`), with a command on the parent ViewModel:

```xml
<DataTemplate DataType="{x:Type yard:WorkspaceTileViewModel}">
  <Button Command="{Binding DataContext.SelectPipCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
          CommandParameter="{Binding}" ToolTip="{Binding Name}" />
</DataTemplate>
```

- Every view declares `d:DataContext="{d:DesignInstance …}"` so the designer and the XAML language service check
  binding paths. WPF has no compiled bindings: a typo in a path fails silently at run time, so check paths
  against the ViewModel by hand.
- Dialog windows take the ViewModel in the constructor and close on its event:
  `viewModel.Closed += () => Dispatcher.BeginInvoke(Close);` (`AddWorkspaceWindow.xaml.cs`).
- Code-behind may do what only the view can: file/folder pickers, drawing (`PerformanceBarView` draws the
  sparkline), Win32 handles, focus, hotkeys. It reads from and calls into the ViewModel; it holds no state the
  ViewModel needs and no business rules.
- Converters live in `UI/Infrastructure/Converters.cs` and are registered once in `App.xaml`
  (`<infra:HexToBrushConverter x:Key="HexBrush" />`). A converter used by one feature only may sit in that
  feature folder (`Yard/GitHubLabelBrushConverter`), still registered in `App.xaml`.
- Styles shared app-wide go in `App.xaml` `Application.Resources`; styles for one view go in its
  `UserControl.Resources`. The app uses WPF's Fluent dark theme (`ThemeMode="Dark"`); no MahApps/WPF-UI.

## Service with DI

A library exposes one registration extension (`src/CodeSwitchX.Telemetry/TelemetryServiceCollectionExtensions.cs`):

```csharp
public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryService>();
        services.AddSingleton<IPricingProvider>(sp => sp.GetRequiredService<TelemetryService>());
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryService>());
        return services;
    }
}
```

UI-level services are registered in `App.ConfigureServices` (`src/CodeSwitchX.UI/App.xaml.cs`):
- One instance, several faces: register the concrete type, then forward interfaces to it with
  `sp => sp.GetRequiredService<Concrete>()` (never two `AddSingleton<Concrete>` instances).
- Delegates instead of service-locator calls: a constructor that needs "something later" takes a `Func<T>` or a
  method group (`() => sp.GetRequiredService<ShellViewModel>()`, `sp.GetRequiredService<SessionEngine>().Get`).
- Keyed services for several brains of one interface: `AddKeyedSingleton<IConductorBrain>(Key, …)`.
- Hosted services start in registration order and the start flows depend on it; `AppHostTests` pins the pairs.
  Inserting a hosted service: read the order comment above `AddCodeSwitchXData` first.
- Debug-only test hooks sit under `#if DEBUG` with an environment variable (`CODESWITCHX_OPEN_MIC_FILE`).

Marshalling bus events onto the UI thread (`PerformanceBarViewModel.InitializeAsync`):

```csharp
_subscriptions.Add(_bus.Subscribe<TelemetryUpdated>(m => _ui.Post(() => Apply(m.Snapshot))));
_tickTimer = _time.CreateTimer(_ => _ui.Post(RecountSessions), null, YardViewModel.TickInterval, YardViewModel.TickInterval);
```

## Unit test

From `tests/CodeSwitchX.UI.Tests/Workspaces/AddWorkspaceViewModelTests.cs`:

```csharp
namespace CodeSwitchX.UI.Tests.Workspaces;

public class AddWorkspaceViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-add-" + Guid.NewGuid().ToString("N"), "Shop");
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly Track _general = new() { Name = "General" };
    private readonly AddWorkspaceViewModel _vm;

    public AddWorkspaceViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general]));
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));   // no real git process
        var registry = new WorkspaceRegistry(_store, new WorkspaceResolver(), new EventBus(NullLogger<EventBus>.Instance));
        _vm = new AddWorkspaceViewModel(new WorkspaceProbe(git), _store, registry, NullLogger<AddWorkspaceViewModel>.Instance);
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [Fact]
    public async Task Probe_errors_are_shown_not_thrown()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = Path.Combine(_root, "missing");

        await _vm.ProbeCommand.ExecuteAsync(null);

        _vm.IsProbed.ShouldBeFalse();
        _vm.ErrorMessage.ShouldNotBeNull().ShouldContain("does not exist");
    }
}
```

- `Xunit` and `Shouldly` are global usings (`tests/Directory.Build.props`); assert with Shouldly, not `Assert.*`.
- Test names say the behaviour as a sentence: `Probe_errors_are_shown_not_thrown`.
- Arrange / act / assert separated by blank lines; one behaviour per test.
- Real objects where they are cheap (`WorkspaceProbe`, `EventBus`), NSubstitute for stores and I/O boundaries.
- Fakes the suite already has: `ImmediateDispatcher` (UI tests), `ListLogger<T>` (`tests/Shared`, assert on log
  lines and levels), `FakeTimeProvider` (advance time instead of sleeping), `ShellTestHarness`.
- Temp folders per test instance with a GUID, deleted in `Dispose`.
- A test needing a privilege or a machine feature that may be missing calls `Assert.Skip(...)`, never passes silently.
- Run: `dotnet test --project tests/<P>.Tests --filter-class "*AddWorkspaceViewModelTests"`.

## Error handling and logging

```csharp
// Expected failures: catch the specific types and turn them into state the user sees.
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
{
    ErrorMessage = ex.Message;
}
// At a boundary (a command, a background round): catch all but cancellation, log, keep running.
catch (Exception ex) when (ex is not OperationCanceledException)
{
    logger.LogWarning("GitHub round: the accounts could not be read ({Failure}); the last results stay", FailureText.Of(ex));
    return null;
}
```

- Message templates with named placeholders (`{Failure}`, `{Total}`), never string interpolation in a log call.
- Pass the exception as the first argument when the stack trace matters: `_logger.LogError(ex, "Registering workspace failed")`.
- Unhandled exceptions are already logged app-wide (`App.OnDispatcherUnhandledException`,
  `UnhandledExceptionLogging` for other threads and unobserved tasks); that is a safety net, not a strategy.
- Fire-and-forget (`_ = SomethingAsync()`) only when the called method handles and logs its own failures.
- Comments explain *why* and carry the issue number of the decision (`// ... (#224)`); don't narrate what the code does.

## EF Core

- `CodeSwitchXDbContext` in `src/CodeSwitchX.Data`; stores implement the `I…Store` interfaces from
  `CodeSwitchX.Core.Persistence`, so Core never references EF.
- New migration: `dotnet tool restore`, then `dotnet ef migrations add <PascalCaseName> --project src/CodeSwitchX.Data`
  (the design-time factory in that project makes it its own startup project). Leave the generated block-scoped
  namespace; hand edits of the body (custom SQL, data fixes) are fine.
- Raw SQL takes table and column names from the EF model, never literals (see `docs/review/decisions.md`, Persistence).

## Anti-patterns

| Don't | Do instead |
|---|---|
| Business logic or state in `*.xaml.cs` | ViewModel or service; code-behind only for view mechanics |
| `.Result`, `.Wait()`, `GetAwaiter().GetResult()` | `await`. The few existing sync waits sit in exit and dispose paths where WPF gives no async hook (`App.OnExit` says why); don't add new ones without asking |
| `async void` | `async Task`; `async void` only for WPF event handlers and `OnStartup` |
| `Application.Current.Dispatcher` / `Dispatcher.Invoke` in a ViewModel | Inject `IUiDispatcher` and `Post` |
| Touching an `ObservableCollection` off the UI thread | Post the change; the collections are UI-thread only |
| `DateTime.Now`, `Task.Delay(…)` for timing logic | `TimeProvider` (`GetUtcNow`, `CreateTimer`, `Task.Delay(…, time, ct)`) |
| `"SaveCommand"`, `"IsBusy"` as strings in C# | `nameof(SaveCommand)`, `e.PropertyName is nameof(Vm.Prop)` |
| Writing the `[ObservableProperty]` backing field | Set the generated property so change notification fires |
| `CancellationToken.None` deep in a call chain that has a token | Pass the caller's `ct` through |
| `Version="…"` on a `PackageReference` | Version in `Directory.Packages.props` |
| `#pragma warning disable` / `NoWarn` to get a green build | Fix the warning; ask if it seems unfixable |
| A new MVVM, DI, UI or test library | Ask first; the stack above is settled |
