# AGENTS.md

This file provides guidance to coding agents when working with code in this repository.

## Overview

`Chorn.Aspire.ExternalProject` is a single NuGet package that lets a .NET Aspire AppHost add and run a .NET project that lives **outside** the current solution (e.g. in another repository) without adding it to the solution.

Since 2.0 it builds on Aspire's own `AddDotnetProject` (package `Aspire.Hosting.Dotnet`, Aspire 17+), which already handles path-based projects: launch profile, Kestrel endpoints, OpenTelemetry, launchUrl and the coordinated build. This package adds only what external projects still need: no IDE launch, `dotnet watch`, a debugger attach command and git support.

The public surface:
- `builder.AddExternalProject(name, csprojPath, configure)` on `IDistributedApplicationBuilder` — the all-in-one shortcut, returns `IResourceBuilder<DotnetProjectResource>`.
- Building blocks on `IResourceBuilder<DotnetProjectResource>`: `WithoutIdeLaunch`, `WithDotnetWatch`, `WithSolutionGroup`.
- Building blocks on any `IResourceBuilder<T> where T : ExecutableResource`: `WithDebuggerAttachCommand`, `WithGitPullCommand`, `WithGitUpToDateHealthCheck`.

## Commands

```powershell
# Build the library (Release produces the NuGet package — GeneratePackageOnBuild is on)
dotnet build Chorn.Aspire.ExternalProject.slnx --configuration Release

# Restore
dotnet restore Chorn.Aspire.ExternalProject.slnx
```

There is **no test project** in this repository. The CI `dotnet test` step uses `--filter FullyQualifiedName!~Example` purely to exclude sample/example projects; it does not run real tests. Do not assume a test harness exists — if you add tests, add a test project and wire it into the solution.

To run the sample end-to-end, launch the AppHost in `Samples/AppHostWithReferencedProject` (it references the two external projects under `Samples/External` by relative path). Running it creates an `aspire.config.json` next to the AppHost; don't commit it.

## CI/CD

- `.github/workflows/dotnet.yml` — build + package on push/PR to `main` (uses .NET 10 SDK).
- `.github/workflows/deploy.yml` — manual (`workflow_dispatch`) publish of the `.nupkg` to NuGet using the `NUGET_API_KEY` secret.
- Releasing: bump `<Version>` and update `<PackageReleaseNotes>` in `Chorn.Aspire.ExternalProject/Chorn.Aspire.ExternalProject.csproj`, then run the Deployment workflow.

## Architecture

The whole library is in `Chorn.Aspire.ExternalProject/`. `ExternalProjectBuilderExtensions` is a `partial` class split over two files:

- `ExternalProjectBuilderExtensions.cs` — `AddExternalProject` and the `DotnetProjectResource` building blocks.
- `ExternalProjectCommandExtensions.cs` — the dashboard commands (Debug, Git Pull) and the git health check.

`AddExternalProject` resolves the path against `builder.AppHostDirectory`, calls `AddDotnetProject` (passing `LaunchProfileName`, `ExcludeLaunchProfile`, `ExcludeKestrelEndpoints` through), then applies the building blocks according to `ExternalProjectResourceOptions`.

### How Aspire launches a DotnetProjectResource (Aspire 17)

This drives most design decisions; re-check it when bumping Aspire (source: `src/Aspire.Hosting.Dotnet/DotnetProjectHostingExtensions.cs` and `DotnetProjectBuildCoordinator.cs` in microsoft/aspire).

