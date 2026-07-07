# AGENTS.md

This file provides guidance to coding agents when working with code in this repository.

## Overview

`Chorn.Aspire.ExternalProject` is a single NuGet package that lets a .NET Aspire AppHost add and run a .NET project that lives **outside** the current solution (e.g. in another repository) without adding it to the solution. It reproduces most of what Aspire's built-in `AddProject` does, but for a project referenced only by its `.csproj` path.

The public surface is one extension method: `builder.AddExternalProject(name, csprojPath, configure)` on `IDistributedApplicationBuilder`.

## Commands

```powershell
# Build the library (Release produces the NuGet package — GeneratePackageOnBuild is on)
dotnet build Chorn.Aspire.ExternalProject.slnx --configuration Release

# Restore
dotnet restore Chorn.Aspire.ExternalProject.slnx
```

There is **no test project** in this repository. The CI `dotnet test` step uses `--filter FullyQualifiedName!~Example` purely to exclude sample/example projects; it does not run real tests. Do not assume a test harness exists — if you add tests, add a test project and wire it into the solution.

To run the sample end-to-end, launch the AppHost in `Samples/AppHostWithReferencedProject` (it references the two external projects under `Samples/External` by relative path).

## CI/CD

- `.github/workflows/dotnet.yml` — build + package on push/PR to `main` (uses .NET 9 SDK).
- `.github/workflows/deploy.yml` — manual (`workflow_dispatch`) publish of the `.nupkg` to NuGet using the `NUGET_API_KEY` secret.
- Releasing: bump `<Version>` and update `<PackageReleaseNotes>` in `Chorn.Aspire.ExternalProject/Chorn.Aspire.ExternalProject.csproj`, then run the Deployment workflow.

## Architecture

The whole library is in `Chorn.Aspire.ExternalProject/`. Key flow, entered from `ExternalProjectBuilderExtensions.AddExternalProject`:

1. **Launch as an executable, not a project.** Aspire cannot add a project it can't reference at compile time, so the external project is added via `builder.AddExecutable(name, "dotnet", folder, ["run", "--project", <csproj>, "--no-launch-profile", ...])`. `dotnet run` handles build+run.
2. **launchSettings.json is required.** `Properties/launchSettings.json` next to the csproj is read and parsed manually (System.Text.Json, camelCase). The first profile with `commandName == "Project"` is used unless `LaunchProfileName` is set. Its `commandLineArgs`, `applicationUrl`, and `environmentVariables` are replayed onto the executable resource. If the file is missing, `AddExternalProject` throws.
3. **`WithExecutableProjectDefaults`** is a hand-ported copy of Aspire's internal `WithProjectDefaults`: it sets the OTLP/OpenTelemetry env vars, calls `WithOtlpExporter()`, reads the external project's `appsettings.json` / `appsettings.{env}.json` to discover Kestrel endpoints, materializes endpoints from the launch profile's `applicationUrl`, and builds `ASPNETCORE_URLS`. **This mirrors Aspire internals** — when bumping the Aspire dependency, re-check this method against the upstream `WithProjectDefaults`.
4. **Dashboard commands** are added via `WithCommand`:
   - `Debug` — attaches a debugger to the running external process (see below).
   - `GitPull` — runs `git pull` in the project folder (skipped when `SkipGitSupport = true`).
5. **Solution groups.** If `SolutionGroup` is set, the resource gets an `ExternalProjectSolutionGroupAnnotation` and `WaitFor`s every other resource sharing the same group, serializing startup to avoid concurrent-build conflicts between external projects that share dependencies.

### Debugger attach (Windows-centric)

Two strategies in `AttachDebugger`:
- **vsjitdebugger (default):** `dotnet run` spawns a child process; the actual app is the first non-`dotnet` child of the tracked PID. `ProcessHelper` walks parent PIDs via `NtQueryInformationProcess` (Windows P/Invoke; the `#if UNIX` branch is a stub) to find it, then starts `LaunchDebuggerCommand` (`vsjitdebugger`, args `-p <pid>`). This is unreliable — see README "Limitations".
- **URL-based:** if `LaunchDebuggerUri` is set, POSTs to `{baseUrl}{LaunchDebuggerUri}` instead. The external project is expected to expose an endpoint that calls `Debugger.Launch()` (see `Samples/External/WeatherApi/Program.cs` `/debug`). More reliable.

### PID/URL tracking — `SnapshotWatcher`

Commands can't get the current resource snapshot at execution time, so `SnapshotWatcher` (registered as a singleton) caches the latest `CustomResourceSnapshot` per resource name. It's populated from the `Debug` command's `UpdateState` callback (`DebugStateChange`), and exposes `GetPid` (reads the `executable.pid` property) and `GetHttpsOrHttpBaseUrl` (picks the best http/https URL). If you touch how PIDs or base URLs are obtained, this is the choke point.

### Other files

- `GitUpToDateHealthCheck` — opt-in via `EnableGitHealthCheck`. Runs `git fetch` then `git status --porcelain=v2 --branch` and parses the `# branch.ab +x -y` line to report unhealthy when behind the remote.
- `CommandLineArgsParser` — copied verbatim from dotnet/runtime; parses launch-profile `commandLineArgs` into an argv list. Don't rewrite it.
- `ExternalProjectResourceOptions` — the configure callback's options; extends Aspire's `ProjectResourceOptions`.

## Conventions

- `Directory.Build.props` applies repo-wide: `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=true`, deterministic builds.
- Everything targets **net10.0** (library and samples).
- Solutions use the XML-based **`.slnx`** format (no legacy `.sln` files).
- `.editorconfig` and the `.DotSettings` (ReSharper) drive style. Notable: `using` directives go **inside** the namespace, file-scoped namespaces, and `this.` qualification on member access — match the existing files.
- The assembly is strong-name signed with `signingkey.snk`.
- Commit messages follow the [gitmoji](https://gitmoji.dev) convention — prefix the message with the relevant emoji (e.g. `⬆️ Updated examples to aspire 9.3.1`, `🐛 Fixed ...`, `♻️ Refactored ...`).
- Known-ignored options: `ExcludeLaunchProfile` and `ExcludeKestrelEndpoint` on the resource options are currently not honored (see README limitations).
