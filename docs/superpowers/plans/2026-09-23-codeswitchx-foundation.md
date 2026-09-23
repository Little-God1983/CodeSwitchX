# CodeSwitchX Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the CodeSwitchX solution scaffold plus the M0 hook relay and the M1 status core, so the WPF Yard shows live Claude Code session status per registered workspace.

**Architecture:** One WPF process hosts a generic host with background services: an in-process Kestrel Event API (named pipe + loopback) receives hook payloads relayed by `csx-hook.exe`; a transcript indexer tails Claude Code JSONL files; both feed a pure state machine inside the Session Engine, which publishes snapshots on an in-process event bus that the Yard view models and the SQLite persistence writer subscribe to. The Snap host in `CodeSwitchX.Hosting` launches VS Code windows and positions/cloaks them with CsWin32.

**Tech Stack:** .NET 10 (SDK 10.0.302), WPF with the built-in Fluent theme, CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.Hosting 10.0.12, EF Core Sqlite 10.0.12, ASP.NET Core minimal APIs (shared framework), CsWin32 0.3.335, H.NotifyIcon.Wpf 2.4.1, Serilog 4.4.0, xunit.v3 4.0.1, Shouldly 4.3.0, NSubstitute 6.2.0.

**Spec:** `docs/superpowers/specs/2026-09-23-codeswitchx-design.md` (section "Implementation slice 1 — Foundation" defines this plan's scope).

## Global Constraints

- Solution file is `CodeSwitchX.slnx` at the repo root; projects live under `src/` and `tests/`, named `CodeSwitchX.<Area>` and `CodeSwitchX.<Area>.Tests`.
- Every project targets `net10.0-windows` (set once in `Directory.Build.props`); C# `latest`, `Nullable` and `ImplicitUsings` enabled; package versions only in `Directory.Packages.props`.
- **Commit and push after every task.** Remote: https://github.com/Little-God1983/CodeSwitchX. The repo-local identity is `Little-God@web.de` (never set globally). After a task's final verification step passes, run `git add -A`, commit with a conventional message (`feat:`, `test:`, `chore:`, `docs:`) whose last line is `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`, then `git push`.
- Dependency direction: `UI -> Hosting, Ingest, Telemetry, Data, Core`; `Hosting, Ingest, Telemetry, Data -> Core`; `Hook` and `Core` reference no other project.
- Data folder is `%LOCALAPPDATA%\CodeSwitchX\` with `codeswitchx.db`, `token`, `endpoint.json`, `logs\`. Every component takes the folder from `AppPaths` so tests can redirect it to a temp directory.
- Relay executable is `csx-hook.exe`; hook entries are recognised by the marker `csx-hook` inside the command string; the relay always exits 0 and spends at most 150 ms connecting.
- Hook events handled: `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Notification`, `Stop`, `SubagentStop`, `SessionEnd`. Unknown events are recorded and logged but never change state.
- Session states: `Starting, Idle, Working, Waiting, Stale, Errored, Ended`. "Waiting" means Claude needs the user.
- Staleness: `Idle` becomes `Stale` after 30 minutes without an event.
- Transcript usage is deduplicated by assistant message id and stored in 1-minute buckets keyed by session, model and minute.
- No network calls leave the machine. The Event API binds only to a named pipe (current-user ACL) and `127.0.0.1`.
- Windows 10 22H2+, x64 and ARM64: no RID-specific code paths; AOT publish RID is passed on the command line.
- Test framework is xunit.v3 with Shouldly assertions; `Xunit` and `Shouldly` are global usings in every test project.

## Review Focus

Failure modes the spec implies but which are easy to miss. Each has a pinned test in the task named.

1. **Sibling roots that share a string prefix** (`C:\Repo\App` and `C:\Repo\App2`): a cwd of `C:\Repo\App2\src` must resolve to `App2`, never to `App`. Pinned in Task 3.
2. **Hook payload with an unknown event name** (a newer Claude Code release): parsed without throwing, `Signal` is null, the session's state is unchanged, the event is still recorded. Pinned in Tasks 7 and 4.
3. **Transcript truncated or rotated below the stored byte offset**: the tailer restarts from 0 instead of throwing or reading garbage. Pinned in Task 9.
4. **`settings.json` that is empty, whitespace or malformed JSON**: the installer refuses to write, reports the problem, and leaves the original file and its backup untouched. Pinned in Task 10.
5. **Relay invoked while CodeSwitchX is down or `endpoint.json` points to a dead port**: exit code 0 within about one second and no output on stdout. Pinned in Task 12.

## File Structure

```
CodeSwitchX.slnx
global.json
Directory.Build.props
Directory.Packages.props
.editorconfig
.gitignore
src/CodeSwitchX.Core/
  CodeSwitchX.Core.csproj
  AppPaths.cs                          data folder layout
  ClaudeCodePaths.cs                   ~/.claude/settings.json and ~/.claude/projects
  Paths/PathNormalizer.cs              canonical path form for comparisons
  Workspaces/Workspace.cs, Track.cs, Worktree.cs, WorkspaceRoot.cs
  Workspaces/WorkspaceResolver.cs      longest-prefix cwd -> workspace
  Workspaces/WorkspaceProbe.cs         inspects a folder/.sln/.code-workspace for registration
  Workspaces/GitInspector.cs           branch + dirty count
  Sessions/SessionState.cs, SessionSignal.cs
  Sessions/SessionStateMachine.cs      pure transitions
  Sessions/HookEvent.cs                normalised hook event
  Sessions/TranscriptUpdate.cs         facts from the indexer
  Sessions/SessionSnapshot.cs          immutable view published on the bus
  Sessions/SessionEngine.cs            reconciles signals into snapshots
  Sessions/ProcessLivenessMonitor.cs   marks sessions Errored when the claude process dies
  Persistence/SessionRecord.cs, SessionEventRecord.cs, UsageBucket.cs, TranscriptCursor.cs, PricingRule.cs, Setting.cs
  Messaging/IEventBus.cs, EventBus.cs, Messages.cs
src/CodeSwitchX.Data/
  CodeSwitchX.Data.csproj
  CodeSwitchXDbContext.cs
  DesignTimeDbContextFactory.cs
  Migrations/                          generated by dotnet-ef
  DatabaseInitializer.cs               Migrate + WAL + seed
  Stores/IWorkspaceStore.cs, WorkspaceStore.cs
  Stores/ISessionStore.cs, SessionStore.cs
  Stores/IUsageStore.cs, UsageStore.cs
  Stores/ISettingsStore.cs, SettingsStore.cs
  PersistenceWriter.cs                 channel + 250 ms batching
  DataServiceCollectionExtensions.cs
src/CodeSwitchX.Ingest/
  CodeSwitchX.Ingest.csproj
  Hooks/HookEnvelopeParser.cs
  Hooks/ClaudeHookInstaller.cs
  Api/EndpointDescriptor.cs, AccessTokenStore.cs, EventApiService.cs
  Transcripts/TranscriptLineParser.cs, TranscriptTailer.cs, TranscriptIndexer.cs
  IngestServiceCollectionExtensions.cs
src/CodeSwitchX.Telemetry/
  CodeSwitchX.Telemetry.csproj
  DefaultPricing.cs, PricingTable.cs, CostEstimator.cs, ContextFillCalculator.cs
  UsageAggregator.cs, TelemetryService.cs
  TelemetryServiceCollectionExtensions.cs
src/CodeSwitchX.Hosting/
  CodeSwitchX.Hosting.csproj
  NativeMethods.txt, NativeMethods.json
  Win32/WindowInfo.cs, IWindowEnumerator.cs, Win32WindowEnumerator.cs
  Win32/IWindowDocker.cs, SnapWindowDocker.cs, WindowLocationWatcher.cs, DwmThumbnail.cs
  VsCode/VsCodeLocator.cs, VsCodeLauncher.cs, VsCodeWindowMatcher.cs
  HostManager.cs, HostedWorkspace.cs
  HostingServiceCollectionExtensions.cs
src/CodeSwitchX.Hook/
  CodeSwitchX.Hook.csproj
  Program.cs, Relay.cs, RelayJsonContext.cs, ProcessChain.cs
src/CodeSwitchX.UI/
  CodeSwitchX.UI.csproj, app.manifest
  App.xaml, App.xaml.cs               host bootstrap, DI, Serilog
  MainWindow.xaml(.cs)
  Shell/ShellViewModel.cs, ShellMode.cs
  Yard/YardViewModel.cs, WorkspaceTileViewModel.cs, ChatRowViewModel.cs, YardView.xaml(.cs)
  Workspaces/AddWorkspaceViewModel.cs, AddWorkspaceWindow.xaml(.cs)
  Cab/CabViewModel.cs, CabView.xaml(.cs)
  Settings/SettingsViewModel.cs, SettingsView.xaml(.cs)
  Telemetry/PerformanceBarViewModel.cs, PerformanceBarView.xaml(.cs)
  Infrastructure/HotkeyService.cs, DispatcherEventBusAdapter.cs, Converters.cs
tests/Directory.Build.props            shared test packages + global usings
tests/CodeSwitchX.Core.Tests/
tests/CodeSwitchX.Data.Tests/
tests/CodeSwitchX.Ingest.Tests/
tests/CodeSwitchX.Telemetry.Tests/
tests/CodeSwitchX.Hosting.Tests/
tests/CodeSwitchX.Hook.Tests/
tests/CodeSwitchX.UI.Tests/
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitignore`, `CodeSwitchX.slnx`
- Create: `src/CodeSwitchX.{Core,Data,Ingest,Telemetry,Hosting,Hook,UI}/CodeSwitchX.<Area>.csproj`
- Create: `src/CodeSwitchX.Core/AppPaths.cs`, `src/CodeSwitchX.Core/ClaudeCodePaths.cs`
- Create: `src/CodeSwitchX.UI/App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `app.manifest` (minimal, replaced in Task 14)
- Create: `src/CodeSwitchX.Hook/Program.cs` (minimal, replaced in Task 12)
- Create: `tests/Directory.Build.props`, `tests/CodeSwitchX.<Area>.Tests/CodeSwitchX.<Area>.Tests.csproj` for Core, Data, Ingest, Telemetry, Hosting, Hook, UI
- Test: `tests/CodeSwitchX.Core.Tests/AppPathsTests.cs`, one `ScaffoldTests.cs` per other test project

**Interfaces:**
- Produces: `CodeSwitchX.Core.AppPaths` (`Root`, `DatabaseFile`, `TokenFile`, `EndpointFile`, `LogsDirectory`, `EnsureCreated()`, `static Default()`), `CodeSwitchX.Core.ClaudeCodePaths` (`SettingsFile`, `ProjectsDirectory`, `static Default()`).

- [ ] **Step 1: Write the root build files**

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "latestFeature"
  }
}
```

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Deterministic>true</Deterministic>
    <Company>CodeSwitchX</Company>
    <Product>CodeSwitchX</Product>
    <Version>0.1.0</Version>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <PackageVersion Include="H.NotifyIcon.Wpf" Version="2.4.1" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
    <PackageVersion Include="Microsoft.Windows.CsWin32" Version="0.3.335" />
    <PackageVersion Include="Serilog" Version="4.4.0" />
    <PackageVersion Include="Serilog.Extensions.Hosting" Version="10.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="7.0.0" />
    <PackageVersion Include="Serilog.Sinks.Debug" Version="3.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
  </ItemGroup>
</Project>
```

`.editorconfig`:

```ini
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
indent_style = space
indent_size = 4
trim_trailing_whitespace = true

[*.{xml,csproj,props,targets,slnx,json,yml,yaml}]
indent_size = 2

[*.cs]
dotnet_sort_system_directives_first = true
csharp_style_namespace_declarations = file_scoped:warning
csharp_style_var_for_built_in_types = false:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_prefer_braces = true:warning
dotnet_style_qualification_for_field = false:suggestion
csharp_style_prefer_primary_constructors = false:suggestion
```

`.gitignore` (standard Visual Studio ignore, abbreviated to what this repo needs):

```gitignore
bin/
obj/
.vs/
*.user
*.suo
TestResults/
*.received.*
artifacts/
node_modules/
```

- [ ] **Step 2: Write the shared test props**

`tests/Directory.Build.props`:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="NSubstitute" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <Using Include="Shouldly" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the project files**

`src/CodeSwitchX.Core/CodeSwitchX.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.Data/CodeSwitchX.Data.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.Ingest/CodeSwitchX.Ingest.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.Telemetry/CodeSwitchX.Telemetry.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.Hosting/CodeSwitchX.Hosting.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <PackageReference Include="Microsoft.Windows.CsWin32">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.Hook/CodeSwitchX.Hook.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>csx-hook</AssemblyName>
    <RootNamespace>CodeSwitchX.Hook</RootNamespace>
    <PublishAot>true</PublishAot>
    <IsAotCompatible>true</IsAotCompatible>
    <InvariantGlobalization>true</InvariantGlobalization>
    <UseSystemResourceKeys>true</UseSystemResourceKeys>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Size</OptimizationPreference>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="CodeSwitchX.Hook.Tests" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.UI/CodeSwitchX.UI.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <AssemblyName>CodeSwitchX</AssemblyName>
    <RootNamespace>CodeSwitchX.UI</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <NoWarn>$(NoWarn);WPF0001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <ProjectReference Include="..\CodeSwitchX.Data\CodeSwitchX.Data.csproj" />
    <ProjectReference Include="..\CodeSwitchX.Ingest\CodeSwitchX.Ingest.csproj" />
    <ProjectReference Include="..\CodeSwitchX.Telemetry\CodeSwitchX.Telemetry.csproj" />
    <ProjectReference Include="..\CodeSwitchX.Hosting\CodeSwitchX.Hosting.csproj" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="H.NotifyIcon.Wpf" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="Serilog.Extensions.Hosting" />
    <PackageReference Include="Serilog.Sinks.File" />
    <PackageReference Include="Serilog.Sinks.Debug" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="CodeSwitchX.UI.Tests" />
  </ItemGroup>
</Project>
```

`src/CodeSwitchX.UI/app.manifest` (PerMonitorV2 DPI awareness, required for correct Snap-host rectangles):

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="CodeSwitchX.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
```

Minimal `App.xaml` / `App.xaml.cs` / `MainWindow.xaml` / `MainWindow.xaml.cs` so the WPF project builds (all replaced in Task 14):

```xml
<Application x:Class="CodeSwitchX.UI.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml"
             ThemeMode="System" />
```

```csharp
namespace CodeSwitchX.UI;

public partial class App : System.Windows.Application
{
}
```

```xml
<Window x:Class="CodeSwitchX.UI.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="CodeSwitchX" Width="1200" Height="800">
  <Grid />
</Window>
```

```csharp
namespace CodeSwitchX.UI;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

Minimal `src/CodeSwitchX.Hook/Program.cs` (replaced in Task 12):

```csharp
return 0;
```

Test project files, one per area. `tests/CodeSwitchX.Core.Tests/CodeSwitchX.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
</Project>
```

`tests/CodeSwitchX.Data.Tests/CodeSwitchX.Data.Tests.csproj`, `tests/CodeSwitchX.Ingest.Tests/...`, `tests/CodeSwitchX.Telemetry.Tests/...`, `tests/CodeSwitchX.Hosting.Tests/...` each reference their `src` project the same way (Ingest.Tests and Data.Tests also add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />`; Ingest.Tests adds `<FrameworkReference Include="Microsoft.AspNetCore.App" />`).

`tests/CodeSwitchX.Hook.Tests/CodeSwitchX.Hook.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\CodeSwitchX.Hook\CodeSwitchX.Hook.csproj" />
  </ItemGroup>
</Project>
```

`tests/CodeSwitchX.UI.Tests/CodeSwitchX.UI.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\CodeSwitchX.UI\CodeSwitchX.UI.csproj" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Write the failing AppPaths test**

`tests/CodeSwitchX.Core.Tests/AppPathsTests.cs`:

```csharp
using CodeSwitchX.Core;

namespace CodeSwitchX.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Files_live_under_the_root()
    {
        var paths = new AppPaths(@"C:\data\csx");

        paths.DatabaseFile.ShouldBe(@"C:\data\csx\codeswitchx.db");
        paths.TokenFile.ShouldBe(@"C:\data\csx\token");
        paths.EndpointFile.ShouldBe(@"C:\data\csx\endpoint.json");
        paths.LogsDirectory.ShouldBe(@"C:\data\csx\logs");
    }

    [Fact]
    public void Default_root_is_LocalAppData_CodeSwitchX()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeSwitchX");

        AppPaths.Default().Root.ShouldBe(expected);
    }

    [Fact]
    public void EnsureCreated_creates_root_and_logs()
    {
        var root = Path.Combine(Path.GetTempPath(), "csx-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AppPaths(root).EnsureCreated();
            Directory.Exists(root).ShouldBeTrue();
            Directory.Exists(Path.Combine(root, "logs")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClaudeCodePaths_point_into_the_home_folder()
    {
        var claude = new ClaudeCodePaths(@"C:\Users\me");

        claude.SettingsFile.ShouldBe(@"C:\Users\me\.claude\settings.json");
        claude.ProjectsDirectory.ShouldBe(@"C:\Users\me\.claude\projects");
    }
}
```

Each other test project gets `ScaffoldTests.cs` proving the project reference wires up, for example in `tests/CodeSwitchX.Data.Tests/ScaffoldTests.cs`:

```csharp
namespace CodeSwitchX.Data.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Referenced_assembly_loads()
    {
        System.Reflection.Assembly.Load("CodeSwitchX.Data").GetName().Name.ShouldBe("CodeSwitchX.Data");
    }
}
```

(Same file for Ingest, Telemetry, Hosting with their assembly names; Hook.Tests loads `csx-hook`; UI.Tests loads `CodeSwitchX`.)

- [ ] **Step 5: Create the solution and add projects**

```powershell
cd C:\GitRepo\CodeSwitchX
dotnet new sln --format slnx -n CodeSwitchX
dotnet sln CodeSwitchX.slnx add src/CodeSwitchX.Core src/CodeSwitchX.Data src/CodeSwitchX.Ingest src/CodeSwitchX.Telemetry src/CodeSwitchX.Hosting src/CodeSwitchX.Hook src/CodeSwitchX.UI --solution-folder src
dotnet sln CodeSwitchX.slnx add tests/CodeSwitchX.Core.Tests tests/CodeSwitchX.Data.Tests tests/CodeSwitchX.Ingest.Tests tests/CodeSwitchX.Telemetry.Tests tests/CodeSwitchX.Hosting.Tests tests/CodeSwitchX.Hook.Tests tests/CodeSwitchX.UI.Tests --solution-folder tests
```

- [ ] **Step 6: Run the Core tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Core.Tests`
Expected: build error `The type or namespace name 'AppPaths' could not be found`.

- [ ] **Step 7: Implement AppPaths and ClaudeCodePaths**

`src/CodeSwitchX.Core/AppPaths.cs`:

```csharp
namespace CodeSwitchX.Core;

/// <summary>Layout of the per-user data folder (%LOCALAPPDATA%\CodeSwitchX by default).</summary>
public sealed class AppPaths
{
    public const string ProductFolderName = "CodeSwitchX";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName));

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "codeswitchx.db");
    public string TokenFile => Path.Combine(Root, "token");
    public string EndpointFile => Path.Combine(Root, "endpoint.json");
    public string LogsDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
```

`src/CodeSwitchX.Core/ClaudeCodePaths.cs`:

```csharp
namespace CodeSwitchX.Core;

/// <summary>Where Claude Code keeps its user-level settings and transcripts.</summary>
public sealed class ClaudeCodePaths
{
    public ClaudeCodePaths(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        HomeDirectory = homeDirectory;
    }

    public static ClaudeCodePaths Default() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string HomeDirectory { get; }
    public string ClaudeDirectory => Path.Combine(HomeDirectory, ".claude");
    public string SettingsFile => Path.Combine(ClaudeDirectory, "settings.json");
    public string ProjectsDirectory => Path.Combine(ClaudeDirectory, "projects");
}
```

- [ ] **Step 8: Build the whole solution and run every test project**

Run: `dotnet build CodeSwitchX.slnx` then `dotnet test CodeSwitchX.slnx`
Expected: build succeeds with 0 errors; every test project reports its tests passed (Core: 4, others: 1 each).

---

### Task 2: Session state machine

**Files:**
- Create: `src/CodeSwitchX.Core/Sessions/SessionState.cs`, `SessionSignal.cs`, `SessionStateMachine.cs`
- Test: `tests/CodeSwitchX.Core.Tests/Sessions/SessionStateMachineTests.cs`

**Interfaces:**
- Produces: `enum SessionState { Starting, Idle, Working, Waiting, Stale, Errored, Ended }`; `enum SessionSignal { SessionStart, PromptSubmit, ToolUse, Notification, Stop, SessionEnd, ProcessGone, StaleTimeout }`; `static class SessionStateMachine { static bool TryNext(SessionState current, SessionSignal signal, out SessionState next); static SessionState Next(SessionState current, SessionSignal signal); static bool NeedsUser(SessionState s); static bool IsLive(SessionState s); }`

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Core.Tests/Sessions/SessionStateMachineTests.cs`:

```csharp
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class SessionStateMachineTests
{
    [Theory]
    // spec diagram
    [InlineData(SessionState.Starting, SessionSignal.SessionStart, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Working, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Working, SessionSignal.Notification, SessionState.Waiting)]
    [InlineData(SessionState.Waiting, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Waiting, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Working, SessionSignal.Stop, SessionState.Idle)]
    [InlineData(SessionState.Idle, SessionSignal.StaleTimeout, SessionState.Stale)]
    [InlineData(SessionState.Working, SessionSignal.ProcessGone, SessionState.Errored)]
    [InlineData(SessionState.Idle, SessionSignal.SessionEnd, SessionState.Ended)]
    // hooks installed mid-session: evidence of life revives any state
    [InlineData(SessionState.Starting, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Stale, SessionSignal.PromptSubmit, SessionState.Working)]
    [InlineData(SessionState.Stale, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Errored, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Idle, SessionSignal.ToolUse, SessionState.Working)]
    [InlineData(SessionState.Idle, SessionSignal.Notification, SessionState.Waiting)]
    [InlineData(SessionState.Waiting, SessionSignal.Stop, SessionState.Idle)]
    [InlineData(SessionState.Working, SessionSignal.SessionEnd, SessionState.Ended)]
    [InlineData(SessionState.Waiting, SessionSignal.SessionEnd, SessionState.Ended)]
    [InlineData(SessionState.Idle, SessionSignal.ProcessGone, SessionState.Errored)]
    [InlineData(SessionState.Waiting, SessionSignal.ProcessGone, SessionState.Errored)]
    [InlineData(SessionState.Working, SessionSignal.SessionStart, SessionState.Idle)]
    public void Defined_transitions(SessionState from, SessionSignal signal, SessionState expected)
    {
        SessionStateMachine.TryNext(from, signal, out var next).ShouldBeTrue();
        next.ShouldBe(expected);
        SessionStateMachine.Next(from, signal).ShouldBe(expected);
    }

    [Theory]
    [InlineData(SessionState.Working, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Waiting, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Ended, SessionSignal.StaleTimeout)]
    [InlineData(SessionState.Ended, SessionSignal.ProcessGone)]
    [InlineData(SessionState.Ended, SessionSignal.Stop)]
    [InlineData(SessionState.Errored, SessionSignal.ProcessGone)]
    [InlineData(SessionState.Stale, SessionSignal.StaleTimeout)]
    public void Undefined_transitions_keep_the_state(SessionState from, SessionSignal signal)
    {
        SessionStateMachine.TryNext(from, signal, out var next).ShouldBeFalse();
        next.ShouldBe(from);
        SessionStateMachine.Next(from, signal).ShouldBe(from);
    }

    [Fact]
    public void Every_state_and_signal_pair_is_total()
    {
        foreach (var state in Enum.GetValues<SessionState>())
        {
            foreach (var signal in Enum.GetValues<SessionSignal>())
            {
                Should.NotThrow(() => SessionStateMachine.Next(state, signal));
            }
        }
    }

    [Fact]
    public void Waiting_is_the_only_state_that_needs_the_user()
    {
        Enum.GetValues<SessionState>().Where(SessionStateMachine.NeedsUser)
            .ShouldBe(new[] { SessionState.Waiting });
    }

    [Fact]
    public void Live_states_exclude_ended_and_errored()
    {
        Enum.GetValues<SessionState>().Where(SessionStateMachine.IsLive).ShouldBe(
            new[] { SessionState.Starting, SessionState.Idle, SessionState.Working, SessionState.Waiting, SessionState.Stale });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Core.Tests --filter FullyQualifiedName~SessionStateMachineTests`
Expected: build error, `SessionState` not found.

- [ ] **Step 3: Implement the enums and the machine**

`src/CodeSwitchX.Core/Sessions/SessionState.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public enum SessionState
{
    Starting,
    Idle,
    Working,
    Waiting,
    Stale,
    Errored,
    Ended,
}
```

`src/CodeSwitchX.Core/Sessions/SessionSignal.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

/// <summary>Normalised inputs to the state machine. Hook events, transcript facts and monitors map onto these.</summary>
public enum SessionSignal
{
    SessionStart,
    PromptSubmit,
    ToolUse,
    Notification,
    Stop,
    SessionEnd,
    ProcessGone,
    StaleTimeout,
}
```

`src/CodeSwitchX.Core/Sessions/SessionStateMachine.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

/// <summary>
/// Pure transition table from the design spec. Signals that carry evidence of life
/// (prompt, tool use, notification, stop, session start) are accepted from any state because
/// hooks may be installed while sessions are already running.
/// </summary>
public static class SessionStateMachine
{
    public static bool TryNext(SessionState current, SessionSignal signal, out SessionState next)
    {
        SessionState? candidate = signal switch
        {
            SessionSignal.SessionStart => SessionState.Idle,
            SessionSignal.PromptSubmit => SessionState.Working,
            SessionSignal.ToolUse => SessionState.Working,
            SessionSignal.Notification => SessionState.Waiting,
            SessionSignal.Stop when current != SessionState.Ended => SessionState.Idle,
            SessionSignal.SessionEnd => SessionState.Ended,
            SessionSignal.ProcessGone when current is not (SessionState.Ended or SessionState.Errored) => SessionState.Errored,
            SessionSignal.StaleTimeout when current == SessionState.Idle => SessionState.Stale,
            _ => null,
        };

        if (candidate is null)
        {
            next = current;
            return false;
        }

        next = candidate.Value;
        return true;
    }

    public static SessionState Next(SessionState current, SessionSignal signal)
    {
        TryNext(current, signal, out var next);
        return next;
    }

    public static bool NeedsUser(SessionState state) => state == SessionState.Waiting;

    public static bool IsLive(SessionState state) => state is not (SessionState.Ended or SessionState.Errored);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Core.Tests --filter FullyQualifiedName~SessionStateMachineTests`
Expected: all pass.

---

### Task 3: Workspace model and resolver

**Files:**
- Create: `src/CodeSwitchX.Core/Paths/PathNormalizer.cs`
- Create: `src/CodeSwitchX.Core/Workspaces/HostMode.cs`, `Track.cs`, `Workspace.cs`, `Worktree.cs`, `WorkspaceRoot.cs`, `IWorkspaceResolver.cs`, `WorkspaceResolver.cs`
- Test: `tests/CodeSwitchX.Core.Tests/Paths/PathNormalizerTests.cs`, `tests/CodeSwitchX.Core.Tests/Workspaces/WorkspaceResolverTests.cs`

**Interfaces:**
- Produces: `PathNormalizer.Normalize(string) -> string` (full path, backslashes, no trailing separator, lower-case); `PathNormalizer.IsWithin(string candidateNormalized, string rootNormalized) -> bool`; entities `Track { Guid Id; string Name; int SortOrder }`, `Workspace { Guid Id; string Name; string RootPath; string? WorkspaceFile; Guid TrackId; string AccentColor; HostMode HostMode; string? VsCodeProfile; bool AutoStart; DateTimeOffset CreatedAt; List<Worktree> Worktrees }`, `Worktree { Guid Id; Guid WorkspaceId; string Path; string? Branch }`, `enum HostMode { Snap, Web }`; `readonly record struct WorkspaceRoot(Guid WorkspaceId, string Path)`; `IWorkspaceResolver { Guid? Resolve(string? path); }`; `WorkspaceResolver : IWorkspaceResolver { void SetRoots(IEnumerable<WorkspaceRoot>); static IEnumerable<WorkspaceRoot> RootsOf(IEnumerable<Workspace>); }`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Core.Tests/Paths/PathNormalizerTests.cs`:

```csharp
using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Tests.Paths;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Repo\App", @"c:\repo\app")]
    [InlineData(@"C:\Repo\App\", @"c:\repo\app")]
    [InlineData(@"C:/Repo/App/src/../", @"c:\repo\app")]
    [InlineData(@"  C:\Repo\App  ", @"c:\repo\app")]
    [InlineData(@"C:\", @"c:")]
    public void Normalize_produces_a_canonical_form(string input, string expected)
    {
        PathNormalizer.Normalize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"c:\repo\app", @"c:\repo\app", true)]
    [InlineData(@"c:\repo\app\src", @"c:\repo\app", true)]
    [InlineData(@"c:\repo\app2", @"c:\repo\app", false)]
    [InlineData(@"c:\repo\app2\src", @"c:\repo\app", false)]
    [InlineData(@"c:\repo", @"c:\repo\app", false)]
    [InlineData(@"c:\repo\app", @"c:", true)]
    public void IsWithin_requires_a_separator_boundary(string candidate, string root, bool expected)
    {
        PathNormalizer.IsWithin(candidate, root).ShouldBe(expected);
    }
}
```

`tests/CodeSwitchX.Core.Tests/Workspaces/WorkspaceResolverTests.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class WorkspaceResolverTests
{
    private static readonly Guid App = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid App2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AppWorktree = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static WorkspaceResolver Resolver()
    {
        var resolver = new WorkspaceResolver();
        resolver.SetRoots(
        [
            new WorkspaceRoot(App, @"C:\Repo\App"),
            new WorkspaceRoot(App2, @"C:\Repo\App2"),
            new WorkspaceRoot(AppWorktree, @"C:\Repo\App\.worktrees\feature-x"),
        ]);
        return resolver;
    }

    [Fact]
    public void Sibling_roots_that_share_a_string_prefix_do_not_collide()
    {
        var resolver = Resolver();

        resolver.Resolve(@"C:\Repo\App2\src").ShouldBe(App2);
        resolver.Resolve(@"C:\Repo\App2").ShouldBe(App2);
        resolver.Resolve(@"C:\Repo\App\src").ShouldBe(App);
    }

    [Fact]
    public void Longest_matching_root_wins_so_worktrees_beat_their_parent()
    {
        Resolver().Resolve(@"C:\Repo\App\.worktrees\feature-x\src\Foo").ShouldBe(AppWorktree);
    }

    [Theory]
    [InlineData(@"c:\repo\app\SRC")]
    [InlineData(@"C:/Repo/App/src/")]
    [InlineData(@"C:\Repo\App")]
    public void Matching_is_case_and_separator_insensitive(string cwd)
    {
        Resolver().Resolve(cwd).ShouldBe(App);
    }

    [Theory]
    [InlineData(@"D:\Elsewhere")]
    [InlineData(@"C:\Repo")]
    [InlineData("")]
    [InlineData(null)]
    public void Unregistered_paths_resolve_to_null(string? cwd)
    {
        Resolver().Resolve(cwd).ShouldBeNull();
    }

    [Fact]
    public void RootsOf_includes_worktrees_as_child_roots()
    {
        var workspace = new Workspace
        {
            Id = App,
            Name = "App",
            RootPath = @"c:\repo\app",
            TrackId = Guid.NewGuid(),
            Worktrees = { new Worktree { WorkspaceId = App, Path = @"c:\repo\app-wt", Branch = "feature" } },
        };

        var roots = WorkspaceResolver.RootsOf([workspace]).ToList();

        roots.Select(r => r.Path).ShouldBe([@"c:\repo\app", @"c:\repo\app-wt"], ignoreOrder: true);
        roots.ShouldAllBe(r => r.WorkspaceId == App);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Core.Tests --filter "FullyQualifiedName~PathNormalizerTests|FullyQualifiedName~WorkspaceResolverTests"`
Expected: build error, `PathNormalizer` not found.

- [ ] **Step 3: Implement the normaliser, entities and resolver**

`src/CodeSwitchX.Core/Paths/PathNormalizer.cs`:

```csharp
namespace CodeSwitchX.Core.Paths;

/// <summary>Canonical form used for every path comparison: full path, backslashes, no trailing separator, lower-case.</summary>
public static class PathNormalizer
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim()).Replace('/', '\\');
        full = full.TrimEnd('\\');
        return full.ToLowerInvariant();
    }

    /// <summary>True when <paramref name="candidate"/> equals <paramref name="root"/> or lies below it. Both must already be normalised.</summary>
    public static bool IsWithin(string candidate, string root)
    {
        if (candidate.Length == root.Length)
        {
            return string.Equals(candidate, root, StringComparison.Ordinal);
        }

        return candidate.Length > root.Length
            && candidate.StartsWith(root, StringComparison.Ordinal)
            && candidate[root.Length] == '\\';
    }
}
```

`src/CodeSwitchX.Core/Workspaces/HostMode.cs`:

```csharp
namespace CodeSwitchX.Core.Workspaces;

public enum HostMode
{
    Snap,
    Web,
}
```

`src/CodeSwitchX.Core/Workspaces/Track.cs`:

```csharp
namespace CodeSwitchX.Core.Workspaces;

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}
```

`src/CodeSwitchX.Core/Workspaces/Workspace.cs`:

```csharp
namespace CodeSwitchX.Core.Workspaces;

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalised (see <see cref="Paths.PathNormalizer"/>) folder that owns the tile. Unique.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Optional <c>.code-workspace</c> file to open instead of the folder.</summary>
    public string? WorkspaceFile { get; set; }

    public Guid TrackId { get; set; }
    public string AccentColor { get; set; } = "#3B82F6";
    public HostMode HostMode { get; set; } = HostMode.Snap;
    public string? VsCodeProfile { get; set; }
    public bool AutoStart { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Worktree> Worktrees { get; set; } = [];
}
```

`src/CodeSwitchX.Core/Workspaces/Worktree.cs`:

```csharp
namespace CodeSwitchX.Core.Workspaces;

public sealed class Worktree
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }

    /// <summary>Normalised path of the worktree; a child root for session mapping.</summary>
    public string Path { get; set; } = string.Empty;
    public string? Branch { get; set; }
}
```

`src/CodeSwitchX.Core/Workspaces/WorkspaceRoot.cs`:

```csharp
namespace CodeSwitchX.Core.Workspaces;

public readonly record struct WorkspaceRoot(Guid WorkspaceId, string Path);
```

`src/CodeSwitchX.Core/Workspaces/IWorkspaceResolver.cs`:

```csharp
namespace CodeSwitchX.Core.Workspaces;

public interface IWorkspaceResolver
{
    /// <summary>Maps a working directory to the workspace whose root (or worktree) contains it, longest root first.</summary>
    Guid? Resolve(string? path);
}
```

`src/CodeSwitchX.Core/Workspaces/WorkspaceResolver.cs`:

```csharp
using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed class WorkspaceResolver : IWorkspaceResolver
{
    private volatile WorkspaceRoot[] _roots = [];

    public void SetRoots(IEnumerable<WorkspaceRoot> roots)
    {
        _roots = roots
            .Select(r => new WorkspaceRoot(r.WorkspaceId, PathNormalizer.Normalize(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .ToArray();
    }

    public static IEnumerable<WorkspaceRoot> RootsOf(IEnumerable<Workspace> workspaces)
    {
        foreach (var workspace in workspaces)
        {
            yield return new WorkspaceRoot(workspace.Id, workspace.RootPath);
            foreach (var worktree in workspace.Worktrees)
            {
                yield return new WorkspaceRoot(workspace.Id, worktree.Path);
            }
        }
    }

    public Guid? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (var root in _roots)
        {
            if (PathNormalizer.IsWithin(normalized, root.Path))
            {
                return root.WorkspaceId;
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Core.Tests`
Expected: all pass.

---

### Task 4: Event bus and Session Engine

**Files:**
- Create: `src/CodeSwitchX.Core/Messaging/IEventBus.cs`, `EventBus.cs`, `Messages.cs`
- Create: `src/CodeSwitchX.Core/Sessions/ProcessRef.cs`, `HookEvent.cs`, `TokenUsage.cs`, `UsageDelta.cs`, `TranscriptUpdate.cs`, `SessionSnapshot.cs`, `SessionEngineOptions.cs`, `SessionEngine.cs`, `IProcessProbe.cs`, `SystemProcessProbe.cs`, `ProcessLivenessMonitor.cs`
- Test: `tests/CodeSwitchX.Core.Tests/Messaging/EventBusTests.cs`, `tests/CodeSwitchX.Core.Tests/Sessions/SessionEngineTests.cs`, `tests/CodeSwitchX.Core.Tests/Sessions/ProcessLivenessMonitorTests.cs`

**Interfaces:**
- Consumes: `SessionStateMachine`, `IWorkspaceResolver` (Tasks 2, 3).
- Produces:
  - `IEventBus { void Publish<T>(T message) where T : class; IDisposable Subscribe<T>(Action<T> handler) where T : class; }`, `EventBus(ILogger<EventBus>)`.
  - Messages: `HookEventReceived(HookEvent Event)`, `TranscriptUpdated(TranscriptUpdate Update)`, `SessionChanged(SessionSnapshot? Previous, SessionSnapshot Current)`, `WorkspaceRegistered(Workspace Workspace)`, `WorkspaceUnregistered(Guid WorkspaceId)`, `WorkspaceRootsChanged()`.
  - `HookEvent` record (init-only): `SessionId, EventName, Signal?, At, Cwd?, TranscriptPath?, ToolName?, ToolUseId?, NotificationType?, Message?, Prompt?, Model?, Source?, RelayPid?, ParentChain (IReadOnlyList<ProcessRef>), RawJson?`.
  - `readonly record struct ProcessRef(int Pid, string Name)`.
  - `readonly record struct TokenUsage(long Input, long Output, long CacheWrite, long CacheRead) { long ContextTokens; long Total; static TokenUsage Zero; }`.
  - `readonly record struct UsageDelta(string Model, DateTimeOffset At, TokenUsage Tokens)`.
  - `TranscriptUpdate` record: `SessionId, TranscriptPath, ObservedAt, Title?, Cwd?, Model?, LastActivityAt?, Usage (IReadOnlyList<UsageDelta>), LatestContext (TokenUsage?), InferredSignal (SessionSignal?)`.
  - `SessionSnapshot` record: `SessionId, WorkspaceId?, Title?, TitleLocked, State, StartedAt, LastEventAt, StateSince, Cwd?, TranscriptPath?, Model?, LastToolName?, LastNotification?, Inferred, ClaudePid?, LatestContext`.
  - `SessionEngine(IEventBus, IWorkspaceResolver, TimeProvider, ILogger<SessionEngine>, SessionEngineOptions?)` with `Snapshots`, `Get(id)`, `Restore(IEnumerable<SessionSnapshot>)`, `Start()`, `Apply(HookEvent)`, `Apply(TranscriptUpdate)`, `MarkProcessGone(string)`, `Rename(string, string)`, `SweepStale()`, `ReResolveWorkspaces()`, `Dispose()`.
  - `IProcessProbe { bool IsAlive(int pid); }`, `SystemProcessProbe`, `ProcessLivenessMonitor : BackgroundService` with internal `Tick()`.

- [ ] **Step 1: Write the failing event bus tests**

`tests/CodeSwitchX.Core.Tests/Messaging/EventBusTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Core.Tests.Messaging;

public class EventBusTests
{
    private sealed record Ping(int N);
    private sealed record Pong(int N);

    [Fact]
    public void Subscribers_receive_only_their_message_type()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var pings = new List<int>();
        var pongs = new List<int>();
        bus.Subscribe<Ping>(p => pings.Add(p.N));
        bus.Subscribe<Pong>(p => pongs.Add(p.N));

        bus.Publish(new Ping(1));
        bus.Publish(new Pong(2));

        pings.ShouldBe([1]);
        pongs.ShouldBe([2]);
    }

    [Fact]
    public void Disposing_the_subscription_stops_delivery()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var received = 0;
        var subscription = bus.Subscribe<Ping>(_ => received++);

        bus.Publish(new Ping(1));
        subscription.Dispose();
        bus.Publish(new Ping(2));

        received.ShouldBe(1);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_others()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var received = 0;
        bus.Subscribe<Ping>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<Ping>(_ => received++);

        Should.NotThrow(() => bus.Publish(new Ping(1)));

        received.ShouldBe(1);
    }

    [Fact]
    public void Publishing_from_inside_a_handler_is_allowed()
    {
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var pongs = new List<int>();
        bus.Subscribe<Ping>(p => bus.Publish(new Pong(p.N * 10)));
        bus.Subscribe<Pong>(p => pongs.Add(p.N));

        bus.Publish(new Ping(4));

        pongs.ShouldBe([40]);
    }
}
```

- [ ] **Step 2: Write the failing session engine tests**

`tests/CodeSwitchX.Core.Tests/Sessions/SessionEngineTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Core.Tests.Sessions;

public class SessionEngineTests
{
    private static readonly Guid AppId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly WorkspaceResolver _resolver = new();
    private readonly List<SessionChanged> _changes = [];
    private readonly SessionEngine _engine;

    public SessionEngineTests()
    {
        _resolver.SetRoots([new WorkspaceRoot(AppId, @"C:\Repo\App")]);
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
        _bus.Subscribe<SessionChanged>(_changes.Add);
        _engine.Start();
    }

    private HookEvent Hook(string name, SessionSignal? signal, string session = "s1", string? cwd = @"C:\Repo\App\src",
        string? prompt = null, string? tool = null, IReadOnlyList<ProcessRef>? chain = null) => new()
    {
        SessionId = session,
        EventName = name,
        Signal = signal,
        At = _time.GetUtcNow(),
        Cwd = cwd,
        TranscriptPath = @"C:\Users\me\.claude\projects\c--repo-app\s1.jsonl",
        Prompt = prompt,
        ToolName = tool,
        ParentChain = chain ?? [],
    };

    [Fact]
    public void SessionStart_creates_an_idle_session_mapped_to_its_workspace()
    {
        _bus.Publish(new HookEventReceived(Hook("SessionStart", SessionSignal.SessionStart)));

        var snapshot = _engine.Get("s1").ShouldNotBeNull();
        snapshot.State.ShouldBe(SessionState.Idle);
        snapshot.WorkspaceId.ShouldBe(AppId);
        snapshot.Inferred.ShouldBeFalse();
        snapshot.StartedAt.ShouldBe(_time.GetUtcNow());
        _changes.Count.ShouldBe(1);
        _changes[0].Previous.ShouldBeNull();
        _changes[0].Current.ShouldBe(snapshot);
    }

    [Fact]
    public void Prompt_submit_sets_the_title_once_trimmed_to_60_characters()
    {
        var longPrompt = new string('x', 100);
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit, prompt: longPrompt));
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit, prompt: "second prompt"));

        var snapshot = _engine.Get("s1")!;
        snapshot.State.ShouldBe(SessionState.Working);
        snapshot.Title!.Length.ShouldBe(60);
        snapshot.Title.ShouldEndWith("…");
        snapshot.Title.ShouldStartWith("xxx");
    }

    [Fact]
    public void Tool_use_records_the_tool_name()
    {
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, tool: "Bash"));

        _engine.Get("s1")!.LastToolName.ShouldBe("Bash");
    }

    [Fact]
    public void Unknown_event_is_recorded_but_does_not_change_state()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));
        _time.Advance(TimeSpan.FromMinutes(1));
        _changes.Clear();

        _engine.Apply(Hook("SomeFutureEvent", signal: null));

        var snapshot = _engine.Get("s1")!;
        snapshot.State.ShouldBe(SessionState.Idle);
        snapshot.LastEventAt.ShouldBe(_time.GetUtcNow());
        _changes.Count.ShouldBe(1, "LastEventAt changed, so a snapshot was published");
    }

    [Fact]
    public void Idle_becomes_stale_after_30_minutes_without_events()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));

        _time.Advance(TimeSpan.FromMinutes(29));
        _engine.SweepStale();
        _engine.Get("s1")!.State.ShouldBe(SessionState.Idle);

        _time.Advance(TimeSpan.FromMinutes(1));
        _engine.SweepStale();
        _engine.Get("s1")!.State.ShouldBe(SessionState.Stale);
        _engine.Get("s1")!.StateSince.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public void The_stale_sweep_runs_on_the_timer()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));

        _time.Advance(TimeSpan.FromMinutes(31));

        _engine.Get("s1")!.State.ShouldBe(SessionState.Stale);
    }

    [Fact]
    public void Sessions_seen_before_their_workspace_is_registered_are_re_resolved()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart, cwd: @"D:\Late\Project"));
        _engine.Get("s1")!.WorkspaceId.ShouldBeNull();

        var lateId = Guid.NewGuid();
        _resolver.SetRoots([new WorkspaceRoot(AppId, @"C:\Repo\App"), new WorkspaceRoot(lateId, @"D:\Late\Project")]);
        _bus.Publish(new WorkspaceRootsChanged());

        _engine.Get("s1")!.WorkspaceId.ShouldBe(lateId);
    }

    [Fact]
    public void Transcript_updates_create_inferred_sessions_until_a_hook_event_arrives()
    {
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s2",
            TranscriptPath = @"C:\t\s2.jsonl",
            ObservedAt = _time.GetUtcNow(),
            Title = "Fix the build",
            Cwd = @"C:\Repo\App",
            Model = "claude-sonnet-5",
            LastActivityAt = _time.GetUtcNow(),
            InferredSignal = SessionSignal.ToolUse,
            LatestContext = new TokenUsage(1000, 50, 200, 3000),
        }));

        var inferred = _engine.Get("s2").ShouldNotBeNull();
        inferred.Inferred.ShouldBeTrue();
        inferred.State.ShouldBe(SessionState.Working);
        inferred.Title.ShouldBe("Fix the build");
        inferred.WorkspaceId.ShouldBe(AppId);
        inferred.Model.ShouldBe("claude-sonnet-5");
        inferred.LatestContext.ContextTokens.ShouldBe(4200);

        _engine.Apply(Hook("Stop", SessionSignal.Stop, session: "s2"));

        var confirmed = _engine.Get("s2")!;
        confirmed.Inferred.ShouldBeFalse();
        confirmed.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Inferred_signals_are_ignored_once_hooks_have_spoken()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));

        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1",
            TranscriptPath = @"C:\t\s1.jsonl",
            ObservedAt = _time.GetUtcNow(),
            InferredSignal = SessionSignal.Stop,
        });

        _engine.Get("s1")!.State.ShouldBe(SessionState.Working);
    }

    [Fact]
    public void A_transcript_summary_title_replaces_a_prompt_title_but_not_a_user_rename()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit, prompt: "first prompt"));
        _engine.Apply(new TranscriptUpdate { SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), Title = "Summary title" });
        _engine.Get("s1")!.Title.ShouldBe("Summary title");

        _engine.Rename("s1", "My chat");
        _engine.Apply(new TranscriptUpdate { SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(), Title = "Another summary" });

        _engine.Get("s1")!.Title.ShouldBe("My chat");
        _engine.Get("s1")!.TitleLocked.ShouldBeTrue();
    }

    [Fact]
    public void Claude_pid_is_the_first_non_shell_ancestor()
    {
        var chain = new List<ProcessRef>
        {
            new(100, "cmd.exe"), new(200, "claude.exe"), new(300, "Code.exe"), new(400, "explorer.exe"),
        };
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, chain: chain));

        _engine.Get("s1")!.ClaudePid.ShouldBe(200);
    }

    [Fact]
    public void Claude_pid_prefers_node_or_claude_when_present_anywhere_in_the_chain()
    {
        var chain = new List<ProcessRef> { new(100, "bash.exe"), new(150, "sh.exe"), new(200, "node.exe"), new(300, "Code.exe") };
        _engine.Apply(Hook("PreToolUse", SessionSignal.ToolUse, chain: chain));

        _engine.Get("s1")!.ClaudePid.ShouldBe(200);
    }

    [Fact]
    public void Process_gone_marks_live_sessions_errored_but_leaves_ended_ones()
    {
        _engine.Apply(Hook("UserPromptSubmit", SessionSignal.PromptSubmit));
        _engine.Apply(Hook("SessionEnd", SessionSignal.SessionEnd, session: "s9"));

        _engine.MarkProcessGone("s1");
        _engine.MarkProcessGone("s9");

        _engine.Get("s1")!.State.ShouldBe(SessionState.Errored);
        _engine.Get("s9")!.State.ShouldBe(SessionState.Ended);
    }

    [Fact]
    public void Restore_loads_snapshots_without_publishing()
    {
        var persisted = new SessionSnapshot
        {
            SessionId = "old",
            State = SessionState.Idle,
            StartedAt = _time.GetUtcNow().AddHours(-2),
            LastEventAt = _time.GetUtcNow().AddHours(-1),
            StateSince = _time.GetUtcNow().AddHours(-1),
        };

        _engine.Restore([persisted]);

        _engine.Get("old").ShouldBe(persisted);
        _changes.ShouldBeEmpty();
    }

    [Fact]
    public void Usage_deltas_update_latest_context_and_last_event_time()
    {
        _engine.Apply(Hook("SessionStart", SessionSignal.SessionStart));
        var later = _time.GetUtcNow().AddMinutes(2);

        _engine.Apply(new TranscriptUpdate
        {
            SessionId = "s1",
            TranscriptPath = "p",
            ObservedAt = later,
            LastActivityAt = later,
            Usage = [new UsageDelta("claude-sonnet-5", later, new TokenUsage(10, 20, 30, 40))],
            LatestContext = new TokenUsage(10, 20, 30, 40),
        });

        var snapshot = _engine.Get("s1")!;
        snapshot.LatestContext.ShouldBe(new TokenUsage(10, 20, 30, 40));
        snapshot.LastEventAt.ShouldBe(later);
        snapshot.Model.ShouldBe("claude-sonnet-5");
    }
}
```

`tests/CodeSwitchX.Core.Tests/Sessions/ProcessLivenessMonitorTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.Core.Tests.Sessions;

public class ProcessLivenessMonitorTests
{
    [Fact]
    public void Tick_marks_sessions_whose_claude_process_is_gone()
    {
        var time = new FakeTimeProvider();
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var engine = new SessionEngine(bus, new WorkspaceResolver(), time, NullLogger<SessionEngine>.Instance);
        engine.Apply(new HookEvent
        {
            SessionId = "alive", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(10, "claude.exe")],
        });
        engine.Apply(new HookEvent
        {
            SessionId = "dead", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = time.GetUtcNow(),
            ParentChain = [new ProcessRef(20, "claude.exe")],
        });
        var probe = Substitute.For<IProcessProbe>();
        probe.IsAlive(10).Returns(true);
        probe.IsAlive(20).Returns(false);
        var monitor = new ProcessLivenessMonitor(engine, probe, time, NullLogger<ProcessLivenessMonitor>.Instance);

        monitor.Tick();

        engine.Get("alive")!.State.ShouldBe(SessionState.Working);
        engine.Get("dead")!.State.ShouldBe(SessionState.Errored);
    }

    [Fact]
    public void SystemProcessProbe_reports_the_current_process_alive_and_a_bogus_pid_dead()
    {
        var probe = new SystemProcessProbe();

        probe.IsAlive(Environment.ProcessId).ShouldBeTrue();
        probe.IsAlive(int.MaxValue - 1).ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Core.Tests`
Expected: build errors for the missing `Messaging` and `Sessions` types.

- [ ] **Step 4: Implement the bus and messages**

`src/CodeSwitchX.Core/Messaging/IEventBus.cs`:

```csharp
namespace CodeSwitchX.Core.Messaging;

/// <summary>In-process, synchronous publish/subscribe. Handlers run on the publisher's thread.</summary>
public interface IEventBus
{
    void Publish<T>(T message) where T : class;
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
}
```

`src/CodeSwitchX.Core/Messaging/EventBus.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Messaging;

public sealed class EventBus : IEventBus
{
    private readonly ILogger<EventBus> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = [];

    public EventBus(ILogger<EventBus> logger)
    {
        _logger = logger;
    }

    public void Publish<T>(T message) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        Subscription[] targets;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var list) || list.Count == 0)
            {
                return;
            }

            targets = list.ToArray();
        }

        foreach (var target in targets)
        {
            try
            {
                ((Action<T>)target.Handler)(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler for {MessageType} threw", typeof(T).Name);
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, typeof(T), handler);
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _subscriptions[typeof(T)] = list;
            }

            list.Add(subscription);
        }

        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.MessageType, out var list))
            {
                list.Remove(subscription);
            }
        }
    }

    private sealed class Subscription(EventBus owner, Type messageType, Delegate handler) : IDisposable
    {
        public Type MessageType { get; } = messageType;
        public Delegate Handler { get; } = handler;

        public void Dispose() => owner.Remove(this);
    }
}
```

`src/CodeSwitchX.Core/Messaging/Messages.cs`:

```csharp
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Messaging;

public sealed record HookEventReceived(HookEvent Event);

public sealed record TranscriptUpdated(TranscriptUpdate Update);

public sealed record SessionChanged(SessionSnapshot? Previous, SessionSnapshot Current);

public sealed record WorkspaceRegistered(Workspace Workspace);

public sealed record WorkspaceUnregistered(Guid WorkspaceId);

/// <summary>Raised after the resolver's roots were replaced; consumers re-map anything keyed by path.</summary>
public sealed record WorkspaceRootsChanged;
```

- [ ] **Step 5: Implement the session records**

`src/CodeSwitchX.Core/Sessions/ProcessRef.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public readonly record struct ProcessRef(int Pid, string Name);
```

`src/CodeSwitchX.Core/Sessions/HookEvent.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

/// <summary>A Claude Code hook payload after normalisation. Unknown events keep <see cref="Signal"/> null.</summary>
public sealed record HookEvent
{
    public required string SessionId { get; init; }
    public required string EventName { get; init; }
    public SessionSignal? Signal { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public string? ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public string? NotificationType { get; init; }
    public string? Message { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }

    /// <summary>SessionStart <c>source</c> or SessionEnd <c>reason</c>.</summary>
    public string? Source { get; init; }
    public int? RelayPid { get; init; }
    public IReadOnlyList<ProcessRef> ParentChain { get; init; } = [];
    public string? RawJson { get; init; }
}
```

`src/CodeSwitchX.Core/Sessions/TokenUsage.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public readonly record struct TokenUsage(long Input, long Output, long CacheWrite, long CacheRead)
{
    public static TokenUsage Zero => default;

    /// <summary>Tokens occupying the context window on the latest turn: fresh input plus everything read or written to cache.</summary>
    public long ContextTokens => Input + CacheWrite + CacheRead;

    public long Total => Input + Output + CacheWrite + CacheRead;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead);
}
```

`src/CodeSwitchX.Core/Sessions/UsageDelta.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public readonly record struct UsageDelta(string Model, DateTimeOffset At, TokenUsage Tokens);
```

`src/CodeSwitchX.Core/Sessions/TranscriptUpdate.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

/// <summary>Facts the transcript indexer extracted from newly appended JSONL lines of one session.</summary>
public sealed record TranscriptUpdate
{
    public required string SessionId { get; init; }
    public required string TranscriptPath { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public IReadOnlyList<UsageDelta> Usage { get; init; } = [];
    public TokenUsage? LatestContext { get; init; }

    /// <summary>Best-effort state signal, only honoured while the session has never received a hook event.</summary>
    public SessionSignal? InferredSignal { get; init; }
}
```

`src/CodeSwitchX.Core/Sessions/SessionSnapshot.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public sealed record SessionSnapshot
{
    public required string SessionId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public bool TitleLocked { get; init; }
    public required SessionState State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastEventAt { get; init; }
    public required DateTimeOffset StateSince { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public string? Model { get; init; }
    public string? LastToolName { get; init; }
    public string? LastNotification { get; init; }

    /// <summary>True while every fact came from transcript inference and no hook event was ever seen.</summary>
    public bool Inferred { get; init; }
    public int? ClaudePid { get; init; }
    public TokenUsage LatestContext { get; init; }
}
```

`src/CodeSwitchX.Core/Sessions/SessionEngineOptions.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public sealed class SessionEngineOptions
{
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int TitleMaxLength { get; set; } = 60;
}
```

- [ ] **Step 6: Implement the engine**

`src/CodeSwitchX.Core/Sessions/SessionEngine.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>Reconciles hook events, transcript facts and liveness into one <see cref="SessionSnapshot"/> per chat.</summary>
public sealed class SessionEngine : IDisposable
{
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "sh", "bash", "zsh", "conhost", "wt",
    };

    private static readonly HashSet<string> ClaudeNames = new(StringComparer.OrdinalIgnoreCase) { "claude", "node" };

    private readonly IEventBus _bus;
    private readonly IWorkspaceResolver _resolver;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionEngine> _logger;
    private readonly SessionEngineOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _sweepTimer;

    public SessionEngine(IEventBus bus, IWorkspaceResolver resolver, TimeProvider time, ILogger<SessionEngine> logger,
        SessionEngineOptions? options = null)
    {
        _bus = bus;
        _resolver = resolver;
        _time = time;
        _logger = logger;
        _options = options ?? new SessionEngineOptions();
    }

    public IReadOnlyCollection<SessionSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.ToArray();
            }
        }
    }

    public SessionSnapshot? Get(string sessionId)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    public void Restore(IEnumerable<SessionSnapshot> persisted)
    {
        lock (_gate)
        {
            foreach (var snapshot in persisted)
            {
                _sessions[snapshot.SessionId] = snapshot;
            }
        }
    }

    public void Start()
    {
        _subscriptions.Add(_bus.Subscribe<HookEventReceived>(m => Apply(m.Event)));
        _subscriptions.Add(_bus.Subscribe<TranscriptUpdated>(m => Apply(m.Update)));
        _subscriptions.Add(_bus.Subscribe<WorkspaceRootsChanged>(_ => ReResolveWorkspaces()));
        _sweepTimer = _time.CreateTimer(_ => SweepStale(), null, _options.SweepInterval, _options.SweepInterval);
    }

    public void Apply(HookEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            previous = _sessions.GetValueOrDefault(e.SessionId);
            var s = previous ?? NewSession(e.SessionId, e.At);

            var state = s.State;
            if (e.Signal is { } signal && SessionStateMachine.TryNext(state, signal, out var next))
            {
                state = next;
            }
            else if (e.Signal is null)
            {
                _logger.LogInformation("Unknown hook event {EventName} for session {SessionId}", e.EventName, e.SessionId);
            }

            var cwd = e.Cwd ?? s.Cwd;
            current = s with
            {
                State = state,
                StateSince = state == s.State && previous is not null ? s.StateSince : e.At,
                LastEventAt = e.At > s.LastEventAt ? e.At : s.LastEventAt,
                Cwd = cwd,
                WorkspaceId = cwd != s.Cwd || s.WorkspaceId is null ? _resolver.Resolve(cwd) : s.WorkspaceId,
                TranscriptPath = e.TranscriptPath ?? s.TranscriptPath,
                Model = e.Model ?? s.Model,
                LastToolName = e.ToolName ?? s.LastToolName,
                LastNotification = e.Signal == SessionSignal.Notification ? e.Message ?? e.NotificationType : s.LastNotification,
                Title = s.Title ?? TitleFromPrompt(e.Prompt),
                Inferred = false,
                ClaudePid = PickClaudePid(e.ParentChain) ?? s.ClaudePid,
            };
            _sessions[e.SessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    public void Apply(TranscriptUpdate u)
    {
        ArgumentNullException.ThrowIfNull(u);
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            previous = _sessions.GetValueOrDefault(u.SessionId);
            var s = previous ?? (NewSession(u.SessionId, u.LastActivityAt ?? u.ObservedAt) with { Inferred = true });

            var state = s.State;
            var stateSince = s.StateSince;
            if (s.Inferred && u.InferredSignal is { } signal && SessionStateMachine.TryNext(state, signal, out var next))
            {
                state = next;
                stateSince = u.LastActivityAt ?? u.ObservedAt;
            }

            var cwd = s.Cwd ?? u.Cwd;
            var model = u.Usage.Count > 0 ? u.Usage[^1].Model : u.Model ?? s.Model;
            var lastEvent = u.LastActivityAt is { } activity && activity > s.LastEventAt ? activity : s.LastEventAt;
            current = s with
            {
                State = state,
                StateSince = stateSince,
                LastEventAt = lastEvent,
                Cwd = cwd,
                WorkspaceId = s.WorkspaceId ?? _resolver.Resolve(cwd),
                TranscriptPath = s.TranscriptPath ?? u.TranscriptPath,
                Model = model,
                Title = s.TitleLocked ? s.Title : u.Title ?? s.Title,
                LatestContext = u.LatestContext ?? s.LatestContext,
            };
            _sessions[u.SessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    public void MarkProcessGone(string sessionId) => Signal(sessionId, SessionSignal.ProcessGone);

    public void Rename(string sessionId, string title)
    {
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous))
            {
                return;
            }

            current = previous with { Title = title, TitleLocked = true };
            _sessions[sessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    public void SweepStale()
    {
        var now = _time.GetUtcNow();
        List<string> stale;
        lock (_gate)
        {
            stale = _sessions.Values
                .Where(s => s.State == SessionState.Idle && now - s.LastEventAt >= _options.StaleAfter)
                .Select(s => s.SessionId)
                .ToList();
        }

        foreach (var id in stale)
        {
            Signal(id, SessionSignal.StaleTimeout);
        }
    }

    public void ReResolveWorkspaces()
    {
        var changes = new List<(SessionSnapshot Previous, SessionSnapshot Current)>();
        lock (_gate)
        {
            foreach (var (id, s) in _sessions.ToArray())
            {
                var resolved = _resolver.Resolve(s.Cwd);
                if (resolved != s.WorkspaceId)
                {
                    var updated = s with { WorkspaceId = resolved };
                    _sessions[id] = updated;
                    changes.Add((s, updated));
                }
            }
        }

        foreach (var (previous, current) in changes)
        {
            _bus.Publish(new SessionChanged(previous, current));
        }
    }

    public void Dispose()
    {
        _sweepTimer?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private void Signal(string sessionId, SessionSignal signal)
    {
        SessionSnapshot? previous;
        SessionSnapshot current;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out previous))
            {
                return;
            }

            if (!SessionStateMachine.TryNext(previous.State, signal, out var next))
            {
                return;
            }

            current = previous with { State = next, StateSince = _time.GetUtcNow() };
            _sessions[sessionId] = current;
        }

        PublishIfChanged(previous, current);
    }

    private SessionSnapshot NewSession(string sessionId, DateTimeOffset at) => new()
    {
        SessionId = sessionId,
        State = SessionState.Starting,
        StartedAt = at,
        LastEventAt = at,
        StateSince = at,
    };

    private void PublishIfChanged(SessionSnapshot? previous, SessionSnapshot current)
    {
        if (previous is null || previous != current)
        {
            _bus.Publish(new SessionChanged(previous, current));
        }
    }

    private string? TitleFromPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var singleLine = string.Join(' ', prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= _options.TitleMaxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, _options.TitleMaxLength - 1), "…");
    }

    private static int? PickClaudePid(IReadOnlyList<ProcessRef> chain)
    {
        if (chain.Count == 0)
        {
            return null;
        }

        foreach (var p in chain)
        {
            if (ClaudeNames.Contains(BaseName(p.Name)))
            {
                return p.Pid;
            }
        }

        foreach (var p in chain)
        {
            if (!ShellNames.Contains(BaseName(p.Name)))
            {
                return p.Pid;
            }
        }

        return null;
    }

    private static string BaseName(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
}
```

`src/CodeSwitchX.Core/Sessions/IProcessProbe.cs`, `SystemProcessProbe.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public interface IProcessProbe
{
    bool IsAlive(int pid);
}
```

```csharp
using System.Diagnostics;

namespace CodeSwitchX.Core.Sessions;

public sealed class SystemProcessProbe : IProcessProbe
{
    public bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
```

`src/CodeSwitchX.Core/Sessions/ProcessLivenessMonitor.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Core.Sessions;

/// <summary>Marks a session Errored when the claude process recorded from the relay's parent chain disappears.</summary>
public sealed class ProcessLivenessMonitor : BackgroundService
{
    private readonly SessionEngine _engine;
    private readonly IProcessProbe _probe;
    private readonly TimeProvider _time;
    private readonly ILogger<ProcessLivenessMonitor> _logger;

    public ProcessLivenessMonitor(SessionEngine engine, IProcessProbe probe, TimeProvider time, ILogger<ProcessLivenessMonitor> logger)
    {
        _engine = engine;
        _probe = probe;
        _time = time;
        _logger = logger;
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Tick();
        }
    }

    internal void Tick()
    {
        foreach (var snapshot in _engine.Snapshots)
        {
            if (snapshot.ClaudePid is not { } pid || !SessionStateMachine.IsLive(snapshot.State))
            {
                continue;
            }

            if (!_probe.IsAlive(pid))
            {
                _logger.LogInformation("Process {Pid} for session {SessionId} is gone", pid, snapshot.SessionId);
                _engine.MarkProcessGone(snapshot.SessionId);
            }
        }
    }
}
```

Add `<InternalsVisibleTo Include="CodeSwitchX.Core.Tests" />` to `CodeSwitchX.Core.csproj` so the test can call `Tick()`.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Core.Tests`
Expected: all pass (the timer test relies on `FakeTimeProvider.Advance` firing the `ITimer` created in `Start()`).

---

### Task 5: Persistence records, store interfaces, DbContext and migration

**Files:**
- Create: `src/CodeSwitchX.Core/Persistence/SessionRecord.cs`, `SessionEventRecord.cs`, `UsageBucket.cs`, `TranscriptCursor.cs`, `PricingRule.cs`, `Setting.cs`, `IWorkspaceStore.cs`, `ISessionStore.cs`, `IUsageStore.cs`, `ISettingsStore.cs`, `DuplicateWorkspaceException.cs`
- Create: `src/CodeSwitchX.Data/CodeSwitchXDbContext.cs`, `UtcTicksConverter.cs`, `DesignTimeDbContextFactory.cs`, `DatabaseInitializer.cs`, `Stores/WorkspaceStore.cs`, `Stores/SessionStore.cs`, `Stores/UsageStore.cs`, `Stores/SettingsStore.cs`, `DataServiceCollectionExtensions.cs`, `Migrations/*` (generated)
- Create: `.config/dotnet-tools.json` (local `dotnet-ef` 10.0.12)
- Test: `tests/CodeSwitchX.Data.Tests/SqliteFixture.cs`, `WorkspaceStoreTests.cs`, `SessionStoreTests.cs`, `UsageStoreTests.cs`, `SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `Workspace`, `Track`, `Worktree`, `SessionSnapshot`, `SessionState`, `TokenUsage` (Tasks 3, 4).
- Produces (all in `CodeSwitchX.Core.Persistence`):
  - `SessionRecord` (mutable entity mirroring `SessionSnapshot`; `static FromSnapshot(SessionSnapshot)`, `ToSnapshot()`, `UpdateFrom(SessionSnapshot)`).
  - `SessionEventRecord { long Id; string SessionId; string Kind; string? ToolName; DateTimeOffset At; string? PayloadJson }`.
  - `UsageBucket { string SessionId; string Model; DateTimeOffset MinuteUtc; long Input, Output, CacheWrite, CacheRead }` with `static DateTimeOffset FloorToMinute(DateTimeOffset)`.
  - `TranscriptCursor { string Path; long ByteOffset; DateTimeOffset LastWriteUtc; string? SessionId }`.
  - `PricingRule { string Model; string? DisplayName; decimal InputPerM, OutputPerM, CacheWritePerM, CacheReadPerM; long ContextWindow }`.
  - `Setting { string Key; string ValueJson }`.
  - `IWorkspaceStore`, `ISessionStore`, `IUsageStore`, `ISettingsStore` (signatures in Step 4), `DuplicateWorkspaceException`.
  - `CodeSwitchX.Data.DataServiceCollectionExtensions.AddCodeSwitchXData(this IServiceCollection, string databaseFile)`, `DatabaseInitializer.InitializeAsync(CancellationToken)`.

- [ ] **Step 1: Write the records and store interfaces in Core**

`src/CodeSwitchX.Core/Persistence/SessionRecord.cs`:

```csharp
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Persistence;

public sealed class SessionRecord
{
    public string Id { get; set; } = string.Empty;
    public Guid? WorkspaceId { get; set; }
    public string? Title { get; set; }
    public bool TitleLocked { get; set; }
    public SessionState State { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public DateTimeOffset StateSince { get; set; }
    public string? Cwd { get; set; }
    public string? TranscriptPath { get; set; }
    public string? Model { get; set; }
    public string? LastToolName { get; set; }
    public string? LastNotification { get; set; }
    public bool Inferred { get; set; }
    public int? ClaudePid { get; set; }
    public long ContextInput { get; set; }
    public long ContextOutput { get; set; }
    public long ContextCacheWrite { get; set; }
    public long ContextCacheRead { get; set; }

    public static SessionRecord FromSnapshot(SessionSnapshot s)
    {
        var record = new SessionRecord { Id = s.SessionId };
        record.UpdateFrom(s);
        return record;
    }

    public void UpdateFrom(SessionSnapshot s)
    {
        WorkspaceId = s.WorkspaceId;
        Title = s.Title;
        TitleLocked = s.TitleLocked;
        State = s.State;
        StartedAt = s.StartedAt;
        LastEventAt = s.LastEventAt;
        StateSince = s.StateSince;
        Cwd = s.Cwd;
        TranscriptPath = s.TranscriptPath;
        Model = s.Model;
        LastToolName = s.LastToolName;
        LastNotification = s.LastNotification;
        Inferred = s.Inferred;
        ClaudePid = s.ClaudePid;
        ContextInput = s.LatestContext.Input;
        ContextOutput = s.LatestContext.Output;
        ContextCacheWrite = s.LatestContext.CacheWrite;
        ContextCacheRead = s.LatestContext.CacheRead;
    }

    public SessionSnapshot ToSnapshot() => new()
    {
        SessionId = Id,
        WorkspaceId = WorkspaceId,
        Title = Title,
        TitleLocked = TitleLocked,
        State = State,
        StartedAt = StartedAt,
        LastEventAt = LastEventAt,
        StateSince = StateSince,
        Cwd = Cwd,
        TranscriptPath = TranscriptPath,
        Model = Model,
        LastToolName = LastToolName,
        LastNotification = LastNotification,
        Inferred = Inferred,
        ClaudePid = ClaudePid,
        LatestContext = new TokenUsage(ContextInput, ContextOutput, ContextCacheWrite, ContextCacheRead),
    };
}
```

`src/CodeSwitchX.Core/Persistence/SessionEventRecord.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public sealed class SessionEventRecord
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The hook event name, e.g. <c>PreToolUse</c>.</summary>
    public string Kind { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Raw payload; only stored when the user opted in.</summary>
    public string? PayloadJson { get; set; }
}
```

`src/CodeSwitchX.Core/Persistence/UsageBucket.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public sealed class UsageBucket
{
    public string SessionId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset MinuteUtc { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite { get; set; }
    public long CacheRead { get; set; }

    public static DateTimeOffset FloorToMinute(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}
```

`src/CodeSwitchX.Core/Persistence/TranscriptCursor.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public sealed class TranscriptCursor
{
    /// <summary>Normalised transcript path (primary key).</summary>
    public string Path { get; set; } = string.Empty;
    public long ByteOffset { get; set; }
    public DateTimeOffset LastWriteUtc { get; set; }
    public string? SessionId { get; set; }
}
```

`src/CodeSwitchX.Core/Persistence/PricingRule.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

/// <summary>Per-model prices in USD per million tokens. <see cref="Model"/> is matched as a prefix of the transcript model id.</summary>
public sealed class PricingRule
{
    public string Model { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public decimal InputPerM { get; set; }
    public decimal OutputPerM { get; set; }
    public decimal CacheWritePerM { get; set; }
    public decimal CacheReadPerM { get; set; }
    public long ContextWindow { get; set; } = 200_000;
}
```

`src/CodeSwitchX.Core/Persistence/Setting.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public sealed class Setting
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "null";
}
```

`src/CodeSwitchX.Core/Persistence/DuplicateWorkspaceException.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public sealed class DuplicateWorkspaceException(string rootPath)
    : InvalidOperationException($"A workspace with root '{rootPath}' is already registered.")
{
    public string RootPath { get; } = rootPath;
}
```

`src/CodeSwitchX.Core/Persistence/IWorkspaceStore.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Persistence;

public interface IWorkspaceStore
{
    Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default);
    Task<Workspace?> FindByRootAsync(string normalizedRoot, CancellationToken ct = default);
    Task AddAsync(Workspace workspace, CancellationToken ct = default);
    Task UpdateAsync(Workspace workspace, CancellationToken ct = default);
    Task RemoveAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken ct = default);
    Task<Track> AddTrackAsync(string name, CancellationToken ct = default);
    Task UpdateTrackAsync(Track track, CancellationToken ct = default);
}
```

`src/CodeSwitchX.Core/Persistence/ISessionStore.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public interface ISessionStore
{
    Task<IReadOnlyList<SessionRecord>> GetActiveSinceAsync(DateTimeOffset lastEventAfter, CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyCollection<SessionRecord> records, CancellationToken ct = default);
    Task AppendEventsAsync(IReadOnlyCollection<SessionEventRecord> events, CancellationToken ct = default);
    Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, int limit, CancellationToken ct = default);
    Task<int> PruneEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
```

`src/CodeSwitchX.Core/Persistence/IUsageStore.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public interface IUsageStore
{
    /// <summary>Adds each delta to the bucket with the same (SessionId, Model, MinuteUtc), creating it when missing.</summary>
    Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default);
    Task<IReadOnlyList<UsageBucket>> GetBucketsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
    Task<IReadOnlyList<TranscriptCursor>> GetCursorsAsync(CancellationToken ct = default);
    Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default);
}
```

`src/CodeSwitchX.Core/Persistence/ISettingsStore.cs`:

```csharp
namespace CodeSwitchX.Core.Persistence;

public interface ISettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
    Task<IReadOnlyList<PricingRule>> GetPricingAsync(CancellationToken ct = default);
    Task UpsertPricingAsync(IReadOnlyCollection<PricingRule> rules, CancellationToken ct = default);

    /// <summary>Inserts the given rules whose <see cref="PricingRule.Model"/> is not present yet; existing rows are never touched.</summary>
    Task EnsurePricingDefaultsAsync(IReadOnlyCollection<PricingRule> defaults, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing store tests**

`tests/CodeSwitchX.Data.Tests/SqliteFixture.cs`:

```csharp
using CodeSwitchX.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Data.Tests;

/// <summary>A fresh migrated SQLite file per test class instance, deleted on dispose.</summary>
public sealed class SqliteFixture : IAsyncDisposable
{
    public SqliteFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "csx-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DatabaseFile = Path.Combine(Root, "test.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeSwitchXData(DatabaseFile);
        Services = services.BuildServiceProvider();
    }

    public string Root { get; }
    public string DatabaseFile { get; }
    public ServiceProvider Services { get; }

    public async Task InitializeAsync()
    {
        await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort; the temp folder is cleaned by the OS eventually
        }
    }
}
```

`tests/CodeSwitchX.Data.Tests/WorkspaceStoreTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Data.Tests;

public class WorkspaceStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private IWorkspaceStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<IWorkspaceStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Initializer_seeds_a_default_track()
    {
        var tracks = await _store.GetTracksAsync();

        tracks.Count.ShouldBe(1);
        tracks[0].Name.ShouldBe("General");
    }

    [Fact]
    public async Task Workspaces_round_trip_with_their_worktrees()
    {
        var track = (await _store.GetTracksAsync())[0];
        var workspace = new Workspace
        {
            Name = "App",
            RootPath = @"c:\repo\app",
            WorkspaceFile = @"c:\repo\app\app.code-workspace",
            TrackId = track.Id,
            AccentColor = "#FF8800",
            HostMode = HostMode.Snap,
            AutoStart = true,
            Worktrees = { new Worktree { Path = @"c:\repo\app-wt", Branch = "feature" } },
        };

        await _store.AddAsync(workspace);
        var loaded = (await _store.GetAllAsync()).ShouldHaveSingleItem();

        loaded.Id.ShouldBe(workspace.Id);
        loaded.Name.ShouldBe("App");
        loaded.WorkspaceFile.ShouldBe(@"c:\repo\app\app.code-workspace");
        loaded.AccentColor.ShouldBe("#FF8800");
        loaded.AutoStart.ShouldBeTrue();
        loaded.Worktrees.ShouldHaveSingleItem().Branch.ShouldBe("feature");
        loaded.CreatedAt.ShouldBe(workspace.CreatedAt);
    }

    [Fact]
    public async Task Adding_a_second_workspace_with_the_same_root_throws()
    {
        var track = (await _store.GetTracksAsync())[0];
        await _store.AddAsync(new Workspace { Name = "A", RootPath = @"c:\repo\app", TrackId = track.Id });

        await Should.ThrowAsync<DuplicateWorkspaceException>(
            () => _store.AddAsync(new Workspace { Name = "B", RootPath = @"c:\repo\app", TrackId = track.Id }));
    }

    [Fact]
    public async Task FindByRoot_Update_and_Remove_work()
    {
        var track = (await _store.GetTracksAsync())[0];
        var workspace = new Workspace { Name = "A", RootPath = @"c:\repo\app", TrackId = track.Id };
        await _store.AddAsync(workspace);

        (await _store.FindByRootAsync(@"c:\repo\app")).ShouldNotBeNull().Id.ShouldBe(workspace.Id);

        workspace.Name = "Renamed";
        workspace.Worktrees.Add(new Worktree { Path = @"c:\repo\app-2", Branch = "b2" });
        await _store.UpdateAsync(workspace);
        var updated = (await _store.GetAllAsync()).ShouldHaveSingleItem();
        updated.Name.ShouldBe("Renamed");
        updated.Worktrees.Count.ShouldBe(1);

        await _store.RemoveAsync(workspace.Id);
        (await _store.GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tracks_can_be_added_and_renamed()
    {
        var track = await _store.AddTrackAsync("Client work");
        track.SortOrder.ShouldBe(1);

        track.Name = "Clients";
        await _store.UpdateTrackAsync(track);

        (await _store.GetTracksAsync()).Select(t => t.Name).ShouldBe(["General", "Clients"]);
    }
}
```

`tests/CodeSwitchX.Data.Tests/SessionStoreTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Data.Tests;

public class SessionStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private ISessionStore _store = null!;
    private readonly DateTimeOffset _now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<ISessionStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private SessionSnapshot Snapshot(string id, SessionState state, DateTimeOffset lastEvent) => new()
    {
        SessionId = id,
        State = state,
        StartedAt = lastEvent.AddMinutes(-10),
        LastEventAt = lastEvent,
        StateSince = lastEvent,
        Title = "Title " + id,
        Model = "claude-sonnet-5",
        LatestContext = new TokenUsage(1, 2, 3, 4),
    };

    [Fact]
    public async Task Upsert_inserts_then_updates_by_session_id()
    {
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Working, _now))]);
        await _store.UpsertAsync([SessionRecord.FromSnapshot(Snapshot("s1", SessionState.Idle, _now.AddMinutes(1)))]);

        var records = await _store.GetActiveSinceAsync(_now.AddHours(-1));

        var record = records.ShouldHaveSingleItem();
        record.State.ShouldBe(SessionState.Idle);
        record.LastEventAt.ShouldBe(_now.AddMinutes(1));
        record.ToSnapshot().LatestContext.ShouldBe(new TokenUsage(1, 2, 3, 4));
    }

    [Fact]
    public async Task GetActiveSince_filters_on_last_event_time()
    {
        await _store.UpsertAsync(
        [
            SessionRecord.FromSnapshot(Snapshot("old", SessionState.Ended, _now.AddDays(-3))),
            SessionRecord.FromSnapshot(Snapshot("new", SessionState.Idle, _now)),
        ]);

        (await _store.GetActiveSinceAsync(_now.AddDays(-1))).Select(r => r.Id).ShouldBe(["new"]);
    }

    [Fact]
    public async Task Events_append_query_newest_first_and_prune()
    {
        await _store.AppendEventsAsync(
        [
            new SessionEventRecord { SessionId = "s1", Kind = "PreToolUse", ToolName = "Bash", At = _now.AddDays(-20) },
            new SessionEventRecord { SessionId = "s1", Kind = "Stop", At = _now },
            new SessionEventRecord { SessionId = "s2", Kind = "Stop", At = _now },
        ]);

        var events = await _store.GetEventsAsync("s1", limit: 10);
        events.Select(e => e.Kind).ShouldBe(["Stop", "PreToolUse"]);

        (await _store.PruneEventsAsync(_now.AddDays(-14))).ShouldBe(1);
        (await _store.GetEventsAsync("s1", limit: 10)).Count.ShouldBe(1);
    }
}
```

`tests/CodeSwitchX.Data.Tests/UsageStoreTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Data.Tests;

public class UsageStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private IUsageStore _store = null!;
    private readonly DateTimeOffset _minute = new(2026, 9, 23, 12, 34, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<IUsageStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Deltas_for_the_same_key_are_summed_into_one_bucket()
    {
        await _store.AddUsageAsync([new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 10, Output = 1, CacheWrite = 5, CacheRead = 100 }]);
        await _store.AddUsageAsync([new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 5, Output = 2, CacheWrite = 0, CacheRead = 50 }]);

        var bucket = (await _store.GetBucketsAsync(_minute, _minute.AddMinutes(1))).ShouldHaveSingleItem();

        bucket.Input.ShouldBe(15);
        bucket.Output.ShouldBe(3);
        bucket.CacheWrite.ShouldBe(5);
        bucket.CacheRead.ShouldBe(150);
    }

    [Fact]
    public async Task Range_query_is_inclusive_of_from_and_exclusive_of_to()
    {
        await _store.AddUsageAsync(
        [
            new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute.AddMinutes(-1), Input = 1 },
            new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute, Input = 2 },
            new UsageBucket { SessionId = "s1", Model = "m", MinuteUtc = _minute.AddMinutes(1), Input = 3 },
            new UsageBucket { SessionId = "s2", Model = "m", MinuteUtc = _minute, Input = 4 },
        ]);

        var buckets = await _store.GetBucketsAsync(_minute, _minute.AddMinutes(1));

        buckets.Select(b => b.Input).ShouldBe([2, 4], ignoreOrder: true);
    }

    [Fact]
    public async Task FloorToMinute_drops_seconds_and_converts_to_utc()
    {
        var local = new DateTimeOffset(2026, 9, 23, 14, 34, 59, 999, TimeSpan.FromHours(2));

        UsageBucket.FloorToMinute(local).ShouldBe(_minute);
    }

    [Fact]
    public async Task Cursors_upsert_by_path()
    {
        await _store.UpsertCursorsAsync([new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 10, LastWriteUtc = _minute, SessionId = "s1" }]);
        await _store.UpsertCursorsAsync([new TranscriptCursor { Path = @"c:\t\s1.jsonl", ByteOffset = 20, LastWriteUtc = _minute.AddSeconds(5), SessionId = "s1" }]);

        var cursor = (await _store.GetCursorsAsync()).ShouldHaveSingleItem();
        cursor.ByteOffset.ShouldBe(20);
        cursor.LastWriteUtc.ShouldBe(_minute.AddSeconds(5));
    }
}
```

`tests/CodeSwitchX.Data.Tests/SettingsStoreTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Data.Tests;

public class SettingsStoreTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private ISettingsStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _store = _db.Get<ISettingsStore>();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private sealed record Hotkeys(string Toggle, int Count);

    [Fact]
    public async Task Settings_round_trip_as_json()
    {
        (await _store.GetAsync<Hotkeys>("hotkeys")).ShouldBeNull();

        await _store.SetAsync("hotkeys", new Hotkeys("Ctrl+Alt+Y", 3));
        await _store.SetAsync("budget", 1_000_000L);

        (await _store.GetAsync<Hotkeys>("hotkeys")).ShouldBe(new Hotkeys("Ctrl+Alt+Y", 3));
        (await _store.GetAsync<long?>("budget")).ShouldBe(1_000_000L);
    }

    [Fact]
    public async Task Pricing_defaults_fill_gaps_without_overwriting_user_edits()
    {
        await _store.UpsertPricingAsync([new PricingRule { Model = "claude-sonnet-5", InputPerM = 99 }]);

        await _store.EnsurePricingDefaultsAsync(
        [
            new PricingRule { Model = "claude-sonnet-5", InputPerM = 3 },
            new PricingRule { Model = "claude-haiku-4-5", InputPerM = 1 },
        ]);

        var rules = (await _store.GetPricingAsync()).ToDictionary(r => r.Model);
        rules["claude-sonnet-5"].InputPerM.ShouldBe(99);
        rules["claude-haiku-4-5"].InputPerM.ShouldBe(1);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Data.Tests`
Expected: build errors, `AddCodeSwitchXData` and the stores do not exist.

- [ ] **Step 4: Implement the DbContext, converter and design-time factory**

`src/CodeSwitchX.Data/UtcTicksConverter.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodeSwitchX.Data;

/// <summary>Stores every DateTimeOffset as UTC ticks (a 64-bit integer) so SQLite can compare and index them and values round-trip exactly.</summary>
public sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcTicksConverter()
        : base(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero))
    {
    }
}
```

`src/CodeSwitchX.Data/CodeSwitchXDbContext.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data;

public sealed class CodeSwitchXDbContext : DbContext
{
    public CodeSwitchXDbContext(DbContextOptions<CodeSwitchXDbContext> options)
        : base(options)
    {
    }

    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Worktree> Worktrees => Set<Worktree>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SessionEventRecord> SessionEvents => Set<SessionEventRecord>();
    public DbSet<UsageBucket> UsageBuckets => Set<UsageBucket>();
    public DbSet<TranscriptCursor> TranscriptCursors => Set<TranscriptCursor>();
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<decimal>().HaveConversion<double>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Track>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Workspace>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.Name).IsRequired().HasMaxLength(200);
            e.Property(w => w.RootPath).IsRequired().HasMaxLength(1024);
            e.HasIndex(w => w.RootPath).IsUnique();
            e.Property(w => w.AccentColor).IsRequired().HasMaxLength(16);
            e.Property(w => w.HostMode).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Track>().WithMany().HasForeignKey(w => w.TrackId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(w => w.Worktrees).WithOne().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(w => w.Worktrees).AutoInclude();
        });

        modelBuilder.Entity<Worktree>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Path).IsRequired().HasMaxLength(1024);
            e.HasIndex(t => t.Path);
        });

        modelBuilder.Entity<SessionRecord>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(128);
            e.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(s => s.WorkspaceId);
            e.HasIndex(s => s.LastEventAt);
        });

        modelBuilder.Entity<SessionEventRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).IsRequired().HasMaxLength(128);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(64);
            e.HasIndex(x => new { x.SessionId, x.At });
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<UsageBucket>(e =>
        {
            e.HasKey(b => new { b.SessionId, b.Model, b.MinuteUtc });
            e.Property(b => b.SessionId).HasMaxLength(128);
            e.Property(b => b.Model).HasMaxLength(128);
            e.HasIndex(b => b.MinuteUtc);
        });

        modelBuilder.Entity<TranscriptCursor>(e =>
        {
            e.HasKey(c => c.Path);
            e.Property(c => c.Path).HasMaxLength(1024);
        });

        modelBuilder.Entity<PricingRule>(e =>
        {
            e.HasKey(p => p.Model);
            e.Property(p => p.Model).HasMaxLength(128);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
            e.Property(s => s.ValueJson).IsRequired();
        });
    }
}
```

`src/CodeSwitchX.Data/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeSwitchX.Data;

/// <summary>Used only by <c>dotnet ef</c> when generating migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CodeSwitchXDbContext>
{
    public CodeSwitchXDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CodeSwitchXDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new CodeSwitchXDbContext(options);
    }
}
```

- [ ] **Step 5: Implement the initializer, stores and registration**

`src/CodeSwitchX.Data/DatabaseInitializer.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Data;

public sealed class DatabaseInitializer
{
    public const string DefaultTrackName = "General";

    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IDbContextFactory<CodeSwitchXDbContext> factory, ILogger<DatabaseInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            _logger.LogInformation("Applying {Count} database migrations", pending.Count);
        }

        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);

        if (!await db.Tracks.AnyAsync(ct))
        {
            db.Tracks.Add(new Track { Name = DefaultTrackName, SortOrder = 0 });
            await db.SaveChangesAsync(ct);
        }
    }
}
```

`src/CodeSwitchX.Data/Stores/WorkspaceStore.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class WorkspaceStore : IWorkspaceStore
{
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public WorkspaceStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
    }

    public async Task<Workspace?> FindByRootAsync(string normalizedRoot, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.RootPath == normalizedRoot, ct);
    }

    public async Task AddAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.Workspaces.AnyAsync(w => w.RootPath == workspace.RootPath, ct))
        {
            throw new DuplicateWorkspaceException(workspace.RootPath);
        }

        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspace.Id, ct)
            ?? throw new KeyNotFoundException($"Workspace {workspace.Id} not found.");

        db.Entry(existing).CurrentValues.SetValues(workspace);
        existing.Worktrees.RemoveAll(t => workspace.Worktrees.All(n => n.Id != t.Id));
        foreach (var worktree in workspace.Worktrees)
        {
            var current = existing.Worktrees.FirstOrDefault(t => t.Id == worktree.Id);
            if (current is null)
            {
                existing.Worktrees.Add(new Worktree { Id = worktree.Id, WorkspaceId = existing.Id, Path = worktree.Path, Branch = worktree.Branch });
            }
            else
            {
                current.Path = worktree.Path;
                current.Branch = worktree.Branch;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Workspaces.Where(w => w.Id == workspaceId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Track> AddTrackAsync(string name, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var maxOrder = await db.Tracks.Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? -1;
        var track = new Track { Name = name, SortOrder = maxOrder + 1 };
        db.Tracks.Add(track);
        await db.SaveChangesAsync(ct);
        return track;
    }

    public async Task UpdateTrackAsync(Track track, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Tracks.Update(track);
        await db.SaveChangesAsync(ct);
    }
}
```

`src/CodeSwitchX.Data/Stores/SessionStore.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class SessionStore : ISessionStore
{
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public SessionStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<SessionRecord>> GetActiveSinceAsync(DateTimeOffset lastEventAfter, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Sessions.AsNoTracking()
            .Where(s => s.LastEventAt >= lastEventAfter)
            .OrderByDescending(s => s.LastEventAt)
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(IReadOnlyCollection<SessionRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var ids = records.Select(r => r.Id).ToArray();
        var existing = await db.Sessions.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        foreach (var record in records)
        {
            if (existing.TryGetValue(record.Id, out var row))
            {
                db.Entry(row).CurrentValues.SetValues(record);
            }
            else
            {
                db.Sessions.Add(record);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task AppendEventsAsync(IReadOnlyCollection<SessionEventRecord> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.SessionEvents.AddRange(events);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetEventsAsync(string sessionId, int limit, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> PruneEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.SessionEvents.Where(e => e.At < olderThan).ExecuteDeleteAsync(ct);
    }
}
```

`src/CodeSwitchX.Data/Stores/UsageStore.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class UsageStore : IUsageStore
{
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public UsageStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task AddUsageAsync(IReadOnlyCollection<UsageBucket> deltas, CancellationToken ct = default)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var delta in deltas)
        {
            var key = new object[] { delta.SessionId, delta.Model, UsageBucket.FloorToMinute(delta.MinuteUtc) };
            var existing = await db.UsageBuckets.FindAsync(key, ct);
            if (existing is null)
            {
                db.UsageBuckets.Add(new UsageBucket
                {
                    SessionId = delta.SessionId,
                    Model = delta.Model,
                    MinuteUtc = UsageBucket.FloorToMinute(delta.MinuteUtc),
                    Input = delta.Input,
                    Output = delta.Output,
                    CacheWrite = delta.CacheWrite,
                    CacheRead = delta.CacheRead,
                });
            }
            else
            {
                existing.Input += delta.Input;
                existing.Output += delta.Output;
                existing.CacheWrite += delta.CacheWrite;
                existing.CacheRead += delta.CacheRead;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UsageBucket>> GetBucketsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.UsageBuckets.AsNoTracking()
            .Where(b => b.MinuteUtc >= fromUtc && b.MinuteUtc < toUtc)
            .OrderBy(b => b.MinuteUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TranscriptCursor>> GetCursorsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.TranscriptCursors.AsNoTracking().ToListAsync(ct);
    }

    public async Task UpsertCursorsAsync(IReadOnlyCollection<TranscriptCursor> cursors, CancellationToken ct = default)
    {
        if (cursors.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var paths = cursors.Select(c => c.Path).ToArray();
        var existing = await db.TranscriptCursors.Where(c => paths.Contains(c.Path)).ToDictionaryAsync(c => c.Path, ct);
        foreach (var cursor in cursors)
        {
            if (existing.TryGetValue(cursor.Path, out var row))
            {
                row.ByteOffset = cursor.ByteOffset;
                row.LastWriteUtc = cursor.LastWriteUtc;
                row.SessionId = cursor.SessionId;
            }
            else
            {
                db.TranscriptCursors.Add(new TranscriptCursor
                {
                    Path = cursor.Path, ByteOffset = cursor.ByteOffset, LastWriteUtc = cursor.LastWriteUtc, SessionId = cursor.SessionId,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
```

`src/CodeSwitchX.Data/Stores/SettingsStore.cs`:

```csharp
using System.Text.Json;
using CodeSwitchX.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitchX.Data.Stores;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<CodeSwitchXDbContext> _factory;

    public SettingsStore(IDbContextFactory<CodeSwitchXDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting is null ? default : JsonSerializer.Deserialize<T>(setting.ValueJson, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        }
        else
        {
            setting.ValueJson = json;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PricingRule>> GetPricingAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.PricingRules.AsNoTracking().OrderBy(p => p.Model).ToListAsync(ct);
    }

    public async Task UpsertPricingAsync(IReadOnlyCollection<PricingRule> rules, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var models = rules.Select(r => r.Model).ToArray();
        var existing = await db.PricingRules.Where(p => models.Contains(p.Model)).ToDictionaryAsync(p => p.Model, ct);
        foreach (var rule in rules)
        {
            if (existing.TryGetValue(rule.Model, out var row))
            {
                db.Entry(row).CurrentValues.SetValues(rule);
            }
            else
            {
                db.PricingRules.Add(rule);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task EnsurePricingDefaultsAsync(IReadOnlyCollection<PricingRule> defaults, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var present = await db.PricingRules.Select(p => p.Model).ToHashSetAsync(ct);
        foreach (var rule in defaults.Where(r => !present.Contains(r.Model)))
        {
            db.PricingRules.Add(rule);
        }

        await db.SaveChangesAsync(ct);
    }
}
```

`src/CodeSwitchX.Data/DataServiceCollectionExtensions.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXData(this IServiceCollection services, string databaseFile)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        services.AddPooledDbContextFactory<CodeSwitchXDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IWorkspaceStore, WorkspaceStore>();
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IUsageStore, UsageStore>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        return services;
    }
}
```

- [ ] **Step 6: Generate the initial migration with a local dotnet-ef tool**

```powershell
cd C:\GitRepo\CodeSwitchX
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12
dotnet build src/CodeSwitchX.Data
dotnet ef migrations add InitialCreate --project src/CodeSwitchX.Data --startup-project src/CodeSwitchX.Data --output-dir Migrations
```

Expected: `Migrations/<timestamp>_InitialCreate.cs`, `<timestamp>_InitialCreate.Designer.cs` and `CodeSwitchXDbContextModelSnapshot.cs` appear. Delete any `design-time.db` file the tool leaves in `src/CodeSwitchX.Data` and add `design-time.db*` to `.gitignore`.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Data.Tests`
Expected: all pass. If `Range_query_is_inclusive_of_from_and_exclusive_of_to` fails with a translation error, the `ConfigureConventions` converter registration is missing.

---

### Task 6: Batched persistence writer

**Files:**
- Create: `src/CodeSwitchX.Data/PersistenceWriterOptions.cs`, `src/CodeSwitchX.Data/PersistenceWriter.cs`
- Modify: `src/CodeSwitchX.Data/DataServiceCollectionExtensions.cs` (register the writer as a hosted service)
- Test: `tests/CodeSwitchX.Data.Tests/PersistenceWriterTests.cs`

**Interfaces:**
- Consumes: `IEventBus`, `SessionChanged`, `HookEventReceived`, `TranscriptUpdated` (Task 4); `ISessionStore`, `IUsageStore` (Task 5).
- Produces: `PersistenceWriter : BackgroundService` with `internal Task FlushAsync(CancellationToken)` and `int Pending`; `PersistenceWriterOptions { TimeSpan FlushInterval = 250 ms; int MaxBatch = 500; bool StorePayloads = false }`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Data.Tests/PersistenceWriterTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CodeSwitchX.Data.Tests;

public class PersistenceWriterTests : IAsyncLifetime
{
    private readonly SqliteFixture _db = new();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private PersistenceWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        await _db.InitializeAsync();
        _writer = new PersistenceWriter(_bus, _db.Get<ISessionStore>(), _db.Get<IUsageStore>(), _time,
            NullLogger<PersistenceWriter>.Instance, new PersistenceWriterOptions());
        _writer.Subscribe();
    }

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        await _db.DisposeAsync();
    }

    private SessionSnapshot Snapshot(string id, SessionState state) => new()
    {
        SessionId = id, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(),
    };

    [Fact]
    public async Task Session_changes_are_upserted_with_the_latest_snapshot_winning()
    {
        _bus.Publish(new SessionChanged(null, Snapshot("s1", SessionState.Starting)));
        _bus.Publish(new SessionChanged(null, Snapshot("s1", SessionState.Working)));
        _writer.Pending.ShouldBe(2);

        await _writer.FlushAsync(CancellationToken.None);

        var records = await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1));
        records.ShouldHaveSingleItem().State.ShouldBe(SessionState.Working);
        _writer.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task Hook_events_are_appended_without_payload_by_default()
    {
        _bus.Publish(new HookEventReceived(new HookEvent
        {
            SessionId = "s1", EventName = "PreToolUse", Signal = SessionSignal.ToolUse, At = _time.GetUtcNow(), ToolName = "Edit", RawJson = "{\"secret\":1}",
        }));

        await _writer.FlushAsync(CancellationToken.None);

        var stored = (await _db.Get<ISessionStore>().GetEventsAsync("s1", 10)).ShouldHaveSingleItem();
        stored.Kind.ShouldBe("PreToolUse");
        stored.ToolName.ShouldBe("Edit");
        stored.PayloadJson.ShouldBeNull();
    }

    [Fact]
    public async Task Usage_deltas_in_the_same_minute_collapse_into_one_bucket()
    {
        var at = _time.GetUtcNow();
        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = at,
            Usage =
            [
                new UsageDelta("claude-sonnet-5", at.AddSeconds(5), new TokenUsage(10, 1, 0, 100)),
                new UsageDelta("claude-sonnet-5", at.AddSeconds(40), new TokenUsage(20, 2, 5, 200)),
                new UsageDelta("claude-sonnet-5", at.AddMinutes(1), new TokenUsage(1, 1, 1, 1)),
            ],
        }));

        await _writer.FlushAsync(CancellationToken.None);

        var buckets = await _db.Get<IUsageStore>().GetBucketsAsync(at, at.AddMinutes(5));
        buckets.Count.ShouldBe(2);
        buckets[0].Input.ShouldBe(30);
        buckets[0].CacheRead.ShouldBe(300);
        buckets[1].Input.ShouldBe(1);
    }

    [Fact]
    public async Task The_background_loop_flushes_on_the_timer()
    {
        using var cts = new CancellationTokenSource();
        await _writer.StartAsync(cts.Token);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", SessionState.Idle)));

        _time.Advance(TimeSpan.FromMilliseconds(300));
        await WaitUntilAsync(() => _writer.Pending == 0);

        (await _db.Get<ISessionStore>().GetActiveSinceAsync(_time.GetUtcNow().AddHours(-1))).Count.ShouldBe(1);
        await _writer.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        condition().ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Data.Tests --filter FullyQualifiedName~PersistenceWriterTests`
Expected: build error, `PersistenceWriter` not found.

- [ ] **Step 3: Implement the writer**

`src/CodeSwitchX.Data/PersistenceWriterOptions.cs`:

```csharp
namespace CodeSwitchX.Data;

public sealed class PersistenceWriterOptions
{
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int MaxBatch { get; set; } = 500;

    /// <summary>Opt-in: store raw hook payload JSON (contains prompts and file paths).</summary>
    public bool StorePayloads { get; set; }
}
```

`src/CodeSwitchX.Data/PersistenceWriter.cs`:

```csharp
using System.Threading.Channels;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Data;

/// <summary>
/// Drains bus messages into SQLite in batches so bursts of tool events never block the publisher
/// or contend on the database.
/// </summary>
public sealed class PersistenceWriter : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly ISessionStore _sessions;
    private readonly IUsageStore _usage;
    private readonly TimeProvider _time;
    private readonly ILogger<PersistenceWriter> _logger;
    private readonly PersistenceWriterOptions _options;
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<IDisposable> _subscriptions = [];
    private int _pending;

    public PersistenceWriter(IEventBus bus, ISessionStore sessions, IUsageStore usage, TimeProvider time,
        ILogger<PersistenceWriter> logger, PersistenceWriterOptions options)
    {
        _bus = bus;
        _sessions = sessions;
        _usage = usage;
        _time = time;
        _logger = logger;
        _options = options;
    }

    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Attach to the bus. Called from <see cref="StartAsync"/>; tests call it directly.</summary>
    public void Subscribe()
    {
        if (_subscriptions.Count > 0)
        {
            return;
        }

        _subscriptions.Add(_bus.Subscribe<SessionChanged>(m => Enqueue(m.Current)));
        _subscriptions.Add(_bus.Subscribe<HookEventReceived>(m => Enqueue(m.Event)));
        _subscriptions.Add(_bus.Subscribe<TranscriptUpdated>(m =>
        {
            if (m.Update.Usage.Count > 0)
            {
                Enqueue(m.Update);
            }
        }));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.FlushInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down: flush what is left with a short grace period
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await FlushAsync(grace.Token).ConfigureAwait(false);
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        var sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
        var events = new List<SessionEventRecord>();
        var buckets = new Dictionary<(string SessionId, string Model, DateTimeOffset Minute), UsageBucket>();
        var taken = 0;

        while (taken < _options.MaxBatch && _queue.Reader.TryRead(out var item))
        {
            taken++;
            switch (item)
            {
                case SessionSnapshot snapshot:
                    sessions[snapshot.SessionId] = SessionRecord.FromSnapshot(snapshot);
                    break;
                case HookEvent hook:
                    events.Add(new SessionEventRecord
                    {
                        SessionId = hook.SessionId,
                        Kind = hook.EventName,
                        ToolName = hook.ToolName,
                        At = hook.At,
                        PayloadJson = _options.StorePayloads ? hook.RawJson : null,
                    });
                    break;
                case TranscriptUpdate update:
                    foreach (var delta in update.Usage)
                    {
                        var minute = UsageBucket.FloorToMinute(delta.At);
                        var key = (update.SessionId, delta.Model, minute);
                        if (!buckets.TryGetValue(key, out var bucket))
                        {
                            bucket = new UsageBucket { SessionId = update.SessionId, Model = delta.Model, MinuteUtc = minute };
                            buckets[key] = bucket;
                        }

                        bucket.Input += delta.Tokens.Input;
                        bucket.Output += delta.Tokens.Output;
                        bucket.CacheWrite += delta.Tokens.CacheWrite;
                        bucket.CacheRead += delta.Tokens.CacheRead;
                    }

                    break;
            }
        }

        if (taken == 0)
        {
            return;
        }

        try
        {
            await _sessions.UpsertAsync(sessions.Values.ToArray(), ct).ConfigureAwait(false);
            await _sessions.AppendEventsAsync(events, ct).ConfigureAwait(false);
            await _usage.AddUsageAsync(buckets.Values.ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Persisting a batch of {Count} items failed; the batch is dropped", taken);
        }
        finally
        {
            Interlocked.Add(ref _pending, -taken);
        }
    }

    public override void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        base.Dispose();
    }

    private void Enqueue(object item)
    {
        if (_queue.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _pending);
        }
    }
}
```

Add `<InternalsVisibleTo Include="CodeSwitchX.Data.Tests" />` to `CodeSwitchX.Data.csproj`.

In `DataServiceCollectionExtensions.AddCodeSwitchXData` add:

```csharp
services.AddSingleton<PersistenceWriterOptions>();
services.AddSingleton<PersistenceWriter>();
services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());
```

(`TimeProvider` and `IEventBus` are registered by the composition root in Task 14: `services.AddSingleton(TimeProvider.System); services.AddSingleton<IEventBus, EventBus>();`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Data.Tests`
Expected: all pass.

---

### Task 7: Hook envelope parser

**Files:**
- Create: `src/CodeSwitchX.Core/Sessions/ChatTitle.cs`
- Modify: `src/CodeSwitchX.Core/Sessions/SessionEngine.cs` (replace the private `TitleFromPrompt` with `ChatTitle.FromPrompt`)
- Create: `src/CodeSwitchX.Ingest/Hooks/HookEnvelopeParser.cs`
- Test: `tests/CodeSwitchX.Ingest.Tests/Hooks/HookEnvelopeParserTests.cs`, `tests/CodeSwitchX.Core.Tests/Sessions/ChatTitleTests.cs`

**Interfaces:**
- Consumes: `HookEvent`, `SessionSignal`, `ProcessRef` (Task 4).
- Produces: `ChatTitle.FromPrompt(string? prompt, int maxLength = 60) -> string?`; `HookEnvelopeParser.Parse(string json, DateTimeOffset receivedAt) -> HookEvent?` (null when the JSON is invalid or has no session id); `HookEnvelopeParser.SignalFor(string eventName, string? notificationType) -> SessionSignal?`.
- Envelope contract shared with the relay (Task 12): `{"event":"PreToolUse","receivedAtUtc":"...","relayPid":123,"parentChain":[{"pid":1,"name":"cmd.exe"}],"payload":{...raw Claude Code hook JSON...}}`. A bare Claude Code payload (object with `hook_event_name`) is accepted too.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Core.Tests/Sessions/ChatTitleTests.cs`:

```csharp
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Core.Tests.Sessions;

public class ChatTitleTests
{
    [Fact]
    public void Short_prompts_are_kept_as_one_line()
    {
        ChatTitle.FromPrompt("Fix the\r\n  build").ShouldBe("Fix the build");
    }

    [Fact]
    public void Long_prompts_are_cut_to_the_limit_with_an_ellipsis()
    {
        var title = ChatTitle.FromPrompt(new string('a', 100), 60)!;

        title.Length.ShouldBe(60);
        title.ShouldEndWith("…");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_prompts_give_no_title(string? prompt)
    {
        ChatTitle.FromPrompt(prompt).ShouldBeNull();
    }
}
```

`tests/CodeSwitchX.Ingest.Tests/Hooks/HookEnvelopeParserTests.cs`:

```csharp
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Hooks;

namespace CodeSwitchX.Ingest.Tests.Hooks;

public class HookEnvelopeParserTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static string Envelope(string eventName, string payloadJson, string parentChain = "[]") =>
        $$"""
        {"event":"{{eventName}}","receivedAtUtc":"2026-09-23T09:59:59.900Z","relayPid":4242,"parentChain":{{parentChain}},"payload":{{payloadJson}}}
        """;

    [Fact]
    public void Pre_tool_use_maps_to_ToolUse_with_tool_name_cwd_and_transcript()
    {
        var json = Envelope("PreToolUse",
            """{"session_id":"abc","transcript_path":"C:\\Users\\me\\.claude\\projects\\x\\abc.jsonl","cwd":"C:\\Repo\\App","hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"dotnet build"},"tool_use_id":"toolu_1"}""",
            """[{"pid":100,"name":"cmd.exe"},{"pid":200,"name":"claude.exe"}]""");

        var e = HookEnvelopeParser.Parse(json, Received).ShouldNotBeNull();

        e.SessionId.ShouldBe("abc");
        e.EventName.ShouldBe("PreToolUse");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
        e.ToolName.ShouldBe("Bash");
        e.ToolUseId.ShouldBe("toolu_1");
        e.Cwd.ShouldBe(@"C:\Repo\App");
        e.TranscriptPath.ShouldBe(@"C:\Users\me\.claude\projects\x\abc.jsonl");
        e.At.ShouldBe(Received);
        e.RelayPid.ShouldBe(4242);
        e.ParentChain.ShouldBe([new ProcessRef(100, "cmd.exe"), new ProcessRef(200, "claude.exe")]);
        e.RawJson.ShouldContain("\"tool_name\"");
    }

    [Theory]
    [InlineData("SessionStart", SessionSignal.SessionStart)]
    [InlineData("UserPromptSubmit", SessionSignal.PromptSubmit)]
    [InlineData("PreToolUse", SessionSignal.ToolUse)]
    [InlineData("PostToolUse", SessionSignal.ToolUse)]
    [InlineData("Notification", SessionSignal.Notification)]
    [InlineData("PermissionRequest", SessionSignal.Notification)]
    [InlineData("Stop", SessionSignal.Stop)]
    [InlineData("SessionEnd", SessionSignal.SessionEnd)]
    public void Known_events_map_to_signals(string eventName, SessionSignal expected)
    {
        HookEnvelopeParser.SignalFor(eventName, null).ShouldBe(expected);
    }

    [Theory]
    [InlineData("SubagentStop")]
    [InlineData("PreCompact")]
    [InlineData("SomethingNew")]
    public void Events_without_a_state_meaning_have_no_signal(string eventName)
    {
        HookEnvelopeParser.SignalFor(eventName, null).ShouldBeNull();
    }

    [Theory]
    [InlineData("permission_prompt", SessionSignal.Notification)]
    [InlineData("idle_prompt", SessionSignal.Notification)]
    [InlineData("elicitation_dialog", SessionSignal.Notification)]
    [InlineData("future_type", SessionSignal.Notification)]
    [InlineData("auth_success", null)]
    public void Informational_notifications_do_not_mean_waiting(string type, SessionSignal? expected)
    {
        HookEnvelopeParser.SignalFor("Notification", type).ShouldBe(expected);
    }

    [Fact]
    public void Unknown_event_names_still_parse_with_a_null_signal()
    {
        var e = HookEnvelopeParser.Parse(Envelope("SomethingNew", """{"session_id":"abc","hook_event_name":"SomethingNew","extra":{"deep":[1,2,3]}}"""), Received);

        e.ShouldNotBeNull();
        e.EventName.ShouldBe("SomethingNew");
        e.Signal.ShouldBeNull();
    }

    [Fact]
    public void Prompt_notification_and_session_fields_are_extracted()
    {
        var prompt = HookEnvelopeParser.Parse(Envelope("UserPromptSubmit", """{"session_id":"abc","hook_event_name":"UserPromptSubmit","prompt":"Fix the build"}"""), Received)!;
        prompt.Prompt.ShouldBe("Fix the build");

        var note = HookEnvelopeParser.Parse(Envelope("Notification", """{"session_id":"abc","hook_event_name":"Notification","message":"Claude needs your permission to use Bash","notification_type":"permission_prompt"}"""), Received)!;
        note.Message.ShouldBe("Claude needs your permission to use Bash");
        note.NotificationType.ShouldBe("permission_prompt");

        var start = HookEnvelopeParser.Parse(Envelope("SessionStart", """{"session_id":"abc","hook_event_name":"SessionStart","source":"startup","model":"claude-sonnet-5"}"""), Received)!;
        start.Source.ShouldBe("startup");
        start.Model.ShouldBe("claude-sonnet-5");

        var end = HookEnvelopeParser.Parse(Envelope("SessionEnd", """{"session_id":"abc","hook_event_name":"SessionEnd","reason":"prompt_input_exit"}"""), Received)!;
        end.Source.ShouldBe("prompt_input_exit");
    }

    [Fact]
    public void The_payloads_own_event_name_wins_over_the_envelope()
    {
        var e = HookEnvelopeParser.Parse(Envelope("Stop", """{"session_id":"abc","hook_event_name":"PostToolUse","tool_name":"Read"}"""), Received)!;

        e.EventName.ShouldBe("PostToolUse");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
    }

    [Fact]
    public void A_bare_claude_payload_is_accepted()
    {
        var e = HookEnvelopeParser.Parse("""{"session_id":"abc","hook_event_name":"Stop","cwd":"C:\\x"}""", Received).ShouldNotBeNull();

        e.Signal.ShouldBe(SessionSignal.Stop);
        e.RelayPid.ShouldBeNull();
        e.ParentChain.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"event":"Stop","payload":{"hook_event_name":"Stop"}}""")]
    [InlineData("""{"event":"Stop","payload":"string"}""")]
    public void Invalid_or_session_less_input_returns_null(string json)
    {
        HookEnvelopeParser.Parse(json, Received).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests` and `dotnet test tests/CodeSwitchX.Core.Tests --filter FullyQualifiedName~ChatTitleTests`
Expected: build errors, `ChatTitle` and `HookEnvelopeParser` missing.

- [ ] **Step 3: Implement ChatTitle and switch the engine to it**

`src/CodeSwitchX.Core/Sessions/ChatTitle.cs`:

```csharp
namespace CodeSwitchX.Core.Sessions;

public static class ChatTitle
{
    public const int DefaultMaxLength = 60;

    public static string? FromPrompt(string? prompt, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var singleLine = string.Join(' ',
            prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength - 1), "…");
    }
}
```

In `SessionEngine.Apply(HookEvent)` replace `Title = s.Title ?? TitleFromPrompt(e.Prompt),` with `Title = s.Title ?? ChatTitle.FromPrompt(e.Prompt, _options.TitleMaxLength),` and delete the private `TitleFromPrompt` method.

- [ ] **Step 4: Implement the parser**

`src/CodeSwitchX.Ingest/Hooks/HookEnvelopeParser.cs`:

```csharp
using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Turns the relay envelope (or a bare Claude Code hook payload) into a <see cref="HookEvent"/>.
/// Tolerant by design: unknown fields are ignored, unknown events keep a null signal, bad JSON yields null.
/// </summary>
public static class HookEnvelopeParser
{
    private static readonly HashSet<string> InformationalNotifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth_success",
    };

    public static HookEvent? Parse(string json, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement payload;
            string? envelopeEvent = null;
            int? relayPid = null;
            var chain = new List<ProcessRef>();

            if (root.TryGetProperty("payload", out var payloadElement))
            {
                if (payloadElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                payload = payloadElement;
                envelopeEvent = GetString(root, "event");
                relayPid = GetInt(root, "relayPid");
                if (root.TryGetProperty("parentChain", out var chainElement) && chainElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in chainElement.EnumerateArray())
                    {
                        var pid = GetInt(item, "pid");
                        var name = GetString(item, "name");
                        if (pid is { } p && name is not null)
                        {
                            chain.Add(new ProcessRef(p, name));
                        }
                    }
                }
            }
            else
            {
                payload = root;
            }

            var sessionId = GetString(payload, "session_id");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var eventName = GetString(payload, "hook_event_name") ?? envelopeEvent;
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return null;
            }

            var notificationType = GetString(payload, "notification_type");
            return new HookEvent
            {
                SessionId = sessionId,
                EventName = eventName,
                Signal = SignalFor(eventName, notificationType),
                At = receivedAt,
                Cwd = GetString(payload, "cwd"),
                TranscriptPath = GetString(payload, "transcript_path"),
                ToolName = GetString(payload, "tool_name"),
                ToolUseId = GetString(payload, "tool_use_id"),
                NotificationType = notificationType,
                Message = GetString(payload, "message") ?? GetString(payload, "title"),
                Prompt = GetString(payload, "prompt"),
                Model = GetString(payload, "model"),
                Source = GetString(payload, "source") ?? GetString(payload, "reason"),
                RelayPid = relayPid,
                ParentChain = chain,
                RawJson = payload.GetRawText(),
            };
        }
    }

    public static SessionSignal? SignalFor(string eventName, string? notificationType) => eventName switch
    {
        "SessionStart" => SessionSignal.SessionStart,
        "UserPromptSubmit" => SessionSignal.PromptSubmit,
        "PreToolUse" or "PostToolUse" => SessionSignal.ToolUse,
        "PermissionRequest" => SessionSignal.Notification,
        "Notification" when notificationType is null || !InformationalNotifications.Contains(notificationType) => SessionSignal.Notification,
        "Stop" => SessionSignal.Stop,
        "SessionEnd" => SessionSignal.SessionEnd,
        _ => null,
    };

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests` and `dotnet test tests/CodeSwitchX.Core.Tests`
Expected: all pass.

---

### Task 8: Event API (named pipe + loopback, token-guarded)

**Files:**
- Create: `src/CodeSwitchX.Ingest/Api/EndpointDescriptor.cs`, `AccessTokenStore.cs`, `EventApiOptions.cs`, `EventApiService.cs`
- Create: `src/CodeSwitchX.Ingest/IngestServiceCollectionExtensions.cs`
- Test: `tests/CodeSwitchX.Ingest.Tests/Api/EventApiServiceTests.cs`, `tests/CodeSwitchX.Ingest.Tests/Api/AccessTokenStoreTests.cs`

**Interfaces:**
- Consumes: `AppPaths` (Task 1), `IEventBus`, `HookEventReceived` (Task 4), `HookEnvelopeParser` (Task 7).
- Produces:
  - `EndpointDescriptor(string PipeName, int Port, int Pid, DateTimeOffset StartedAtUtc)` with `static EndpointDescriptor? TryRead(string file)` and `void Write(string file)`; JSON property names `pipeName`, `port`, `pid`, `startedAtUtc` (the relay reads this file).
  - `AccessTokenStore(AppPaths)` with `string GetOrCreate()`; token file holds 64 hex chars; header `Authorization: Bearer <token>` or `X-CodeSwitchX-Token: <token>`.
  - `EventApiOptions { bool EnableNamedPipe = true; string PipeName = "CodeSwitchX-" + Environment.UserName; bool EnableLoopback = true; int LoopbackPort = 0 }`.
  - `EventApiService : IHostedService` with `EndpointDescriptor? Endpoint`; routes `POST /events` (202 accepted, 400 bad payload, 401 bad token, 413 over 1 MB) and `GET /health` (200, token not required).
  - `IngestServiceCollectionExtensions.AddCodeSwitchXIngest(this IServiceCollection, Action<EventApiOptions>? configure = null)`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Ingest.Tests/Api/AccessTokenStoreTests.cs`:

```csharp
using CodeSwitchX.Core;
using CodeSwitchX.Ingest.Api;

namespace CodeSwitchX.Ingest.Tests.Api;

public class AccessTokenStoreTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-token-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public void Creates_a_64_hex_token_once_and_reuses_it()
    {
        var store = new AccessTokenStore(_paths);

        var first = store.GetOrCreate();
        var second = store.GetOrCreate();

        first.Length.ShouldBe(64);
        first.ShouldAllBe(c => Uri.IsHexDigit(c));
        second.ShouldBe(first);
        File.ReadAllText(_paths.TokenFile).Trim().ShouldBe(first);
    }

    [Fact]
    public void Token_file_is_readable_only_by_the_current_user()
    {
        new AccessTokenStore(_paths).GetOrCreate();

        var rules = new FileInfo(_paths.TokenFile).GetAccessControl()
            .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToList();

        rules.ShouldHaveSingleItem().IdentityReference.ShouldBe(System.Security.Principal.WindowsIdentity.GetCurrent().User);
    }
}
```

`tests/CodeSwitchX.Ingest.Tests/Api/EventApiServiceTests.cs`:

```csharp
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Ingest.Tests.Api;

public class EventApiServiceTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-api-" + Guid.NewGuid().ToString("N")));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<HookEvent> _received = [];
    private readonly string _pipeName = "csx-test-" + Guid.NewGuid().ToString("N");
    private EventApiService _api = null!;
    private string _token = null!;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        _bus.Subscribe<HookEventReceived>(m => _received.Add(m.Event));
        var tokens = new AccessTokenStore(_paths);
        _token = tokens.GetOrCreate();
        _api = new EventApiService(_paths, _bus, tokens, TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = _pipeName, LoopbackPort = 0 });
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        Directory.Delete(_paths.Root, recursive: true);
    }

    private const string Body = """{"event":"Stop","payload":{"session_id":"s1","hook_event_name":"Stop","cwd":"C:\\x"}}""";

    private HttpClient Loopback() => new() { BaseAddress = new Uri($"http://127.0.0.1:{_api.Endpoint!.Port}/") };

    private HttpClient Pipe()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ct);
                return pipe;
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://pipe/") };
    }

    private static HttpRequestMessage Post(string body, string? token) 
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "events") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public void Endpoint_file_describes_the_running_server()
    {
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile).ShouldNotBeNull();

        descriptor.Port.ShouldBe(_api.Endpoint!.Port);
        descriptor.Port.ShouldBeGreaterThan(0);
        descriptor.PipeName.ShouldBe(_pipeName);
        descriptor.Pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task Loopback_post_with_token_is_accepted_and_published()
    {
        using var client = Loopback();

        var response = await client.SendAsync(Post(Body, _token));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _received.ShouldHaveSingleItem().SessionId.ShouldBe("s1");
    }

    [Fact]
    public async Task Named_pipe_post_with_token_is_accepted_and_published()
    {
        using var client = Pipe();

        var response = await client.SendAsync(Post(Body, _token));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _received.ShouldHaveSingleItem().Signal.ShouldBe(SessionSignal.Stop);
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_rejected()
    {
        using var client = Loopback();

        (await client.SendAsync(Post(Body, null))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Post(Body, new string('0', 64)))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Custom_header_is_accepted_too()
    {
        using var client = Loopback();
        var request = new HttpRequestMessage(HttpMethod.Post, "events") { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CodeSwitchX-Token", _token);

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Bad_payload_is_a_400_and_health_needs_no_token()
    {
        using var client = Loopback();

        (await client.SendAsync(Post("{\"nope\":true}", _token))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stopping_removes_the_endpoint_file()
    {
        await _api.StopAsync(CancellationToken.None);

        File.Exists(_paths.EndpointFile).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests --filter FullyQualifiedName~Api`
Expected: build errors for the missing `Api` types.

- [ ] **Step 3: Implement the descriptor and token store**

`src/CodeSwitchX.Ingest/Api/EndpointDescriptor.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSwitchX.Ingest.Api;

/// <summary>Written to <c>endpoint.json</c> so the relay knows where to post. Property names are part of the relay contract.</summary>
public sealed record EndpointDescriptor(
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static EndpointDescriptor? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonSerializer.Deserialize<EndpointDescriptor>(File.ReadAllText(file), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string file)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, file, overwrite: true);
    }
}
```

`src/CodeSwitchX.Ingest/Api/AccessTokenStore.cs`:

```csharp
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CodeSwitchX.Core;

namespace CodeSwitchX.Ingest.Api;

/// <summary>Per-install random secret shared by the API and the relay; stored with a current-user-only ACL.</summary>
public sealed class AccessTokenStore
{
    private readonly AppPaths _paths;
    private readonly Lock _gate = new();
    private string? _cached;

    public AccessTokenStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string GetOrCreate()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (File.Exists(_paths.TokenFile))
            {
                var existing = File.ReadAllText(_paths.TokenFile).Trim();
                if (existing.Length == 64 && existing.All(Uri.IsHexDigit))
                {
                    return _cached = existing;
                }
            }

            _paths.EnsureCreated();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(_paths.TokenFile, token);
            RestrictToCurrentUser(_paths.TokenFile);
            return _cached = token;
        }
    }

    public static bool Matches(string token, string? candidate)
    {
        if (candidate is null || candidate.Length != token.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(candidate));
    }

    private static void RestrictToCurrentUser(string file)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current user SID.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
```

- [ ] **Step 4: Implement the options and the service**

`src/CodeSwitchX.Ingest/Api/EventApiOptions.cs`:

```csharp
namespace CodeSwitchX.Ingest.Api;

public sealed class EventApiOptions
{
    public bool EnableNamedPipe { get; set; } = true;
    public string PipeName { get; set; } = "CodeSwitchX-" + Sanitize(Environment.UserName);
    public bool EnableLoopback { get; set; } = true;

    /// <summary>0 lets Kestrel pick a free port; the chosen port is published in endpoint.json.</summary>
    public int LoopbackPort { get; set; }
    public long MaxBodyBytes { get; set; } = 1024 * 1024;

    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
}
```

`src/CodeSwitchX.Ingest/Api/EventApiService.cs`:

```csharp
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Ingest.Hooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Api;

/// <summary>In-process Kestrel endpoint that receives relayed hook payloads and publishes them on the bus.</summary>
public sealed class EventApiService : IHostedService
{
    private readonly AppPaths _paths;
    private readonly IEventBus _bus;
    private readonly AccessTokenStore _tokens;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly EventApiOptions _options;
    private WebApplication? _app;

    public EventApiService(AppPaths paths, IEventBus bus, AccessTokenStore tokens, TimeProvider time,
        ILoggerFactory loggerFactory, EventApiOptions options)
    {
        _paths = paths;
        _bus = bus;
        _tokens = tokens;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<EventApiService>();
        _options = options;
    }

    public EndpointDescriptor? Endpoint { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var token = _tokens.GetOrCreate();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "CodeSwitchX.Ingest" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = _options.MaxBodyBytes;
            if (_options.EnableNamedPipe)
            {
                kestrel.ListenNamedPipe(_options.PipeName);
            }

            if (_options.EnableLoopback)
            {
                kestrel.ListenLocalhost(_options.LoopbackPort);
            }
        });

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { pid = Environment.ProcessId, product = AppPaths.ProductFolderName }));

        app.MapPost("/events", async (HttpContext context) =>
        {
            if (!IsAuthorized(context, token))
            {
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var hookEvent = HookEnvelopeParser.Parse(body, _time.GetUtcNow());
            if (hookEvent is null)
            {
                _logger.LogWarning("Rejected hook payload of {Length} bytes: not a recognised envelope", body.Length);
                return Results.BadRequest(new { error = "payload is not a hook envelope with a session_id" });
            }

            _bus.Publish(new HookEventReceived(hookEvent));
            return Results.Accepted();
        });

        await app.StartAsync(cancellationToken);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var port = addresses
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) && uri.Host != "pipe" ? uri.Port : 0)
            .FirstOrDefault(p => p > 0);

        Endpoint = new EndpointDescriptor(_options.EnableNamedPipe ? _options.PipeName : string.Empty, port, Environment.ProcessId, _time.GetUtcNow());
        Endpoint.Write(_paths.EndpointFile);
        _logger.LogInformation("Event API listening on pipe {Pipe} and port {Port}", Endpoint.PipeName, Endpoint.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            File.Delete(_paths.EndpointFile);
        }
        catch (IOException)
        {
        }

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }

    private static bool IsAuthorized(HttpContext context, string token)
    {
        string? candidate = null;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = authorization["Bearer ".Length..].Trim();
        }
        else if (context.Request.Headers.TryGetValue("X-CodeSwitchX-Token", out var header))
        {
            candidate = header.ToString().Trim();
        }

        return AccessTokenStore.Matches(token, candidate);
    }
}
```

`src/CodeSwitchX.Ingest/IngestServiceCollectionExtensions.cs` (extended in Tasks 9 and 10):

```csharp
using CodeSwitchX.Ingest.Api;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Ingest;

public static class IngestServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXIngest(this IServiceCollection services, Action<EventApiOptions>? configure = null)
    {
        var options = new EventApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<AccessTokenStore>();
        services.AddSingleton<EventApiService>();
        services.AddHostedService(sp => sp.GetRequiredService<EventApiService>());
        return services;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests`
Expected: all pass. If the named-pipe test fails to connect, confirm `kestrel.ListenNamedPipe` is called before `Build()`; Kestrel's named-pipe transport applies a current-user-only ACL by default.

---

### Task 9: Transcript indexer

**Files:**
- Create: `src/CodeSwitchX.Ingest/Transcripts/TranscriptLine.cs`, `TranscriptLineParser.cs`, `TranscriptTailer.cs`, `TranscriptIndexerOptions.cs`, `TranscriptIndexer.cs`
- Modify: `src/CodeSwitchX.Ingest/IngestServiceCollectionExtensions.cs`
- Test: `tests/CodeSwitchX.Ingest.Tests/Transcripts/TranscriptLineParserTests.cs`, `TranscriptTailerTests.cs`, `TranscriptIndexerTests.cs`

**Interfaces:**
- Consumes: `ClaudeCodePaths`, `PathNormalizer`, `IUsageStore` (cursors), `IEventBus`, `TranscriptUpdated`, `TranscriptUpdate`, `UsageDelta`, `TokenUsage`, `ChatTitle`, `SessionSignal`.
- Produces:
  - `abstract record TranscriptLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd)` with subtypes `AssistantLine(string? MessageId, string? Model, TokenUsage? Usage, bool HasToolUse)`, `UserLine(string? Text, bool IsToolResult, bool IsMeta)`, `SummaryLine(string Title)`, `OtherLine`.
  - `TranscriptLineParser.TryParse(string line) -> TranscriptLine?` (null on invalid JSON).
  - `TranscriptTailer.ReadNewLines(string path, long fromOffset) -> TailResult(IReadOnlyList<string> Lines, long NewOffset, bool Truncated)`.
  - `TranscriptIndexer : BackgroundService` with `internal Task ScanAsync(CancellationToken)`, `internal Task ProcessFileAsync(string path, CancellationToken)`, `TranscriptIndexerOptions { TimeSpan ScanInterval = 2 s; TimeSpan WorkingWindow = 5 s; int MessageIdMemory = 512 }`.

- [ ] **Step 1: Write the failing parser and tailer tests**

`tests/CodeSwitchX.Ingest.Tests/Transcripts/TranscriptLineParserTests.cs`:

```csharp
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Transcripts;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public class TranscriptLineParserTests
{
    [Fact]
    public void Assistant_line_yields_usage_model_and_message_id()
    {
        const string line = """{"type":"assistant","sessionId":"s1","cwd":"C:\\Repo\\App","timestamp":"2026-09-23T10:00:05.000Z","uuid":"a1","message":{"id":"msg_1","model":"claude-sonnet-5","role":"assistant","content":[{"type":"tool_use","id":"tu1","name":"Bash","input":{"command":"ls"}}],"usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":500,"cache_read_input_tokens":3000,"service_tier":"standard"}}}""";

        var parsed = TranscriptLineParser.TryParse(line).ShouldBeOfType<AssistantLine>();

        parsed.SessionId.ShouldBe("s1");
        parsed.Cwd.ShouldBe(@"C:\Repo\App");
        parsed.Timestamp.ShouldBe(new DateTimeOffset(2026, 9, 23, 10, 0, 5, TimeSpan.Zero));
        parsed.MessageId.ShouldBe("msg_1");
        parsed.Model.ShouldBe("claude-sonnet-5");
        parsed.Usage.ShouldBe(new TokenUsage(100, 20, 500, 3000));
        parsed.HasToolUse.ShouldBeTrue();
    }

    [Fact]
    public void Assistant_line_without_usage_has_null_usage()
    {
        var parsed = TranscriptLineParser.TryParse("""{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"hi"}]}}""")
            .ShouldBeOfType<AssistantLine>();

        parsed.Usage.ShouldBeNull();
        parsed.HasToolUse.ShouldBeFalse();
    }

    [Fact]
    public void User_prompt_with_string_content_is_a_prompt()
    {
        var parsed = TranscriptLineParser.TryParse("""{"type":"user","sessionId":"s1","timestamp":"2026-09-23T10:00:00Z","message":{"role":"user","content":"Fix the build"}}""")
            .ShouldBeOfType<UserLine>();

        parsed.Text.ShouldBe("Fix the build");
        parsed.IsToolResult.ShouldBeFalse();
        parsed.IsMeta.ShouldBeFalse();
    }

    [Fact]
    public void User_text_block_is_a_prompt_and_tool_result_blocks_are_not()
    {
        TranscriptLineParser.TryParse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Hello there"}]}}""")
            .ShouldBeOfType<UserLine>().Text.ShouldBe("Hello there");

        var result = TranscriptLineParser.TryParse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"ok"}]}}""")
            .ShouldBeOfType<UserLine>();
        result.IsToolResult.ShouldBeTrue();
        result.Text.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"type":"user","isMeta":true,"message":{"role":"user","content":"Caveat: ..."}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<local-command-stdout>done</local-command-stdout>"}}""")]
    public void Meta_and_slash_command_lines_are_flagged_meta(string line)
    {
        TranscriptLineParser.TryParse(line).ShouldBeOfType<UserLine>().IsMeta.ShouldBeTrue();
    }

    [Fact]
    public void Summary_line_carries_the_title()
    {
        TranscriptLineParser.TryParse("""{"type":"summary","summary":"Fix build errors in App","leafUuid":"a1"}""")
            .ShouldBeOfType<SummaryLine>().Title.ShouldBe("Fix build errors in App");
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init"}""", "system")]
    [InlineData("""{"type":"progress"}""", "progress")]
    [InlineData("""{"no":"type"}""", "")]
    public void Other_types_are_kept_as_other(string line, string type)
    {
        TranscriptLineParser.TryParse(line).ShouldBeOfType<OtherLine>().Type.ShouldBe(type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public void Garbage_returns_null(string line)
    {
        TranscriptLineParser.TryParse(line).ShouldBeNull();
    }
}
```

`tests/CodeSwitchX.Ingest.Tests/Transcripts/TranscriptTailerTests.cs`:

```csharp
using System.Text;
using CodeSwitchX.Ingest.Transcripts;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public class TranscriptTailerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "csx-tail-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Reads_complete_lines_and_leaves_a_partial_trailing_line_for_later()
    {
        File.WriteAllText(_file, "{\"a\":1}\n{\"b\":2}\n{\"partial\":", Encoding.UTF8);

        var first = TranscriptTailer.ReadNewLines(_file, 0);

        first.Lines.ShouldBe(["{\"a\":1}", "{\"b\":2}"]);
        first.NewOffset.ShouldBe(Encoding.UTF8.GetByteCount("{\"a\":1}\n{\"b\":2}\n"));
        first.Truncated.ShouldBeFalse();

        File.AppendAllText(_file, "3}\r\n", Encoding.UTF8);
        var second = TranscriptTailer.ReadNewLines(_file, first.NewOffset);

        second.Lines.ShouldBe(["{\"partial\":3}"]);
        second.NewOffset.ShouldBe(new FileInfo(_file).Length);
    }

    [Fact]
    public void Offset_beyond_the_file_length_restarts_from_zero_and_reports_truncation()
    {
        File.WriteAllText(_file, "{\"a\":1}\n", Encoding.UTF8);

        var result = TranscriptTailer.ReadNewLines(_file, 5000);

        result.Truncated.ShouldBeTrue();
        result.Lines.ShouldBe(["{\"a\":1}"]);
        result.NewOffset.ShouldBe(8);
    }

    [Fact]
    public void Nothing_new_returns_no_lines_and_the_same_offset()
    {
        File.WriteAllText(_file, "{\"a\":1}\n", Encoding.UTF8);

        var result = TranscriptTailer.ReadNewLines(_file, 8);

        result.Lines.ShouldBeEmpty();
        result.NewOffset.ShouldBe(8);
    }

    [Fact]
    public void A_file_locked_for_writing_by_another_process_can_still_be_read()
    {
        using var writer = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.UTF8.GetBytes("{\"a\":1}\n"));
        writer.Flush();

        TranscriptTailer.ReadNewLines(_file, 0).Lines.ShouldBe(["{\"a\":1}"]);
    }
}
```

- [ ] **Step 2: Write the failing indexer tests**

`tests/CodeSwitchX.Ingest.Tests/Transcripts/TranscriptIndexerTests.cs`:

```csharp
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Ingest.Transcripts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.Ingest.Tests.Transcripts;

public class TranscriptIndexerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-idx-" + Guid.NewGuid().ToString("N"));
    private readonly ClaudeCodePaths _claude;
    private readonly string _projectDir;
    // Five minutes after the fixture timestamps, so only lines stamped "now minus a few seconds" count as recent.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 10, 5, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IUsageStore _cursors = Substitute.For<IUsageStore>();
    private readonly List<TranscriptUpdate> _updates = [];
    private readonly TranscriptIndexer _indexer;

    public TranscriptIndexerTests()
    {
        _claude = new ClaudeCodePaths(_home);
        _projectDir = Path.Combine(_claude.ProjectsDirectory, "C--Repo-App");
        Directory.CreateDirectory(_projectDir);
        _cursors.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>([]));
        _bus.Subscribe<TranscriptUpdated>(m => _updates.Add(m.Update));
        _indexer = new TranscriptIndexer(_claude, _cursors, _bus, _time, NullLogger<TranscriptIndexer>.Instance, new TranscriptIndexerOptions());
    }

    public void Dispose()
    {
        _indexer.Dispose();
        Directory.Delete(_home, recursive: true);
    }

    private string Transcript(string sessionId) => Path.Combine(_projectDir, sessionId + ".jsonl");

    private static string User(string session, string text, string ts = "2026-09-23T10:00:00.000Z") =>
        $$$"""{"type":"user","sessionId":"{{{session}}}","cwd":"C:\\Repo\\App","timestamp":"{{{ts}}}","message":{"role":"user","content":"{{{text}}}"}}""";

    private static string Assistant(string session, string messageId, string content, string ts = "2026-09-23T10:00:05.000Z",
        int input = 100, int output = 20, int cacheWrite = 500, int cacheRead = 3000) =>
        $$$"""{"type":"assistant","sessionId":"{{{session}}}","timestamp":"{{{ts}}}","message":{"id":"{{{messageId}}}","model":"claude-sonnet-5","role":"assistant","content":[{{{content}}}],"usage":{"input_tokens":{{{input}}},"output_tokens":{{{output}}},"cache_creation_input_tokens":{{{cacheWrite}}},"cache_read_input_tokens":{{{cacheRead}}}}}}""";

    private const string TextBlock = """{"type":"text","text":"Sure"}""";
    private const string ToolBlock = """{"type":"tool_use","id":"tu1","name":"Bash","input":{"command":"ls"}}""";
    private static string ToolResult(string session, string ts = "2026-09-23T10:00:06.000Z") =>
        $$$"""{"type":"user","sessionId":"{{{session}}}","timestamp":"{{{ts}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"ok"}]}}""";
    private static string Summary(string title) => $$"""{"type":"summary","summary":"{{title}}","leafUuid":"x"}""";

    [Fact]
    public async Task First_scan_reports_title_cwd_model_usage_and_context()
    {
        File.WriteAllLines(Transcript("s1"),
        [
            User("s1", "Fix the build please"),
            Assistant("s1", "msg_1", TextBlock),
            Assistant("s1", "msg_1", ToolBlock),
            ToolResult("s1"),
            Assistant("s1", "msg_2", TextBlock, ts: "2026-09-23T10:00:08.000Z", input: 50, output: 5, cacheWrite: 0, cacheRead: 3600),
        ]);

        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.SessionId.ShouldBe("s1");
        update.TranscriptPath.ShouldBe(Transcript("s1"));
        update.Title.ShouldBe("Fix the build please");
        update.Cwd.ShouldBe(@"C:\Repo\App");
        update.Model.ShouldBe("claude-sonnet-5");
        update.Usage.Count.ShouldBe(2, "msg_1 appears twice but counts once");
        update.Usage[0].Tokens.ShouldBe(new TokenUsage(100, 20, 500, 3000));
        update.Usage[1].Tokens.ShouldBe(new TokenUsage(50, 5, 0, 3600));
        update.LatestContext.ShouldBe(new TokenUsage(50, 5, 0, 3600));
        update.LastActivityAt.ShouldBe(new DateTimeOffset(2026, 9, 23, 10, 0, 8, TimeSpan.Zero));
    }

    [Fact]
    public async Task Summary_title_beats_the_first_prompt()
    {
        File.WriteAllLines(Transcript("s1"), [Summary("Build fixes"), User("s1", "Fix the build please")]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Title.ShouldBe("Build fixes");
    }

    [Fact]
    public async Task Session_id_falls_back_to_the_file_name()
    {
        File.WriteAllLines(Transcript("from-name"), ["""{"type":"user","message":{"role":"user","content":"hi"}}"""]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().SessionId.ShouldBe("from-name");
    }

    [Fact]
    public async Task Second_scan_only_reports_new_lines_and_dedupes_across_scans()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix"), Assistant("s1", "msg_1", TextBlock)]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldBeEmpty();

        File.AppendAllLines(Transcript("s1"), [Assistant("s1", "msg_1", ToolBlock)]);
        await _indexer.ScanAsync(CancellationToken.None);

        var update = _updates.ShouldHaveSingleItem();
        update.Usage.ShouldBeEmpty();
        update.Title.ShouldBeNull("the title was already reported");
    }

    [Fact]
    public async Task A_partial_trailing_line_is_not_consumed_until_completed()
    {
        var path = Transcript("s1");
        File.WriteAllText(path, User("s1", "Fix") + "\n" + Assistant("s1", "msg_1", TextBlock)[..40]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.ShouldHaveSingleItem().Usage.ShouldBeEmpty();
        _updates.Clear();

        File.AppendAllText(path, Assistant("s1", "msg_1", TextBlock)[40..] + "\n");
        await _indexer.ScanAsync(CancellationToken.None);

        _updates.ShouldHaveSingleItem().Usage.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Truncated_file_restarts_from_zero_without_throwing()
    {
        var path = Transcript("s1");
        File.WriteAllLines(path, [User("s1", "one"), User("s1", "two"), User("s1", "three")]);
        await _indexer.ScanAsync(CancellationToken.None);
        _updates.Clear();

        File.WriteAllLines(path, [User("s1", "fresh")]);
        _time.Advance(TimeSpan.FromSeconds(1));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        await Should.NotThrowAsync(() => _indexer.ScanAsync(CancellationToken.None));

        _updates.ShouldHaveSingleItem().Title.ShouldBe("fresh");
    }

    [Fact]
    public async Task Cursors_are_persisted_after_each_scan_and_restored_on_start()
    {
        File.WriteAllLines(Transcript("s1"), [User("s1", "Fix")]);
        await _indexer.ScanAsync(CancellationToken.None);

        await _cursors.Received(1).UpsertCursorsAsync(
            Arg.Is<IReadOnlyCollection<TranscriptCursor>>(c => c.Count == 1 && c.First().SessionId == "s1" && c.First().ByteOffset == new FileInfo(Transcript("s1")).Length),
            Arg.Any<CancellationToken>());

        var restored = Substitute.For<IUsageStore>();
        restored.GetCursorsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TranscriptCursor>>(
        [
            new TranscriptCursor { Path = Transcript("s1").ToLowerInvariant(), ByteOffset = new FileInfo(Transcript("s1")).Length, LastWriteUtc = DateTimeOffset.UtcNow, SessionId = "s1" },
        ]));
        using var second = new TranscriptIndexer(_claude, restored, _bus, _time, NullLogger<TranscriptIndexer>.Instance, new TranscriptIndexerOptions());
        _updates.Clear();

        await second.ScanAsync(CancellationToken.None);

        _updates.ShouldBeEmpty("the restored cursor already covers the file");
    }

    [Fact]
    public async Task Inferred_signal_is_working_when_recent_waiting_with_pending_tool_and_stop_otherwise()
    {
        var recent = _time.GetUtcNow().AddSeconds(-2).ToString("O");
        File.WriteAllLines(Transcript("recent"), [User("recent", "go", recent), Assistant("recent", "m", TextBlock, ts: recent)]);
        File.WriteAllLines(Transcript("pending"), [Assistant("pending", "m", ToolBlock, ts: recent)]);
        File.WriteAllLines(Transcript("old"), [User("old", "go"), Assistant("old", "m", TextBlock)]);

        await _indexer.ScanAsync(CancellationToken.None);

        _updates.Single(u => u.SessionId == "recent").InferredSignal.ShouldBe(SessionSignal.ToolUse);
        _updates.Single(u => u.SessionId == "pending").InferredSignal.ShouldBe(SessionSignal.Notification);
        _updates.Single(u => u.SessionId == "old").InferredSignal.ShouldBe(SessionSignal.Stop);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests --filter FullyQualifiedName~Transcripts`
Expected: build errors for the missing `Transcripts` types.

- [ ] **Step 4: Implement the line model, parser and tailer**

`src/CodeSwitchX.Ingest/Transcripts/TranscriptLine.cs`:

```csharp
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Transcripts;

public abstract record TranscriptLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd);

public sealed record AssistantLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd,
    string? MessageId, string? Model, TokenUsage? Usage, bool HasToolUse) : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record UserLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd,
    string? Text, bool IsToolResult, bool IsMeta) : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record SummaryLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd, string Title)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);

public sealed record OtherLine(string Type, DateTimeOffset? Timestamp, string? SessionId, string? Cwd)
    : TranscriptLine(Type, Timestamp, SessionId, Cwd);
```

`src/CodeSwitchX.Ingest/Transcripts/TranscriptLineParser.cs`:

```csharp
using System.Text.Json;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Ingest.Transcripts;

public static class TranscriptLineParser
{
    private static readonly string[] MetaPrefixes = ["<command-name>", "<local-command-stdout>", "<local-command-stderr>", "<system-reminder>", "<command-message>"];

    public static TranscriptLine? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = GetString(root, "type") ?? string.Empty;
            var timestamp = GetString(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : (DateTimeOffset?)null;
            var sessionId = GetString(root, "sessionId");
            var cwd = GetString(root, "cwd");
            root.TryGetProperty("message", out var message);

            switch (type)
            {
                case "assistant":
                {
                    var usage = ParseUsage(message);
                    var hasToolUse = message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array
                        && content.EnumerateArray().Any(b => GetString(b, "type") == "tool_use");
                    return new AssistantLine(type, timestamp, sessionId, cwd, GetString(message, "id"), GetString(message, "model"), usage, hasToolUse);
                }

                case "user":
                {
                    var isMeta = root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True;
                    var (text, isToolResult) = ExtractUserText(message);
                    if (text is not null && MetaPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
                    {
                        isMeta = true;
                    }

                    return new UserLine(type, timestamp, sessionId, cwd, text, isToolResult, isMeta);
                }

                case "summary":
                    return GetString(root, "summary") is { Length: > 0 } title
                        ? new SummaryLine(type, timestamp, sessionId, cwd, title)
                        : new OtherLine(type, timestamp, sessionId, cwd);

                default:
                    return new OtherLine(type, timestamp, sessionId, cwd);
            }
        }
    }

    private static TokenUsage? ParseUsage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new TokenUsage(
            GetLong(usage, "input_tokens"),
            GetLong(usage, "output_tokens"),
            GetLong(usage, "cache_creation_input_tokens"),
            GetLong(usage, "cache_read_input_tokens"));
    }

    private static (string? Text, bool IsToolResult) ExtractUserText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
        {
            return (null, false);
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return (content.GetString(), false);
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return (null, false);
        }

        var isToolResult = false;
        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { Length: > 0 } text:
                    return (text, false);
                case "tool_result":
                    isToolResult = true;
                    break;
            }
        }

        return (null, isToolResult);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l) ? l : 0;
}
```

`src/CodeSwitchX.Ingest/Transcripts/TranscriptTailer.cs`:

```csharp
using System.Text;

namespace CodeSwitchX.Ingest.Transcripts;

public readonly record struct TailResult(IReadOnlyList<string> Lines, long NewOffset, bool Truncated);

/// <summary>Reads whole lines appended after a byte offset; a trailing line without a newline is left for the next call.</summary>
public static class TranscriptTailer
{
    public static TailResult ReadNewLines(string path, long fromOffset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var truncated = false;
        if (fromOffset > stream.Length)
        {
            truncated = true;
            fromOffset = 0;
        }

        if (fromOffset == stream.Length)
        {
            return new TailResult([], fromOffset, truncated);
        }

        stream.Position = fromOffset;
        var buffer = new byte[stream.Length - fromOffset];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0)
        {
            return new TailResult([], fromOffset, truncated);
        }

        var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        return new TailResult(lines, fromOffset + lastNewline + 1, truncated);
    }
}
```

- [ ] **Step 5: Implement the indexer**

`src/CodeSwitchX.Ingest/Transcripts/TranscriptIndexerOptions.cs`:

```csharp
namespace CodeSwitchX.Ingest.Transcripts;

public sealed class TranscriptIndexerOptions
{
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A transcript written within this window counts as Working when hooks are absent.</summary>
    public TimeSpan WorkingWindow { get; set; } = TimeSpan.FromSeconds(5);
    public int MessageIdMemory { get; set; } = 512;
}
```

`src/CodeSwitchX.Ingest/Transcripts/TranscriptIndexer.cs`:

```csharp
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Transcripts;

/// <summary>
/// Tails every <c>*.jsonl</c> under <c>~/.claude/projects</c> from its stored byte offset and publishes
/// <see cref="TranscriptUpdated"/> with titles, usage deltas and an inferred state signal.
/// </summary>
public sealed class TranscriptIndexer : BackgroundService
{
    private readonly ClaudeCodePaths _claude;
    private readonly IUsageStore _cursorStore;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<TranscriptIndexer> _logger;
    private readonly TranscriptIndexerOptions _options;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private bool _cursorsLoaded;
    private FileSystemWatcher? _watcher;
    private volatile bool _dirty = true;

    public TranscriptIndexer(ClaudeCodePaths claude, IUsageStore cursorStore, IEventBus bus, TimeProvider time,
        ILogger<TranscriptIndexer> logger, TranscriptIndexerOptions options)
    {
        _claude = claude;
        _cursorStore = cursorStore;
        _bus = bus;
        _time = time;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartWatcher();
        using var timer = new PeriodicTimer(_options.ScanInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (_dirty || _watcher is null)
                {
                    _dirty = false;
                    await ScanAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureCursorsLoadedAsync(ct).ConfigureAwait(false);
            if (!Directory.Exists(_claude.ProjectsDirectory))
            {
                return;
            }

            var changed = new List<TranscriptCursor>();
            foreach (var path in Directory.EnumerateFiles(_claude.ProjectsDirectory, "*.jsonl", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var cursor = await ProcessFileCoreAsync(path, ct).ConfigureAwait(false);
                if (cursor is not null)
                {
                    changed.Add(cursor);
                }
            }

            if (changed.Count > 0)
            {
                await _cursorStore.UpsertCursorsAsync(changed, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    internal async Task ProcessFileAsync(string path, CancellationToken ct)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureCursorsLoadedAsync(ct).ConfigureAwait(false);
            var cursor = await ProcessFileCoreAsync(path, ct).ConfigureAwait(false);
            if (cursor is not null)
            {
                await _cursorStore.UpsertCursorsAsync([cursor], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task EnsureCursorsLoadedAsync(CancellationToken ct)
    {
        if (_cursorsLoaded)
        {
            return;
        }

        foreach (var cursor in await _cursorStore.GetCursorsAsync(ct).ConfigureAwait(false))
        {
            _files[cursor.Path] = new FileState(_options.MessageIdMemory)
            {
                Offset = cursor.ByteOffset,
                LastWriteUtc = cursor.LastWriteUtc,
                SessionId = cursor.SessionId,
                TitleReported = true,
            };
        }

        _cursorsLoaded = true;
    }

    private Task<TranscriptCursor?> ProcessFileCoreAsync(string path, CancellationToken ct)
    {
        var key = PathNormalizer.Normalize(path);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                return Task.FromResult<TranscriptCursor?>(null);
            }
        }
        catch (IOException)
        {
            return Task.FromResult<TranscriptCursor?>(null);
        }

        if (!_files.TryGetValue(key, out var state))
        {
            state = new FileState(_options.MessageIdMemory);
            _files[key] = state;
        }

        if (info.Length == state.Offset && state.Offset > 0)
        {
            return Task.FromResult<TranscriptCursor?>(null);
        }

        TailResult tail;
        try
        {
            tail = TranscriptTailer.ReadNewLines(path, state.Offset);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Transcript {Path} is not readable right now", path);
            return Task.FromResult<TranscriptCursor?>(null);
        }

        if (tail.Truncated)
        {
            _logger.LogInformation("Transcript {Path} shrank below the stored offset; re-reading from the start", path);
            state.Reset();
        }

        if (tail.Lines.Count == 0 && tail.NewOffset == state.Offset)
        {
            return Task.FromResult<TranscriptCursor?>(null);
        }

        var update = BuildUpdate(path, state, tail.Lines);
        state.Offset = tail.NewOffset;
        state.LastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (update is not null)
        {
            _bus.Publish(new TranscriptUpdated(update));
        }

        return Task.FromResult<TranscriptCursor?>(new TranscriptCursor
        {
            Path = key, ByteOffset = state.Offset, LastWriteUtc = state.LastWriteUtc, SessionId = state.SessionId,
        });
    }

    private TranscriptUpdate? BuildUpdate(string path, FileState state, IReadOnlyList<string> lines)
    {
        var usage = new List<UsageDelta>();
        string? newTitle = null;
        var summaryTitle = false;
        string? cwd = null;
        string? model = null;
        DateTimeOffset? lastActivity = null;
        TokenUsage? latestContext = null;
        var pendingToolUse = state.PendingToolUse;

        foreach (var raw in lines)
        {
            var line = TranscriptLineParser.TryParse(raw);
            if (line is null)
            {
                _logger.LogDebug("Skipping unparsable line in {Path}", path);
                continue;
            }

            state.SessionId ??= line.SessionId;
            cwd ??= line.Cwd;
            if (line.Timestamp is { } ts && (lastActivity is null || ts > lastActivity))
            {
                lastActivity = ts;
            }

            switch (line)
            {
                case AssistantLine assistant:
                    model = assistant.Model ?? model;
                    if (assistant.HasToolUse)
                    {
                        pendingToolUse = true;
                    }

                    if (assistant.Usage is { } tokens && assistant.MessageId is { } id && state.RememberMessage(id))
                    {
                        usage.Add(new UsageDelta(assistant.Model ?? model ?? "unknown", assistant.Timestamp ?? _time.GetUtcNow(), tokens));
                        latestContext = tokens;
                    }
                    else if (assistant.Usage is { } sameMessage)
                    {
                        latestContext = sameMessage;
                    }

                    break;

                case UserLine user:
                    if (user.IsToolResult)
                    {
                        pendingToolUse = false;
                    }
                    else if (!user.IsMeta && user.Text is not null)
                    {
                        pendingToolUse = false;
                        if (!state.TitleReported && !summaryTitle && newTitle is null)
                        {
                            newTitle = ChatTitle.FromPrompt(user.Text);
                        }
                    }

                    break;

                case SummaryLine summary:
                    if (!state.HasSummary)
                    {
                        newTitle = ChatTitle.FromPrompt(summary.Title);
                        summaryTitle = true;
                        state.HasSummary = true;
                    }

                    break;
            }
        }

        state.PendingToolUse = pendingToolUse;
        if (newTitle is not null)
        {
            state.TitleReported = true;
        }

        var sessionId = state.SessionId ?? Path.GetFileNameWithoutExtension(path);
        state.SessionId = sessionId;

        var now = _time.GetUtcNow();
        var recentlyWritten = lastActivity is { } last && now - last <= _options.WorkingWindow;
        var inferred = !recentlyWritten ? SessionSignal.Stop
            : pendingToolUse ? SessionSignal.Notification
            : SessionSignal.ToolUse;

        return new TranscriptUpdate
        {
            SessionId = sessionId,
            TranscriptPath = path,
            ObservedAt = now,
            Title = newTitle,
            Cwd = cwd,
            Model = model,
            LastActivityAt = lastActivity,
            Usage = usage,
            LatestContext = latestContext,
            InferredSignal = inferred,
        };
    }

    private void StartWatcher()
    {
        try
        {
            Directory.CreateDirectory(_claude.ProjectsDirectory);
            _watcher = new FileSystemWatcher(_claude.ProjectsDirectory, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, _) => _dirty = true;
            _watcher.Created += (_, _) => _dirty = true;
            _watcher.Renamed += (_, _) => _dirty = true;
            _watcher.Error += (_, e) =>
            {
                _logger.LogWarning(e.GetException(), "Transcript watcher error; falling back to periodic scans");
                _dirty = true;
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Cannot watch {Dir}; using periodic scans only", _claude.ProjectsDirectory);
            _watcher = null;
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _scanGate.Dispose();
        base.Dispose();
    }

    private sealed class FileState(int memory)
    {
        private readonly Queue<string> _recent = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public long Offset { get; set; }
        public DateTimeOffset LastWriteUtc { get; set; }
        public string? SessionId { get; set; }
        public bool TitleReported { get; set; }
        public bool HasSummary { get; set; }
        public bool PendingToolUse { get; set; }

        /// <returns>True when the id was not seen before.</returns>
        public bool RememberMessage(string id)
        {
            if (!_seen.Add(id))
            {
                return false;
            }

            _recent.Enqueue(id);
            while (_recent.Count > memory)
            {
                _seen.Remove(_recent.Dequeue());
            }

            return true;
        }

        public void Reset()
        {
            Offset = 0;
            _recent.Clear();
            _seen.Clear();
            TitleReported = false;
            HasSummary = false;
            PendingToolUse = false;
        }
    }
}
```

Add to `IngestServiceCollectionExtensions.AddCodeSwitchXIngest` (before `return services;`):

```csharp
services.AddSingleton<TranscriptIndexerOptions>();
services.AddSingleton<TranscriptIndexer>();
services.AddHostedService(sp => sp.GetRequiredService<TranscriptIndexer>());
```

Add `<InternalsVisibleTo Include="CodeSwitchX.Ingest.Tests" />` to `CodeSwitchX.Ingest.csproj`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests`
Expected: all pass. In `Truncated_file_restarts_from_zero_without_throwing` the rewritten file is shorter than the stored offset, so `TailResult.Truncated` triggers `FileState.Reset()` and the title is reported again.

---

### Task 10: Claude Code hook installer

**Files:**
- Create: `src/CodeSwitchX.Ingest/Hooks/HookInstallStatus.cs`, `HookInstallException.cs`, `ClaudeHookInstaller.cs`
- Modify: `src/CodeSwitchX.Ingest/IngestServiceCollectionExtensions.cs`
- Test: `tests/CodeSwitchX.Ingest.Tests/Hooks/ClaudeHookInstallerTests.cs`

**Interfaces:**
- Consumes: `ClaudeCodePaths` (Task 1).
- Produces: `enum HookInstallState { NotInstalled, Partial, Outdated, Installed }`; `HookInstallStatus(HookInstallState State, IReadOnlyList<string> InstalledEvents, IReadOnlyList<string> MissingEvents, string SettingsFile)`; `HookInstallResult(bool Changed, string? BackupFile)`; `ClaudeHookInstaller(ClaudeCodePaths, ILogger<ClaudeHookInstaller>)` with `HookInstallStatus GetStatus(string relayExecutable)`, `HookInstallResult Install(string relayExecutable)`, `HookInstallResult Uninstall()`, `static string[] Events`, `const string Marker = "csx-hook"`, `static string BuildCommand(string exe, string eventName)`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Ingest.Tests/Hooks/ClaudeHookInstallerTests.cs`:

```csharp
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using CodeSwitchX.Ingest.Hooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Ingest.Tests.Hooks;

public class ClaudeHookInstallerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "csx-hooks-" + Guid.NewGuid().ToString("N"));
    private readonly ClaudeCodePaths _paths;
    private readonly ClaudeHookInstaller _installer;
    private const string Exe = @"C:\Program Files\CodeSwitchX\csx-hook.exe";

    public ClaudeHookInstallerTests()
    {
        _paths = new ClaudeCodePaths(_home);
        _installer = new ClaudeHookInstaller(_paths, NullLogger<ClaudeHookInstaller>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private JsonObject Settings() => JsonNode.Parse(File.ReadAllText(_paths.SettingsFile))!.AsObject();

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_paths.ClaudeDirectory);
        File.WriteAllText(_paths.SettingsFile, json);
    }

    [Fact]
    public void Install_into_a_missing_file_creates_all_eight_events()
    {
        var result = _installer.Install(Exe);

        result.Changed.ShouldBeTrue();
        result.BackupFile.ShouldBeNull();
        var hooks = Settings()["hooks"]!.AsObject();
        hooks.Select(kv => kv.Key).ShouldBe(ClaudeHookInstaller.Events, ignoreOrder: true);
        var pre = hooks["PreToolUse"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        pre.ContainsKey("matcher").ShouldBeFalse();
        var hook = pre["hooks"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        hook["type"]!.GetValue<string>().ShouldBe("command");
        hook["command"]!.GetValue<string>().ShouldBe($"\"{Exe}\" PreToolUse");
        hook["timeout"]!.GetValue<int>().ShouldBe(5);
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
    }

    [Fact]
    public void Install_preserves_unrelated_settings_and_foreign_hooks_and_backs_up_first()
    {
        WriteSettings("""
        {
          "permissions": { "allow": ["Bash(git *)"] },
          "hooks": {
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo foreign" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "notify.exe" } ] } ]
          }
        }
        """);

        var result = _installer.Install(Exe);

        result.BackupFile.ShouldNotBeNull();
        File.Exists(result.BackupFile).ShouldBeTrue();
        File.ReadAllText(result.BackupFile!).ShouldContain("echo foreign");
        var settings = Settings();
        settings["permissions"]!["allow"]![0]!.GetValue<string>().ShouldBe("Bash(git *)");
        var pre = settings["hooks"]!["PreToolUse"]!.AsArray();
        pre.Count.ShouldBe(2);
        pre[0]!["matcher"]!.GetValue<string>().ShouldBe("Bash");
        settings["hooks"]!["Stop"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Install_is_idempotent()
    {
        _installer.Install(Exe);
        var before = File.ReadAllText(_paths.SettingsFile);

        var result = _installer.Install(Exe);

        result.Changed.ShouldBeFalse();
        File.ReadAllText(_paths.SettingsFile).ShouldBe(before);
        Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*").Length.ShouldBe(1);
    }

    [Fact]
    public void A_moved_executable_reports_Outdated_and_reinstall_fixes_the_path()
    {
        _installer.Install(@"C:\old\csx-hook.exe");

        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Outdated);

        _installer.Install(Exe).Changed.ShouldBeTrue();
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.Installed);
        Settings()["hooks"]!["Stop"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Partial_installs_are_detected()
    {
        _installer.Install(Exe);
        var settings = Settings();
        settings["hooks"]!.AsObject().Remove("Notification");
        File.WriteAllText(_paths.SettingsFile, settings.ToJsonString());

        var status = _installer.GetStatus(Exe);

        status.State.ShouldBe(HookInstallState.Partial);
        status.MissingEvents.ShouldBe(["Notification"]);
    }

    [Fact]
    public void Uninstall_removes_only_our_entries_and_empty_containers()
    {
        WriteSettings("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify.exe"}]}]},"theme":"dark"}""");
        _installer.Install(Exe);

        var result = _installer.Uninstall();

        result.Changed.ShouldBeTrue();
        var settings = Settings();
        settings["theme"]!.GetValue<string>().ShouldBe("dark");
        var hooks = settings["hooks"]!.AsObject();
        hooks.Select(kv => kv.Key).ShouldBe(["Stop"]);
        hooks["Stop"]!.AsArray().ShouldHaveSingleItem()!["hooks"]!.AsArray().ShouldHaveSingleItem()!["command"]!.GetValue<string>().ShouldBe("notify.exe");
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public void Uninstall_drops_the_hooks_object_when_nothing_is_left()
    {
        _installer.Install(Exe);

        _installer.Uninstall();

        Settings().ContainsKey("hooks").ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Malformed_settings_are_refused_and_left_untouched(string content)
    {
        WriteSettings(content);

        Should.Throw<HookInstallException>(() => _installer.Install(Exe));

        File.ReadAllText(_paths.SettingsFile).ShouldBe(content);
        Directory.GetFiles(_paths.ClaudeDirectory, "settings.json.csx-backup-*").ShouldBeEmpty();
        _installer.GetStatus(Exe).State.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public void Command_quotes_the_executable_path()
    {
        ClaudeHookInstaller.BuildCommand(Exe, "Stop").ShouldBe("\"C:\\Program Files\\CodeSwitchX\\csx-hook.exe\" Stop");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests --filter FullyQualifiedName~ClaudeHookInstallerTests`
Expected: build errors for the missing installer types.

- [ ] **Step 3: Implement the installer**

`src/CodeSwitchX.Ingest/Hooks/HookInstallStatus.cs`:

```csharp
namespace CodeSwitchX.Ingest.Hooks;

public enum HookInstallState
{
    NotInstalled,
    Partial,
    Outdated,
    Installed,
}

public sealed record HookInstallStatus(HookInstallState State, IReadOnlyList<string> InstalledEvents, IReadOnlyList<string> MissingEvents, string SettingsFile);

public sealed record HookInstallResult(bool Changed, string? BackupFile);
```

`src/CodeSwitchX.Ingest/Hooks/HookInstallException.cs`:

```csharp
namespace CodeSwitchX.Ingest.Hooks;

public sealed class HookInstallException(string message, Exception? inner = null) : Exception(message, inner);
```

`src/CodeSwitchX.Ingest/Hooks/ClaudeHookInstaller.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSwitchX.Core;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Ingest.Hooks;

/// <summary>
/// Merges csx-hook entries into the user-level Claude Code settings.json. Our entries are the ones whose
/// command contains <see cref="Marker"/>; nothing else in the file is ever touched.
/// </summary>
public sealed class ClaudeHookInstaller
{
    public const string Marker = "csx-hook";
    public const int TimeoutSeconds = 5;

    public static readonly string[] Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SubagentStop", "SessionEnd",
    ];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ClaudeCodePaths _paths;
    private readonly ILogger<ClaudeHookInstaller> _logger;

    public ClaudeHookInstaller(ClaudeCodePaths paths, ILogger<ClaudeHookInstaller> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public static string BuildCommand(string relayExecutable, string eventName) => $"\"{relayExecutable}\" {eventName}";

    public HookInstallStatus GetStatus(string relayExecutable)
    {
        JsonObject settings;
        try
        {
            settings = Load();
        }
        catch (HookInstallException)
        {
            return new HookInstallStatus(HookInstallState.NotInstalled, [], Events, _paths.SettingsFile);
        }

        var installed = new List<string>();
        var outdated = false;
        foreach (var eventName in Events)
        {
            var ours = OurHooks(settings, eventName).ToList();
            if (ours.Count == 0)
            {
                continue;
            }

            installed.Add(eventName);
            var expected = BuildCommand(relayExecutable, eventName);
            if (ours.Any(h => !string.Equals(h["command"]?.GetValue<string>(), expected, StringComparison.OrdinalIgnoreCase)))
            {
                outdated = true;
            }
        }

        var missing = Events.Except(installed).ToList();
        var state = installed.Count == 0 ? HookInstallState.NotInstalled
            : missing.Count > 0 ? HookInstallState.Partial
            : outdated ? HookInstallState.Outdated
            : HookInstallState.Installed;
        return new HookInstallStatus(state, installed, missing, _paths.SettingsFile);
    }

    public HookInstallResult Install(string relayExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relayExecutable);
        var settings = Load();
        var before = settings.ToJsonString(WriteOptions);

        var hooks = settings["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = [];
            settings["hooks"] = hooks;
        }

        foreach (var eventName in Events)
        {
            var groups = hooks[eventName] as JsonArray;
            if (groups is null)
            {
                groups = [];
                hooks[eventName] = groups;
            }

            var command = BuildCommand(relayExecutable, eventName);
            var ours = OurHooks(settings, eventName).ToList();
            if (ours.Count == 0)
            {
                groups.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                        ["timeout"] = TimeoutSeconds,
                    }),
                });
            }
            else
            {
                foreach (var hook in ours)
                {
                    hook["command"] = command;
                }
            }
        }

        return Save(settings, before);
    }

    public HookInstallResult Uninstall()
    {
        var settings = Load();
        var before = settings.ToJsonString(WriteOptions);

        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var eventName in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[eventName] is not JsonArray groups)
                {
                    continue;
                }

                foreach (var group in groups.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is JsonArray entries)
                    {
                        foreach (var entry in entries.OfType<JsonObject>().Where(IsOurs).ToList())
                        {
                            entries.Remove(entry);
                        }

                        if (entries.Count == 0)
                        {
                            groups.Remove(group);
                        }
                    }
                }

                if (groups.Count == 0)
                {
                    hooks.Remove(eventName);
                }
            }

            if (hooks.Count == 0)
            {
                settings.Remove("hooks");
            }
        }

        return Save(settings, before);
    }

    private static IEnumerable<JsonObject> OurHooks(JsonObject settings, string eventName)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[eventName] is not JsonArray groups)
        {
            yield break;
        }

        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries.OfType<JsonObject>().Where(IsOurs))
            {
                yield return entry;
            }
        }
    }

    private static bool IsOurs(JsonObject hook) =>
        hook["command"] is JsonValue value
        && value.TryGetValue<string>(out var command)
        && command.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    private JsonObject Load()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(_paths.SettingsFile);
        }
        catch (IOException ex)
        {
            throw new HookInstallException($"Cannot read {_paths.SettingsFile}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new HookInstallException($"{_paths.SettingsFile} is empty; refusing to overwrite it. Fix or delete the file and retry.");
        }

        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new HookInstallException($"{_paths.SettingsFile} does not contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new HookInstallException($"{_paths.SettingsFile} is not valid JSON: {ex.Message}", ex);
        }
    }

    private HookInstallResult Save(JsonObject settings, string before)
    {
        var after = settings.ToJsonString(WriteOptions);
        if (after == before && File.Exists(_paths.SettingsFile))
        {
            return new HookInstallResult(false, null);
        }

        Directory.CreateDirectory(_paths.ClaudeDirectory);
        string? backup = null;
        if (File.Exists(_paths.SettingsFile))
        {
            backup = $"{_paths.SettingsFile}.csx-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
            File.Copy(_paths.SettingsFile, backup, overwrite: true);
        }

        var tmp = _paths.SettingsFile + ".csx-tmp";
        File.WriteAllText(tmp, after + Environment.NewLine);
        File.Move(tmp, _paths.SettingsFile, overwrite: true);
        _logger.LogInformation("Updated {File} (backup: {Backup})", _paths.SettingsFile, backup ?? "none");
        return new HookInstallResult(true, backup);
    }
}
```

Add to `IngestServiceCollectionExtensions.AddCodeSwitchXIngest`: `services.AddSingleton<ClaudeHookInstaller>();`

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Ingest.Tests`
Expected: all pass. In `Install_is_idempotent` the second install compares the re-serialised JSON with the original, finds no difference and creates no second backup.

---

### Task 11: Telemetry — pricing, cost, context fill, aggregation

**Files:**
- Create: `src/CodeSwitchX.Telemetry/DefaultPricing.cs`, `PricingTable.cs`, `CostEstimator.cs`, `ContextFillCalculator.cs`, `UsageTotals.cs`, `UsageAggregator.cs`, `TelemetrySnapshot.cs`, `TelemetryService.cs`, `TelemetryServiceCollectionExtensions.cs`
- Test: `tests/CodeSwitchX.Telemetry.Tests/PricingTableTests.cs`, `CostEstimatorTests.cs`, `ContextFillCalculatorTests.cs`, `UsageAggregatorTests.cs`, `TelemetryServiceTests.cs`

**Interfaces:**
- Consumes: `PricingRule`, `UsageBucket`, `IUsageStore`, `ISettingsStore` (Task 5); `TokenUsage`, `UsageDelta`, `TranscriptUpdate`, `TranscriptUpdated`, `IEventBus` (Task 4).
- Produces:
  - `DefaultPricing.Rules : IReadOnlyList<PricingRule>` — USD per million tokens, cache write = 1.25 × input and cache read = 0.10 × input unless Anthropic publishes a different rate (Fable 5.1 cache read 0.25, Opus 5.5 cache read 0.20). Values are estimates the user can edit.
  - `PricingTable(IEnumerable<PricingRule>)` with `PricingRule Find(string? model)` (longest segment-boundary prefix match, else `PricingTable.Fallback`), `static PricingTable Default`.
  - `CostEstimator.Estimate(TokenUsage, PricingRule) -> decimal`.
  - `ContextFillCalculator.Fill(TokenUsage latest, PricingRule) -> double` (0..1) and `enum ContextPressure { Normal, Amber, Red }` via `Level(double fill)` (Amber ≥ 0.8, Red ≥ 0.9).
  - `readonly record struct UsageTotals(TokenUsage Tokens, decimal Cost)`.
  - `UsageAggregator(PricingTable)` with `Sum(IEnumerable<UsageBucket>)`, `Window(buckets, DateTimeOffset now, TimeSpan span)`, `Today(buckets, now, TimeZoneInfo)`, `RateSeries(buckets, now, int minutes) -> long[]` (tokens per minute, oldest first), `BySession(buckets) -> IReadOnlyDictionary<string, UsageTotals>`.
  - `TelemetrySnapshot(UsageTotals Today, UsageTotals FiveHours, long[] RatePerMinute, DateTimeOffset AsOf)`; message `TelemetryUpdated(TelemetrySnapshot Snapshot)`.
  - `TelemetryService : IHostedService` with `TelemetrySnapshot Current`, `PricingTable Pricing`, `Task ReloadPricingAsync(CancellationToken)`; `AddCodeSwitchXTelemetry(this IServiceCollection)`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Telemetry.Tests/PricingTableTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class PricingTableTests
{
    [Theory]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("claude-sonnet-5-20260115", "claude-sonnet-5")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("claude-opus-5", "claude-opus-5")]
    [InlineData("claude-opus-5-20260401", "claude-opus-5")]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("CLAUDE-HAIKU-4-5-20251001", "claude-haiku-4-5")]
    public void Find_uses_the_longest_prefix_on_a_segment_boundary(string model, string expectedRule)
    {
        PricingTable.Default.Find(model).Model.ShouldBe(expectedRule);
    }

    [Theory]
    [InlineData("claude-opus-55")]
    [InlineData("gpt-5")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_models_get_the_zero_cost_fallback(string? model)
    {
        var rule = PricingTable.Default.Find(model);

        rule.ShouldBeSameAs(PricingTable.Fallback);
        rule.InputPerM.ShouldBe(0);
        rule.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void Default_rules_cover_the_current_lineup_with_1M_context()
    {
        var rules = DefaultPricing.Rules.ToDictionary(r => r.Model);

        rules["claude-fable-5-1"].ContextWindow.ShouldBe(1_000_000);
        rules["claude-opus-5"].InputPerM.ShouldBe(5m);
        rules["claude-opus-5"].OutputPerM.ShouldBe(25m);
        rules["claude-sonnet-5"].InputPerM.ShouldBe(2m);
        rules["claude-haiku-4-5"].ContextWindow.ShouldBe(200_000);
        rules.Values.ShouldAllBe(r => r.CacheWritePerM >= r.InputPerM && r.CacheReadPerM < r.InputPerM);
    }

    [Fact]
    public void User_rules_replace_defaults_with_the_same_model()
    {
        var table = new PricingTable([new PricingRule { Model = "claude-sonnet-5", InputPerM = 42, ContextWindow = 500_000 }]);

        table.Find("claude-sonnet-5").InputPerM.ShouldBe(42);
        table.Find("claude-sonnet-5").ContextWindow.ShouldBe(500_000);
    }
}
```

`tests/CodeSwitchX.Telemetry.Tests/CostEstimatorTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class CostEstimatorTests
{
    [Fact]
    public void Cost_is_tokens_times_rate_per_million()
    {
        var rule = new PricingRule { Model = "m", InputPerM = 3m, OutputPerM = 15m, CacheWritePerM = 3.75m, CacheReadPerM = 0.30m };
        var usage = new TokenUsage(Input: 1_000_000, Output: 100_000, CacheWrite: 200_000, CacheRead: 2_000_000);

        CostEstimator.Estimate(usage, rule).ShouldBe(3m + 1.5m + 0.75m + 0.60m);
    }

    [Fact]
    public void Zero_usage_costs_nothing()
    {
        CostEstimator.Estimate(TokenUsage.Zero, PricingTable.Default.Find("claude-opus-5")).ShouldBe(0m);
    }
}
```

`tests/CodeSwitchX.Telemetry.Tests/ContextFillCalculatorTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class ContextFillCalculatorTests
{
    private static readonly PricingRule Rule = new() { Model = "m", ContextWindow = 200_000 };

    [Fact]
    public void Fill_is_context_tokens_over_the_window()
    {
        ContextFillCalculator.Fill(new TokenUsage(10_000, 5_000, 20_000, 70_000), Rule).ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void Fill_is_clamped_to_one_and_safe_for_a_zero_window()
    {
        ContextFillCalculator.Fill(new TokenUsage(400_000, 0, 0, 0), Rule).ShouldBe(1.0);
        ContextFillCalculator.Fill(new TokenUsage(1, 0, 0, 0), new PricingRule { Model = "m", ContextWindow = 0 }).ShouldBe(0.0);
    }

    [Theory]
    [InlineData(0.0, ContextPressure.Normal)]
    [InlineData(0.79, ContextPressure.Normal)]
    [InlineData(0.80, ContextPressure.Amber)]
    [InlineData(0.89, ContextPressure.Amber)]
    [InlineData(0.90, ContextPressure.Red)]
    [InlineData(1.0, ContextPressure.Red)]
    public void Pressure_thresholds_follow_the_spec(double fill, ContextPressure expected)
    {
        ContextFillCalculator.Level(fill).ShouldBe(expected);
    }
}
```

`tests/CodeSwitchX.Telemetry.Tests/UsageAggregatorTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class UsageAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 30, 0, TimeSpan.Zero);
    private readonly UsageAggregator _aggregator = new(new PricingTable([new PricingRule { Model = "m", InputPerM = 1m, OutputPerM = 10m, CacheWritePerM = 1m, CacheReadPerM = 0.1m }]));

    private static UsageBucket Bucket(string session, DateTimeOffset minute, long input, long output = 0) =>
        new() { SessionId = session, Model = "m", MinuteUtc = minute, Input = input, Output = output };

    [Fact]
    public void Sum_adds_tokens_and_cost_across_buckets()
    {
        var totals = _aggregator.Sum([Bucket("a", Now, 1_000_000, 100_000), Bucket("b", Now, 1_000_000)]);

        totals.Tokens.Input.ShouldBe(2_000_000);
        totals.Tokens.Output.ShouldBe(100_000);
        totals.Cost.ShouldBe(2m + 1m);
    }

    [Fact]
    public void Window_keeps_buckets_newer_than_now_minus_span()
    {
        var buckets = new[]
        {
            Bucket("a", Now.AddHours(-6), 1),
            Bucket("a", Now.AddHours(-5), 2),
            Bucket("a", Now.AddHours(-4), 4),
            Bucket("a", Now, 8),
        };

        _aggregator.Window(buckets, Now, TimeSpan.FromHours(5)).Tokens.Input.ShouldBe(14);
    }

    [Fact]
    public void Today_uses_the_local_calendar_day()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        // 2026-09-23 00:30 Berlin is 2026-09-22 22:30 UTC: still "today" in Berlin, "yesterday" in UTC.
        var buckets = new[]
        {
            Bucket("a", new DateTimeOffset(2026, 9, 22, 21, 59, 0, TimeSpan.Zero), 1),
            Bucket("a", new DateTimeOffset(2026, 9, 22, 22, 30, 0, TimeSpan.Zero), 2),
            Bucket("a", Now, 4),
        };

        _aggregator.Today(buckets, Now, berlin).Tokens.Input.ShouldBe(6);
        _aggregator.Today(buckets, Now, TimeZoneInfo.Utc).Tokens.Input.ShouldBe(4);
    }

    [Fact]
    public void RateSeries_is_oldest_first_zero_filled_and_counts_all_token_kinds()
    {
        var buckets = new[]
        {
            Bucket("a", Now.AddMinutes(-2), 5, 1),
            new UsageBucket { SessionId = "a", Model = "m", MinuteUtc = Now, Input = 1, CacheRead = 9 },
            Bucket("a", Now.AddMinutes(-10), 100),
        };

        var series = _aggregator.RateSeries(buckets, Now, minutes: 5);

        series.ShouldBe([0, 0, 6, 0, 10]);
    }

    [Fact]
    public void BySession_groups_totals()
    {
        var totals = _aggregator.BySession([Bucket("a", Now, 1), Bucket("b", Now, 2), Bucket("a", Now.AddMinutes(-1), 3)]);

        totals["a"].Tokens.Input.ShouldBe(4);
        totals["b"].Tokens.Input.ShouldBe(2);
    }
}
```

`tests/CodeSwitchX.Telemetry.Tests/TelemetryServiceTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.Telemetry.Tests;

public class TelemetryServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 30, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IUsageStore _usage = Substitute.For<IUsageStore>();
    private readonly ISettingsStore _settings = Substitute.For<ISettingsStore>();

    public TelemetryServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PricingRule>>(
            [new PricingRule { Model = "claude-sonnet-5", InputPerM = 2m, OutputPerM = 10m, CacheWritePerM = 2.5m, CacheReadPerM = 0.2m, ContextWindow = 1_000_000 }]));
        _usage.GetBucketsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<UsageBucket>>(
            [new UsageBucket { SessionId = "old", Model = "claude-sonnet-5", MinuteUtc = _time.GetUtcNow().AddHours(-1), Input = 1_000_000 }]));
    }

    private TelemetryService Service() => new(_usage, _settings, _bus, _time, NullLogger<TelemetryService>.Instance);

    [Fact]
    public async Task Start_seeds_pricing_defaults_and_loads_recent_buckets()
    {
        var service = Service();

        await service.StartAsync(CancellationToken.None);

        await _settings.Received(1).EnsurePricingDefaultsAsync(Arg.Is<IReadOnlyCollection<PricingRule>>(r => r.Count == DefaultPricing.Rules.Count), Arg.Any<CancellationToken>());
        await _usage.Received(1).GetBucketsAsync(_time.GetUtcNow().AddDays(-7), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        service.Current.Today.Tokens.Input.ShouldBe(1_000_000);
        service.Current.Today.Cost.ShouldBe(2m);
        service.Current.FiveHours.Tokens.Input.ShouldBe(1_000_000);
    }

    [Fact]
    public async Task Transcript_usage_updates_the_snapshot_and_publishes()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        TelemetrySnapshot? published = null;
        _bus.Subscribe<TelemetryUpdated>(m => published = m.Snapshot);

        _bus.Publish(new TranscriptUpdated(new TranscriptUpdate
        {
            SessionId = "s1", TranscriptPath = "p", ObservedAt = _time.GetUtcNow(),
            Usage = [new UsageDelta("claude-sonnet-5", _time.GetUtcNow(), new TokenUsage(500_000, 100_000, 0, 0))],
        }));

        published.ShouldNotBeNull();
        published.Today.Tokens.Input.ShouldBe(1_500_000);
        published.Today.Tokens.Output.ShouldBe(100_000);
        published.Today.Cost.ShouldBe(2m + 1m + 1m);
        published.RatePerMinute.Length.ShouldBe(60);
        published.RatePerMinute[^1].ShouldBe(600_000);
    }

    [Fact]
    public async Task Pricing_reload_picks_up_user_edits()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        _settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PricingRule>>(
            [new PricingRule { Model = "claude-sonnet-5", InputPerM = 4m, OutputPerM = 10m }]));

        await service.ReloadPricingAsync(CancellationToken.None);

        service.Pricing.Find("claude-sonnet-5").InputPerM.ShouldBe(4m);
        service.Current.Today.Cost.ShouldBe(4m);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Telemetry.Tests`
Expected: build errors for the missing types.

- [ ] **Step 3: Implement pricing, cost and context fill**

`src/CodeSwitchX.Telemetry/DefaultPricing.cs`:

```csharp
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Telemetry;

/// <summary>
/// Shipped defaults in USD per million tokens (Anthropic list prices as of 2026-09; estimates the user can edit).
/// Cache write is 1.25x input and cache read 0.10x input unless Anthropic publishes a different rate.
/// </summary>
public static class DefaultPricing
{
    public static IReadOnlyList<PricingRule> Rules { get; } =
    [
        Rule("claude-fable-5-1", "Claude Fable 5.1", 10m, 50m, 12.5m, 0.25m, 1_000_000),
        Rule("claude-fable-5", "Claude Fable 5", 10m, 50m, 12.5m, 1m, 1_000_000),
        Rule("claude-opus-5-5", "Claude Opus 5.5", 4m, 20m, 5m, 0.20m, 1_000_000),
        Rule("claude-opus-5", "Claude Opus 5", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-8", "Claude Opus 4.8", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-7", "Claude Opus 4.7", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-6", "Claude Opus 4.6", 5m, 25m, 6.25m, 0.50m, 1_000_000),
        Rule("claude-opus-4-5", "Claude Opus 4.5", 5m, 25m, 6.25m, 0.50m, 200_000),
        Rule("claude-opus-4-1", "Claude Opus 4.1", 15m, 75m, 18.75m, 1.50m, 200_000),
        Rule("claude-opus-4", "Claude Opus 4", 15m, 75m, 18.75m, 1.50m, 200_000),
        Rule("claude-sonnet-5", "Claude Sonnet 5", 2m, 10m, 2.5m, 0.20m, 1_000_000),
        Rule("claude-sonnet-4-6", "Claude Sonnet 4.6", 3m, 15m, 3.75m, 0.30m, 1_000_000),
        Rule("claude-sonnet-4-5", "Claude Sonnet 4.5", 3m, 15m, 3.75m, 0.30m, 200_000),
        Rule("claude-sonnet-4", "Claude Sonnet 4", 3m, 15m, 3.75m, 0.30m, 200_000),
        Rule("claude-haiku-4-5", "Claude Haiku 4.5", 1m, 5m, 1.25m, 0.10m, 200_000),
        Rule("claude-3-5-haiku", "Claude Haiku 3.5", 0.8m, 4m, 1m, 0.08m, 200_000),
    ];

    private static PricingRule Rule(string model, string display, decimal input, decimal output, decimal cacheWrite, decimal cacheRead, long context) => new()
    {
        Model = model,
        DisplayName = display,
        InputPerM = input,
        OutputPerM = output,
        CacheWritePerM = cacheWrite,
        CacheReadPerM = cacheRead,
        ContextWindow = context,
    };
}
```

`src/CodeSwitchX.Telemetry/PricingTable.cs`:

```csharp
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Telemetry;

public sealed class PricingTable
{
    public static readonly PricingRule Fallback = new()
    {
        Model = string.Empty,
        DisplayName = "Unknown model (no pricing)",
        ContextWindow = 200_000,
    };

    private readonly PricingRule[] _rules;

    public PricingTable(IEnumerable<PricingRule> rules)
    {
        _rules = rules
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderByDescending(r => r.Model.Length)
            .ToArray();
    }

    public static PricingTable Default { get; } = new(DefaultPricing.Rules);

    public IReadOnlyList<PricingRule> Rules => _rules;

    /// <summary>Longest rule whose model id equals the given id or is a prefix of it ending on a '-' boundary.</summary>
    public PricingRule Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Fallback;
        }

        foreach (var rule in _rules)
        {
            if (Matches(model, rule.Model))
            {
                return rule;
            }
        }

        return Fallback;
    }

    private static bool Matches(string model, string prefix) =>
        model.Length >= prefix.Length
        && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (model.Length == prefix.Length || model[prefix.Length] == '-');
}
```

`src/CodeSwitchX.Telemetry/CostEstimator.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public static class CostEstimator
{
    private const decimal Million = 1_000_000m;

    public static decimal Estimate(TokenUsage usage, PricingRule rule) =>
        (usage.Input * rule.InputPerM
         + usage.Output * rule.OutputPerM
         + usage.CacheWrite * rule.CacheWritePerM
         + usage.CacheRead * rule.CacheReadPerM) / Million;
}
```

`src/CodeSwitchX.Telemetry/ContextFillCalculator.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public enum ContextPressure
{
    Normal,
    Amber,
    Red,
}

public static class ContextFillCalculator
{
    public const double AmberThreshold = 0.8;
    public const double RedThreshold = 0.9;

    public static double Fill(TokenUsage latest, PricingRule rule)
    {
        if (rule.ContextWindow <= 0)
        {
            return 0.0;
        }

        return Math.Clamp((double)latest.ContextTokens / rule.ContextWindow, 0.0, 1.0);
    }

    public static ContextPressure Level(double fill) => fill switch
    {
        >= RedThreshold => ContextPressure.Red,
        >= AmberThreshold => ContextPressure.Amber,
        _ => ContextPressure.Normal,
    };
}
```

- [ ] **Step 4: Implement the aggregator and the service**

`src/CodeSwitchX.Telemetry/UsageTotals.cs`:

```csharp
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public readonly record struct UsageTotals(TokenUsage Tokens, decimal Cost)
{
    public static UsageTotals Zero => default;
}
```

`src/CodeSwitchX.Telemetry/UsageAggregator.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;

namespace CodeSwitchX.Telemetry;

public sealed class UsageAggregator
{
    private readonly PricingTable _pricing;

    public UsageAggregator(PricingTable pricing)
    {
        _pricing = pricing;
    }

    public UsageTotals Sum(IEnumerable<UsageBucket> buckets)
    {
        var tokens = TokenUsage.Zero;
        var cost = 0m;
        foreach (var bucket in buckets)
        {
            var usage = new TokenUsage(bucket.Input, bucket.Output, bucket.CacheWrite, bucket.CacheRead);
            tokens += usage;
            cost += CostEstimator.Estimate(usage, _pricing.Find(bucket.Model));
        }

        return new UsageTotals(tokens, cost);
    }

    public UsageTotals Window(IEnumerable<UsageBucket> buckets, DateTimeOffset now, TimeSpan span)
    {
        var from = now - span;
        return Sum(buckets.Where(b => b.MinuteUtc >= from && b.MinuteUtc <= now));
    }

    public UsageTotals Today(IEnumerable<UsageBucket> buckets, DateTimeOffset now, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var startLocal = new DateTimeOffset(localNow.Date, localNow.Offset);
        var startUtc = startLocal.ToUniversalTime();
        return Sum(buckets.Where(b => b.MinuteUtc >= startUtc && b.MinuteUtc <= now));
    }

    /// <summary>Total tokens per minute for the last <paramref name="minutes"/> minutes, oldest first, current minute last.</summary>
    public long[] RateSeries(IEnumerable<UsageBucket> buckets, DateTimeOffset now, int minutes)
    {
        var series = new long[minutes];
        var currentMinute = UsageBucket.FloorToMinute(now);
        foreach (var bucket in buckets)
        {
            var age = (int)Math.Floor((currentMinute - UsageBucket.FloorToMinute(bucket.MinuteUtc)).TotalMinutes);
            if (age < 0 || age >= minutes)
            {
                continue;
            }

            series[minutes - 1 - age] += bucket.Input + bucket.Output + bucket.CacheWrite + bucket.CacheRead;
        }

        return series;
    }

    public IReadOnlyDictionary<string, UsageTotals> BySession(IEnumerable<UsageBucket> buckets) =>
        buckets.GroupBy(b => b.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Sum(g), StringComparer.Ordinal);
}
```

`src/CodeSwitchX.Telemetry/TelemetrySnapshot.cs`:

```csharp
namespace CodeSwitchX.Telemetry;

public sealed record TelemetrySnapshot(UsageTotals Today, UsageTotals FiveHours, long[] RatePerMinute, DateTimeOffset AsOf)
{
    public static TelemetrySnapshot Empty(DateTimeOffset asOf) => new(UsageTotals.Zero, UsageTotals.Zero, new long[60], asOf);
}

public sealed record TelemetryUpdated(TelemetrySnapshot Snapshot);
```

`src/CodeSwitchX.Telemetry/TelemetryService.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Telemetry;

/// <summary>Keeps the last seven days of usage buckets in memory and publishes a fresh snapshot after every transcript update.</summary>
public sealed class TelemetryService : IHostedService, IDisposable
{
    public static readonly TimeSpan History = TimeSpan.FromDays(7);
    public const int RateMinutes = 60;

    private readonly IUsageStore _usage;
    private readonly ISettingsStore _settings;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<TelemetryService> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string SessionId, string Model, DateTimeOffset Minute), UsageBucket> _buckets = [];
    private IDisposable? _subscription;
    private UsageAggregator _aggregator = new(PricingTable.Default);

    public TelemetryService(IUsageStore usage, ISettingsStore settings, IEventBus bus, TimeProvider time, ILogger<TelemetryService> logger)
    {
        _usage = usage;
        _settings = settings;
        _bus = bus;
        _time = time;
        _logger = logger;
        Current = TelemetrySnapshot.Empty(time.GetUtcNow());
    }

    public PricingTable Pricing { get; private set; } = PricingTable.Default;
    public TelemetrySnapshot Current { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _settings.EnsurePricingDefaultsAsync(DefaultPricing.Rules.ToArray(), cancellationToken).ConfigureAwait(false);
        await ReloadPricingAsync(cancellationToken, publish: false).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var stored = await _usage.GetBucketsAsync(now - History, now.AddMinutes(1), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var bucket in stored)
            {
                _buckets[(bucket.SessionId, bucket.Model, bucket.MinuteUtc)] = bucket;
            }
        }

        _subscription = _bus.Subscribe<TranscriptUpdated>(OnTranscriptUpdated);
        Recompute(publish: true);
        _logger.LogInformation("Telemetry loaded {Count} usage buckets", stored.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public Task ReloadPricingAsync(CancellationToken ct) => ReloadPricingAsync(ct, publish: true);

    private async Task ReloadPricingAsync(CancellationToken ct, bool publish)
    {
        var rules = await _settings.GetPricingAsync(ct).ConfigureAwait(false);
        Pricing = new PricingTable(DefaultPricing.Rules.Concat(rules));
        _aggregator = new UsageAggregator(Pricing);
        Recompute(publish);
    }

    private void OnTranscriptUpdated(TranscriptUpdated message)
    {
        if (message.Update.Usage.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var delta in message.Update.Usage)
            {
                var minute = UsageBucket.FloorToMinute(delta.At);
                var key = (message.Update.SessionId, delta.Model, minute);
                if (!_buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new UsageBucket { SessionId = message.Update.SessionId, Model = delta.Model, MinuteUtc = minute };
                    _buckets[key] = bucket;
                }

                bucket.Input += delta.Tokens.Input;
                bucket.Output += delta.Tokens.Output;
                bucket.CacheWrite += delta.Tokens.CacheWrite;
                bucket.CacheRead += delta.Tokens.CacheRead;
            }
        }

        Recompute(publish: true);
    }

    private void Recompute(bool publish)
    {
        var now = _time.GetUtcNow();
        UsageBucket[] buckets;
        lock (_gate)
        {
            var cutoff = now - History;
            foreach (var stale in _buckets.Where(kv => kv.Key.Minute < cutoff).Select(kv => kv.Key).ToList())
            {
                _buckets.Remove(stale);
            }

            buckets = _buckets.Values.ToArray();
        }

        Current = new TelemetrySnapshot(
            _aggregator.Today(buckets, now, _time.LocalTimeZone),
            _aggregator.Window(buckets, now, TimeSpan.FromHours(5)),
            _aggregator.RateSeries(buckets, now, RateMinutes),
            now);

        if (publish)
        {
            _bus.Publish(new TelemetryUpdated(Current));
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
```

`src/CodeSwitchX.Telemetry/TelemetryServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryService>());
        return services;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Telemetry.Tests`
Expected: all pass. `Today_uses_the_local_calendar_day` needs the Windows time-zone id `W. Europe Standard Time`, which every Windows install has.

---

### Task 12: Native AOT hook relay (`csx-hook.exe`)

**Files:**
- Create: `src/CodeSwitchX.Hook/Program.cs` (replace), `Relay.cs`, `EndpointInfo.cs`, `RelayJsonContext.cs`, `ProcessChain.cs`
- Modify: `tests/CodeSwitchX.Hook.Tests/CodeSwitchX.Hook.Tests.csproj` (add test-only references to `CodeSwitchX.Ingest` and `CodeSwitchX.Core` so the real Event API can serve as the integration server)
- Test: `tests/CodeSwitchX.Hook.Tests/RelayTests.cs`, `ProcessChainTests.cs`, `RelayEndToEndTests.cs`

**Interfaces:**
- Consumes (contract only, no project reference): `endpoint.json` shape from Task 8 (`pipeName`, `port`, `pid`, `startedAtUtc`), `token` file, `POST /events` with `Authorization: Bearer`, envelope shape from Task 7.
- Produces: `internal static class Relay { Task<int> RunAsync(string[] args, Stream stdin, string dataDirectory); string BuildEnvelope(string eventName, string payload, DateTimeOffset now, int relayPid, IReadOnlyList<ProcessInfo> chain); EndpointInfo? ReadEndpoint(string file); const int ConnectTimeoutMs = 150; const int TotalTimeoutMs = 1000; }`, `internal readonly record struct ProcessInfo(int Pid, string Name)`, `internal static class ProcessChain { IReadOnlyList<ProcessInfo> Ancestors(int maxDepth); IReadOnlyList<ProcessInfo> Ancestors(int startPid, IReadOnlyDictionary<int, (int Parent, string Name)> table, int maxDepth); }`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.Hook.Tests/CodeSwitchX.Hook.Tests.csproj` becomes:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\CodeSwitchX.Hook\CodeSwitchX.Hook.csproj" />
    <ProjectReference Include="..\..\src\CodeSwitchX.Ingest\CodeSwitchX.Ingest.csproj" />
    <ProjectReference Include="..\..\src\CodeSwitchX.Core\CodeSwitchX.Core.csproj" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

`tests/CodeSwitchX.Hook.Tests/RelayTests.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSwitchX.Hook;

namespace CodeSwitchX.Hook.Tests;

public class RelayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csx-relay-" + Guid.NewGuid().ToString("N"));

    public RelayTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Stream Stdin(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Envelope_embeds_json_payload_as_an_object()
    {
        var now = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var chain = new List<ProcessInfo> { new(100, "cmd.exe"), new(200, "claude.exe") };

        var json = Relay.BuildEnvelope("PreToolUse", """{"session_id":"s1","tool_name":"Bash"}""", now, 4242, chain);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("event").GetString().ShouldBe("PreToolUse");
        root.GetProperty("receivedAtUtc").GetDateTimeOffset().ShouldBe(now);
        root.GetProperty("relayPid").GetInt32().ShouldBe(4242);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(2);
        root.GetProperty("parentChain")[1].GetProperty("name").GetString().ShouldBe("claude.exe");
        root.GetProperty("payload").GetProperty("session_id").GetString().ShouldBe("s1");
    }

    [Fact]
    public void Envelope_keeps_non_json_stdin_as_a_string()
    {
        var json = Relay.BuildEnvelope("Stop", "not json", DateTimeOffset.UtcNow, 1, []);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("payload").GetString().ShouldBe("not json");
    }

    [Fact]
    public void ReadEndpoint_parses_the_descriptor_and_rejects_garbage()
    {
        var file = Path.Combine(_dir, "endpoint.json");
        File.WriteAllText(file, """{"pipeName":"csx-p","port":51234,"pid":7,"startedAtUtc":"2026-09-23T10:00:00Z"}""");

        var endpoint = Relay.ReadEndpoint(file).ShouldNotBeNull();
        endpoint.PipeName.ShouldBe("csx-p");
        endpoint.Port.ShouldBe(51234);

        File.WriteAllText(file, "{ nope");
        Relay.ReadEndpoint(file).ShouldBeNull();
        Relay.ReadEndpoint(Path.Combine(_dir, "missing.json")).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_endpoint_file_exits_zero_quickly()
    {
        var sw = Stopwatch.StartNew();

        var code = await Relay.RunAsync(["Stop"], Stdin("{}"), _dir);

        code.ShouldBe(0);
        sw.ElapsedMilliseconds.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task Dead_port_and_missing_pipe_exit_zero_within_the_budget()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var deadPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        File.WriteAllText(Path.Combine(_dir, "endpoint.json"), $$"""{"pipeName":"csx-nonexistent-{{Guid.NewGuid():N}}","port":{{deadPort}},"pid":1,"startedAtUtc":"2026-09-23T10:00:00Z"}""");
        File.WriteAllText(Path.Combine(_dir, "token"), new string('a', 64));
        var sw = Stopwatch.StartNew();

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"s1"}"""), _dir);

        code.ShouldBe(0);
        sw.ElapsedMilliseconds.ShouldBeLessThan(3000);
    }

    [Fact]
    public async Task Missing_token_exits_zero()
    {
        File.WriteAllText(Path.Combine(_dir, "endpoint.json"), """{"pipeName":"","port":1,"pid":1,"startedAtUtc":"2026-09-23T10:00:00Z"}""");

        (await Relay.RunAsync(["Stop"], Stdin("{}"), _dir)).ShouldBe(0);
    }
}
```

`tests/CodeSwitchX.Hook.Tests/ProcessChainTests.cs`:

```csharp
using CodeSwitchX.Hook;

namespace CodeSwitchX.Hook.Tests;

public class ProcessChainTests
{
    [Fact]
    public void Walks_parents_from_a_table_and_stops_at_the_root_or_depth()
    {
        var table = new Dictionary<int, (int Parent, string Name)>
        {
            [10] = (20, "csx-hook.exe"),
            [20] = (30, "cmd.exe"),
            [30] = (40, "claude.exe"),
            [40] = (0, "Code.exe"),
        };

        ProcessChain.Ancestors(10, table, maxDepth: 8).ShouldBe([new ProcessInfo(20, "cmd.exe"), new ProcessInfo(30, "claude.exe"), new ProcessInfo(40, "Code.exe")]);
        ProcessChain.Ancestors(10, table, maxDepth: 2).ShouldBe([new ProcessInfo(20, "cmd.exe"), new ProcessInfo(30, "claude.exe")]);
        ProcessChain.Ancestors(99, table, maxDepth: 8).ShouldBeEmpty();
    }

    [Fact]
    public void Cycles_in_the_table_terminate()
    {
        var table = new Dictionary<int, (int Parent, string Name)> { [1] = (2, "a"), [2] = (1, "b") };

        ProcessChain.Ancestors(1, table, maxDepth: 8).Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void Live_snapshot_finds_the_test_hosts_parent()
    {
        var chain = ProcessChain.Ancestors(maxDepth: 8);

        chain.ShouldNotBeEmpty();
        chain.ShouldAllBe(p => p.Pid != Environment.ProcessId && p.Name.Length > 0);
    }
}
```

`tests/CodeSwitchX.Hook.Tests/RelayEndToEndTests.cs`:

```csharp
using System.Text;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hook;
using CodeSwitchX.Ingest.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSwitchX.Hook.Tests;

public class RelayEndToEndTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-e2e-" + Guid.NewGuid().ToString("N")));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<HookEvent> _received = [];
    private EventApiService _api = null!;

    public async ValueTask InitializeAsync()
    {
        _paths.EnsureCreated();
        _bus.Subscribe<HookEventReceived>(m => _received.Add(m.Event));
        _api = new EventApiService(_paths, _bus, new AccessTokenStore(_paths), TimeProvider.System, NullLoggerFactory.Instance,
            new EventApiOptions { PipeName = "csx-e2e-" + Guid.NewGuid().ToString("N"), LoopbackPort = 0 });
        await _api.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.StopAsync(CancellationToken.None);
        Directory.Delete(_paths.Root, recursive: true);
    }

    private static Stream Stdin(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Relay_delivers_over_the_named_pipe()
    {
        var code = await Relay.RunAsync(["PreToolUse"], Stdin("""{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Bash","cwd":"C:\\Repo"}"""), _paths.Root);

        code.ShouldBe(0);
        var e = _received.ShouldHaveSingleItem();
        e.SessionId.ShouldBe("s1");
        e.Signal.ShouldBe(SessionSignal.ToolUse);
        e.ToolName.ShouldBe("Bash");
        e.RelayPid.ShouldBe(Environment.ProcessId);
        e.ParentChain.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Relay_falls_back_to_loopback_when_the_pipe_is_gone()
    {
        var descriptor = EndpointDescriptor.TryRead(_paths.EndpointFile)!;
        (descriptor with { PipeName = "csx-gone-" + Guid.NewGuid().ToString("N") }).Write(_paths.EndpointFile);

        var code = await Relay.RunAsync(["Stop"], Stdin("""{"session_id":"s2","hook_event_name":"Stop"}"""), _paths.Root);

        code.ShouldBe(0);
        _received.ShouldHaveSingleItem().SessionId.ShouldBe("s2");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Hook.Tests`
Expected: build errors, `Relay` and `ProcessChain` missing.

- [ ] **Step 3: Implement the relay**

`src/CodeSwitchX.Hook/Program.cs`:

```csharp
using CodeSwitchX.Hook;

return await Relay.RunAsync(args, Console.OpenStandardInput(), Relay.DefaultDataDirectory).ConfigureAwait(false);
```

`src/CodeSwitchX.Hook/EndpointInfo.cs` and `RelayJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace CodeSwitchX.Hook;

internal sealed class EndpointInfo
{
    [JsonPropertyName("pipeName")]
    public string? PipeName { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("startedAtUtc")]
    public string? StartedAtUtc { get; set; }
}
```

```csharp
using System.Text.Json.Serialization;

namespace CodeSwitchX.Hook;

[JsonSerializable(typeof(EndpointInfo))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class RelayJsonContext : JsonSerializerContext;
```

`src/CodeSwitchX.Hook/ProcessChain.cs`:

```csharp
using System.Runtime.InteropServices;

namespace CodeSwitchX.Hook;

internal readonly record struct ProcessInfo(int Pid, string Name);

/// <summary>Parent-process walk via Toolhelp32; AOT-safe (LibraryImport, blittable struct).</summary>
internal static unsafe partial class ProcessChain
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint hSnapshot, PROCESSENTRY32W* lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint hSnapshot, PROCESSENTRY32W* lppe);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    internal static IReadOnlyList<ProcessInfo> Ancestors(int maxDepth)
    {
        try
        {
            return Ancestors(Environment.ProcessId, Snapshot(), maxDepth);
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<ProcessInfo> Ancestors(int startPid, IReadOnlyDictionary<int, (int Parent, string Name)> table, int maxDepth)
    {
        var chain = new List<ProcessInfo>();
        var visited = new HashSet<int> { startPid };
        var current = startPid;
        while (chain.Count < maxDepth && table.TryGetValue(current, out var entry) && entry.Parent != 0 && visited.Add(entry.Parent))
        {
            if (!table.TryGetValue(entry.Parent, out var parent))
            {
                break;
            }

            chain.Add(new ProcessInfo(entry.Parent, parent.Name));
            current = entry.Parent;
        }

        return chain;
    }

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var table = new Dictionary<int, (int, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle || snapshot == 0)
        {
            return table;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (!Process32FirstW(snapshot, &entry))
            {
                return table;
            }

            do
            {
                var name = new string(entry.szExeFile);
                table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, name);
            }
            while (Process32NextW(snapshot, &entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return table;
    }
}
```

`src/CodeSwitchX.Hook/Relay.cs`:

```csharp
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeSwitchX.Hook;

/// <summary>
/// Forwards one Claude Code hook payload (stdin) to the running CodeSwitchX instance.
/// Never writes to stdout, never throws, always exits 0, so Claude Code is never disturbed.
/// </summary>
internal static class Relay
{
    internal const int ConnectTimeoutMs = 150;
    internal const int TotalTimeoutMs = 1000;
    internal const int MaxPayloadBytes = 1024 * 1024;
    internal const int MaxParentDepth = 8;

    internal static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeSwitchX");

    internal static async Task<int> RunAsync(string[] args, Stream stdin, string dataDirectory)
    {
        try
        {
            var eventName = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "Unknown";
            var endpoint = ReadEndpoint(Path.Combine(dataDirectory, "endpoint.json"));
            if (endpoint is null)
            {
                return 0;
            }

            var token = ReadToken(Path.Combine(dataDirectory, "token"));
            if (token is null)
            {
                return 0;
            }

            var payload = await ReadPayloadAsync(stdin).ConfigureAwait(false);
            var envelope = BuildEnvelope(eventName, payload, DateTimeOffset.UtcNow, Environment.ProcessId, ProcessChain.Ancestors(MaxParentDepth));

            using var cts = new CancellationTokenSource(TotalTimeoutMs);
            await PostAsync(endpoint, token, envelope, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Swallow everything: the relay must never affect the hook's exit code.
        }

        return 0;
    }

    internal static EndpointInfo? ReadEndpoint(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var info = JsonSerializer.Deserialize(File.ReadAllText(file), RelayJsonContext.Default.EndpointInfo);
            return info is null || (info.Port <= 0 && string.IsNullOrEmpty(info.PipeName)) ? null : info;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ReadToken(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var token = File.ReadAllText(file).Trim();
            return token.Length == 0 ? null : token;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<string> ReadPayloadAsync(Stream stdin)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxPayloadBytes && (read = await stdin.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static string BuildEnvelope(string eventName, string payload, DateTimeOffset now, int relayPid, IReadOnlyList<ProcessInfo> chain)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("event", eventName);
            writer.WriteString("receivedAtUtc", now);
            writer.WriteNumber("relayPid", relayPid);
            writer.WriteStartArray("parentChain");
            foreach (var process in chain)
            {
                writer.WriteStartObject();
                writer.WriteNumber("pid", process.Pid);
                writer.WriteString("name", process.Name);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("payload");
            if (TryParseJson(payload, out var document))
            {
                using (document)
                {
                    document.RootElement.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteStringValue(payload);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static bool TryParseJson(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static async Task PostAsync(EndpointInfo endpoint, string token, string envelope, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(endpoint.PipeName))
        {
            try
            {
                await PostViaPipeAsync(endpoint.PipeName, token, envelope, ct).ConfigureAwait(false);
                return;
            }
            catch when (endpoint.Port > 0)
            {
                // fall through to loopback
            }
        }

        if (endpoint.Port > 0)
        {
            await PostViaLoopbackAsync(endpoint.Port, token, envelope, ct).ConfigureAwait(false);
        }
    }

    private static async Task PostViaPipeAsync(string pipeName, string token, string envelope, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancel) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ConnectTimeoutMs, cancel).ConfigureAwait(false);
                return pipe;
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://pipe/"), Timeout = TimeSpan.FromMilliseconds(TotalTimeoutMs) };
        await SendAsync(client, token, envelope, ct).ConfigureAwait(false);
    }

    private static async Task PostViaLoopbackAsync(int port, string token, string envelope, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMilliseconds(TotalTimeoutMs) };
        await SendAsync(client, token, envelope, ct).ConfigureAwait(false);
    }

    private static async Task SendAsync(HttpClient client, string token, string envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "events")
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.Hook.Tests`
Expected: all pass. The named-pipe test passes because the relay reads the pipe name Kestrel published in `endpoint.json`.

- [ ] **Step 5: Verify the AOT publish**

Run: `dotnet publish src/CodeSwitchX.Hook -c Release -r win-x64`
Expected: `src/CodeSwitchX.Hook/bin/Release/net10.0-windows/win-x64/publish/csx-hook.exe` exists and is under 4 MB with no AOT analyzer warnings (IL2026/IL3050). If the machine lacks the C++ build tools the publish fails with an `ILCompiler`/`link.exe` error; record that in the final report and fall back to the framework-dependent build for local testing.

---

### Task 13: Hosting — Win32 layer, VS Code launcher, Snap host and HostManager

**Files:**
- Create: `src/CodeSwitchX.Hosting/NativeMethods.txt`, `NativeMethods.json`
- Create: `src/CodeSwitchX.Hosting/Win32/ScreenRect.cs`, `WindowInfo.cs`, `IWindowEnumerator.cs`, `Win32WindowEnumerator.cs`, `IWindowDocker.cs`, `SnapWindowDocker.cs`, `WindowLocationWatcher.cs`, `DwmThumbnail.cs`, `HotkeyInterop.cs`
- Create: `src/CodeSwitchX.Hosting/VsCode/VsCodeLocator.cs`, `IVsCodeLauncher.cs`, `VsCodeLauncher.cs`, `VsCodeWindowMatcher.cs`
- Create: `src/CodeSwitchX.Hosting/HostState.cs`, `HostedWorkspace.cs`, `HostManagerOptions.cs`, `HostManager.cs`, `HostingServiceCollectionExtensions.cs`
- Test: `tests/CodeSwitchX.Hosting.Tests/VsCodeWindowMatcherTests.cs`, `VsCodeLauncherTests.cs`, `VsCodeLocatorTests.cs`, `ScreenRectTests.cs`, `HostManagerTests.cs`

**Interfaces:**
- Consumes: `Workspace` (Task 3), `IEventBus` (Task 4).
- Produces:
  - `readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom) { int Width; int Height; static ScreenRect FromSize(int left, int top, int width, int height); }`
  - `sealed record WindowInfo(nint Hwnd, uint ProcessId, string ClassName, string Title)`
  - `IWindowEnumerator { IReadOnlyList<WindowInfo> TopLevelWindows(); string? ProcessName(uint pid); }`, `Win32WindowEnumerator`
  - `IWindowDocker { void MoveTo(nint hwnd, ScreenRect rect); void Cloak(nint hwnd); void Uncloak(nint hwnd); void BringToFront(nint hwnd); bool IsAlive(nint hwnd); ScreenRect? GetRect(nint hwnd); }`, `SnapWindowDocker`
  - `WindowLocationWatcher : IDisposable` (create on the UI thread) with `event Action<nint>? Moved`
  - `DwmThumbnail : IDisposable` with `static DwmThumbnail? TryRegister(nint destination, nint source)`, `void Update(ScreenRect destinationClientRect, byte opacity, bool visible)`
  - `HotkeyInterop { static bool Register(nint hwnd, int id, HotkeyModifiers modifiers, uint virtualKey); static bool Unregister(nint hwnd, int id); const int WmHotkey = 0x0312; }`, `[Flags] enum HotkeyModifiers { Alt = 1, Control = 2, Shift = 4, Win = 8, NoRepeat = 0x4000 }`
  - `VsCodeLocator { static string? FindExecutable(); static string? FindExecutable(Func<string, bool> fileExists, Func<string, string?> env); }`
  - `IVsCodeLauncher { LaunchResult Launch(Workspace workspace); }`, `readonly record struct LaunchResult(bool Started, int? ProcessId, string? Error)`, `VsCodeLauncher(string? executable = null)` with `static string BuildArguments(Workspace)` and `static string DisplayNameForMatching(Workspace)`
  - `VsCodeWindowMatcher.FindNew(IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> after, string displayName, Func<uint, string?> processName) -> WindowInfo?`
  - `enum HostState { NotStarted, Starting, Running, Stopped }`, `HostedWorkspace { Guid WorkspaceId; HostState State; nint Hwnd; uint ProcessId; bool Visible; ScreenRect? TargetRect; string? Error; DateTimeOffset? StartedAt }`
  - `sealed record HostStateChanged(Guid WorkspaceId, HostState State, nint Hwnd, string? Error)`
  - `HostManager(IWindowEnumerator, IWindowDocker, IVsCodeLauncher, IEventBus, TimeProvider, ILogger<HostManager>, HostManagerOptions?)` with `Task<HostedWorkspace> OpenAsync(Workspace, CancellationToken)`, `void ShowInCab(Guid, ScreenRect)`, `void HideAll()`, `void SnapBack(nint hwnd)`, `void PollLiveness()`, `void Forget(Guid)`, `HostedWorkspace? Get(Guid)`, `IReadOnlyList<HostedWorkspace> All`
  - `HostManagerOptions { TimeSpan DiscoveryTimeout = 20 s; TimeSpan PollInterval = 250 ms }`
  - `AddCodeSwitchXHosting(this IServiceCollection)`

Win32 signatures come from CsWin32 code generation; if a generated overload differs from the code below (for example `HWND.Value` type or `Span<char>` helpers), adapt the private implementation and keep the public interfaces unchanged.

- [ ] **Step 1: Write the failing pure-logic tests**

`tests/CodeSwitchX.Hosting.Tests/ScreenRectTests.cs`:

```csharp
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class ScreenRectTests
{
    [Fact]
    public void Width_and_height_derive_from_edges()
    {
        var rect = ScreenRect.FromSize(10, 20, 300, 200);

        rect.Right.ShouldBe(310);
        rect.Bottom.ShouldBe(220);
        rect.Width.ShouldBe(300);
        rect.Height.ShouldBe(200);
        rect.ShouldBe(new ScreenRect(10, 20, 310, 220));
    }
}
```

`tests/CodeSwitchX.Hosting.Tests/VsCodeWindowMatcherTests.cs`:

```csharp
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeWindowMatcherTests
{
    private static readonly Dictionary<uint, string> Processes = new() { [10] = "Code", [20] = "chrome", [30] = "Code" };
    private static string? ProcessName(uint pid) => Processes.GetValueOrDefault(pid);

    private static readonly WindowInfo Existing = new(100, 10, "Chrome_WidgetWin_1", "App - Visual Studio Code");

    [Fact]
    public void Picks_the_new_code_window_whose_title_names_the_workspace()
    {
        var after = new List<WindowInfo>
        {
            Existing,
            new(200, 20, "Chrome_WidgetWin_1", "App - Google Chrome"),
            new(300, 30, "Chrome_WidgetWin_1", string.Empty),
            new(400, 30, "Chrome_WidgetWin_1", "Program.cs - App2 - Visual Studio Code"),
            new(500, 30, "Chrome_WidgetWin_1", "Program.cs - App - Visual Studio Code"),
        };

        VsCodeWindowMatcher.FindNew([Existing], after, "App", ProcessName)!.Hwnd.ShouldBe((nint)500);
        VsCodeWindowMatcher.FindNew([Existing], after, "App2", ProcessName)!.Hwnd.ShouldBe((nint)400);
    }

    [Fact]
    public void Ignores_windows_that_existed_before_launch_and_other_processes()
    {
        var after = new List<WindowInfo> { Existing, new(200, 20, "Chrome_WidgetWin_1", "App - Visual Studio Code") };

        VsCodeWindowMatcher.FindNew([Existing], after, "App", ProcessName).ShouldBeNull();
    }

    [Fact]
    public void Matching_is_case_insensitive_and_prefers_titles_that_say_visual_studio_code()
    {
        var after = new List<WindowInfo>
        {
            new(600, 30, "Chrome_WidgetWin_1", "app"),
            new(700, 30, "Chrome_WidgetWin_1", "APP - Visual Studio Code"),
        };

        VsCodeWindowMatcher.FindNew([], after, "App", ProcessName)!.Hwnd.ShouldBe((nint)700);
    }
}
```

`tests/CodeSwitchX.Hosting.Tests/VsCodeLauncherTests.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeLauncherTests
{
    [Fact]
    public void Folder_workspaces_open_the_root_in_a_new_window()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe("--new-window \"c:\\repo\\app\"");
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("app");
    }

    [Fact]
    public void Workspace_files_and_profiles_are_passed_through()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app", WorkspaceFile = @"c:\repo\app\app.code-workspace", VsCodeProfile = "Dev Kit" };

        VsCodeLauncher.BuildArguments(workspace).ShouldBe("--new-window --profile \"Dev Kit\" \"c:\\repo\\app\\app.code-workspace\"");
        VsCodeLauncher.DisplayNameForMatching(workspace).ShouldBe("app (Workspace)");
    }

    [Fact]
    public void Launch_without_an_executable_reports_an_error_instead_of_throwing()
    {
        var launcher = new VsCodeLauncher(executable: @"C:\definitely\missing\Code.exe");

        var result = launcher.Launch(new Workspace { Name = "App", RootPath = @"c:\repo\app" });

        result.Started.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }
}
```

`tests/CodeSwitchX.Hosting.Tests/VsCodeLocatorTests.cs`:

```csharp
using CodeSwitchX.Hosting.VsCode;

namespace CodeSwitchX.Hosting.Tests;

public class VsCodeLocatorTests
{
    [Fact]
    public void Explicit_override_wins()
    {
        var exe = VsCodeLocator.FindExecutable(_ => true, name => name == "CODESWITCHX_VSCODE_EXE" ? @"D:\Tools\Code.exe" : null);

        exe.ShouldBe(@"D:\Tools\Code.exe");
    }

    [Fact]
    public void User_install_is_found_under_LocalAppData()
    {
        var exe = VsCodeLocator.FindExecutable(
            path => path == @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe",
            name => name == "LOCALAPPDATA" ? @"C:\Users\me\AppData\Local" : null);

        exe.ShouldBe(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe");
    }

    [Fact]
    public void Path_entries_with_code_cmd_resolve_to_the_sibling_Code_exe()
    {
        var exe = VsCodeLocator.FindExecutable(
            path => path is @"C:\VSCode\bin\code.cmd" or @"C:\VSCode\Code.exe",
            name => name == "PATH" ? @"C:\Windows;C:\VSCode\bin" : null);

        exe.ShouldBe(@"C:\VSCode\Code.exe");
    }

    [Fact]
    public void Nothing_found_returns_null()
    {
        VsCodeLocator.FindExecutable(_ => false, _ => null).ShouldBeNull();
    }
}
```

`tests/CodeSwitchX.Hosting.Tests/HostManagerTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.Hosting.Tests;

public class HostManagerTests
{
    private readonly IWindowEnumerator _windows = Substitute.For<IWindowEnumerator>();
    private readonly IWindowDocker _docker = Substitute.For<IWindowDocker>();
    private readonly IVsCodeLauncher _launcher = Substitute.For<IVsCodeLauncher>();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<HostStateChanged> _changes = [];
    private readonly Workspace _workspace = new() { Name = "App", RootPath = @"c:\repo\app" };
    private readonly HostManager _manager;

    public HostManagerTests()
    {
        _bus.Subscribe<HostStateChanged>(_changes.Add);
        _windows.ProcessName(30).Returns("Code");
        _docker.IsAlive(Arg.Any<nint>()).Returns(true);
        _manager = new HostManager(_windows, _docker, _launcher, _bus, TimeProvider.System, NullLogger<HostManager>.Instance,
            new HostManagerOptions { DiscoveryTimeout = TimeSpan.FromMilliseconds(500), PollInterval = TimeSpan.FromMilliseconds(5) });
    }

    private void WindowAppearsAfterLaunch()
    {
        var before = new List<WindowInfo>();
        var after = new List<WindowInfo> { new(500, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code") };
        _windows.TopLevelWindows().Returns(before, after);
        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1234, null));
    }

    [Fact]
    public async Task Open_launches_and_finds_the_new_window()
    {
        WindowAppearsAfterLaunch();

        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Running);
        hosted.Hwnd.ShouldBe((nint)500);
        hosted.ProcessId.ShouldBe(30u);
        _changes.Select(c => c.State).ShouldBe([HostState.Starting, HostState.Running]);
        _manager.Get(_workspace.Id).ShouldBeSameAs(hosted);
    }

    [Fact]
    public async Task Open_is_a_no_op_while_the_window_is_alive()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);

        await _manager.OpenAsync(_workspace, CancellationToken.None);

        _launcher.Received(1).Launch(_workspace);
    }

    [Fact]
    public async Task Launch_failure_and_discovery_timeout_end_in_Stopped()
    {
        _windows.TopLevelWindows().Returns([]);
        _launcher.Launch(_workspace).Returns(new LaunchResult(false, null, "code not found"));
        (await _manager.OpenAsync(_workspace, CancellationToken.None)).State.ShouldBe(HostState.Stopped);
        _changes[^1].Error.ShouldBe("code not found");

        _launcher.Launch(_workspace).Returns(new LaunchResult(true, 1, null));
        var hosted = await _manager.OpenAsync(_workspace, CancellationToken.None);

        hosted.State.ShouldBe(HostState.Stopped);
        hosted.Error.ShouldContain("window");
    }

    [Fact]
    public async Task ShowInCab_uncloaks_moves_and_hides_the_others()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var other = new Workspace { Name = "Other", RootPath = @"c:\repo\other" };
        _windows.TopLevelWindows().Returns([], [new WindowInfo(600, 30, "Chrome_WidgetWin_1", "other - Visual Studio Code")]);
        _launcher.Launch(other).Returns(new LaunchResult(true, 2, null));
        await _manager.OpenAsync(other, CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);

        _manager.ShowInCab(other.Id, rect);
        _manager.ShowInCab(_workspace.Id, rect);

        _docker.Received(1).Uncloak(500);
        _docker.Received(1).MoveTo(500, rect);
        _docker.Received(1).BringToFront(500);
        _docker.Received(1).Cloak(600);
        _manager.Get(_workspace.Id)!.Visible.ShouldBeTrue();
        _manager.Get(other.Id)!.Visible.ShouldBeFalse();
    }

    [Fact]
    public async Task HideAll_cloaks_visible_windows_and_SnapBack_restores_the_target_rect()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _manager.ShowInCab(_workspace.Id, rect);
        _docker.ClearReceivedCalls();

        _docker.GetRect(500).Returns(ScreenRect.FromSize(50, 60, 1600, 900));
        _manager.SnapBack(500);
        _docker.Received(1).MoveTo(500, rect);

        _docker.GetRect(500).Returns(rect);
        _manager.SnapBack(500);
        _docker.Received(1).MoveTo(500, rect);

        _manager.HideAll();
        _docker.Received(1).Cloak(500);
        _manager.Get(_workspace.Id)!.Visible.ShouldBeFalse();
    }

    [Fact]
    public async Task Liveness_poll_marks_closed_windows_stopped()
    {
        WindowAppearsAfterLaunch();
        await _manager.OpenAsync(_workspace, CancellationToken.None);
        _docker.IsAlive(500).Returns(false);

        _manager.PollLiveness();

        _manager.Get(_workspace.Id)!.State.ShouldBe(HostState.Stopped);
        _changes[^1].State.ShouldBe(HostState.Stopped);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Hosting.Tests`
Expected: build errors for the missing types.

- [ ] **Step 3: Add the CsWin32 configuration**

`src/CodeSwitchX.Hosting/NativeMethods.txt`:

```
EnumWindows
GetWindowThreadProcessId
GetClassName
GetWindowTextLength
GetWindowText
IsWindowVisible
IsWindow
GetAncestor
GetWindowRect
SetWindowPos
ShowWindow
SetForegroundWindow
SetWinEventHook
UnhookWinEvent
DwmSetWindowAttribute
DwmRegisterThumbnail
DwmUnregisterThumbnail
DwmUpdateThumbnailProperties
DwmQueryThumbnailSourceSize
RegisterHotKey
UnregisterHotKey
EVENT_OBJECT_LOCATIONCHANGE
WINEVENT_OUTOFCONTEXT
WINEVENT_SKIPOWNPROCESS
DWM_TNP_RECTDESTINATION
DWM_TNP_VISIBLE
DWM_TNP_OPACITY
DWM_TNP_SOURCECLIENTAREAONLY
```

`src/CodeSwitchX.Hosting/NativeMethods.json`:

```json
{
  "$schema": "https://aka.ms/CsWin32.schema.json",
  "public": false,
  "allowMarshaling": true
}
```

- [ ] **Step 4: Implement the Win32 layer**

`src/CodeSwitchX.Hosting/Win32/ScreenRect.cs`:

```csharp
namespace CodeSwitchX.Hosting.Win32;

/// <summary>Physical screen pixels, Win32 convention (right and bottom exclusive).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public static ScreenRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);
}
```

`src/CodeSwitchX.Hosting/Win32/WindowInfo.cs`, `IWindowEnumerator.cs`, `IWindowDocker.cs`:

```csharp
namespace CodeSwitchX.Hosting.Win32;

public sealed record WindowInfo(nint Hwnd, uint ProcessId, string ClassName, string Title);
```

```csharp
namespace CodeSwitchX.Hosting.Win32;

public interface IWindowEnumerator
{
    /// <summary>Visible, unowned top-level windows.</summary>
    IReadOnlyList<WindowInfo> TopLevelWindows();

    /// <summary>Process image name without extension, e.g. "Code"; null when the process is gone or inaccessible.</summary>
    string? ProcessName(uint pid);
}
```

```csharp
namespace CodeSwitchX.Hosting.Win32;

public interface IWindowDocker
{
    void MoveTo(nint hwnd, ScreenRect rect);
    void Cloak(nint hwnd);
    void Uncloak(nint hwnd);
    void BringToFront(nint hwnd);
    bool IsAlive(nint hwnd);
    ScreenRect? GetRect(nint hwnd);
}
```

`src/CodeSwitchX.Hosting/Win32/Win32WindowEnumerator.cs`:

```csharp
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

public sealed class Win32WindowEnumerator : IWindowEnumerator
{
    public IReadOnlyList<WindowInfo> TopLevelWindows()
    {
        var result = new List<WindowInfo>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != hwnd)
            {
                return true;
            }

            uint pid;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            }

            result.Add(new WindowInfo(hwnd.Value, pid, ClassNameOf(hwnd), TitleOf(hwnd)));
            return true;
        }, default);
        return result;
    }

    public string? ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ClassNameOf(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    private static string TitleOf(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        Span<char> buffer = length < 1024 ? stackalloc char[length + 1] : new char[length + 1];
        var copied = PInvoke.GetWindowText(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : string.Empty;
    }
}
```

`src/CodeSwitchX.Hosting/Win32/SnapWindowDocker.cs`:

```csharp
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Positions, cloaks and focuses foreign top-level windows without reparenting them.</summary>
public sealed class SnapWindowDocker : IWindowDocker
{
    public void MoveTo(nint hwnd, ScreenRect rect)
    {
        var h = new HWND(hwnd);
        PInvoke.ShowWindow(h, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SetWindowPos(h, HWND.Null, rect.Left, rect.Top, rect.Width, rect.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
    }

    public void Cloak(nint hwnd) => SetCloak(hwnd, cloak: true);

    public void Uncloak(nint hwnd) => SetCloak(hwnd, cloak: false);

    public void BringToFront(nint hwnd)
    {
        var h = new HWND(hwnd);
        PInvoke.SetWindowPos(h, HWND.HWND_TOP, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        PInvoke.SetForegroundWindow(h);
    }

    public bool IsAlive(nint hwnd) => PInvoke.IsWindow(new HWND(hwnd));

    public ScreenRect? GetRect(nint hwnd)
    {
        if (!PInvoke.GetWindowRect(new HWND(hwnd), out var rect))
        {
            return null;
        }

        return new ScreenRect(rect.left, rect.top, rect.right, rect.bottom);
    }

    private static unsafe void SetCloak(nint hwnd, bool cloak)
    {
        var value = cloak ? 1 : 0;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, sizeof(int));
    }
}
```

`src/CodeSwitchX.Hosting/Win32/WindowLocationWatcher.cs`:

```csharp
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>
/// Raises <see cref="Moved"/> when any top-level window moves or resizes. Create and dispose on a thread
/// with a message loop (the WPF UI thread): out-of-context WinEvent callbacks arrive through that loop.
/// </summary>
public sealed class WindowLocationWatcher : IDisposable
{
    private const int ObjIdWindow = 0;
    private readonly WINEVENTPROC _callback;
    private readonly HWINEVENTHOOK _hook;

    public WindowLocationWatcher()
    {
        _callback = OnWinEvent;
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            HMODULE.Null, _callback, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
    }

    public event Action<nint>? Moved;

    private void OnWinEvent(HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (idObject == ObjIdWindow && idChild == 0 && hwnd != HWND.Null)
        {
            Moved?.Invoke(hwnd.Value);
        }
    }

    public void Dispose()
    {
        if (_hook != default)
        {
            PInvoke.UnhookWinEvent(_hook);
        }
    }
}
```

`src/CodeSwitchX.Hosting/Win32/DwmThumbnail.cs`:

```csharp
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CodeSwitchX.Hosting.Win32;

/// <summary>Live DWM preview of a source window drawn inside a destination window's client area.</summary>
public sealed class DwmThumbnail : IDisposable
{
    private nuint _id;

    private DwmThumbnail(nuint id)
    {
        _id = id;
    }

    public static DwmThumbnail? TryRegister(nint destination, nint source)
    {
        var hr = PInvoke.DwmRegisterThumbnail(new HWND(destination), new HWND(source), out var id);
        return hr.Succeeded ? new DwmThumbnail(id) : null;
    }

    public void Update(ScreenRect destinationClientRect, byte opacity, bool visible)
    {
        if (_id == 0)
        {
            return;
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE | PInvoke.DWM_TNP_OPACITY | PInvoke.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT
            {
                left = destinationClientRect.Left, top = destinationClientRect.Top,
                right = destinationClientRect.Right, bottom = destinationClientRect.Bottom,
            },
            fVisible = visible,
            opacity = opacity,
            fSourceClientAreaOnly = true,
        };
        PInvoke.DwmUpdateThumbnailProperties(_id, in properties);
    }

    public void Dispose()
    {
        if (_id != 0)
        {
            PInvoke.DwmUnregisterThumbnail(_id);
            _id = 0;
        }
    }
}
```

`src/CodeSwitchX.Hosting/Win32/HotkeyInterop.cs`:

```csharp
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace CodeSwitchX.Hosting.Win32;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}

public static class HotkeyInterop
{
    public const int WmHotkey = 0x0312;

    public static bool Register(nint hwnd, int id, HotkeyModifiers modifiers, uint virtualKey) =>
        PInvoke.RegisterHotKey(new HWND(hwnd), id, (HOT_KEY_MODIFIERS)(uint)modifiers, virtualKey);

    public static bool Unregister(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);
}
```

- [ ] **Step 5: Implement the VS Code helpers**

`src/CodeSwitchX.Hosting/VsCode/VsCodeLocator.cs`:

```csharp
namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeLocator
{
    public const string OverrideVariable = "CODESWITCHX_VSCODE_EXE";

    public static string? FindExecutable() => FindExecutable(File.Exists, Environment.GetEnvironmentVariable);

    public static string? FindExecutable(Func<string, bool> fileExists, Func<string, string?> env)
    {
        if (env(OverrideVariable) is { Length: > 0 } explicitPath && fileExists(explicitPath))
        {
            return explicitPath;
        }

        foreach (var root in new[] { env("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "Programs") : null, env("ProgramFiles"), env("ProgramFiles(x86)") })
        {
            if (root is null)
            {
                continue;
            }

            var candidate = Path.Combine(root, "Microsoft VS Code", "Code.exe");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        foreach (var dir in (env("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (fileExists(Path.Combine(dir, "code.cmd")))
            {
                var sibling = Path.GetFullPath(Path.Combine(dir, "..", "Code.exe"));
                if (fileExists(sibling))
                {
                    return sibling;
                }
            }
        }

        return null;
    }
}
```

`src/CodeSwitchX.Hosting/VsCode/IVsCodeLauncher.cs` and `VsCodeLauncher.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public readonly record struct LaunchResult(bool Started, int? ProcessId, string? Error);

public interface IVsCodeLauncher
{
    LaunchResult Launch(Workspace workspace);
}
```

```csharp
using System.Diagnostics;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Hosting.VsCode;

public sealed class VsCodeLauncher : IVsCodeLauncher
{
    private readonly string? _executable;

    public VsCodeLauncher(string? executable = null)
    {
        _executable = executable ?? VsCodeLocator.FindExecutable();
    }

    public string? Executable => _executable;

    public static string BuildArguments(Workspace workspace)
    {
        var target = workspace.WorkspaceFile ?? workspace.RootPath;
        var profile = string.IsNullOrWhiteSpace(workspace.VsCodeProfile) ? string.Empty : $" --profile \"{workspace.VsCodeProfile}\"";
        return $"--new-window{profile} \"{target}\"";
    }

    /// <summary>The text VS Code puts in its title for this target: the folder name, or "<file> (Workspace)".</summary>
    public static string DisplayNameForMatching(Workspace workspace) =>
        workspace.WorkspaceFile is { Length: > 0 } file
            ? $"{Path.GetFileNameWithoutExtension(file)} (Workspace)"
            : Path.GetFileName(workspace.RootPath.TrimEnd('\\', '/'));

    public LaunchResult Launch(Workspace workspace)
    {
        if (_executable is null || !File.Exists(_executable))
        {
            return new LaunchResult(false, null, $"VS Code executable not found ({_executable ?? "no candidate"}). Set {VsCodeLocator.OverrideVariable}.");
        }

        try
        {
            var info = new ProcessStartInfo(_executable, BuildArguments(workspace))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Directory.Exists(workspace.RootPath) ? workspace.RootPath : null,
            };
            using var process = Process.Start(info);
            return new LaunchResult(true, process?.Id, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new LaunchResult(false, null, ex.Message);
        }
    }
}
```

`src/CodeSwitchX.Hosting/VsCode/VsCodeWindowMatcher.cs`:

```csharp
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting.VsCode;

public static class VsCodeWindowMatcher
{
    public const string ElectronClass = "Chrome_WidgetWin_1";
    public const string ProcessName = "Code";
    private const string TitleSuffix = "Visual Studio Code";

    public static WindowInfo? FindNew(IReadOnlyList<WindowInfo> before, IReadOnlyList<WindowInfo> after, string displayName, Func<uint, string?> processName)
    {
        var known = before.Select(w => w.Hwnd).ToHashSet();
        var candidates = after
            .Where(w => !known.Contains(w.Hwnd))
            .Where(w => string.Equals(w.ClassName, ElectronClass, StringComparison.Ordinal))
            .Where(w => w.Title.Length > 0 && w.Title.Contains(displayName, StringComparison.OrdinalIgnoreCase))
            .Where(w => string.Equals(processName(w.ProcessId), ProcessName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.FirstOrDefault(w => w.Title.Contains(TitleSuffix, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }
}
```

- [ ] **Step 6: Implement the HostManager**

`src/CodeSwitchX.Hosting/HostState.cs`, `HostedWorkspace.cs`, `HostManagerOptions.cs`:

```csharp
namespace CodeSwitchX.Hosting;

public enum HostState
{
    NotStarted,
    Starting,
    Running,
    Stopped,
}

public sealed record HostStateChanged(Guid WorkspaceId, HostState State, nint Hwnd, string? Error);
```

```csharp
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.Hosting;

/// <summary>Mutable per-workspace host record. Only <see cref="HostManager"/> writes to it.</summary>
public sealed class HostedWorkspace
{
    public HostedWorkspace(Guid workspaceId)
    {
        WorkspaceId = workspaceId;
    }

    public Guid WorkspaceId { get; }
    public HostState State { get; internal set; } = HostState.NotStarted;
    public nint Hwnd { get; internal set; }
    public uint ProcessId { get; internal set; }
    public bool Visible { get; internal set; }
    public ScreenRect? TargetRect { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
}
```

```csharp
namespace CodeSwitchX.Hosting;

public sealed class HostManagerOptions
{
    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}
```

`src/CodeSwitchX.Hosting/HostManager.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.Hosting;

/// <summary>Launches VS Code per workspace, tracks its window and keeps it docked over the Cab or cloaked.</summary>
public sealed class HostManager
{
    private readonly IWindowEnumerator _windows;
    private readonly IWindowDocker _docker;
    private readonly IVsCodeLauncher _launcher;
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<HostManager> _logger;
    private readonly HostManagerOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HostedWorkspace> _hosted = [];

    public HostManager(IWindowEnumerator windows, IWindowDocker docker, IVsCodeLauncher launcher, IEventBus bus, TimeProvider time,
        ILogger<HostManager> logger, HostManagerOptions? options = null)
    {
        _windows = windows;
        _docker = docker;
        _launcher = launcher;
        _bus = bus;
        _time = time;
        _logger = logger;
        _options = options ?? new HostManagerOptions();
    }

    public IReadOnlyList<HostedWorkspace> All
    {
        get
        {
            lock (_gate)
            {
                return _hosted.Values.ToArray();
            }
        }
    }

    public HostedWorkspace? Get(Guid workspaceId)
    {
        lock (_gate)
        {
            return _hosted.GetValueOrDefault(workspaceId);
        }
    }

    public async Task<HostedWorkspace> OpenAsync(Workspace workspace, CancellationToken ct)
    {
        HostedWorkspace hosted;
        lock (_gate)
        {
            if (!_hosted.TryGetValue(workspace.Id, out hosted!))
            {
                hosted = new HostedWorkspace(workspace.Id);
                _hosted[workspace.Id] = hosted;
            }

            if (hosted.State == HostState.Running && _docker.IsAlive(hosted.Hwnd))
            {
                return hosted;
            }

            if (hosted.State == HostState.Starting)
            {
                return hosted;
            }

            Transition(hosted, HostState.Starting, error: null);
        }

        var before = _windows.TopLevelWindows();
        var launch = _launcher.Launch(workspace);
        if (!launch.Started)
        {
            _logger.LogWarning("Launching VS Code for {Workspace} failed: {Error}", workspace.Name, launch.Error);
            lock (_gate)
            {
                Transition(hosted, HostState.Stopped, launch.Error ?? "launch failed");
            }

            return hosted;
        }

        var displayName = VsCodeLauncher.DisplayNameForMatching(workspace);
        var deadline = _time.GetUtcNow() + _options.DiscoveryTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
            var match = VsCodeWindowMatcher.FindNew(before, _windows.TopLevelWindows(), displayName, _windows.ProcessName);
            if (match is not null)
            {
                lock (_gate)
                {
                    hosted.Hwnd = match.Hwnd;
                    hosted.ProcessId = match.ProcessId;
                    hosted.StartedAt = _time.GetUtcNow();
                    hosted.Visible = false;
                    Transition(hosted, HostState.Running, error: null);
                }

                _logger.LogInformation("VS Code window {Hwnd} found for {Workspace}", match.Hwnd, workspace.Name);
                return hosted;
            }
        }

        lock (_gate)
        {
            Transition(hosted, HostState.Stopped, $"No VS Code window titled '{displayName}' appeared within {_options.DiscoveryTimeout.TotalSeconds:0}s");
        }

        return hosted;
    }

    public void ShowInCab(Guid workspaceId, ScreenRect rect)
    {
        lock (_gate)
        {
            if (!_hosted.TryGetValue(workspaceId, out var target) || target.State != HostState.Running)
            {
                return;
            }

            foreach (var other in _hosted.Values.Where(h => h != target && h.State == HostState.Running && h.Visible))
            {
                _docker.Cloak(other.Hwnd);
                other.Visible = false;
            }

            target.TargetRect = rect;
            _docker.Uncloak(target.Hwnd);
            _docker.MoveTo(target.Hwnd, rect);
            _docker.BringToFront(target.Hwnd);
            target.Visible = true;
        }
    }

    public void HideAll()
    {
        lock (_gate)
        {
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && h.Visible))
            {
                _docker.Cloak(hosted.Hwnd);
                hosted.Visible = false;
            }
        }
    }

    /// <summary>Called from the WinEvent watcher: put a docked window back if the user dragged it.</summary>
    public void SnapBack(nint hwnd)
    {
        lock (_gate)
        {
            var hosted = _hosted.Values.FirstOrDefault(h => h.Hwnd == hwnd && h.Visible && h.TargetRect is not null);
            if (hosted is null)
            {
                return;
            }

            var current = _docker.GetRect(hwnd);
            if (current is not null && current != hosted.TargetRect)
            {
                _docker.MoveTo(hwnd, hosted.TargetRect!.Value);
            }
        }
    }

    public void PollLiveness()
    {
        lock (_gate)
        {
            foreach (var hosted in _hosted.Values.Where(h => h.State == HostState.Running && !_docker.IsAlive(h.Hwnd)).ToList())
            {
                hosted.Visible = false;
                Transition(hosted, HostState.Stopped, "VS Code window closed");
            }
        }
    }

    public void Forget(Guid workspaceId)
    {
        lock (_gate)
        {
            _hosted.Remove(workspaceId);
        }
    }

    private void Transition(HostedWorkspace hosted, HostState state, string? error)
    {
        hosted.State = state;
        hosted.Error = error;
        _bus.Publish(new HostStateChanged(hosted.WorkspaceId, state, hosted.Hwnd, error));
    }
}
```

`src/CodeSwitchX.Hosting/HostingServiceCollectionExtensions.cs`:

```csharp
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSwitchX.Hosting;

public static class HostingServiceCollectionExtensions
{
    public static IServiceCollection AddCodeSwitchXHosting(this IServiceCollection services)
    {
        services.AddSingleton<IWindowEnumerator, Win32WindowEnumerator>();
        services.AddSingleton<IWindowDocker, SnapWindowDocker>();
        services.AddSingleton<IVsCodeLauncher>(_ => new VsCodeLauncher());
        services.AddSingleton<HostManagerOptions>();
        services.AddSingleton<HostManager>();
        return services;
    }
}
```

- [ ] **Step 7: Build and run the tests**

Run: `dotnet build src/CodeSwitchX.Hosting` then `dotnet test tests/CodeSwitchX.Hosting.Tests`
Expected: build succeeds (fix CsWin32 overload mismatches inside the private helpers only) and all tests pass. The Win32 classes are exercised manually in Task 14.

---

### Task 14: Core workspace registry, git inspector, probe; UI bootstrap and shell

**Files:**
- Create: `src/CodeSwitchX.Core/Workspaces/GitInspector.cs`, `WorkspaceProbe.cs`, `WorkspaceRegistry.cs`
- Create: `src/CodeSwitchX.Telemetry/TokenFormat.cs`, `IPricingProvider.cs`; modify `TelemetryService` to implement `IPricingProvider`
- Create: `src/CodeSwitchX.UI/Infrastructure/IUiDispatcher.cs`, `WpfUiDispatcher.cs`, `StartupCoordinator.cs`
- Create: `src/CodeSwitchX.UI/Shell/ShellMode.cs`, `ShellViewModel.cs`
- Replace: `src/CodeSwitchX.UI/App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`
- Modify: `src/CodeSwitchX.UI/CodeSwitchX.UI.csproj` (relay copy target)
- Test: `tests/CodeSwitchX.Core.Tests/Workspaces/GitInspectorTests.cs`, `WorkspaceProbeTests.cs`, `WorkspaceRegistryTests.cs`; `tests/CodeSwitchX.Telemetry.Tests/TokenFormatTests.cs`; `tests/CodeSwitchX.UI.Tests/ImmediateDispatcher.cs`, `StartupCoordinatorTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–13.
- Produces:
  - `GitInfo(bool IsRepository, string? Branch, int DirtyCount)`; `GitInspector(Func<string, string, CancellationToken, Task<string?>>? runGit = null)` with `static string? ReadBranch(string root)`, `Task<GitInfo> InspectAsync(string root, CancellationToken)`, `static Task<string?> RunGitAsync(string workingDirectory, string arguments, CancellationToken)`.
  - `WorktreeInfo(string Path, string? Branch)`; `WorkspaceProbeResult(string RootPath, string SuggestedName, string? WorkspaceFile, bool IsGitRepository, string? Branch, IReadOnlyList<string> SolutionFiles, bool HasClaudeMd, IReadOnlyList<WorktreeInfo> Worktrees)`; `WorkspaceProbe(GitInspector)` with `Task<WorkspaceProbeResult> ProbeAsync(string input, CancellationToken)` and `static IReadOnlyList<WorktreeInfo> ParseWorktreeList(string porcelain, string mainRootNormalized)`.
  - `WorkspaceRegistry(IWorkspaceStore, WorkspaceResolver, IEventBus)` with `Task<IReadOnlyList<Workspace>> LoadAsync(ct)`, `Task RegisterAsync(Workspace, ct)`, `Task UnregisterAsync(Guid, ct)`.
  - `TokenFormat.Compact(long) -> string`; `IPricingProvider { PricingTable Pricing { get; } }`.
  - `IUiDispatcher { void Post(Action action); }`; `StartupCoordinator(WorkspaceRegistry, ISessionStore, SessionEngine, TimeProvider, ILogger<StartupCoordinator>)` with `Task RunAsync(CancellationToken)`.
  - `enum ShellMode { Yard, Cab, Settings }`; `ShellViewModel` (see Step 6) — `YardViewModel`, `CabViewModel`, `SettingsViewModel`, `PerformanceBarViewModel` are defined in Tasks 15–18; this task only wires `ShellViewModel` against their constructors as listed there, so build Tasks 14–18 together before running the UI.

- [ ] **Step 1: Write the failing Core and Telemetry tests**

`tests/CodeSwitchX.Core.Tests/Workspaces/GitInspectorTests.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class GitInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-git-" + Guid.NewGuid().ToString("N"));

    public GitInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Branch_is_read_from_a_symbolic_HEAD()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/feature/x\n");

        GitInspector.ReadBranch(_root).ShouldBe("feature/x");
    }

    [Fact]
    public void Detached_HEAD_gives_a_short_hash()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "0123456789abcdef0123456789abcdef01234567\n");

        GitInspector.ReadBranch(_root).ShouldBe("0123456");
    }

    [Fact]
    public void Worktrees_follow_the_gitdir_pointer_file()
    {
        var gitdir = Path.Combine(_root, "real-gitdir");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(gitdir, "HEAD"), "ref: refs/heads/wt-branch\n");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitdir}\n");

        GitInspector.ReadBranch(worktree).ShouldBe("wt-branch");
    }

    [Fact]
    public void Non_repositories_have_no_branch()
    {
        GitInspector.ReadBranch(_root).ShouldBeNull();
    }

    [Fact]
    public async Task Inspect_counts_porcelain_status_lines()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, args, _) => Task.FromResult<string?>(args.Contains("status") ? " M a.cs\n?? b.cs\n" : null));

        var info = await inspector.InspectAsync(_root, CancellationToken.None);

        info.ShouldBe(new GitInfo(true, "main", 2));
    }

    [Fact]
    public async Task Inspect_without_git_available_still_reports_the_branch()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        var inspector = new GitInspector((_, _, _) => Task.FromResult<string?>(null));

        (await inspector.InspectAsync(_root, CancellationToken.None)).ShouldBe(new GitInfo(true, "main", 0));
        (await inspector.InspectAsync(Path.Combine(_root, "nope"), CancellationToken.None)).ShouldBe(new GitInfo(false, null, 0));
    }
}
```

`tests/CodeSwitchX.Core.Tests/Workspaces/WorkspaceProbeTests.cs`:

```csharp
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Workspaces;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class WorkspaceProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-probe-" + Guid.NewGuid().ToString("N"), "MyApp");
    private readonly WorkspaceProbe _probe;

    public WorkspaceProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_root, "MyApp.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Legacy.sln"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "CLAUDE.md"), "# notes");
        File.WriteAllText(Path.Combine(_root, "MyApp.code-workspace"), "{}");
        var porcelain = $"worktree {_root}\nHEAD abc\nbranch refs/heads/main\n\nworktree {Path.Combine(_root, "..", "MyApp-wt")}\nHEAD def\nbranch refs/heads/feature\n\n";
        _probe = new WorkspaceProbe(new GitInspector((_, args, _) => Task.FromResult<string?>(args.Contains("worktree") ? porcelain : " M x\n")));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [Fact]
    public async Task A_folder_is_probed_for_git_solutions_claude_md_and_worktrees()
    {
        var result = await _probe.ProbeAsync(_root, CancellationToken.None);

        result.RootPath.ShouldBe(PathNormalizer.Normalize(_root));
        result.SuggestedName.ShouldBe("MyApp");
        result.WorkspaceFile.ShouldBeNull();
        result.IsGitRepository.ShouldBeTrue();
        result.Branch.ShouldBe("main");
        result.SolutionFiles.Select(Path.GetFileName).ShouldBe(["Legacy.sln", "MyApp.slnx"], ignoreOrder: true);
        result.HasClaudeMd.ShouldBeTrue();
        var worktree = result.Worktrees.ShouldHaveSingleItem();
        worktree.Branch.ShouldBe("feature");
        worktree.Path.ShouldBe(PathNormalizer.Normalize(Path.Combine(_root, "..", "MyApp-wt")));
    }

    [Fact]
    public async Task A_solution_file_selects_its_folder_as_root()
    {
        var result = await _probe.ProbeAsync(Path.Combine(_root, "MyApp.slnx"), CancellationToken.None);

        result.RootPath.ShouldBe(PathNormalizer.Normalize(_root));
        result.SuggestedName.ShouldBe("MyApp");
        result.WorkspaceFile.ShouldBeNull();
    }

    [Fact]
    public async Task A_code_workspace_file_is_kept_as_the_launch_target()
    {
        var result = await _probe.ProbeAsync(Path.Combine(_root, "MyApp.code-workspace"), CancellationToken.None);

        result.RootPath.ShouldBe(PathNormalizer.Normalize(_root));
        result.WorkspaceFile.ShouldBe(Path.Combine(_root, "MyApp.code-workspace"));
    }

    [Fact]
    public async Task Unsupported_inputs_throw_a_clear_argument_error()
    {
        await Should.ThrowAsync<ArgumentException>(() => _probe.ProbeAsync(Path.Combine(_root, "CLAUDE.md"), CancellationToken.None));
        await Should.ThrowAsync<DirectoryNotFoundException>(() => _probe.ProbeAsync(Path.Combine(_root, "missing"), CancellationToken.None));
    }

    [Fact]
    public void ParseWorktreeList_skips_the_main_root_and_handles_detached_entries()
    {
        const string porcelain = "worktree C:/repo/app\nHEAD 111\nbranch refs/heads/main\n\nworktree C:/repo/app-a\nHEAD 222\nbranch refs/heads/a\n\nworktree C:/repo/app-b\nHEAD 333\ndetached\n\n";

        var list = WorkspaceProbe.ParseWorktreeList(porcelain, @"c:\repo\app");

        list.ShouldBe([new WorktreeInfo(@"c:\repo\app-a", "a"), new WorktreeInfo(@"c:\repo\app-b", null)]);
    }
}
```

`tests/CodeSwitchX.Core.Tests/Workspaces/WorkspaceRegistryTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.Core.Tests.Workspaces;

public class WorkspaceRegistryTests
{
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly WorkspaceResolver _resolver = new();
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly List<object> _messages = [];
    private readonly WorkspaceRegistry _registry;

    public WorkspaceRegistryTests()
    {
        _bus.Subscribe<WorkspaceRegistered>(_messages.Add);
        _bus.Subscribe<WorkspaceUnregistered>(_messages.Add);
        _bus.Subscribe<WorkspaceRootsChanged>(_messages.Add);
        _registry = new WorkspaceRegistry(_store, _resolver, _bus);
    }

    [Fact]
    public async Task Register_normalises_paths_saves_and_reloads_the_resolver()
    {
        var workspace = new Workspace { Name = "App", RootPath = @"C:\Repo\App\", Worktrees = { new Worktree { Path = @"C:/Repo/App-wt" } } };
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([workspace]));

        await _registry.RegisterAsync(workspace, CancellationToken.None);

        workspace.RootPath.ShouldBe(@"c:\repo\app");
        workspace.Worktrees[0].Path.ShouldBe(@"c:\repo\app-wt");
        workspace.Worktrees[0].WorkspaceId.ShouldBe(workspace.Id);
        await _store.Received(1).AddAsync(workspace, Arg.Any<CancellationToken>());
        _resolver.Resolve(@"C:\Repo\App\src").ShouldBe(workspace.Id);
        _resolver.Resolve(@"C:\Repo\App-wt\x").ShouldBe(workspace.Id);
        _messages.OfType<WorkspaceRootsChanged>().ShouldNotBeEmpty();
        _messages.OfType<WorkspaceRegistered>().ShouldHaveSingleItem().Workspace.ShouldBeSameAs(workspace);
    }

    [Fact]
    public async Task Unregister_removes_and_reloads()
    {
        var id = Guid.NewGuid();
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([]));

        await _registry.UnregisterAsync(id, CancellationToken.None);

        await _store.Received(1).RemoveAsync(id, Arg.Any<CancellationToken>());
        _messages.OfType<WorkspaceUnregistered>().ShouldHaveSingleItem().WorkspaceId.ShouldBe(id);
    }
}
```

`tests/CodeSwitchX.Telemetry.Tests/TokenFormatTests.cs`:

```csharp
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.Telemetry.Tests;

public class TokenFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(999_950, "1M")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(3_400_000_000, "3.4B")]
    public void Compact_uses_K_M_B_suffixes(long tokens, string expected)
    {
        TokenFormat.Compact(tokens).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Write the failing UI tests**

`tests/CodeSwitchX.UI.Tests/ImmediateDispatcher.cs`:

```csharp
using CodeSwitchX.UI.Infrastructure;

namespace CodeSwitchX.UI.Tests;

/// <summary>Runs posted work synchronously; tests never need a WPF dispatcher.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
```

`tests/CodeSwitchX.UI.Tests/StartupCoordinatorTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests;

public class StartupCoordinatorTests
{
    [Fact]
    public async Task Run_loads_workspaces_restores_recent_sessions_and_starts_the_engine()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var bus = new EventBus(NullLogger<EventBus>.Instance);
        var resolver = new WorkspaceResolver();
        var workspaces = Substitute.For<IWorkspaceStore>();
        var workspace = new Workspace { Name = "App", RootPath = @"c:\repo\app" };
        workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([workspace]));
        var sessions = Substitute.For<ISessionStore>();
        var record = SessionRecord.FromSnapshot(new SessionSnapshot
        {
            SessionId = "s1", State = SessionState.Idle, StartedAt = time.GetUtcNow().AddHours(-2), LastEventAt = time.GetUtcNow().AddHours(-1), StateSince = time.GetUtcNow().AddHours(-1), Cwd = @"c:\repo\app\src",
        });
        sessions.GetActiveSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionRecord>>([record]));
        var engine = new SessionEngine(bus, resolver, time, NullLogger<SessionEngine>.Instance);
        var coordinator = new StartupCoordinator(new WorkspaceRegistry(workspaces, resolver, bus), sessions, engine, time, NullLogger<StartupCoordinator>.Instance);

        await coordinator.RunAsync(CancellationToken.None);

        await sessions.Received(1).GetActiveSinceAsync(time.GetUtcNow().AddHours(-24), Arg.Any<CancellationToken>());
        engine.Get("s1")!.WorkspaceId.ShouldBe(workspace.Id);
        resolver.Resolve(@"c:\repo\app").ShouldBe(workspace.Id);

        bus.Publish(new HookEventReceived(new HookEvent { SessionId = "s2", EventName = "SessionStart", Signal = SessionSignal.SessionStart, At = time.GetUtcNow() }));
        engine.Get("s2").ShouldNotBeNull("Start() must have subscribed the engine to the bus");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.Core.Tests`, `dotnet test tests/CodeSwitchX.Telemetry.Tests`, `dotnet test tests/CodeSwitchX.UI.Tests`
Expected: build errors for the missing types.

- [ ] **Step 4: Implement the Core pieces**

`src/CodeSwitchX.Core/Workspaces/GitInspector.cs`:

```csharp
using System.Diagnostics;

namespace CodeSwitchX.Core.Workspaces;

public sealed record GitInfo(bool IsRepository, string? Branch, int DirtyCount);

/// <summary>Cheap git facts for tiles: branch from HEAD (no process), dirty count from <c>git status --porcelain</c>.</summary>
public sealed class GitInspector
{
    private readonly Func<string, string, CancellationToken, Task<string?>> _runGit;

    public GitInspector(Func<string, string, CancellationToken, Task<string?>>? runGit = null)
    {
        _runGit = runGit ?? RunGitAsync;
    }

    public static string? ReadBranch(string root)
    {
        var gitDir = ResolveGitDir(root);
        if (gitDir is null)
        {
            return null;
        }

        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile))
        {
            return null;
        }

        var head = File.ReadAllText(headFile).Trim();
        const string refPrefix = "ref: refs/heads/";
        if (head.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            return head[refPrefix.Length..];
        }

        return head.Length >= 7 ? head[..7] : head;
    }

    public async Task<GitInfo> InspectAsync(string root, CancellationToken ct)
    {
        if (ResolveGitDir(root) is null)
        {
            return new GitInfo(false, null, 0);
        }

        var branch = ReadBranch(root);
        var status = await _runGit(root, "status --porcelain --untracked-files=normal", ct).ConfigureAwait(false);
        var dirty = status is null
            ? 0
            : status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Trim('\r').Length > 0);
        return new GitInfo(true, branch, dirty);
    }

    public static async Task<string?> RunGitAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static string? ResolveGitDir(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        var pointer = File.ReadAllText(dotGit).Trim();
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = pointer[prefix.Length..].Trim();
        var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(root, target));
        return Directory.Exists(resolved) ? resolved : null;
    }
}
```

`src/CodeSwitchX.Core/Workspaces/WorkspaceProbe.cs`:

```csharp
using CodeSwitchX.Core.Paths;

namespace CodeSwitchX.Core.Workspaces;

public sealed record WorktreeInfo(string Path, string? Branch);

public sealed record WorkspaceProbeResult(
    string RootPath,
    string SuggestedName,
    string? WorkspaceFile,
    bool IsGitRepository,
    string? Branch,
    IReadOnlyList<string> SolutionFiles,
    bool HasClaudeMd,
    IReadOnlyList<WorktreeInfo> Worktrees);

/// <summary>Inspects what the user picked in "Add workspace" and proposes the registration.</summary>
public sealed class WorkspaceProbe
{
    private static readonly string[] SolutionPatterns = ["*.sln", "*.slnx"];
    private readonly GitInspector _git;

    public WorkspaceProbe(GitInspector git)
    {
        _git = git;
    }

    public async Task<WorkspaceProbeResult> ProbeAsync(string input, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        string root;
        string? workspaceFile = null;
        if (File.Exists(input))
        {
            var extension = Path.GetExtension(input);
            if (extension.Equals(".code-workspace", StringComparison.OrdinalIgnoreCase))
            {
                workspaceFile = Path.GetFullPath(input);
            }
            else if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Pick a folder, a .code-workspace file, or a .sln/.slnx solution.", nameof(input));
            }

            root = Path.GetDirectoryName(Path.GetFullPath(input))!;
        }
        else if (Directory.Exists(input))
        {
            root = Path.GetFullPath(input);
        }
        else
        {
            throw new DirectoryNotFoundException($"'{input}' does not exist.");
        }

        var normalizedRoot = PathNormalizer.Normalize(root);
        var git = await _git.InspectAsync(root, ct).ConfigureAwait(false);
        var worktrees = git.IsRepository
            ? ParseWorktreeList(await GitInspector.RunGitOrAsync(_git, root, "worktree list --porcelain", ct).ConfigureAwait(false) ?? string.Empty, normalizedRoot)
            : [];
        var solutions = SolutionPatterns.SelectMany(p => Directory.EnumerateFiles(root, p, SearchOption.TopDirectoryOnly)).OrderBy(f => f).ToList();

        return new WorkspaceProbeResult(
            normalizedRoot,
            Path.GetFileName(root.TrimEnd('\\', '/')),
            workspaceFile,
            git.IsRepository,
            git.Branch,
            solutions,
            File.Exists(Path.Combine(root, "CLAUDE.md")),
            worktrees);
    }

    public static IReadOnlyList<WorktreeInfo> ParseWorktreeList(string porcelain, string mainRootNormalized)
    {
        var result = new List<WorktreeInfo>();
        string? path = null;
        string? branch = null;
        foreach (var raw in porcelain.Split('\n').Select(l => l.TrimEnd('\r')).Append(string.Empty))
        {
            if (raw.Length == 0)
            {
                if (path is not null)
                {
                    var normalized = PathNormalizer.Normalize(path);
                    if (normalized != mainRootNormalized)
                    {
                        result.Add(new WorktreeInfo(normalized, branch));
                    }
                }

                path = null;
                branch = null;
                continue;
            }

            if (raw.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = raw["worktree ".Length..];
            }
            else if (raw.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = raw["branch refs/heads/".Length..];
            }
        }

        return result;
    }
}
```

Add to `GitInspector` a small forwarding helper so the probe can reuse the injected runner:

```csharp
    /// <summary>Runs git through the inspector's configured runner (real process by default, fake in tests).</summary>
    public static Task<string?> RunGitOrAsync(GitInspector inspector, string workingDirectory, string arguments, CancellationToken ct) =>
        inspector._runGit(workingDirectory, arguments, ct);
```

`src/CodeSwitchX.Core/Workspaces/WorkspaceRegistry.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;

namespace CodeSwitchX.Core.Workspaces;

/// <summary>Single entry point for changing registered workspaces: persists, refreshes the resolver, notifies the bus.</summary>
public sealed class WorkspaceRegistry
{
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceResolver _resolver;
    private readonly IEventBus _bus;

    public WorkspaceRegistry(IWorkspaceStore store, WorkspaceResolver resolver, IEventBus bus)
    {
        _store = store;
        _resolver = resolver;
        _bus = bus;
    }

    public async Task<IReadOnlyList<Workspace>> LoadAsync(CancellationToken ct)
    {
        var workspaces = await _store.GetAllAsync(ct).ConfigureAwait(false);
        _resolver.SetRoots(WorkspaceResolver.RootsOf(workspaces));
        _bus.Publish(new WorkspaceRootsChanged());
        return workspaces;
    }

    public async Task RegisterAsync(Workspace workspace, CancellationToken ct)
    {
        workspace.RootPath = PathNormalizer.Normalize(workspace.RootPath);
        foreach (var worktree in workspace.Worktrees)
        {
            worktree.WorkspaceId = workspace.Id;
            worktree.Path = PathNormalizer.Normalize(worktree.Path);
        }

        await _store.AddAsync(workspace, ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        _bus.Publish(new WorkspaceRegistered(workspace));
    }

    public async Task UnregisterAsync(Guid workspaceId, CancellationToken ct)
    {
        await _store.RemoveAsync(workspaceId, ct).ConfigureAwait(false);
        await LoadAsync(ct).ConfigureAwait(false);
        _bus.Publish(new WorkspaceUnregistered(workspaceId));
    }
}
```

`src/CodeSwitchX.Telemetry/TokenFormat.cs` and `IPricingProvider.cs`:

```csharp
using System.Globalization;

namespace CodeSwitchX.Telemetry;

public static class TokenFormat
{
    public static string Compact(long tokens)
    {
        var abs = Math.Abs(tokens);
        return abs switch
        {
            < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => Scale(tokens, 1_000, "K"),
            < 1_000_000_000 => Scale(tokens, 1_000_000, "M"),
            _ => Scale(tokens, 1_000_000_000, "B"),
        };
    }

    private static string Scale(long tokens, long divisor, string suffix)
    {
        var value = Math.Round((double)tokens / divisor, 1);
        if (value >= 1000)
        {
            return Compact((long)(value * divisor));
        }

        var text = value >= 100 || value == Math.Floor(value)
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#", CultureInfo.InvariantCulture);
        return text + suffix;
    }
}
```

```csharp
namespace CodeSwitchX.Telemetry;

public interface IPricingProvider
{
    PricingTable Pricing { get; }
}
```

Change `TelemetryService` to `public sealed class TelemetryService : IHostedService, IDisposable, IPricingProvider` and register `services.AddSingleton<IPricingProvider>(sp => sp.GetRequiredService<TelemetryService>());` in `AddCodeSwitchXTelemetry`.

- [ ] **Step 5: Implement the UI infrastructure and startup**

`src/CodeSwitchX.UI/Infrastructure/IUiDispatcher.cs`, `WpfUiDispatcher.cs`:

```csharp
namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Marshals bus callbacks onto the UI thread; tests substitute a synchronous implementation.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
```

```csharp
using System.Windows.Threading;

namespace CodeSwitchX.UI.Infrastructure;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}
```

`src/CodeSwitchX.UI/Infrastructure/StartupCoordinator.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Ordered startup: workspaces into the resolver, recent sessions into the engine, then the engine goes live.</summary>
public sealed class StartupCoordinator
{
    public static readonly TimeSpan SessionRestoreWindow = TimeSpan.FromHours(24);

    private readonly WorkspaceRegistry _workspaces;
    private readonly ISessionStore _sessions;
    private readonly SessionEngine _engine;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupCoordinator> _logger;

    public StartupCoordinator(WorkspaceRegistry workspaces, ISessionStore sessions, SessionEngine engine, TimeProvider time, ILogger<StartupCoordinator> logger)
    {
        _workspaces = workspaces;
        _sessions = sessions;
        _engine = engine;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var workspaces = await _workspaces.LoadAsync(ct).ConfigureAwait(false);
        var records = await _sessions.GetActiveSinceAsync(_time.GetUtcNow() - SessionRestoreWindow, ct).ConfigureAwait(false);
        _engine.Restore(records.Select(r => r.ToSnapshot()));
        _engine.ReResolveWorkspaces();
        _engine.Start();
        _logger.LogInformation("Restored {Workspaces} workspaces and {Sessions} sessions", workspaces.Count, records.Count);
    }
}
```

`src/CodeSwitchX.UI/Shell/ShellMode.cs`:

```csharp
namespace CodeSwitchX.UI.Shell;

public enum ShellMode
{
    Yard,
    Cab,
    Settings,
}
```

- [ ] **Step 6: Implement the shell view model**

`src/CodeSwitchX.UI/Shell/ShellViewModel.cs`:

```csharp
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Shell;

/// <summary>Owns the Yard/Cab/Settings mode switch and drives the HostManager for the active workspace.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly HostManager _host;
    private readonly ILogger<ShellViewModel> _logger;

    [ObservableProperty]
    private ShellMode _mode = ShellMode.Yard;

    [ObservableProperty]
    private Guid? _activeWorkspaceId;

    [ObservableProperty]
    private string? _statusMessage;

    public ShellViewModel(YardViewModel yard, CabViewModel cab, SettingsViewModel settings, PerformanceBarViewModel performanceBar,
        HostManager host, ILogger<ShellViewModel> logger)
    {
        Yard = yard;
        Cab = cab;
        Settings = settings;
        PerformanceBar = performanceBar;
        _host = host;
        _logger = logger;
        Yard.OpenRequested += id => _ = EnterCabAsync(id);
        Cab.BackRequested += BackToYard;
    }

    public YardViewModel Yard { get; }
    public CabViewModel Cab { get; }
    public SettingsViewModel Settings { get; }
    public PerformanceBarViewModel PerformanceBar { get; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await Yard.InitializeAsync(ct);
        await PerformanceBar.InitializeAsync(ct);
        Settings.Refresh();
    }

    [RelayCommand]
    public async Task EnterCabAsync(Guid workspaceId)
    {
        var tile = Yard.FindTile(workspaceId);
        if (tile is null)
        {
            return;
        }

        ActiveWorkspaceId = workspaceId;
        Cab.SetActive(tile, Yard.Tiles);
        Mode = ShellMode.Cab;
        StatusMessage = null;

        try
        {
            var hosted = await _host.OpenAsync(tile.Workspace, CancellationToken.None);
            if (hosted.State != HostState.Running)
            {
                StatusMessage = hosted.Error ?? "VS Code did not start.";
                return;
            }

            if (Cab.LastHostRect is { } rect && Mode == ShellMode.Cab && ActiveWorkspaceId == workspaceId)
            {
                _host.ShowInCab(workspaceId, rect);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening workspace {Workspace} failed", tile.Name);
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    public void BackToYard()
    {
        _host.HideAll();
        Mode = ShellMode.Yard;
    }

    [RelayCommand]
    public void ToggleMode()
    {
        if (Mode == ShellMode.Cab)
        {
            BackToYard();
        }
        else if (ActiveWorkspaceId is { } id)
        {
            _ = EnterCabAsync(id);
        }
    }

    [RelayCommand]
    public async Task JumpToAsync(int oneBasedIndex)
    {
        var tiles = Yard.Tiles.ToList();
        if (oneBasedIndex < 1 || oneBasedIndex > tiles.Count)
        {
            return;
        }

        await EnterCabAsync(tiles[oneBasedIndex - 1].Id);
    }

    [RelayCommand]
    public void OpenSettings()
    {
        _host.HideAll();
        Settings.Refresh();
        Mode = ShellMode.Settings;
    }

    [RelayCommand]
    public void CloseSettings() => Mode = ShellMode.Yard;

    /// <summary>Called by the Cab view whenever the host area's screen rectangle changes.</summary>
    public void UpdateCabRect(ScreenRect rect)
    {
        Cab.LastHostRect = rect;
        if (Mode == ShellMode.Cab && ActiveWorkspaceId is { } id)
        {
            _host.ShowInCab(id, rect);
        }
    }
}
```

- [ ] **Step 7: Implement App, MainWindow and the relay copy**

`src/CodeSwitchX.UI/App.xaml`:

```xml
<Application x:Class="CodeSwitchX.UI.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:infra="clr-namespace:CodeSwitchX.UI.Infrastructure"
             ShutdownMode="OnMainWindowClose"
             ThemeMode="System">
  <Application.Resources>
    <infra:SessionStateToBrushConverter x:Key="StateBrush" />
    <infra:HexToBrushConverter x:Key="HexBrush" />
    <infra:PressureToBrushConverter x:Key="PressureBrush" />
    <infra:HostStateToTextConverter x:Key="HostStateText" />
    <infra:EnumEqualsToVisibilityConverter x:Key="EnumVisible" />
    <infra:NullToCollapsedConverter x:Key="NullToCollapsed" />
    <BooleanToVisibilityConverter x:Key="BoolVisible" />
    <DrawingImage x:Key="AppIcon">
      <DrawingImage.Drawing>
        <DrawingGroup>
          <GeometryDrawing Brush="#1F2937">
            <GeometryDrawing.Geometry>
              <RectangleGeometry Rect="0,0,32,32" RadiusX="6" RadiusY="6" />
            </GeometryDrawing.Geometry>
          </GeometryDrawing>
          <GeometryDrawing Brush="#F59E0B">
            <GeometryDrawing.Geometry>
              <EllipseGeometry Center="10,16" RadiusX="4" RadiusY="4" />
            </GeometryDrawing.Geometry>
          </GeometryDrawing>
          <GeometryDrawing Brush="#22C55E">
            <GeometryDrawing.Geometry>
              <EllipseGeometry Center="22,16" RadiusX="4" RadiusY="4" />
            </GeometryDrawing.Geometry>
          </GeometryDrawing>
        </DrawingGroup>
      </DrawingImage.Drawing>
    </DrawingImage>
  </Application.Resources>
</Application>
```

`src/CodeSwitchX.UI/App.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Threading;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.Hosting;
using CodeSwitchX.Ingest;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Workspaces;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodeSwitchX.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Default();
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "codeswitchx-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Debug()
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog(Log.Logger);
            ConfigureServices(builder.Services, paths);
            _host = builder.Build();

            await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
            await _host.Services.GetRequiredService<StartupCoordinator>().RunAsync(CancellationToken.None);
            await _host.StartAsync();

            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync(CancellationToken.None);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CodeSwitchX failed to start");
            MessageBox.Show($"CodeSwitchX failed to start:\n\n{ex.Message}\n\nSee {paths.LogsDirectory}", "CodeSwitchX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(ClaudeCodePaths.Default());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Current.Dispatcher));

        services.AddSingleton<WorkspaceResolver>();
        services.AddSingleton<IWorkspaceResolver>(sp => sp.GetRequiredService<WorkspaceResolver>());
        services.AddSingleton<WorkspaceRegistry>();
        services.AddSingleton<GitInspector>();
        services.AddSingleton<WorkspaceProbe>();
        services.AddSingleton<SessionEngineOptions>();
        services.AddSingleton<SessionEngine>();
        services.AddSingleton<IProcessProbe, SystemProcessProbe>();
        services.AddHostedService<ProcessLivenessMonitor>();

        services.AddCodeSwitchXData(paths.DatabaseFile);
        services.AddCodeSwitchXIngest();
        services.AddCodeSwitchXTelemetry();
        services.AddCodeSwitchXHosting();

        services.AddSingleton<StartupCoordinator>();
        services.AddSingleton<YardViewModel>();
        services.AddSingleton<CabViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PerformanceBarViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<AddWorkspaceViewModel>();
        services.AddSingleton<Func<AddWorkspaceViewModel>>(sp => () => sp.GetRequiredService<AddWorkspaceViewModel>());
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _host.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Host shutdown did not complete cleanly");
            }

            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
```

`src/CodeSwitchX.UI/MainWindow.xaml`:

```xml
<Window x:Class="CodeSwitchX.UI.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:shell="clr-namespace:CodeSwitchX.UI.Shell"
        xmlns:yard="clr-namespace:CodeSwitchX.UI.Yard"
        xmlns:cab="clr-namespace:CodeSwitchX.UI.Cab"
        xmlns:settings="clr-namespace:CodeSwitchX.UI.Settings"
        xmlns:telemetry="clr-namespace:CodeSwitchX.UI.Telemetry"
        Title="CodeSwitchX" Width="1400" Height="900" MinWidth="900" MinHeight="600"
        Icon="{StaticResource AppIcon}"
        d:DataContext="{d:DesignInstance shell:ShellViewModel}"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        mc:Ignorable="d">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="*" />
      <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>

    <yard:YardView Grid.Row="0" DataContext="{Binding Yard}"
                   Visibility="{Binding DataContext.Mode, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource EnumVisible}, ConverterParameter=Yard}" />
    <cab:CabView x:Name="CabView" Grid.Row="0" DataContext="{Binding Cab}"
                 Visibility="{Binding DataContext.Mode, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource EnumVisible}, ConverterParameter=Cab}" />
    <settings:SettingsView Grid.Row="0" DataContext="{Binding Settings}"
                           Visibility="{Binding DataContext.Mode, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource EnumVisible}, ConverterParameter=Settings}" />

    <telemetry:PerformanceBarView Grid.Row="1" DataContext="{Binding PerformanceBar}" />
  </Grid>
</Window>
```

`src/CodeSwitchX.UI/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Workspaces;

namespace CodeSwitchX.UI;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray;
    private readonly HostManager _host;
    private readonly Func<AddWorkspaceViewModel> _addWorkspaceFactory;
    private WindowLocationWatcher? _locationWatcher;
    private System.Windows.Threading.DispatcherTimer? _livenessTimer;

    public MainWindow(ShellViewModel shell, HotkeyService hotkeys, TrayIconService tray, HostManager host, Func<AddWorkspaceViewModel> addWorkspaceFactory)
    {
        InitializeComponent();
        _shell = shell;
        _hotkeys = hotkeys;
        _tray = tray;
        _host = host;
        _addWorkspaceFactory = addWorkspaceFactory;
        DataContext = shell;
        CabView.HostRectChanged += rect => _shell.UpdateCabRect(rect);
        shell.Yard.AddWorkspaceRequested += () => _ = ShowAddWorkspaceAsync();
    }

    private async Task ShowAddWorkspaceAsync()
    {
        var viewModel = _addWorkspaceFactory();
        await viewModel.LoadAsync(CancellationToken.None);
        var dialog = new AddWorkspaceWindow(viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>Mouse "back" button (XButton1) returns to the Yard, as the spec asks.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && _shell.Mode == ShellMode.Cab)
        {
            _shell.BackToYard();
            e.Handled = true;
        }

        base.OnPreviewMouseDown(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkeys.Attach(hwnd, _shell);
        _tray.Attach(this, _shell);
        _locationWatcher = new WindowLocationWatcher();
        _locationWatcher.Moved += movedHwnd => _host.SnapBack(movedHwnd);
        _livenessTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => _host.PollLiveness(), Dispatcher);
        _livenessTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _livenessTimer?.Stop();
        _locationWatcher?.Dispose();
        _hotkeys.Detach();
        _tray.Detach();
        _host.HideAll();
        base.OnClosed(e);
    }
}
```

Add to `CodeSwitchX.UI.csproj` (inside `<Project>`): a build-order-only reference to the relay and a copy step so `relay\csx-hook.exe` sits next to the app in every build output:

```xml
  <ItemGroup>
    <ProjectReference Include="..\CodeSwitchX.Hook\CodeSwitchX.Hook.csproj" ReferenceOutputAssembly="false" Private="false" />
  </ItemGroup>
  <Target Name="CopyRelay" AfterTargets="Build">
    <ItemGroup>
      <RelayFiles Include="..\CodeSwitchX.Hook\bin\$(Configuration)\$(TargetFramework)\csx-hook.*" />
    </ItemGroup>
    <Copy SourceFiles="@(RelayFiles)" DestinationFolder="$(OutDir)relay" SkipUnchangedFiles="true" />
  </Target>
```

- [ ] **Step 8: Build and run the Core, Telemetry and UI tests**

Run: `dotnet test tests/CodeSwitchX.Core.Tests`, `dotnet test tests/CodeSwitchX.Telemetry.Tests`, then (after Tasks 15–18 exist) `dotnet build CodeSwitchX.slnx` and `dotnet test tests/CodeSwitchX.UI.Tests`
Expected: all pass. The UI project only compiles once Tasks 15–18 supply `YardViewModel`, `CabViewModel`, `SettingsViewModel`, `PerformanceBarViewModel`, `HotkeyService`, `TrayIconService`, the views and the converters.

---

### Task 15: Yard — tiles, chat rows, converters

**Files:**
- Create: `src/CodeSwitchX.UI/Yard/ChatRowViewModel.cs`, `WorkspaceTileViewModel.cs`, `TrackGroupViewModel.cs`, `YardViewModel.cs`, `YardView.xaml`, `YardView.xaml.cs`
- Create: `src/CodeSwitchX.UI/Infrastructure/Converters.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Yard/ChatRowViewModelTests.cs`, `YardViewModelTests.cs`

**Interfaces:**
- Consumes: `SessionEngine`, `SessionSnapshot`, `SessionChanged`, `WorkspaceRegistered/Unregistered`, `HostStateChanged`, `IWorkspaceStore`, `WorkspaceRegistry`, `GitInspector`, `IPricingProvider`, `ContextFillCalculator`, `IUiDispatcher`.
- Produces:
  - `ChatRowViewModel(string sessionId)` with `Title, State, StateSince, ElapsedText, ContextFill, Pressure, Inferred, LastToolName, IsLive`, `Update(SessionSnapshot, PricingTable)`, `Tick(DateTimeOffset now)`, `static string FormatElapsed(TimeSpan)`.
  - `WorkspaceTileViewModel(Workspace, YardViewModel owner)` with `Id, Name, Workspace, AccentColor, Branch, DirtyCount, HostState, NeedsAttention, HasInferredChats, Chats, AttentionRank`, `Upsert(SessionSnapshot, PricingTable)`, `Tick(now)`, commands `Open, RevealInExplorer, OpenTerminal, Unregister`.
  - `TrackGroupViewModel(Track)` with `Name`, `SortOrder`, `ObservableCollection<WorkspaceTileViewModel> Tiles`.
  - `YardViewModel(IWorkspaceStore, WorkspaceRegistry, SessionEngine, IPricingProvider, GitInspector, IEventBus, IUiDispatcher, TimeProvider, ILogger<YardViewModel>)` with `Tracks`, `Tiles` (flattened, ordered), `NeedsMeFirst`, `event Action<Guid>? OpenRequested`, `event Action? AddWorkspaceRequested`, `InitializeAsync(ct)`, `FindTile(Guid)`, `Tick(DateTimeOffset)`, `RefreshGitAsync(ct)`, `RequestOpen(Guid)`, `UnregisterAsync(Guid)`, commands `AddWorkspace`, `RefreshGit`.
  - Converters in `CodeSwitchX.UI.Infrastructure`: `SessionStateToBrushConverter`, `HexToBrushConverter`, `PressureToBrushConverter`, `HostStateToTextConverter`, `EnumEqualsToVisibilityConverter`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.UI.Tests/Yard/ChatRowViewModelTests.cs`:

```csharp
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Yard;

public class ChatRowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly PricingTable Pricing = new([new PricingRule { Model = "claude-sonnet-5", ContextWindow = 1_000_000 }]);

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(65, "1m")]
    [InlineData(3_700, "1h 01m")]
    [InlineData(90_000, "1d 1h")]
    public void Elapsed_is_compact(int seconds, string expected)
    {
        ChatRowViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Fact]
    public void Update_copies_the_snapshot_and_computes_context_pressure()
    {
        var row = new ChatRowViewModel("s1");
        row.Update(new SessionSnapshot
        {
            SessionId = "s1", Title = "Fix build", State = SessionState.Waiting, StartedAt = Now.AddMinutes(-10), LastEventAt = Now, StateSince = Now.AddSeconds(-30),
            Model = "claude-sonnet-5", Inferred = true, LastToolName = "Bash", LatestContext = new TokenUsage(100_000, 0, 50_000, 700_000),
        }, Pricing);
        row.Tick(Now);

        row.Title.ShouldBe("Fix build");
        row.State.ShouldBe(SessionState.Waiting);
        row.ElapsedText.ShouldBe("30s");
        row.ContextFill.ShouldBe(0.85, tolerance: 1e-9);
        row.Pressure.ShouldBe(ContextPressure.Amber);
        row.Inferred.ShouldBeTrue();
        row.LastToolName.ShouldBe("Bash");
        row.IsLive.ShouldBeTrue();
    }

    [Fact]
    public void Untitled_sessions_show_a_placeholder()
    {
        var row = new ChatRowViewModel("abcdef12-3456");
        row.Update(new SessionSnapshot { SessionId = "abcdef12-3456", State = SessionState.Idle, StartedAt = Now, LastEventAt = Now, StateSince = Now }, Pricing);

        row.Title.ShouldBe("Chat abcdef12");
    }
}
```

`tests/CodeSwitchX.UI.Tests/Yard/YardViewModelTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Yard;

public class YardViewModelTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly EventBus _bus = new(NullLogger<EventBus>.Instance);
    private readonly IWorkspaceStore _store = Substitute.For<IWorkspaceStore>();
    private readonly WorkspaceResolver _resolver = new();
    private readonly SessionEngine _engine;
    private readonly Track _general = new() { Name = "General", SortOrder = 0 };
    private readonly Track _clients = new() { Name = "Clients", SortOrder = 1 };
    private readonly Workspace _app;
    private readonly Workspace _shop;
    private readonly YardViewModel _yard;

    public YardViewModelTests()
    {
        _app = new Workspace { Name = "App", RootPath = @"c:\repo\app", TrackId = _general.Id };
        _shop = new Workspace { Name = "Shop", RootPath = @"c:\repo\shop", TrackId = _clients.Id };
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general, _clients]));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([_app, _shop]));
        _engine = new SessionEngine(_bus, _resolver, _time, NullLogger<SessionEngine>.Instance);
        var pricing = Substitute.For<IPricingProvider>();
        pricing.Pricing.Returns(PricingTable.Default);
        var registry = new WorkspaceRegistry(_store, _resolver, _bus);
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        _yard = new YardViewModel(_store, registry, _engine, pricing, git, _bus, new ImmediateDispatcher(), _time, NullLogger<YardViewModel>.Instance);
    }

    private SessionSnapshot Snapshot(string id, Guid workspaceId, SessionState state) => new()
    {
        SessionId = id, WorkspaceId = workspaceId, State = state, StartedAt = _time.GetUtcNow(), LastEventAt = _time.GetUtcNow(), StateSince = _time.GetUtcNow(), Title = "T " + id,
    };

    [Fact]
    public async Task Initialize_builds_track_groups_with_their_tiles()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _yard.Tracks.Select(t => t.Name).ShouldBe(["General", "Clients"]);
        _yard.Tracks[0].Tiles.ShouldHaveSingleItem().Name.ShouldBe("App");
        _yard.Tracks[1].Tiles.ShouldHaveSingleItem().Name.ShouldBe("Shop");
        _yard.Tiles.Select(t => t.Name).ShouldBe(["App", "Shop"]);
    }

    [Fact]
    public async Task Session_changes_land_on_the_owning_tile_and_raise_attention()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Working)));
        _bus.Publish(new SessionChanged(null, Snapshot("s2", _shop.Id, SessionState.Waiting)));
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _shop.Id, SessionState.Idle)));

        var shop = _yard.FindTile(_shop.Id)!;
        shop.Chats.Select(c => c.SessionId).ShouldBe(["s1", "s2"]);
        shop.Chats[0].State.ShouldBe(SessionState.Idle);
        shop.NeedsAttention.ShouldBeTrue();
        _yard.FindTile(_app.Id)!.NeedsAttention.ShouldBeFalse();
    }

    [Fact]
    public async Task Needs_me_first_puts_waiting_tiles_at_the_front()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _bus.Publish(new SessionChanged(null, Snapshot("s2", _shop.Id, SessionState.Waiting)));

        _yard.NeedsMeFirst = true;

        _yard.Tiles.Select(t => t.Name).ShouldBe(["Shop", "App"]);
        _yard.Tracks.Select(t => t.Name).ShouldBe(["Clients", "General"], "the track with a waiting chat moves up");
        _yard.Tracks.Single(t => t.Name == "General").Tiles.Select(t => t.Name).ShouldBe(["App"], "tiles stay inside their track group");
    }

    [Fact]
    public async Task Ended_chats_disappear_ten_minutes_after_they_end()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        _bus.Publish(new SessionChanged(null, Snapshot("s1", _app.Id, SessionState.Ended)));

        _time.Advance(TimeSpan.FromMinutes(9));
        _yard.Tick(_time.GetUtcNow());
        _yard.FindTile(_app.Id)!.Chats.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        _yard.Tick(_time.GetUtcNow());
        _yard.FindTile(_app.Id)!.Chats.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registered_and_unregistered_workspaces_update_the_groups()
    {
        await _yard.InitializeAsync(CancellationToken.None);
        var extra = new Workspace { Name = "Extra", RootPath = @"c:\repo\extra", TrackId = _general.Id };

        _bus.Publish(new WorkspaceRegistered(extra));
        _yard.Tracks[0].Tiles.Select(t => t.Name).ShouldBe(["App", "Extra"]);

        _bus.Publish(new WorkspaceUnregistered(_app.Id));
        _yard.Tracks[0].Tiles.Select(t => t.Name).ShouldBe(["Extra"]);
    }

    [Fact]
    public async Task Host_state_changes_are_reflected_on_the_tile()
    {
        await _yard.InitializeAsync(CancellationToken.None);

        _bus.Publish(new HostStateChanged(_app.Id, HostState.Running, 42, null));

        _yard.FindTile(_app.Id)!.HostState.ShouldBe(HostState.Running);
    }

    [Fact]
    public async Task Engine_snapshots_present_at_startup_are_shown()
    {
        _engine.Restore([Snapshot("old", _app.Id, SessionState.Idle)]);

        await _yard.InitializeAsync(CancellationToken.None);

        _yard.FindTile(_app.Id)!.Chats.ShouldHaveSingleItem().SessionId.ShouldBe("old");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.UI.Tests --filter FullyQualifiedName~Yard`
Expected: build errors for the missing `Yard` types.

- [ ] **Step 3: Implement the view models**

`src/CodeSwitchX.UI/Yard/ChatRowViewModel.cs`:

```csharp
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Yard;

public sealed partial class ChatRowViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private SessionState _state;
    [ObservableProperty] private DateTimeOffset _stateSince;
    [ObservableProperty] private string _elapsedText = string.Empty;
    [ObservableProperty] private double _contextFill;
    [ObservableProperty] private ContextPressure _pressure;
    [ObservableProperty] private bool _inferred;
    [ObservableProperty] private string? _lastToolName;

    public ChatRowViewModel(string sessionId)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
    public bool IsLive => SessionStateMachine.IsLive(State);
    public bool NeedsUser => SessionStateMachine.NeedsUser(State);

    public void Update(SessionSnapshot snapshot, PricingTable pricing)
    {
        Title = snapshot.Title ?? $"Chat {snapshot.SessionId[..Math.Min(8, snapshot.SessionId.Length)]}";
        State = snapshot.State;
        StateSince = snapshot.StateSince;
        Inferred = snapshot.Inferred;
        LastToolName = snapshot.LastToolName;
        var rule = pricing.Find(snapshot.Model);
        ContextFill = ContextFillCalculator.Fill(snapshot.LatestContext, rule);
        Pressure = ContextFillCalculator.Level(ContextFill);
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(NeedsUser));
    }

    public void Tick(DateTimeOffset now) => ElapsedText = FormatElapsed(now - StateSince);

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{(int)elapsed.TotalSeconds}s";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes}m";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m";
        }

        return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
    }
}
```

`src/CodeSwitchX.UI/Yard/WorkspaceTileViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Diagnostics;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Yard;

public sealed partial class WorkspaceTileViewModel : ObservableObject
{
    public static readonly TimeSpan EndedRowLifetime = TimeSpan.FromMinutes(10);

    private readonly YardViewModel _owner;

    [ObservableProperty] private string? _branch;
    [ObservableProperty] private int _dirtyCount;
    [ObservableProperty] private HostState _hostState = HostState.NotStarted;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _hasInferredChats;

    public WorkspaceTileViewModel(Workspace workspace, YardViewModel owner)
    {
        Workspace = workspace;
        _owner = owner;
    }

    public Workspace Workspace { get; }
    public Guid Id => Workspace.Id;
    public string Name => Workspace.Name;
    public string AccentColor => Workspace.AccentColor;
    public string RootPath => Workspace.RootPath;
    public ObservableCollection<ChatRowViewModel> Chats { get; } = [];

    /// <summary>0 = waiting on the user, 1 = working, 2 = everything else. Used by "Needs me first".</summary>
    public int AttentionRank => NeedsAttention ? 0 : Chats.Any(c => c.State == SessionState.Working) ? 1 : 2;

    public void Upsert(SessionSnapshot snapshot, PricingTable pricing)
    {
        var row = Chats.FirstOrDefault(c => c.SessionId == snapshot.SessionId);
        if (row is null)
        {
            row = new ChatRowViewModel(snapshot.SessionId);
            Chats.Add(row);
        }

        row.Update(snapshot, pricing);
        Recompute();
    }

    public void Remove(string sessionId)
    {
        var row = Chats.FirstOrDefault(c => c.SessionId == sessionId);
        if (row is not null)
        {
            Chats.Remove(row);
            Recompute();
        }
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var row in Chats.Where(c => !c.IsLive && now - c.StateSince >= EndedRowLifetime).ToList())
        {
            Chats.Remove(row);
        }

        foreach (var row in Chats)
        {
            row.Tick(now);
        }

        Recompute();
    }

    private void Recompute()
    {
        NeedsAttention = Chats.Any(c => c.NeedsUser);
        HasInferredChats = Chats.Any(c => c.Inferred && c.IsLive);
        OnPropertyChanged(nameof(AttentionRank));
    }

    [RelayCommand]
    private void Open() => _owner.RequestOpen(Id);

    [RelayCommand]
    private void RevealInExplorer() => TryStart("explorer.exe", $"\"{RootPath}\"");

    [RelayCommand]
    private void OpenTerminal()
    {
        if (!TryStart("wt.exe", $"-d \"{RootPath}\""))
        {
            TryStart("cmd.exe", $"/K cd /d \"{RootPath}\"");
        }
    }

    [RelayCommand]
    private Task UnregisterAsync() => _owner.UnregisterAsync(Id);

    private static bool TryStart(string file, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
```

`src/CodeSwitchX.UI/Yard/TrackGroupViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CodeSwitchX.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Yard;

public sealed class TrackGroupViewModel : ObservableObject
{
    public TrackGroupViewModel(Track track)
    {
        Track = track;
    }

    public Track Track { get; }
    public Guid Id => Track.Id;
    public string Name => Track.Name;
    public int SortOrder => Track.SortOrder;
    public ObservableCollection<WorkspaceTileViewModel> Tiles { get; } = [];
}
```

`src/CodeSwitchX.UI/Yard/YardViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Yard;

/// <summary>The tile board: Track groups of workspace tiles, each with live chat rows.</summary>
public sealed partial class YardViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan GitRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IWorkspaceStore _store;
    private readonly WorkspaceRegistry _registry;
    private readonly SessionEngine _engine;
    private readonly IPricingProvider _pricing;
    private readonly GitInspector _git;
    private readonly IEventBus _bus;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger<YardViewModel> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private ITimer? _tickTimer;
    private ITimer? _gitTimer;
    private int _gitRefreshRunning;

    [ObservableProperty] private bool _needsMeFirst;
    [ObservableProperty] private bool _hooksInferredOnly;

    public YardViewModel(IWorkspaceStore store, WorkspaceRegistry registry, SessionEngine engine, IPricingProvider pricing, GitInspector git,
        IEventBus bus, IUiDispatcher ui, TimeProvider time, ILogger<YardViewModel> logger)
    {
        _store = store;
        _registry = registry;
        _engine = engine;
        _pricing = pricing;
        _git = git;
        _bus = bus;
        _ui = ui;
        _time = time;
        _logger = logger;
    }

    public ObservableCollection<TrackGroupViewModel> Tracks { get; } = [];

    public IEnumerable<WorkspaceTileViewModel> Tiles => Tracks.SelectMany(t => t.Tiles);

    public event Action<Guid>? OpenRequested;
    public event Action? AddWorkspaceRequested;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var tracks = await _store.GetTracksAsync(ct);
        var workspaces = await _store.GetAllAsync(ct);
        Tracks.Clear();
        foreach (var track in tracks.OrderBy(t => t.SortOrder).ThenBy(t => t.Name))
        {
            var group = new TrackGroupViewModel(track);
            foreach (var workspace in workspaces.Where(w => w.TrackId == track.Id).OrderBy(w => w.Name))
            {
                group.Tiles.Add(new WorkspaceTileViewModel(workspace, this));
            }

            Tracks.Add(group);
        }

        foreach (var snapshot in _engine.Snapshots)
        {
            Apply(snapshot);
        }

        _subscriptions.Add(_bus.Subscribe<SessionChanged>(m => _ui.Post(() => Apply(m.Current))));
        _subscriptions.Add(_bus.Subscribe<WorkspaceRegistered>(m => _ui.Post(() => AddTile(m.Workspace))));
        _subscriptions.Add(_bus.Subscribe<WorkspaceUnregistered>(m => _ui.Post(() => RemoveTile(m.WorkspaceId))));
        _subscriptions.Add(_bus.Subscribe<HostStateChanged>(m => _ui.Post(() => Apply(m))));

        _tickTimer = _time.CreateTimer(_ => _ui.Post(() => Tick(_time.GetUtcNow())), null, TickInterval, TickInterval);
        _gitTimer = _time.CreateTimer(_ => _ = RefreshGitAsync(CancellationToken.None), null, TimeSpan.Zero, GitRefreshInterval);
    }

    public WorkspaceTileViewModel? FindTile(Guid workspaceId) => Tiles.FirstOrDefault(t => t.Id == workspaceId);

    public void RequestOpen(Guid workspaceId) => OpenRequested?.Invoke(workspaceId);

    public async Task UnregisterAsync(Guid workspaceId)
    {
        try
        {
            await _registry.UnregisterAsync(workspaceId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unregistering workspace {Id} failed", workspaceId);
        }
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var tile in Tiles.ToList())
        {
            tile.Tick(now);
        }

        HooksInferredOnly = Tiles.Any(t => t.HasInferredChats) && !Tiles.SelectMany(t => t.Chats).Any(c => !c.Inferred && c.IsLive);
        if (NeedsMeFirst)
        {
            Resort();
        }
    }

    public async Task RefreshGitAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _gitRefreshRunning, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var tile in Tiles.ToList())
            {
                var info = await _git.InspectAsync(tile.RootPath, ct).ConfigureAwait(false);
                _ui.Post(() =>
                {
                    tile.Branch = info.Branch;
                    tile.DirtyCount = info.DirtyCount;
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Git refresh failed");
        }
        finally
        {
            Volatile.Write(ref _gitRefreshRunning, 0);
        }
    }

    [RelayCommand]
    private void AddWorkspace() => AddWorkspaceRequested?.Invoke();

    [RelayCommand]
    private Task RefreshGit() => RefreshGitAsync(CancellationToken.None);

    partial void OnNeedsMeFirstChanged(bool value) => Resort();

    internal void Apply(SessionSnapshot snapshot)
    {
        if (snapshot.WorkspaceId is not { } workspaceId)
        {
            return;
        }

        var tile = FindTile(workspaceId);
        if (tile is null)
        {
            return;
        }

        foreach (var other in Tiles.Where(t => t.Id != workspaceId && t.Chats.Any(c => c.SessionId == snapshot.SessionId)))
        {
            other.Remove(snapshot.SessionId);
        }

        tile.Upsert(snapshot, _pricing.Pricing);
        if (NeedsMeFirst)
        {
            Resort();
        }
    }

    internal void Apply(HostStateChanged change)
    {
        var tile = FindTile(change.WorkspaceId);
        if (tile is not null)
        {
            tile.HostState = change.State;
        }
    }

    private void AddTile(Workspace workspace)
    {
        if (FindTile(workspace.Id) is not null)
        {
            return;
        }

        var group = Tracks.FirstOrDefault(t => t.Id == workspace.TrackId);
        if (group is null)
        {
            group = new TrackGroupViewModel(new Track { Id = workspace.TrackId, Name = "Track", SortOrder = int.MaxValue });
            Tracks.Add(group);
            _ = ReloadTrackNamesAsync();
        }

        var tile = new WorkspaceTileViewModel(workspace, this);
        var index = group.Tiles.TakeWhile(t => string.Compare(t.Name, tile.Name, StringComparison.OrdinalIgnoreCase) < 0).Count();
        group.Tiles.Insert(index, tile);
        foreach (var snapshot in _engine.Snapshots.Where(s => s.WorkspaceId == workspace.Id))
        {
            tile.Upsert(snapshot, _pricing.Pricing);
        }

        _ = RefreshGitAsync(CancellationToken.None);
    }

    private async Task ReloadTrackNamesAsync()
    {
        var tracks = await _store.GetTracksAsync(CancellationToken.None);
        _ui.Post(() =>
        {
            var byId = tracks.ToDictionary(t => t.Id);
            foreach (var group in Tracks.ToList())
            {
                if (byId.TryGetValue(group.Id, out var track) && group.Track.Name != track.Name)
                {
                    var replacement = new TrackGroupViewModel(track);
                    foreach (var tile in group.Tiles)
                    {
                        replacement.Tiles.Add(tile);
                    }

                    Tracks[Tracks.IndexOf(group)] = replacement;
                }
            }
        });
    }

    private void RemoveTile(Guid workspaceId)
    {
        foreach (var group in Tracks)
        {
            var tile = group.Tiles.FirstOrDefault(t => t.Id == workspaceId);
            if (tile is not null)
            {
                group.Tiles.Remove(tile);
                return;
            }
        }
    }

    private void Resort()
    {
        var orderedGroups = NeedsMeFirst
            ? Tracks.OrderBy(g => g.Tiles.Count == 0 ? 2 : g.Tiles.Min(t => t.AttentionRank)).ThenBy(g => g.SortOrder).ThenBy(g => g.Name).ToList()
            : Tracks.OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToList();
        for (var i = 0; i < orderedGroups.Count; i++)
        {
            var currentIndex = Tracks.IndexOf(orderedGroups[i]);
            if (currentIndex != i)
            {
                Tracks.Move(currentIndex, i);
            }
        }

        foreach (var group in Tracks)
        {
            var ordered = NeedsMeFirst
                ? group.Tiles.OrderBy(t => t.AttentionRank).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : group.Tiles.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var current = group.Tiles.IndexOf(ordered[i]);
                if (current != i)
                {
                    group.Tiles.Move(current, i);
                }
            }
        }

        OnPropertyChanged(nameof(Tiles));
    }

    public void Dispose()
    {
        _tickTimer?.Dispose();
        _gitTimer?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
```

- [ ] **Step 4: Implement the converters and the view**

`src/CodeSwitchX.UI/Infrastructure/Converters.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Hosting;
using CodeSwitchX.Telemetry;

namespace CodeSwitchX.UI.Infrastructure;

public sealed class SessionStateToBrushConverter : IValueConverter
{
    public static readonly IReadOnlyDictionary<SessionState, Brush> Brushes = new Dictionary<SessionState, Brush>
    {
        [SessionState.Starting] = Freeze("#60A5FA"),
        [SessionState.Idle] = Freeze("#9CA3AF"),
        [SessionState.Working] = Freeze("#22C55E"),
        [SessionState.Waiting] = Freeze("#F59E0B"),
        [SessionState.Stale] = Freeze("#6B7280"),
        [SessionState.Errored] = Freeze("#EF4444"),
        [SessionState.Ended] = Freeze("#4B5563"),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionState state && Brushes.TryGetValue(state, out var brush) ? brush : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is string hex && hex.Length > 0 ? SessionStateToBrushConverter.Freeze(hex) : System.Windows.Media.Brushes.SteelBlue;
        }
        catch (FormatException)
        {
            return System.Windows.Media.Brushes.SteelBlue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PressureToBrushConverter : IValueConverter
{
    private static readonly Brush Normal = SessionStateToBrushConverter.Freeze("#3B82F6");
    private static readonly Brush Amber = SessionStateToBrushConverter.Freeze("#F59E0B");
    private static readonly Brush Red = SessionStateToBrushConverter.Freeze("#EF4444");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ContextPressure.Red => Red,
        ContextPressure.Amber => Amber,
        _ => Normal,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HostStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HostState.Starting => "Starting…",
        HostState.Running => "VS Code open",
        HostState.Stopped => "Stopped",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound enum's name equals the converter parameter.</summary>
public sealed class EnumEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

`src/CodeSwitchX.UI/Yard/YardView.xaml`:

```xml
<UserControl x:Class="CodeSwitchX.UI.Yard.YardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:yard="clr-namespace:CodeSwitchX.UI.Yard">
  <UserControl.Resources>
    <DataTemplate x:Key="ChatRowTemplate" DataType="{x:Type yard:ChatRowViewModel}">
      <Grid Margin="0,2">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="14" />
          <ColumnDefinition Width="*" />
          <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
          <RowDefinition Height="Auto" />
          <RowDefinition Height="3" />
        </Grid.RowDefinitions>
        <Ellipse Grid.Column="0" Width="8" Height="8" VerticalAlignment="Center" Fill="{Binding State, Converter={StaticResource StateBrush}}"
                 ToolTip="{Binding State}" />
        <TextBlock Grid.Column="1" Text="{Binding Title}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="4,0">
          <TextBlock.ToolTip>
            <TextBlock>
              <Run Text="{Binding State, Mode=OneWay}" /> · last tool: <Run Text="{Binding LastToolName, Mode=OneWay}" />
            </TextBlock>
          </TextBlock.ToolTip>
        </TextBlock>
        <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
          <TextBlock Text="inferred" FontSize="10" Opacity="0.6" Margin="0,0,6,0"
                     Visibility="{Binding Inferred, Converter={StaticResource BoolVisible}}" ToolTip="Hooks not installed: state guessed from the transcript" />
          <TextBlock Text="{Binding ElapsedText}" FontSize="11" Opacity="0.7" MinWidth="34" TextAlignment="Right" />
        </StackPanel>
        <ProgressBar Grid.Row="1" Grid.Column="1" Grid.ColumnSpan="2" Margin="4,0,0,0" Height="3" Minimum="0" Maximum="1"
                     Value="{Binding ContextFill, Mode=OneWay}" Foreground="{Binding Pressure, Converter={StaticResource PressureBrush}}"
                     BorderThickness="0" Background="#22000000" ToolTip="Context window fill" />
      </Grid>
    </DataTemplate>

    <DataTemplate x:Key="TileTemplate" DataType="{x:Type yard:WorkspaceTileViewModel}">
      <Border x:Name="TileBorder" Width="300" MinHeight="150" Margin="8" Padding="12" CornerRadius="10" BorderThickness="2"
              BorderBrush="{Binding AccentColor, Converter={StaticResource HexBrush}}" Background="{DynamicResource {x:Static SystemColors.ControlBrushKey}}"
              Cursor="Hand">
        <Border.InputBindings>
          <MouseBinding MouseAction="LeftClick" Command="{Binding OpenCommand}" />
        </Border.InputBindings>
        <Border.ContextMenu>
          <ContextMenu>
            <MenuItem Header="Open" Command="{Binding OpenCommand}" />
            <MenuItem Header="Reveal in Explorer" Command="{Binding RevealInExplorerCommand}" />
            <MenuItem Header="Open terminal here" Command="{Binding OpenTerminalCommand}" />
            <Separator />
            <MenuItem Header="Unregister" Command="{Binding UnregisterCommand}" />
          </ContextMenu>
        </Border.ContextMenu>
        <Border.Style>
          <Style TargetType="Border">
            <Style.Triggers>
              <DataTrigger Binding="{Binding NeedsAttention}" Value="True">
                <Setter Property="BorderBrush" Value="#F59E0B" />
                <DataTrigger.EnterActions>
                  <BeginStoryboard x:Name="Pulse">
                    <Storyboard RepeatBehavior="Forever" AutoReverse="True">
                      <DoubleAnimation Storyboard.TargetProperty="Opacity" From="1" To="0.55" Duration="0:0:0.7" />
                    </Storyboard>
                  </BeginStoryboard>
                </DataTrigger.EnterActions>
                <DataTrigger.ExitActions>
                  <StopStoryboard BeginStoryboardName="Pulse" />
                </DataTrigger.ExitActions>
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </Border.Style>
        <StackPanel>
          <DockPanel>
            <TextBlock DockPanel.Dock="Right" FontSize="11" Opacity="0.7" VerticalAlignment="Center"
                       Text="{Binding HostState, Converter={StaticResource HostStateText}}" />
            <TextBlock Text="{Binding Name}" FontSize="16" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
          </DockPanel>
          <StackPanel Orientation="Horizontal" Margin="0,4,0,8" Opacity="0.75">
            <TextBlock Text="&#xE9F5;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Margin="0,0,4,0" VerticalAlignment="Center" />
            <TextBlock Text="{Binding Branch, TargetNullValue=no git}" />
            <TextBlock Text=" · " />
            <TextBlock><Run Text="{Binding DirtyCount, Mode=OneWay}" /> dirty</TextBlock>
          </StackPanel>
          <ItemsControl ItemsSource="{Binding Chats}" ItemTemplate="{StaticResource ChatRowTemplate}" />
          <TextBlock Text="No Claude Code chats yet" FontStyle="Italic" Opacity="0.5" Margin="0,6,0,0">
            <TextBlock.Style>
              <Style TargetType="TextBlock">
                <Setter Property="Visibility" Value="Collapsed" />
                <Style.Triggers>
                  <DataTrigger Binding="{Binding Chats.Count}" Value="0">
                    <Setter Property="Visibility" Value="Visible" />
                  </DataTrigger>
                </Style.Triggers>
              </Style>
            </TextBlock.Style>
          </TextBlock>
        </StackPanel>
      </Border>
    </DataTemplate>
  </UserControl.Resources>

  <DockPanel>
    <Border DockPanel.Dock="Top" Padding="12,8" BorderThickness="0,0,0,1" BorderBrush="#22000000">
      <DockPanel>
        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
          <CheckBox Content="Needs me first" IsChecked="{Binding NeedsMeFirst}" VerticalAlignment="Center" Margin="0,0,16,0" />
          <Button Content="Add workspace" Command="{Binding AddWorkspaceCommand}" Margin="0,0,8,0" />
          <Button Content="Settings" Command="{Binding DataContext.OpenSettingsCommand, RelativeSource={RelativeSource AncestorType=Window}}" />
        </StackPanel>
        <TextBlock Text="Yard" FontSize="20" FontWeight="SemiBold" VerticalAlignment="Center" />
        <TextBlock Margin="16,0,0,0" VerticalAlignment="Center" Foreground="#F59E0B" Text="Hooks not installed — states are inferred from transcripts"
                   Visibility="{Binding HooksInferredOnly, Converter={StaticResource BoolVisible}}" />
      </DockPanel>
    </Border>
    <ScrollViewer VerticalScrollBarVisibility="Auto">
      <ItemsControl ItemsSource="{Binding Tracks}" Margin="8">
        <ItemsControl.ItemTemplate>
          <DataTemplate DataType="{x:Type yard:TrackGroupViewModel}">
            <StackPanel Margin="0,0,0,16">
              <TextBlock Text="{Binding Name}" FontSize="13" FontWeight="SemiBold" Opacity="0.7" Margin="8,0,0,0" />
              <ItemsControl ItemsSource="{Binding Tiles}" ItemTemplate="{StaticResource TileTemplate}">
                <ItemsControl.ItemsPanel>
                  <ItemsPanelTemplate>
                    <WrapPanel />
                  </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
              </ItemsControl>
            </StackPanel>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>
  </DockPanel>
</UserControl>
```

`src/CodeSwitchX.UI/Yard/YardView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace CodeSwitchX.UI.Yard;

public partial class YardView : UserControl
{
    public YardView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.UI.Tests --filter FullyQualifiedName~Yard`
Expected: all pass. (The UI test project builds the whole UI assembly, so Tasks 16–18 must be in place first; run this step after them if building earlier fails on missing types.)

---

### Task 16: Add Workspace dialog

**Files:**
- Create: `src/CodeSwitchX.UI/Workspaces/AddWorkspaceViewModel.cs`, `AddWorkspaceWindow.xaml`, `AddWorkspaceWindow.xaml.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Workspaces/AddWorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: `WorkspaceProbe`, `WorkspaceRegistry`, `IWorkspaceStore`, `DuplicateWorkspaceException`, `HostMode`.
- Produces: `AddWorkspaceViewModel(WorkspaceProbe, IWorkspaceStore, WorkspaceRegistry, ILogger<AddWorkspaceViewModel>)` with `InputPath, Name, RootPath, WorkspaceFile, IsGitRepository, Branch, HasClaudeMd, SolutionSummary, Worktrees, Tracks, SelectedTrack, NewTrackName, AccentColors, SelectedAccent, VsCodeProfile, AutoStart, ErrorMessage, IsProbed, IsBusy`, `LoadAsync(ct)`, commands `Probe`, `Save` (enabled when probed and named), `Cancel`; events `Saved(Workspace)`, `Closed()`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.UI.Tests/Workspaces/AddWorkspaceViewModelTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Paths;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(_root, "Shop.slnx"), string.Empty);
        _store.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([_general]));
        _store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([]));
        _store.AddTrackAsync("Clients", Arg.Any<CancellationToken>()).Returns(ci => Task.FromResult(new Track { Name = "Clients", SortOrder = 1 }));
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        var registry = new WorkspaceRegistry(_store, new WorkspaceResolver(), new EventBus(NullLogger<EventBus>.Instance));
        _vm = new AddWorkspaceViewModel(new WorkspaceProbe(git), _store, registry, NullLogger<AddWorkspaceViewModel>.Instance);
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [Fact]
    public async Task Probe_fills_the_form_from_the_folder()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.SaveCommand.CanExecute(null).ShouldBeFalse();
        _vm.InputPath = _root;

        await _vm.ProbeCommand.ExecuteAsync(null);

        _vm.IsProbed.ShouldBeTrue();
        _vm.Name.ShouldBe("Shop");
        _vm.RootPath.ShouldBe(PathNormalizer.Normalize(_root));
        _vm.IsGitRepository.ShouldBeTrue();
        _vm.Branch.ShouldBe("main");
        _vm.SolutionSummary.ShouldBe("Shop.slnx");
        _vm.SelectedTrack.ShouldBe(_general);
        _vm.ErrorMessage.ShouldBeNull();
        _vm.SaveCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Probe_errors_are_shown_not_thrown()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = Path.Combine(_root, "missing");

        await _vm.ProbeCommand.ExecuteAsync(null);

        _vm.IsProbed.ShouldBeFalse();
        _vm.ErrorMessage.ShouldContain("does not exist");
    }

    [Fact]
    public async Task Save_creates_a_new_track_when_named_and_registers_the_workspace()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = _root;
        await _vm.ProbeCommand.ExecuteAsync(null);
        _vm.NewTrackName = "Clients";
        _vm.SelectedAccent = "#FF8800";
        _vm.AutoStart = true;
        Workspace? saved = null;
        _vm.Saved += w => saved = w;

        await _vm.SaveCommand.ExecuteAsync(null);

        await _store.Received(1).AddTrackAsync("Clients", Arg.Any<CancellationToken>());
        await _store.Received(1).AddAsync(Arg.Is<Workspace>(w => w.Name == "Shop" && w.AccentColor == "#FF8800" && w.AutoStart && w.RootPath == PathNormalizer.Normalize(_root)), Arg.Any<CancellationToken>());
        saved.ShouldNotBeNull();
        saved.TrackId.ShouldNotBe(_general.Id);
    }

    [Fact]
    public async Task Duplicate_roots_show_an_error_and_keep_the_dialog_open()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.InputPath = _root;
        await _vm.ProbeCommand.ExecuteAsync(null);
        _store.AddAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>()).Returns(_ => throw new DuplicateWorkspaceException(_vm.RootPath));
        var closed = false;
        _vm.Closed += () => closed = true;

        await _vm.SaveCommand.ExecuteAsync(null);

        _vm.ErrorMessage.ShouldContain("already registered");
        closed.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.UI.Tests --filter FullyQualifiedName~AddWorkspace`
Expected: build errors, `AddWorkspaceViewModel` missing.

- [ ] **Step 3: Implement the view model**

`src/CodeSwitchX.UI/Workspaces/AddWorkspaceViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Workspaces;

public sealed partial class AddWorkspaceViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> AccentColors =
    [
        "#3B82F6", "#22C55E", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#14B8A6", "#64748B",
    ];

    private readonly WorkspaceProbe _probe;
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceRegistry _registry;
    private readonly ILogger<AddWorkspaceViewModel> _logger;

    [ObservableProperty] private string _inputPath = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private string _name = string.Empty;
    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private string? _workspaceFile;
    [ObservableProperty] private bool _isGitRepository;
    [ObservableProperty] private string? _branch;
    [ObservableProperty] private bool _hasClaudeMd;
    [ObservableProperty] private string _solutionSummary = string.Empty;
    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private string _newTrackName = string.Empty;
    [ObservableProperty] private string _selectedAccent = AccentColors[0];
    [ObservableProperty] private string? _vsCodeProfile;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isProbed;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] [NotifyCanExecuteChangedFor(nameof(ProbeCommand))] private bool _isBusy;

    public AddWorkspaceViewModel(WorkspaceProbe probe, IWorkspaceStore store, WorkspaceRegistry registry, ILogger<AddWorkspaceViewModel> logger)
    {
        _probe = probe;
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<WorktreeInfo> Worktrees { get; } = [];
    public IReadOnlyList<string> Accents => AccentColors;

    public event Action<Workspace>? Saved;
    public event Action? Closed;

    public async Task LoadAsync(CancellationToken ct)
    {
        Tracks.Clear();
        foreach (var track in await _store.GetTracksAsync(ct))
        {
            Tracks.Add(track);
        }

        SelectedTrack ??= Tracks.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ProbeAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var result = await _probe.ProbeAsync(InputPath.Trim().Trim('"'), CancellationToken.None);
            Name = result.SuggestedName;
            RootPath = result.RootPath;
            WorkspaceFile = result.WorkspaceFile;
            IsGitRepository = result.IsGitRepository;
            Branch = result.Branch;
            HasClaudeMd = result.HasClaudeMd;
            SolutionSummary = result.SolutionFiles.Count == 0 ? "no solution files" : string.Join(", ", result.SolutionFiles.Select(Path.GetFileName));
            Worktrees.Clear();
            foreach (var worktree in result.Worktrees)
            {
                Worktrees.Add(worktree);
            }

            IsProbed = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            IsProbed = false;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanProbe() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var track = SelectedTrack;
            if (!string.IsNullOrWhiteSpace(NewTrackName))
            {
                track = await _store.AddTrackAsync(NewTrackName.Trim(), CancellationToken.None);
            }

            track ??= Tracks.FirstOrDefault() ?? await _store.AddTrackAsync("General", CancellationToken.None);

            var workspace = new Workspace
            {
                Name = Name.Trim(),
                RootPath = RootPath,
                WorkspaceFile = WorkspaceFile,
                TrackId = track.Id,
                AccentColor = SelectedAccent,
                HostMode = HostMode.Snap,
                VsCodeProfile = string.IsNullOrWhiteSpace(VsCodeProfile) ? null : VsCodeProfile.Trim(),
                AutoStart = AutoStart,
                Worktrees = Worktrees.Select(w => new Worktree { Path = w.Path, Branch = w.Branch }).ToList(),
            };

            await _registry.RegisterAsync(workspace, CancellationToken.None);
            Saved?.Invoke(workspace);
            Closed?.Invoke();
        }
        catch (DuplicateWorkspaceException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registering workspace failed");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => IsProbed && !IsBusy && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();
}
```

- [ ] **Step 4: Implement the dialog**

`src/CodeSwitchX.UI/Workspaces/AddWorkspaceWindow.xaml`:

```xml
<Window x:Class="CodeSwitchX.UI.Workspaces.AddWorkspaceWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ws="clr-namespace:CodeSwitchX.UI.Workspaces"
        Title="Add workspace" Width="560" SizeToContent="Height" WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        d:DataContext="{d:DesignInstance ws:AddWorkspaceViewModel}"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="d">
  <StackPanel Margin="16">
    <TextBlock Text="Folder, .code-workspace, .sln or .slnx" FontWeight="SemiBold" />
    <DockPanel Margin="0,4,0,8">
      <Button DockPanel.Dock="Right" Content="Detect" Command="{Binding ProbeCommand}" Margin="4,0,0,0" MinWidth="80" IsDefault="True" />
      <Button DockPanel.Dock="Right" Content="File…" Click="BrowseFile" Margin="4,0,0,0" />
      <Button DockPanel.Dock="Right" Content="Folder…" Click="BrowseFolder" Margin="4,0,0,0" />
      <TextBox Text="{Binding InputPath, UpdateSourceTrigger=PropertyChanged}" />
    </DockPanel>

    <Border Padding="10" CornerRadius="6" Background="#11000000" Visibility="{Binding IsProbed, Converter={StaticResource BoolVisible}}" Margin="0,0,0,8">
      <StackPanel>
        <TextBlock><Run Text="Root: " FontWeight="SemiBold" /><Run Text="{Binding RootPath, Mode=OneWay}" /></TextBlock>
        <TextBlock><Run Text="Git: " FontWeight="SemiBold" /><Run Text="{Binding Branch, Mode=OneWay, TargetNullValue=not a repository}" /></TextBlock>
        <TextBlock><Run Text="Solutions: " FontWeight="SemiBold" /><Run Text="{Binding SolutionSummary, Mode=OneWay}" /></TextBlock>
        <TextBlock Text="CLAUDE.md present" Visibility="{Binding HasClaudeMd, Converter={StaticResource BoolVisible}}" />
        <TextBlock><Run Text="Worktrees: " FontWeight="SemiBold" /><Run Text="{Binding Worktrees.Count, Mode=OneWay}" /></TextBlock>
      </StackPanel>
    </Border>

    <Grid IsEnabled="{Binding IsProbed}">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="120" />
        <ColumnDefinition Width="*" />
      </Grid.ColumnDefinitions>
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" />
      </Grid.RowDefinitions>
      <TextBlock Grid.Row="0" Text="Name" VerticalAlignment="Center" />
      <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}" Margin="0,2" />
      <TextBlock Grid.Row="1" Text="Track" VerticalAlignment="Center" />
      <DockPanel Grid.Row="1" Grid.Column="1" Margin="0,2">
        <TextBox DockPanel.Dock="Right" Width="180" Text="{Binding NewTrackName, UpdateSourceTrigger=PropertyChanged}" ToolTip="Type a name to create a new track" Margin="4,0,0,0" />
        <ComboBox ItemsSource="{Binding Tracks}" SelectedItem="{Binding SelectedTrack}" DisplayMemberPath="Name" />
      </DockPanel>
      <TextBlock Grid.Row="2" Text="Accent" VerticalAlignment="Center" />
      <ListBox Grid.Row="2" Grid.Column="1" ItemsSource="{Binding Accents}" SelectedItem="{Binding SelectedAccent}" BorderThickness="0" Background="Transparent" Margin="0,2">
        <ListBox.ItemsPanel>
          <ItemsPanelTemplate><StackPanel Orientation="Horizontal" /></ItemsPanelTemplate>
        </ListBox.ItemsPanel>
        <ListBox.ItemTemplate>
          <DataTemplate>
            <Border Width="22" Height="22" CornerRadius="11" Margin="2" Background="{Binding Converter={StaticResource HexBrush}}" />
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
      <TextBlock Grid.Row="3" Text="VS Code profile" VerticalAlignment="Center" />
      <TextBox Grid.Row="3" Grid.Column="1" Text="{Binding VsCodeProfile, UpdateSourceTrigger=PropertyChanged}" Margin="0,2" ToolTip="Optional --profile name" />
      <CheckBox Grid.Row="4" Grid.Column="1" Content="Start with CodeSwitchX" IsChecked="{Binding AutoStart}" Margin="0,6" />
    </Grid>

    <TextBlock Text="{Binding ErrorMessage}" Foreground="#EF4444" TextWrapping="Wrap" Margin="0,8,0,0"
               Visibility="{Binding ErrorMessage, Converter={StaticResource NullToCollapsed}}" />

    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
      <Button Content="Cancel" Command="{Binding CancelCommand}" MinWidth="80" Margin="0,0,8,0" IsCancel="True" />
      <Button Content="Add" Command="{Binding SaveCommand}" MinWidth="80" />
    </StackPanel>
  </StackPanel>
</Window>
```

Add a `NullToCollapsedConverter` to `Infrastructure/Converters.cs` and register it in `App.xaml` as `NullToCollapsed`:

```csharp
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
```

`src/CodeSwitchX.UI/Workspaces/AddWorkspaceWindow.xaml.cs`:

```csharp
using System.Windows;
using Microsoft.Win32;

namespace CodeSwitchX.UI.Workspaces;

public partial class AddWorkspaceWindow : Window
{
    private readonly AddWorkspaceViewModel _viewModel;

    public AddWorkspaceWindow(AddWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Closed += () => Dispatcher.BeginInvoke(Close);
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick the workspace root folder" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InputPath = dialog.FolderName;
            _viewModel.ProbeCommand.Execute(null);
        }
    }

    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Workspace or solution|*.code-workspace;*.sln;*.slnx", Title = "Pick a .code-workspace or solution" };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InputPath = dialog.FileName;
            _viewModel.ProbeCommand.Execute(null);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.UI.Tests --filter FullyQualifiedName~AddWorkspace`
Expected: all pass.

---

### Task 17: Cab, global hotkeys and tray icon

**Files:**
- Create: `src/CodeSwitchX.UI/Cab/CabViewModel.cs`, `CabView.xaml`, `CabView.xaml.cs`
- Create: `src/CodeSwitchX.UI/Infrastructure/HotkeyService.cs`, `TrayIconService.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Cab/CabViewModelTests.cs`, `tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs`

**Interfaces:**
- Consumes: `WorkspaceTileViewModel` (Task 15), `ScreenRect`, `HotkeyInterop`, `HostManager`, `ShellViewModel`.
- Produces: `CabViewModel` with `ActiveTile`, `Pips`, `LastHostRect`, `SetActive(WorkspaceTileViewModel, IEnumerable<WorkspaceTileViewModel>)`, `event Action? BackRequested`, `event Action<Guid>? SwitchRequested`, commands `Back`, `SelectPip(WorkspaceTileViewModel)`; `CabView` with `event Action<ScreenRect>? HostRectChanged`; `HotkeyService.Attach(nint hwnd, ShellViewModel)`, `Detach()`; `TrayIconService.Attach(Window, ShellViewModel)`, `Detach()`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.UI.Tests/Cab/CabViewModelTests.cs`:

```csharp
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Yard;

namespace CodeSwitchX.UI.Tests.Cab;

public class CabViewModelTests
{
    [Fact]
    public void SetActive_lists_every_other_tile_as_a_pip()
    {
        var cab = new CabViewModel();
        var yard = ShellTestHarness.CreateYardWithoutInit();
        var a = new WorkspaceTileViewModel(new Workspace { Name = "A", RootPath = @"c:\a" }, yard);
        var b = new WorkspaceTileViewModel(new Workspace { Name = "B", RootPath = @"c:\b" }, yard);
        var c = new WorkspaceTileViewModel(new Workspace { Name = "C", RootPath = @"c:\c" }, yard);

        cab.SetActive(b, [a, b, c]);

        cab.ActiveTile.ShouldBe(b);
        cab.Pips.ShouldBe([a, c]);
    }

    [Fact]
    public void Back_and_pip_selection_raise_events()
    {
        var cab = new CabViewModel();
        var yard = ShellTestHarness.CreateYardWithoutInit();
        var a = new WorkspaceTileViewModel(new Workspace { Name = "A", RootPath = @"c:\a" }, yard);
        var back = false;
        Guid? switched = null;
        cab.BackRequested += () => back = true;
        cab.SwitchRequested += id => switched = id;

        cab.BackCommand.Execute(null);
        cab.SelectPipCommand.Execute(a);

        back.ShouldBeTrue();
        switched.ShouldBe(a.Id);
    }
}
```

`tests/CodeSwitchX.UI.Tests/ShellTestHarness.cs`:

```csharp
using CodeSwitchX.Core;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Core.Workspaces;
using CodeSwitchX.Data;
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.VsCode;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Cab;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Shell;
using CodeSwitchX.UI.Telemetry;
using CodeSwitchX.UI.Yard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CodeSwitchX.UI.Tests;

/// <summary>Builds a ShellViewModel with substituted stores and Win32 seams; everything else is the real code.</summary>
public sealed class ShellTestHarness
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
    public EventBus Bus { get; } = new(NullLogger<EventBus>.Instance);
    public IWorkspaceStore Workspaces { get; } = Substitute.For<IWorkspaceStore>();
    public IUsageStore Usage { get; } = Substitute.For<IUsageStore>();
    public ISettingsStore Settings { get; } = Substitute.For<ISettingsStore>();
    public IWindowEnumerator Windows { get; } = Substitute.For<IWindowEnumerator>();
    public IWindowDocker Docker { get; } = Substitute.For<IWindowDocker>();
    public IVsCodeLauncher Launcher { get; } = Substitute.For<IVsCodeLauncher>();
    public WorkspaceResolver Resolver { get; } = new();
    public SessionEngine Engine { get; }
    public HostManager Host { get; }
    public ShellViewModel Shell { get; }
    public Workspace App { get; } = new() { Name = "App", RootPath = @"c:\repo\app" };
    public Track General { get; } = new() { Name = "General" };

    public ShellTestHarness()
    {
        Time.SetLocalTimeZone(TimeZoneInfo.Utc);
        App.TrackId = General.Id;
        Workspaces.GetTracksAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Track>>([General]));
        Workspaces.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Workspace>>([App]));
        Usage.GetBucketsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<UsageBucket>>([]));
        Settings.GetPricingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<PricingRule>>([]));
        Windows.ProcessName(Arg.Any<uint>()).Returns("Code");
        Docker.IsAlive(Arg.Any<nint>()).Returns(true);

        Engine = new SessionEngine(Bus, Resolver, Time, NullLogger<SessionEngine>.Instance);
        Host = new HostManager(Windows, Docker, Launcher, Bus, TimeProvider.System, NullLogger<HostManager>.Instance,
            new HostManagerOptions { DiscoveryTimeout = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(5) });

        var telemetry = new TelemetryService(Usage, Settings, Bus, Time, NullLogger<TelemetryService>.Instance);
        var registry = new WorkspaceRegistry(Workspaces, Resolver, Bus);
        var git = new GitInspector((_, _, _) => Task.FromResult<string?>(null));
        var dispatcher = new ImmediateDispatcher();
        var yard = new YardViewModel(Workspaces, registry, Engine, telemetry, git, Bus, dispatcher, Time, NullLogger<YardViewModel>.Instance);
        var cab = new CabViewModel();
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "csx-shell-" + Guid.NewGuid().ToString("N")));
        var claude = new ClaudeCodePaths(Path.Combine(paths.Root, "home"));
        var settings = new SettingsViewModel(new ClaudeHookInstaller(claude, NullLogger<ClaudeHookInstaller>.Instance), Settings, new PersistenceWriterOptions(), paths, claude, NullLogger<SettingsViewModel>.Instance);
        var bar = new PerformanceBarViewModel(telemetry, Engine, Bus, dispatcher, Settings);
        Shell = new ShellViewModel(yard, cab, settings, bar, Host, NullLogger<ShellViewModel>.Instance);
    }

    public static YardViewModel CreateYardWithoutInit() => new ShellTestHarness().Shell.Yard;

    public void VsCodeWindowAppears(nint hwnd = 500)
    {
        Windows.TopLevelWindows().Returns([], [new WindowInfo(hwnd, 30, "Chrome_WidgetWin_1", "Program.cs - app - Visual Studio Code")]);
        Launcher.Launch(Arg.Any<Workspace>()).Returns(new LaunchResult(true, 1, null));
    }
}
```

`tests/CodeSwitchX.UI.Tests/Shell/ShellViewModelTests.cs`:

```csharp
using CodeSwitchX.Hosting;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Shell;

public class ShellViewModelTests
{
    private readonly ShellTestHarness _h = new();

    [Fact]
    public async Task EnterCab_switches_mode_opens_vscode_and_docks_into_the_known_rect()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        var rect = ScreenRect.FromSize(0, 28, 1600, 900);
        _h.Shell.Cab.LastHostRect = rect;

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.ActiveWorkspaceId.ShouldBe(_h.App.Id);
        _h.Shell.Cab.ActiveTile!.Id.ShouldBe(_h.App.Id);
        _h.Host.Get(_h.App.Id)!.State.ShouldBe(HostState.Running);
        _h.Docker.Received(1).MoveTo(500, rect);
        _h.Shell.StatusMessage.ShouldBeNull();
    }

    [Fact]
    public async Task EnterCab_reports_launch_problems_in_the_status_message()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.Windows.TopLevelWindows().Returns([]);
        _h.Launcher.Launch(Arg.Any<CodeSwitchX.Core.Workspaces.Workspace>()).Returns(new CodeSwitchX.Hosting.VsCode.LaunchResult(false, null, "code not found"));

        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
        _h.Shell.StatusMessage.ShouldBe("code not found");
    }

    [Fact]
    public async Task BackToYard_hides_windows_and_ToggleMode_round_trips()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.BackToYard();
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);
        _h.Docker.Received(1).Cloak(500);

        _h.Shell.ToggleMode();
        await Task.Delay(50);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task UpdateCabRect_redocks_only_while_in_cab_mode()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        await _h.Shell.EnterCabAsync(_h.App.Id);
        var rect = ScreenRect.FromSize(10, 40, 800, 600);

        _h.Shell.UpdateCabRect(rect);
        _h.Docker.Received(1).MoveTo(500, rect);

        _h.Shell.BackToYard();
        _h.Shell.UpdateCabRect(ScreenRect.FromSize(0, 0, 50, 50));
        _h.Docker.DidNotReceive().MoveTo(500, ScreenRect.FromSize(0, 0, 50, 50));
    }

    [Fact]
    public async Task JumpTo_out_of_range_is_ignored_and_in_range_enters_the_cab()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();

        await _h.Shell.JumpToAsync(5);
        _h.Shell.Mode.ShouldBe(ShellMode.Yard);

        await _h.Shell.JumpToAsync(1);
        _h.Shell.Mode.ShouldBe(ShellMode.Cab);
    }

    [Fact]
    public async Task Settings_mode_hides_vscode_windows()
    {
        await _h.Shell.InitializeAsync(CancellationToken.None);
        _h.VsCodeWindowAppears();
        _h.Shell.Cab.LastHostRect = ScreenRect.FromSize(0, 0, 100, 100);
        await _h.Shell.EnterCabAsync(_h.App.Id);

        _h.Shell.OpenSettings();

        _h.Shell.Mode.ShouldBe(ShellMode.Settings);
        _h.Docker.Received(1).Cloak(500);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.UI.Tests --filter "FullyQualifiedName~Cab|FullyQualifiedName~Shell"`
Expected: build errors for the missing `Cab`, `Settings`, `Telemetry` and infrastructure types.

- [ ] **Step 3: Implement the Cab**

`src/CodeSwitchX.UI/Cab/CabViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Yard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeSwitchX.UI.Cab;

/// <summary>One workspace full-window plus a 28 px strip of pips for the other tiles.</summary>
public sealed partial class CabViewModel : ObservableObject
{
    [ObservableProperty] private WorkspaceTileViewModel? _activeTile;

    public ObservableCollection<WorkspaceTileViewModel> Pips { get; } = [];

    /// <summary>Last known screen rectangle of the host area; set by the view, consumed by the shell.</summary>
    public ScreenRect? LastHostRect { get; set; }

    public event Action? BackRequested;
    public event Action<Guid>? SwitchRequested;

    public void SetActive(WorkspaceTileViewModel tile, IEnumerable<WorkspaceTileViewModel> allTiles)
    {
        ActiveTile = tile;
        Pips.Clear();
        foreach (var other in allTiles.Where(t => t.Id != tile.Id))
        {
            Pips.Add(other);
        }
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void SelectPip(WorkspaceTileViewModel tile) => SwitchRequested?.Invoke(tile.Id);
}
```

Add to `ShellViewModel`'s constructor: `Cab.SwitchRequested += id => _ = EnterCabAsync(id);`.

`src/CodeSwitchX.UI/Cab/CabView.xaml`:

```xml
<UserControl x:Class="CodeSwitchX.UI.Cab.CabView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:yard="clr-namespace:CodeSwitchX.UI.Yard">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="28" />
      <RowDefinition Height="*" />
    </Grid.RowDefinitions>

    <DockPanel Grid.Row="0" Background="#1F2937" LastChildFill="True">
      <Button DockPanel.Dock="Left" Content="&#x2190; Yard" Command="{Binding BackCommand}" Padding="10,0" BorderThickness="0" Background="Transparent" Foreground="White"
              ToolTip="Back to the Yard (Ctrl+Alt+Y)" />
      <TextBlock DockPanel.Dock="Left" Text="{Binding ActiveTile.Name}" Foreground="White" FontWeight="SemiBold" VerticalAlignment="Center" Margin="8,0,16,0" />
      <TextBlock DockPanel.Dock="Right" Foreground="#FCD34D" VerticalAlignment="Center" Margin="12,0"
                 Text="{Binding DataContext.StatusMessage, RelativeSource={RelativeSource AncestorType=Window}}" />
      <ItemsControl ItemsSource="{Binding Pips}" VerticalAlignment="Center">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><StackPanel Orientation="Horizontal" /></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate DataType="{x:Type yard:WorkspaceTileViewModel}">
            <Button Command="{Binding DataContext.SelectPipCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}" CommandParameter="{Binding}"
                    Background="Transparent" BorderThickness="0" Padding="4,0" ToolTip="{Binding Name}">
              <StackPanel Orientation="Horizontal">
                <Ellipse Width="10" Height="10" VerticalAlignment="Center">
                  <Ellipse.Style>
                    <Style TargetType="Ellipse">
                      <Setter Property="Fill" Value="{Binding AccentColor, Converter={StaticResource HexBrush}}" />
                      <Style.Triggers>
                        <DataTrigger Binding="{Binding NeedsAttention}" Value="True">
                          <Setter Property="Fill" Value="#F59E0B" />
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </Ellipse.Style>
                </Ellipse>
                <TextBlock Text="{Binding Name}" Foreground="#D1D5DB" FontSize="11" Margin="4,0,0,0" VerticalAlignment="Center" />
              </StackPanel>
            </Button>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </DockPanel>

    <Border x:Name="HostArea" Grid.Row="1" Background="#111827">
      <TextBlock Text="VS Code is docked here. If it does not appear, check the message in the strip above." Foreground="#6B7280"
                 HorizontalAlignment="Center" VerticalAlignment="Center" TextWrapping="Wrap" MaxWidth="480" TextAlignment="Center" />
    </Border>
  </Grid>
</UserControl>
```

`src/CodeSwitchX.UI/Cab/CabView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeSwitchX.Hosting.Win32;

namespace CodeSwitchX.UI.Cab;

public partial class CabView : UserControl
{
    private ScreenRect? _lastRect;
    private Window? _window;

    public CabView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => Publish();
        HostArea.SizeChanged += (_, _) => Publish();
    }

    /// <summary>Screen rectangle (physical pixels) of the host area, raised whenever it changes.</summary>
    public event Action<ScreenRect>? HostRectChanged;

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.LocationChanged += OnWindowMoved;
            _window.StateChanged += OnWindowMoved;
        }

        Publish();
    }

    private void Detach()
    {
        if (_window is not null)
        {
            _window.LocationChanged -= OnWindowMoved;
            _window.StateChanged -= OnWindowMoved;
            _window = null;
        }
    }

    private void OnWindowMoved(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        if (!IsVisible || HostArea.ActualWidth <= 0 || HostArea.ActualHeight <= 0 || PresentationSource.FromVisual(HostArea) is not { } source)
        {
            return;
        }

        var scale = source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var topLeft = HostArea.PointToScreen(new Point(0, 0));
        var rect = ScreenRect.FromSize(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(HostArea.ActualWidth * scale.M11),
            (int)Math.Round(HostArea.ActualHeight * scale.M22));

        if (rect != _lastRect)
        {
            _lastRect = rect;
            HostRectChanged?.Invoke(rect);
        }
    }
}
```

- [ ] **Step 4: Implement hotkeys and the tray icon**

`src/CodeSwitchX.UI/Infrastructure/HotkeyService.cs`:

```csharp
using System.Windows.Interop;
using CodeSwitchX.Hosting.Win32;
using CodeSwitchX.UI.Shell;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>Ctrl+Alt+Y toggles Yard/Cab; Ctrl+Alt+1..9 jumps to a tile.</summary>
public sealed class HotkeyService
{
    private const int ToggleId = 1;
    private const int JumpBaseId = 10;
    private const uint VkY = 0x59;
    private const uint Vk1 = 0x31;

    private readonly ILogger<HotkeyService> _logger;
    private HwndSource? _source;
    private ShellViewModel? _shell;
    private nint _hwnd;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    public void Attach(nint hwnd, ShellViewModel shell)
    {
        _hwnd = hwnd;
        _shell = shell;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        Register(ToggleId, VkY, "Ctrl+Alt+Y");
        for (var i = 1; i <= 9; i++)
        {
            Register(JumpBaseId + i, Vk1 + (uint)(i - 1), $"Ctrl+Alt+{i}");
        }
    }

    public void Detach()
    {
        if (_hwnd == 0)
        {
            return;
        }

        HotkeyInterop.Unregister(_hwnd, ToggleId);
        for (var i = 1; i <= 9; i++)
        {
            HotkeyInterop.Unregister(_hwnd, JumpBaseId + i);
        }

        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = 0;
    }

    private void Register(int id, uint virtualKey, string label)
    {
        if (!HotkeyInterop.Register(_hwnd, id, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat, virtualKey))
        {
            _logger.LogWarning("Global hotkey {Hotkey} is already taken by another application", label);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != HotkeyInterop.WmHotkey || _shell is null)
        {
            return 0;
        }

        var id = wParam.ToInt32();
        if (id == ToggleId)
        {
            _shell.ToggleMode();
            handled = true;
        }
        else if (id > JumpBaseId && id <= JumpBaseId + 9)
        {
            _ = _shell.JumpToAsync(id - JumpBaseId);
            handled = true;
        }

        return 0;
    }
}
```

`src/CodeSwitchX.UI/Infrastructure/TrayIconService.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeSwitchX.UI.Shell;
using H.NotifyIcon;

namespace CodeSwitchX.UI.Infrastructure;

public sealed class TrayIconService
{
    private TaskbarIcon? _icon;

    public void Attach(Window window, ShellViewModel shell)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Show CodeSwitchX", () => Show(window)));
        menu.Items.Add(MenuItem("Back to Yard", () =>
        {
            shell.BackToYard();
            Show(window);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Exit", () => Application.Current.Shutdown()));

        _icon = new TaskbarIcon
        {
            ToolTipText = "CodeSwitchX",
            IconSource = (ImageSource)Application.Current.FindResource("AppIcon"),
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseDown += (_, _) => Show(window);
        _icon.ForceCreate();
    }

    public void Detach()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private static void Show(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CodeSwitchX.UI.Tests --filter "FullyQualifiedName~Cab|FullyQualifiedName~Shell"`
Expected: all pass once Task 18 supplies `SettingsViewModel` and `PerformanceBarViewModel`.

---

### Task 18: Settings and performance bar

**Files:**
- Create: `src/CodeSwitchX.UI/Settings/SettingKeys.cs`, `SettingsViewModel.cs`, `SettingsView.xaml`, `SettingsView.xaml.cs`
- Create: `src/CodeSwitchX.UI/Telemetry/PerformanceBarViewModel.cs`, `PerformanceBarView.xaml`, `PerformanceBarView.xaml.cs`
- Test: `tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`, `tests/CodeSwitchX.UI.Tests/Telemetry/PerformanceBarViewModelTests.cs`

**Interfaces:**
- Consumes: `ClaudeHookInstaller`, `HookInstallStatus`, `ISettingsStore`, `PersistenceWriterOptions`, `AppPaths`, `ClaudeCodePaths`, `TelemetryService`, `TelemetryUpdated`, `SessionEngine`, `SessionChanged`, `TokenFormat`.
- Produces: `SettingKeys { StorePayloads, FiveHourBudgetTokens, RelayExecutable }`; `SettingsViewModel(ClaudeHookInstaller, ISettingsStore, PersistenceWriterOptions, AppPaths, ClaudeCodePaths, ILogger<SettingsViewModel>)` with `RelayExecutable, HookState, HookStatusText, LastMessage, DataFolder, LogsFolder, SettingsFile, StorePayloads, FiveHourBudgetTokens`, `Refresh()`, `LoadAsync(ct)`, commands `InstallHooks, RemoveHooks, OpenDataFolder, OpenLogsFolder, Close`; `PerformanceBarViewModel(TelemetryService, SessionEngine, IEventBus, IUiDispatcher, ISettingsStore)` with `TokensTodayText, CostTodayText, FiveHourText, FiveHourPercent, HasBudget, ActiveSessions, WaitingSessions, RateNormalized (double[]), IsExpanded`, `InitializeAsync(ct)`, `Apply(TelemetrySnapshot)`, `RecountSessions()`, `SetBudget(long?)`.

- [ ] **Step 1: Write the failing tests**

`tests/CodeSwitchX.UI.Tests/Settings/SettingsViewModelTests.cs`:

```csharp
using CodeSwitchX.Core;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CodeSwitchX.UI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Settings;

public class SettingsViewModelTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "csx-settings-" + Guid.NewGuid().ToString("N")));
    private readonly ClaudeCodePaths _claude;
    private readonly ISettingsStore _store = Substitute.For<ISettingsStore>();
    private readonly PersistenceWriterOptions _writerOptions = new();
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        _claude = new ClaudeCodePaths(Path.Combine(_paths.Root, "home"));
        _store.GetAsync<bool?>(SettingKeys.StorePayloads, Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
        _store.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(5_000_000));
        _store.GetAsync<string>(SettingKeys.RelayExecutable, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        _vm = new SettingsViewModel(new ClaudeHookInstaller(_claude, NullLogger<ClaudeHookInstaller>.Instance), _store, _writerOptions, _paths, _claude, NullLogger<SettingsViewModel>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_applies_persisted_values_and_defaults_the_relay_path()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads.ShouldBeTrue();
        _writerOptions.StorePayloads.ShouldBeTrue();
        _vm.FiveHourBudgetTokens.ShouldBe(5_000_000);
        _vm.RelayExecutable.ShouldBe(Path.Combine(AppContext.BaseDirectory, "relay", "csx-hook.exe"));
        _vm.DataFolder.ShouldBe(_paths.Root);
        _vm.SettingsFile.ShouldBe(_claude.SettingsFile);
    }

    [Fact]
    public async Task Install_and_remove_hooks_update_the_status()
    {
        await _vm.LoadAsync(CancellationToken.None);
        _vm.HookState.ShouldBe(HookInstallState.NotInstalled);

        _vm.InstallHooksCommand.Execute(null);
        _vm.HookState.ShouldBe(HookInstallState.Installed);
        _vm.HookStatusText.ShouldContain("8 of 8");
        File.Exists(_claude.SettingsFile).ShouldBeTrue();

        _vm.RemoveHooksCommand.Execute(null);
        _vm.HookState.ShouldBe(HookInstallState.NotInstalled);
    }

    [Fact]
    public async Task Toggling_store_payloads_persists_and_updates_the_writer()
    {
        await _vm.LoadAsync(CancellationToken.None);

        _vm.StorePayloads = false;
        await Task.Delay(50);

        _writerOptions.StorePayloads.ShouldBeFalse();
        await _store.Received().SetAsync(SettingKeys.StorePayloads, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_settings_json_surfaces_as_a_message_not_a_crash()
    {
        Directory.CreateDirectory(_claude.ClaudeDirectory);
        File.WriteAllText(_claude.SettingsFile, "{ broken");
        await _vm.LoadAsync(CancellationToken.None);

        _vm.InstallHooksCommand.Execute(null);

        _vm.LastMessage.ShouldContain("not valid JSON");
        File.ReadAllText(_claude.SettingsFile).ShouldBe("{ broken");
    }
}
```

`tests/CodeSwitchX.UI.Tests/Telemetry/PerformanceBarViewModelTests.cs`:

```csharp
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Settings;
using CodeSwitchX.UI.Telemetry;
using NSubstitute;

namespace CodeSwitchX.UI.Tests.Telemetry;

public class PerformanceBarViewModelTests
{
    private readonly ShellTestHarness _h = new();

    [Fact]
    public async Task Snapshot_is_formatted_for_the_bar()
    {
        _h.Settings.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(2_000_000));
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);
        var rate = new long[60];
        rate[^1] = 500;
        rate[^2] = 1000;

        bar.Apply(new TelemetrySnapshot(
            new UsageTotals(new TokenUsage(1_200_000, 34_000, 0, 0), 3.456m),
            new UsageTotals(new TokenUsage(1_000_000, 0, 0, 0), 2m),
            rate,
            _h.Time.GetUtcNow()));

        bar.TokensTodayText.ShouldBe("1.2M");
        bar.CostTodayText.ShouldBe("$3.46 est.");
        bar.HasBudget.ShouldBeTrue();
        bar.FiveHourText.ShouldBe("1M / 2M");
        bar.FiveHourPercent.ShouldBe(50);
        bar.RateNormalized.Length.ShouldBe(60);
        bar.RateNormalized[^2].ShouldBe(1.0);
        bar.RateNormalized[^1].ShouldBe(0.5);
    }

    [Fact]
    public async Task Without_a_budget_the_five_hour_figure_stands_alone()
    {
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);

        bar.Apply(new TelemetrySnapshot(UsageTotals.Zero, new UsageTotals(new TokenUsage(750_000, 0, 0, 0), 0m), new long[60], _h.Time.GetUtcNow()));

        bar.HasBudget.ShouldBeFalse();
        bar.FiveHourText.ShouldBe("750K");
        bar.FiveHourPercent.ShouldBe(0);
    }

    [Fact]
    public async Task Session_counts_follow_the_engine()
    {
        var bar = _h.Shell.PerformanceBar;
        await bar.InitializeAsync(CancellationToken.None);
        var now = _h.Time.GetUtcNow();

        _h.Engine.Apply(new HookEvent { SessionId = "a", EventName = "UserPromptSubmit", Signal = SessionSignal.PromptSubmit, At = now });
        _h.Engine.Apply(new HookEvent { SessionId = "b", EventName = "Notification", Signal = SessionSignal.Notification, At = now });
        _h.Engine.Apply(new HookEvent { SessionId = "c", EventName = "SessionEnd", Signal = SessionSignal.SessionEnd, At = now });

        bar.ActiveSessions.ShouldBe(2);
        bar.WaitingSessions.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CodeSwitchX.UI.Tests`
Expected: build errors for `SettingsViewModel`, `PerformanceBarViewModel`, `SettingKeys`.

- [ ] **Step 3: Implement Settings**

`src/CodeSwitchX.UI/Settings/SettingKeys.cs`:

```csharp
namespace CodeSwitchX.UI.Settings;

public static class SettingKeys
{
    public const string StorePayloads = "persistence.storePayloads";
    public const string FiveHourBudgetTokens = "telemetry.fiveHourBudgetTokens";
    public const string RelayExecutable = "hooks.relayExecutable";
}
```

`src/CodeSwitchX.UI/Settings/SettingsViewModel.cs`:

```csharp
using System.Diagnostics;
using CodeSwitchX.Core;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Data;
using CodeSwitchX.Ingest.Hooks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CodeSwitchX.UI.Settings;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ClaudeHookInstaller _installer;
    private readonly ISettingsStore _settings;
    private readonly PersistenceWriterOptions _writerOptions;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    [ObservableProperty] private string _relayExecutable;
    [ObservableProperty] private HookInstallState _hookState;
    [ObservableProperty] private string _hookStatusText = string.Empty;
    [ObservableProperty] private string? _lastMessage;
    [ObservableProperty] private bool _storePayloads;
    [ObservableProperty] private long? _fiveHourBudgetTokens;

    public SettingsViewModel(ClaudeHookInstaller installer, ISettingsStore settings, PersistenceWriterOptions writerOptions, AppPaths paths, ClaudeCodePaths claude,
        ILogger<SettingsViewModel> logger)
    {
        _installer = installer;
        _settings = settings;
        _writerOptions = writerOptions;
        _logger = logger;
        DataFolder = paths.Root;
        LogsFolder = paths.LogsDirectory;
        SettingsFile = claude.SettingsFile;
        _relayExecutable = DefaultRelayExecutable;
    }

    public static string DefaultRelayExecutable => Path.Combine(AppContext.BaseDirectory, "relay", "csx-hook.exe");

    public string DataFolder { get; }
    public string LogsFolder { get; }
    public string SettingsFile { get; }
    public event Action<long?>? BudgetChanged;
    public event Action? CloseRequested;

    public async Task LoadAsync(CancellationToken ct)
    {
        _loading = true;
        try
        {
            StorePayloads = await _settings.GetAsync<bool?>(SettingKeys.StorePayloads, ct) ?? false;
            _writerOptions.StorePayloads = StorePayloads;
            FiveHourBudgetTokens = await _settings.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, ct);
            RelayExecutable = await _settings.GetAsync<string>(SettingKeys.RelayExecutable, ct) ?? DefaultRelayExecutable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading settings failed");
            LastMessage = ex.Message;
        }
        finally
        {
            _loading = false;
        }

        Refresh();
    }

    public void Refresh()
    {
        var status = _installer.GetStatus(RelayExecutable);
        HookState = status.State;
        HookStatusText = status.State switch
        {
            HookInstallState.Installed => $"Installed ({status.InstalledEvents.Count} of {ClaudeHookInstaller.Events.Length} events)",
            HookInstallState.Partial => $"Partial: missing {string.Join(", ", status.MissingEvents)}",
            HookInstallState.Outdated => "Installed, but pointing at a different csx-hook.exe. Reinstall to update the path.",
            _ => "Not installed. Tile states fall back to transcript inference.",
        };
    }

    [RelayCommand]
    private void InstallHooks()
    {
        try
        {
            var result = _installer.Install(RelayExecutable);
            LastMessage = result.Changed
                ? $"Hooks written to {SettingsFile}" + (result.BackupFile is null ? string.Empty : $" (backup: {Path.GetFileName(result.BackupFile)})")
                : "Hooks were already up to date.";
        }
        catch (HookInstallException ex)
        {
            LastMessage = ex.Message;
        }

        Refresh();
    }

    [RelayCommand]
    private void RemoveHooks()
    {
        try
        {
            var result = _installer.Uninstall();
            LastMessage = result.Changed ? "CodeSwitchX hooks removed." : "No CodeSwitchX hooks were present.";
        }
        catch (HookInstallException ex)
        {
            LastMessage = ex.Message;
        }

        Refresh();
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataFolder);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(LogsFolder);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    partial void OnStorePayloadsChanged(bool value)
    {
        _writerOptions.StorePayloads = value;
        Persist(SettingKeys.StorePayloads, value);
    }

    partial void OnFiveHourBudgetTokensChanged(long? value)
    {
        Persist(SettingKeys.FiveHourBudgetTokens, value);
        BudgetChanged?.Invoke(value);
    }

    partial void OnRelayExecutableChanged(string value)
    {
        Persist(SettingKeys.RelayExecutable, value);
        Refresh();
    }

    private void Persist<T>(string key, T value)
    {
        if (_loading)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _settings.SetAsync(key, value, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving setting {Key} failed", key);
            }
        });
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }
}
```

`src/CodeSwitchX.UI/Settings/SettingsView.xaml`:

```xml
<UserControl x:Class="CodeSwitchX.UI.Settings.SettingsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ScrollViewer VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="24" MaxWidth="760" HorizontalAlignment="Left">
      <DockPanel>
        <Button DockPanel.Dock="Right" Content="Back to Yard" Command="{Binding DataContext.CloseSettingsCommand, RelativeSource={RelativeSource AncestorType=Window}}" />
        <TextBlock Text="Settings" FontSize="22" FontWeight="SemiBold" />
      </DockPanel>

      <TextBlock Text="Claude Code hooks" FontSize="16" FontWeight="SemiBold" Margin="0,24,0,6" />
      <TextBlock TextWrapping="Wrap" Opacity="0.8"
                 Text="CodeSwitchX adds csx-hook.exe to the user-level Claude Code settings so every session reports its state. Existing hooks are kept; a backup is written first." />
      <TextBlock Margin="0,8,0,0"><Run Text="Status: " FontWeight="SemiBold" /><Run Text="{Binding HookStatusText, Mode=OneWay}" /></TextBlock>
      <TextBlock Margin="0,4,0,0" Opacity="0.7"><Run Text="Settings file: " /><Run Text="{Binding SettingsFile, Mode=OneWay}" /></TextBlock>
      <DockPanel Margin="0,8,0,0">
        <TextBlock Text="Relay executable" VerticalAlignment="Center" Width="140" />
        <TextBox Text="{Binding RelayExecutable, UpdateSourceTrigger=LostFocus}" />
      </DockPanel>
      <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
        <Button Content="Install hooks" Command="{Binding InstallHooksCommand}" MinWidth="120" Margin="0,0,8,0" />
        <Button Content="Remove hooks" Command="{Binding RemoveHooksCommand}" MinWidth="120" />
      </StackPanel>
      <TextBlock Text="{Binding LastMessage}" Margin="0,8,0,0" TextWrapping="Wrap" Foreground="#2563EB"
                 Visibility="{Binding LastMessage, Converter={StaticResource NullToCollapsed}}" />

      <TextBlock Text="Telemetry" FontSize="16" FontWeight="SemiBold" Margin="0,24,0,6" />
      <DockPanel>
        <TextBlock Text="5-hour soft budget (tokens)" VerticalAlignment="Center" Width="200" />
        <TextBox Text="{Binding FiveHourBudgetTokens, UpdateSourceTrigger=LostFocus, TargetNullValue=''}" Width="160" HorizontalAlignment="Left"
                 ToolTip="Leave empty for no budget. CodeSwitchX cannot read your real plan limit; this is a reminder line, not a hard limit." />
      </DockPanel>

      <TextBlock Text="Privacy" FontSize="16" FontWeight="SemiBold" Margin="0,24,0,6" />
      <CheckBox Content="Store raw hook payloads in the database (contains prompts and file paths)" IsChecked="{Binding StorePayloads}" />

      <TextBlock Text="Data" FontSize="16" FontWeight="SemiBold" Margin="0,24,0,6" />
      <TextBlock><Run Text="Folder: " /><Run Text="{Binding DataFolder, Mode=OneWay}" /></TextBlock>
      <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
        <Button Content="Open data folder" Command="{Binding OpenDataFolderCommand}" Margin="0,0,8,0" />
        <Button Content="Open logs" Command="{Binding OpenLogsFolderCommand}" />
      </StackPanel>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

`src/CodeSwitchX.UI/Settings/SettingsView.xaml.cs`:

```csharp
using System.Windows.Controls;

namespace CodeSwitchX.UI.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
```

In `ShellViewModel.InitializeAsync` replace `Settings.Refresh();` with `await Settings.LoadAsync(ct); Settings.BudgetChanged += PerformanceBar.SetBudget; Settings.CloseRequested += CloseSettings;`.

- [ ] **Step 4: Implement the performance bar**

`src/CodeSwitchX.UI/Telemetry/PerformanceBarViewModel.cs`:

```csharp
using System.Globalization;
using CodeSwitchX.Core.Messaging;
using CodeSwitchX.Core.Persistence;
using CodeSwitchX.Core.Sessions;
using CodeSwitchX.Telemetry;
using CodeSwitchX.UI.Infrastructure;
using CodeSwitchX.UI.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeSwitchX.UI.Telemetry;

/// <summary>Slim bottom bar: tokens today, estimated cost, 5-hour window against a soft budget, rate sparkline, session counts.</summary>
public sealed partial class PerformanceBarViewModel : ObservableObject, IDisposable
{
    private readonly TelemetryService _telemetry;
    private readonly SessionEngine _engine;
    private readonly IEventBus _bus;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private long? _budget;

    [ObservableProperty] private string _tokensTodayText = "0";
    [ObservableProperty] private string _costTodayText = "$0.00 est.";
    [ObservableProperty] private string _fiveHourText = "0";
    [ObservableProperty] private double _fiveHourPercent;
    [ObservableProperty] private bool _hasBudget;
    [ObservableProperty] private int _activeSessions;
    [ObservableProperty] private int _waitingSessions;
    [ObservableProperty] private double[] _rateNormalized = new double[TelemetryService.RateMinutes];
    [ObservableProperty] private bool _isExpanded = true;

    public PerformanceBarViewModel(TelemetryService telemetry, SessionEngine engine, IEventBus bus, IUiDispatcher ui, ISettingsStore settings)
    {
        _telemetry = telemetry;
        _engine = engine;
        _bus = bus;
        _ui = ui;
        _settings = settings;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _budget = await _settings.GetAsync<long?>(SettingKeys.FiveHourBudgetTokens, ct);
        _subscriptions.Add(_bus.Subscribe<TelemetryUpdated>(m => _ui.Post(() => Apply(m.Snapshot))));
        _subscriptions.Add(_bus.Subscribe<SessionChanged>(_ => _ui.Post(RecountSessions)));
        Apply(_telemetry.Current);
        RecountSessions();
    }

    public void SetBudget(long? budget)
    {
        _budget = budget;
        Apply(_telemetry.Current);
    }

    public void Apply(TelemetrySnapshot snapshot)
    {
        TokensTodayText = TokenFormat.Compact(snapshot.Today.Tokens.Total);
        CostTodayText = string.Create(CultureInfo.InvariantCulture, $"${snapshot.Today.Cost:0.00} est.");

        var fiveHour = snapshot.FiveHours.Tokens.Total;
        HasBudget = _budget is > 0;
        FiveHourText = HasBudget ? $"{TokenFormat.Compact(fiveHour)} / {TokenFormat.Compact(_budget!.Value)}" : TokenFormat.Compact(fiveHour);
        FiveHourPercent = HasBudget ? Math.Min(100.0, 100.0 * fiveHour / _budget!.Value) : 0;

        var max = snapshot.RatePerMinute.Length == 0 ? 0 : snapshot.RatePerMinute.Max();
        RateNormalized = snapshot.RatePerMinute.Select(v => max == 0 ? 0.0 : (double)v / max).ToArray();
    }

    public void RecountSessions()
    {
        var snapshots = _engine.Snapshots;
        ActiveSessions = snapshots.Count(s => SessionStateMachine.IsLive(s.State));
        WaitingSessions = snapshots.Count(s => SessionStateMachine.NeedsUser(s.State));
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }
}
```

`src/CodeSwitchX.UI/Telemetry/PerformanceBarView.xaml`:

```xml
<UserControl x:Class="CodeSwitchX.UI.Telemetry.PerformanceBarView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Border BorderThickness="0,1,0,0" BorderBrush="#22000000" Padding="12,4">
    <DockPanel>
      <ToggleButton DockPanel.Dock="Right" IsChecked="{Binding IsExpanded}" Content="&#x25BE;" BorderThickness="0" Background="Transparent" Padding="6,0" ToolTip="Collapse or expand" />
      <StackPanel Orientation="Horizontal" Visibility="{Binding IsExpanded, Converter={StaticResource BoolVisible}}">
        <StackPanel.Resources>
          <Style TargetType="TextBlock" x:Key="Label">
            <Setter Property="Opacity" Value="0.6" />
            <Setter Property="FontSize" Value="11" />
            <Setter Property="Margin" Value="0,0,4,0" />
            <Setter Property="VerticalAlignment" Value="Center" />
          </Style>
          <Style TargetType="TextBlock" x:Key="Value">
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Margin" Value="0,0,20,0" />
            <Setter Property="VerticalAlignment" Value="Center" />
          </Style>
        </StackPanel.Resources>
        <TextBlock Style="{StaticResource Label}" Text="tokens today" />
        <TextBlock Style="{StaticResource Value}" Text="{Binding TokensTodayText}" />
        <TextBlock Style="{StaticResource Label}" Text="cost today" />
        <TextBlock Style="{StaticResource Value}" Text="{Binding CostTodayText}" ToolTip="Estimate from the editable pricing table" />
        <TextBlock Style="{StaticResource Label}" Text="5-hour window" />
        <TextBlock Style="{StaticResource Value}" Text="{Binding FiveHourText}" Margin="0,0,6,0" />
        <ProgressBar Width="90" Height="6" Minimum="0" Maximum="100" Value="{Binding FiveHourPercent, Mode=OneWay}" Margin="0,0,20,0" VerticalAlignment="Center"
                     Visibility="{Binding HasBudget, Converter={StaticResource BoolVisible}}" />
        <TextBlock Style="{StaticResource Label}" Text="rate (60 min)" />
        <Canvas x:Name="Sparkline" Width="120" Height="18" Margin="0,0,20,0" VerticalAlignment="Center" ClipToBounds="True">
          <Polyline x:Name="SparklinePath" Stroke="#3B82F6" StrokeThickness="1.5" StrokeLineJoin="Round" />
        </Canvas>
        <TextBlock Style="{StaticResource Label}" Text="active chats" />
        <TextBlock Style="{StaticResource Value}" Text="{Binding ActiveSessions}" />
        <TextBlock Style="{StaticResource Label}" Text="waiting" />
        <TextBlock Style="{StaticResource Value}" Text="{Binding WaitingSessions}" Foreground="#F59E0B" />
      </StackPanel>
    </DockPanel>
  </Border>
</UserControl>
```

`src/CodeSwitchX.UI/Telemetry/PerformanceBarView.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeSwitchX.UI.Telemetry;

public partial class PerformanceBarView : UserControl
{
    public PerformanceBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Sparkline.SizeChanged += (_, _) => Redraw();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PerformanceBarViewModel old)
        {
            old.PropertyChanged -= OnViewModelChanged;
        }

        if (e.NewValue is PerformanceBarViewModel vm)
        {
            vm.PropertyChanged += OnViewModelChanged;
            Redraw();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PerformanceBarViewModel.RateNormalized) or null)
        {
            Redraw();
        }
    }

    private void Redraw()
    {
        if (DataContext is not PerformanceBarViewModel vm || vm.RateNormalized.Length < 2 || Sparkline.ActualWidth <= 0)
        {
            return;
        }

        var points = new PointCollection();
        var width = Sparkline.ActualWidth;
        var height = Sparkline.ActualHeight;
        var step = width / (vm.RateNormalized.Length - 1);
        for (var i = 0; i < vm.RateNormalized.Length; i++)
        {
            points.Add(new Point(i * step, height - vm.RateNormalized[i] * (height - 2) - 1));
        }

        SparklinePath.Points = points;
    }
}
```

- [ ] **Step 5: Build everything and run every test project**

Run: `dotnet build CodeSwitchX.slnx` then `dotnet test CodeSwitchX.slnx`
Expected: 0 build errors; every test project green.

- [ ] **Step 6: Manual smoke run (M0/M1 exit check)**

Run: `dotnet run --project src/CodeSwitchX.UI`
Check, and record the outcome in the final report:
1. The window opens on the Yard with the "General" track and no tiles; the performance bar shows zeros.
2. Add workspace → pick a folder with a `.git` → tile appears with the branch name.
3. Settings → Install hooks → status reads "Installed (8 of 8 events)"; `%USERPROFILE%\.claude\settings.json` gained eight `csx-hook` entries and a backup file exists.
4. Start `claude` in a terminal inside that folder and send a prompt → within a second the tile shows a chat row that flips to Working and back to Idle; a permission prompt turns the row amber and the tile border pulses.
5. Click the tile → VS Code opens, and the window is positioned over the Cab area; Ctrl+Alt+Y returns to the Yard and the VS Code window disappears (cloaked); Ctrl+Alt+Y again brings it back.
6. Dragging the VS Code window snaps it back; closing VS Code shows "Stopped" on the tile.

---

## Self-review notes

- Every spec item in "Implementation slice 1" maps to a task: scaffold (1), Core (2–4, 7, 14), Data (5–6), Ingest (7–10), Telemetry (11), Hook (12), Hosting (13), UI (14–18).
- Deferred on purpose, matching the spec: toasts, DWM live thumbnails on tiles (the `DwmThumbnail` helper exists but no tile uses it yet), Needs-Me Queue, Link extension, flagship features, Web hosting, process/GPU sampling, MSIX.
- Review Focus items are pinned: sibling roots (Task 3 `Sibling_roots_that_share_a_string_prefix_do_not_collide`), unknown hook events (Task 7 `Unknown_event_names_still_parse_with_a_null_signal`, Task 4 `Unknown_event_is_recorded_but_does_not_change_state`), truncated transcripts (Task 9 `Truncated_file_restarts_from_zero_without_throwing`), malformed settings.json (Task 10 `Malformed_settings_are_refused_and_left_untouched`, Task 18 `Malformed_settings_json_surfaces_as_a_message_not_a_crash`), dead relay endpoint (Task 12 `Dead_port_and_missing_pipe_exit_zero_within_the_budget`).
- Known compile-time risk: CsWin32 overload shapes in Task 13 and the `ThemeMode` property on `Application` (WPF0001 experimental API) in Task 14. Both are contained in one file each.