- **No IDE** (`DEBUG_SESSION_PORT` unset): Aspire builds all path-based projects together, then starts the built app's MSBuild `RunCommand` directly (`App.exe`, or `dotnet App.dll` without an apphost). No `dotnet run`.
- **Under an IDE**: the IDE passes `DEBUG_SESSION_INFO` with its supported launch configurations. Visual Studio reports `["project","browser"]`, so Aspire prebuilds and then asks VS to launch the project. **VS answers 500 for a project outside its solution, and the resource ends `FailedToStart`.**
- `WithoutIdeLaunch` removes the resource's `SupportsDebuggingAnnotation`, so Aspire starts the process itself. Under VS that is `dotnet run --project … --configuration <cfg> --no-launch-profile` with the app as a child. `AddExternalProject` applies this unless `LaunchFromIde` is set.
- **Watch:** Aspire 17 does not watch path-based projects (nothing reads `RunConfiguration.WatchEnabled`). `WithDotnetWatch` replaces the launch command with `WithLaunchToolArgs` (a later call replaces Aspire's) → `dotnet watch --non-interactive --project <csproj> run [--configuration X] --no-launch-profile --`. It also resets the command to `dotnet`, because without an IDE Aspire would otherwise switch it to the built app. The trailing `--` hands the launch profile's `commandLineArgs` (added by Aspire as ordinary args) to the app.
- Both `SupportsDebuggingAnnotation` and `WithLaunchToolArgs` are experimental (`ASPIREEXTENSION001`); the suppressions are local `#pragma`s. The Aspire dependency is pinned to `[17.0.0, 18.0.0)` for that reason.

### Debugger attach (Windows-centric)

`AttachDebugger` first maps the tracked PID to the app process, then attaches:
- **Visual Studio (default, `DebuggerAttachMode.VisualStudio`, Windows only):** `VisualStudioAttacher` enumerates the running object table for `!VisualStudio.DTE.<ver>:<pid>` entries and drives the DTE late-bound (`dynamic`, no interop package). Order: a VS whose open solution contains the project (walks `Solution.Projects`, recursing into solution folders; prefers `Debugger.CurrentMode == 1`, i.e. not debugging), then the VS that is an ancestor (`devenv`) of the AppHost process, then the command fallback. It runs on a dedicated STA thread with an `IOleMessageFilter` that retries calls VS rejects while busy. VS instances at a different elevation are invisible in the ROT. Built-in COM must be enabled (it is not under Native AOT/trimming; AppHosts aren't AOT).
- **Command (`vsjitdebugger -p <pid>`, fallback or `DebuggerAttachMode.Command`):** often fails with "No installed debugger has Just-In-Time debugging enabled" even when JIT is registered correctly.

Finding the app: `FindAppProcess` maps the tracked PID (`executable.pid`) to the app. If the tracked process is not named `dotnet`, it is the app (no-IDE launch). If it is `dotnet` without children (other than `conhost`), it is `dotnet App.dll`. Otherwise it is a `dotnet run`/`dotnet watch` wrapper, and a BFS over a parent→children map (via `ProcessHelper.GetParent`, which uses `NtQueryInformationProcess`; the `#if UNIX` branch is a stub) finds the nearest non-`dotnet` descendant. Known gap: `dotnet App.dll` under a wrapper is not found.
- **URL-based:** if `LaunchDebuggerUri` is set, POSTs to `{baseUrl}{LaunchDebuggerUri}` instead. The external project is expected to expose an endpoint that calls `Debugger.Launch()` (see `Samples/External/WeatherApi/Program.cs` `/debug`). More reliable.

### PID/URL lookup

The `Debug` command reads the resource's live PID and base URL on demand from `ResourceNotificationService.TryGetCurrentState(resourceName, out ResourceEvent)`. The `GetPid` / `GetHttpsOrHttpBaseUrl` helpers wrap this — that's the choke point if you change how PIDs or base URLs are obtained.

### Other files

- `GitUpToDateHealthCheck` — runs `git fetch` then `git status --porcelain=v2 --branch` and parses the `# branch.ab +x -y` line to report unhealthy when behind the remote. Git commands run in the project folder (`GetProjectFolder`: the csproj directory for .NET projects, else the working directory).
- `ExternalProjectSolutionGroupAnnotation` — marks resources for `WithSolutionGroup`, which `WaitFor`s every other resource in the same group. Rarely needed now that Aspire coordinates builds.
- `ExternalProjectResourceOptions` — the configure callback's options; extends Aspire's `ProjectResourceOptions`.
- `DebuggerAttachOptions` / `DebuggerAttachMode` — options for `WithDebuggerAttachCommand`.
- `VisualStudioAttacher` — the DTE-based attach (see above).

## Conventions

- `Directory.Build.props` applies repo-wide: `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=true`, deterministic builds.
- The library targets **net10.0**; the samples target **net11.0** (needs the .NET 11 SDK locally — CI only builds the library).
- The sample AppHost uses `Microsoft.NET.Sdk` plus `Aspire.Hosting.AppHost`/`Aspire.Hosting.Dotnet` packages (the `Aspire.AppHost.Sdk` is obsolete since Aspire 17, warning ASPIRE012).
- Solutions use the XML-based **`.slnx`** format (no legacy `.sln` files).
- `.editorconfig` and the `.DotSettings` (ReSharper) drive style. Notable: `using` directives go **inside** the namespace, file-scoped namespaces, and `this.` qualification on member access — match the existing files.
- The assembly is strong-name signed with `signingkey.snk`.
- **Do not `git commit` or `git push` without the user's explicit approval.** Make the changes, let the user review, and commit only when they say so.
- Commit messages follow the [gitmoji](https://gitmoji.dev) convention — prefix the message with the relevant emoji (e.g. `⬆️ Updated examples to aspire 9.3.1`, `🐛 Fixed ...`, `♻️ Refactored ...`).
