# CodeSwitchX

A WPF tray app for Windows that watches Claude Code chats (via hooks and transcripts), docks VS Code windows
(the "Cab"), shows workspaces as tiles (the "Yard"), and runs Raven, a voice assistant built on headless Claude.

Long-form conventions with code examples: the `csharp-conventions` skill (`.claude/skills/csharp-conventions/SKILL.md`).
Load it before writing C#/XAML here.

## Solution layout (`CodeSwitchX.slnx`)

| Project | Role |
|---|---|
| `src/CodeSwitchX.Core` | Domain: sessions and their state machine, event bus, workspaces, GitHub reads, persistence interfaces. No UI, no EF. |
| `src/CodeSwitchX.Data` | EF Core + SQLite: `CodeSwitchXDbContext`, stores, `PersistenceWriter`, migrations. |
| `src/CodeSwitchX.Ingest` | Event API (named pipe + 127.0.0.1, ASP.NET Core), hook ingestion, transcript indexer, MCP tools for Raven. |
| `src/CodeSwitchX.Hook` | `csx-hook.exe`: the relay Claude Code runs as a hook. Exe, AOT-compatible, no reflection. |
| `src/CodeSwitchX.Hosting` | VS Code windows: launch, hide/show, dock, Win32 (CsWin32), the companion extension installer. |
| `src/CodeSwitchX.Telemetry` | Token usage, cost estimate, context fill, cache warmth. |
| `src/CodeSwitchX.Conductor` | Raven's brains on `LittleGod.Llm.ClaudeCli` (headless Claude Code sessions). |
| `src/CodeSwitchX.Voice` | Speech: Whisper.net dictation, Open mic (ONNX VAD/turn), TTS sidecars (Python), sounds. |
| `src/CodeSwitchX.UI` | WPF app (`CodeSwitchX.exe`): App/DI, views and ViewModels per feature folder. |
| `tests/<Project>.Tests` | One xUnit v3 project per `src` project; `tests/Shared` is compiled into all of them. |
| `vscode-companion/` | JS VS Code extension, embedded into Hosting as resources and packed at run time. |
| `scripts/build.ps1` | The **stable build**: bumps `<Version>`, publishes to `E:\StableVersion`, commits and pushes. Only run when asked. |

## Build and test

- Build: `dotnet build CodeSwitchX.slnx` (about 30 s incremental). For a change confined to one project,
  `dotnet build src/<Project>` or `dotnet build tests/<Project>.Tests` is enough.
- All tests: `dotnet test --solution CodeSwitchX.slnx` (Microsoft Testing Platform, set in `global.json`).
  About 75 s for ~5100 tests.
- One test project: `dotnet test --project tests/<P>.Tests [--filter-class "*NameTests"] [--filter-method "*Name*"]`
  (MTP filters, not `--filter "FullyQualifiedName~…"`). The built exe takes the same filters:
  `tests/<P>.Tests/bin/Debug/net10.0-windows/<P>.Tests.exe`.
- **After every change: build, and run the affected tests, before reporting back.** Warnings are errors here
  (see below), so a green build means zero warnings.

## Language and build settings (`Directory.Build.props`, applies to every project)

- `net10.0-windows`, `LangVersion latest` (C# 14), `Nullable enable`, `ImplicitUsings enable`.
- `TreatWarningsAsErrors`, `MSBuildTreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on: an `.editorconfig`
  violation with warning severity (block-scoped namespace, missing braces) **fails the build**.
- Central package management: versions live in `Directory.Packages.props`; `<PackageReference>` has no `Version`.
  Adding a package means asking first (global rule) and adding a `<PackageVersion>` there.
- Conventions used: file-scoped namespaces (enforced), braces always (enforced), `_camelCase` private fields,
  collection expressions (`[]`, `[.. x]`), records for messages and snapshots, `required` and primary constructors
  sparingly (services take explicit constructors with `readonly` fields). The C# 14 `field` keyword and
  `[ObservableProperty]` on partial properties are not used: stay with field-based `[ObservableProperty]`.

## UI (WPF) conventions

- MVVM: CommunityToolkit.Mvvm. ViewModels are `sealed partial class XViewModel : ObservableObject` with
  `[ObservableProperty] private T _name;` fields and `[RelayCommand]` methods. No Prism, no ReactiveUI.
- DI: Generic Host built in `App.CreateHostBuilder` (`App.xaml.cs`); registrations in `App.ConfigureServices`,
  plus one `AddCodeSwitchX<Layer>()` extension per library. Almost everything is a singleton. Hosted services
  start in registration order and the order matters (comment in `ConfigureServices`, `AppHostTests`).
- DataContext: `MainWindow` gets `ShellViewModel` in its constructor; child views get theirs by binding
  (`<yard:YardView DataContext="{Binding Yard}"/>`); items use implicit `DataTemplate DataType=…`. Dialog windows
  take their ViewModel in the constructor. XAML declares `d:DataContext="{d:DesignInstance …}"`. No ViewModel locator.
- UI thread: ViewModels and services never touch `Dispatcher` directly; they take `IUiDispatcher` and `Post`
  (tests substitute `ImmediateDispatcher`). `UiDispatcherExtensions.InvokeAsync/RunAsync` for reads with a timeout.
- Time: inject `TimeProvider` (tests use `FakeTimeProvider`); never `DateTime.Now` or `Task.Delay` without it.
- Code-behind is allowed for view-only work (dialogs, Win32, drawing, focus, hotkeys); business logic goes in the
  ViewModel or a service.
- Converters: `UI/Infrastructure/Converters.cs` (one file), registered once in `App.xaml` with a short key.
  Attached-property behaviours sit in `Infrastructure` or the feature folder; there is no Behaviors package.
- Styling: `ThemeMode="Dark"` (WPF Fluent, .NET 9+), shared styles in `App.xaml`, view-local styles in each
  `UserControl.Resources`. No separate ResourceDictionary files, no third-party UI library.

## Where new files go

- Feature folders, not layer folders: `UI/<Feature>/XView.xaml(.cs)` + `XViewModel.cs` side by side
  (`Yard`, `Cab`, `Raven`, `Settings`, `Telemetry`, `Voice`, `Workspaces`, `Shell`); cross-cutting UI helpers in
  `UI/Infrastructure`. Namespace = folder (`CodeSwitchX.UI.Yard`).
- Tests mirror the source path: `tests/CodeSwitchX.UI.Tests/Yard/YardViewModelTests.cs`; big suites split into
  partial files (`RavenPanelViewModelTests.Mute.cs`).

## Gotchas

- WPF projects lose `System.IO` from implicit usings (clash with `Shapes.Path`): write `using Path = System.IO.Path;`.
- EF migrations: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/CodeSwitchX.Data`.
  Migrations keep the block-scoped namespaces `dotnet ef` writes (the one exempted rule); their bodies may be edited.
- `src/CodeSwitchX.Hook` must stay AOT-safe: JSON goes through `RelayJsonContext` (source-generated), no reflection.
- Win32 APIs in Hosting come from CsWin32: add the name to `src/CodeSwitchX.Hosting/NativeMethods.txt`. Hook and UI
  have no CsWin32 and keep their few hand-written P/Invokes.
- `LittleGod.Llm.ClaudeCli` comes from the local feed `E:\NuGetFeed` (`nuget.config`), pinned to an exact version.
- `<Version>` in `Directory.Build.props` is bumped by `scripts/build.ps1` only; don't edit it by hand.
- `docs/review/decisions.md` lists settled "by design" rulings; read it before calling something a bug.
- Building a test project rebuilds `CodeSwitchX.exe` from the checked-out branch. Three `HotkeyServiceTests` fail
  while another CodeSwitchX (e.g. the stable build) runs and holds the global hotkeys; that is not a regression.
- Git stores LF; working copies may be CRLF. Never mix line endings inside one file.
- The repo is public: no private repo names, issue titles or tokens in code, tests or commits.
