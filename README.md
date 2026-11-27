# Aspire.ExternalProject
Aspire.ExternalProject is a .NET Aspire package for running a .NET project that is external to the current solution
(e.g. in another repository) from your AppHost.

[![NuGet version (Chorn.Aspire.ExternalProject)](https://img.shields.io/nuget/v/Chorn.Aspire.ExternalProject.svg?style=flat-square)](https://www.nuget.org/packages/Chorn.Aspire.ExternalProject/)

Since Aspire 17, Aspire itself can add a project by path with `AddDotnetProject` (package `Aspire.Hosting.Dotnet`).
Version 2.0 of this package builds on it and adds what external projects still need:

* **No IDE launch.** Under Visual Studio, Aspire hands .NET projects to the IDE to launch, and Visual Studio rejects
  projects that are not in its solution, so the resource fails to start. External projects are started by Aspire instead.
* **`dotnet watch`.** Aspire 17 has no watch support for path-based projects.
* **A `Debug` dashboard command** to attach a debugger to the running project.
* **Git support:** a `Git Pull` dashboard command and an optional git up-to-date health check.

## Usage
Install the package from NuGet:
```
dotnet add package Chorn.Aspire.ExternalProject
```

Add an external project via its project file in your AppHost. A relative path is resolved against the AppHost directory:
```csharp
builder.AddExternalProject("resourcename", "../../OtherRepo/src/Api/Api.csproj");
```

This calls Aspire's `AddDotnetProject`, so the launch profile, Kestrel endpoints, OpenTelemetry, the dashboard
launch URL and the coordinated build all work the way Aspire does them. It also adds two dashboard commands:

* `Debug` - attaches a debugger to the external project.
* `Git Pull` - pulls the latest changes from the git repository of the external project.

### Building blocks
`AddExternalProject` is a shortcut. You can also start from Aspire's `AddDotnetProject` and add only what you need:
```csharp
builder.AddDotnetProject("resourcename", "../../OtherRepo/src/Api/Api.csproj")
	.WithoutIdeLaunch()               // let Aspire start it, Visual Studio can't
	.WithDotnetWatch()                // recompile/hot reload on change
	.WithDebuggerAttachCommand(d => d.LaunchDebuggerUri = "/debug")
	.WithGitPullCommand()
	.WithGitUpToDateHealthCheck();
```
`WithDebuggerAttachCommand`, `WithGitPullCommand` and `WithGitUpToDateHealthCheck` work on any executable resource.

### Options
Remove the git integration with `SkipGitSupport`:
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.SkipGitSupport = true;
});
```

Add a git up-to-date health check with `EnableGitHealthCheck`:
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.EnableGitHealthCheck = true;
});
```

By default the `Debug` command attaches through a running Visual Studio (Windows only), the same way
Debug → Attach to Process does:
1. a Visual Studio whose open solution contains the external project, so breakpoints in its source are hit
   (one that is not debugging yet is preferred),
2. otherwise the Visual Studio that debugs the AppHost,
3. otherwise `LaunchDebuggerCommand` (`vsjitdebugger` by default).

So the easiest way to debug an external project is to open its solution in a second Visual Studio and click `Debug`
in the dashboard. Visual Studio instances running at a different elevation than the AppHost (e.g. "Run as
administrator") can't be reached. Set `DebuggerAttachMode = DebuggerAttachMode.Command` to always use
`LaunchDebuggerCommand`, e.g. for Rider.

As an alternative that works with any debugger, set `LaunchDebuggerUri` to an endpoint in your external project that
starts the debugger via `Debugger.Launch()` (see the sample for the external project code):
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.LaunchDebuggerUri = "/debug";
});
```

The launch profile options are passed to Aspire: `LaunchProfileName` picks a profile, `ExcludeLaunchProfile` skips
`launchSettings.json` entirely and `ExcludeKestrelEndpoints` ignores endpoints from Kestrel configuration.
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.ExcludeLaunchProfile = true;
}).WithHttpEndpoint();
```

Set `UseWatch` to start the external project with `dotnet watch` instead of `dotnet run`, so source
changes are recompiled (hot reloaded, or a rebuild/restart for edits that can't be hot reloaded).
It runs non-interactively, so a rude edit restarts the process automatically instead of prompting.
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.UseWatch = true;
});
```
Note: with `UseWatch` the process id changes when the app restarts, so the `Debug` command may need to
be re-run after a restart. The URL-based debugger (`LaunchDebuggerUri`) tolerates restarts better.

Set `LaunchFromIde` to let the IDE launch the project like Aspire does for its own projects. Only do this if your
IDE can launch the project (Visual Studio can't for projects outside its solution); `UseWatch` is then ignored under
the IDE.

## Migrating from 1.x
* `AddExternalProject` now returns `IResourceBuilder<DotnetProjectResource>` instead of
  `IResourceBuilder<ExecutableResource>`.
* A relative project path is resolved against the AppHost directory, not the current directory.
* `launchSettings.json` is no longer required.
* `ExcludeKestrelEndpoints` is now honored.
* Your AppHost needs the `Aspire.Hosting.Dotnet` package (it comes with this package as a dependency).

## Limitations and Known Issues
* The `Debug` command can't find the app when it runs as `dotnet App.dll` (no apphost) under `dotnet run` or
  `dotnet watch`.
* The `vsjitdebugger` fallback often fails with "No installed debugger has Just-In-Time debugging enabled", even when
  JIT debugging is enabled. The Visual Studio attach (the default) avoids it.
  If you are using Rider you can set the `LaunchDebuggerCommand/Arguments` to start Rider with the correct process and solution.
* Turning off IDE launch and the `dotnet watch` launch rely on experimental Aspire APIs (`ASPIREEXTENSION001`),
  so this package is pinned to one Aspire major version.
* With `UseWatch`, Aspire's coordinated build runs first and `dotnet watch` builds again.
* Aspire builds external projects together before launching them, so `SolutionGroup` is rarely needed anymore.
  It can still help when projects rebuild themselves at launch (under an IDE, or with `UseWatch`).
* Publishing goes through Aspire's .NET project publishing, which has not been tested with this package.

## Motivation
Often it is not feasible to have a single solution for all projects, especially when working across multiple repositories.
With this package you can temporarily add and remove external projects to your Aspire app host without having to add them to your solution while
still being able to debug them.

## Technical Details
The project is added with Aspire's `AddDotnetProject`. `WithoutIdeLaunch` removes the resource's
`SupportsDebuggingAnnotation`, so Aspire starts the process itself instead of sending it to the IDE.
`WithDotnetWatch` replaces the launch command via `WithLaunchToolArgs`.

The `Debug` command finds the app process from the process id Aspire reports: without an IDE that is the app itself,
under an IDE it is a `dotnet run` process with the app as its child, and under `dotnet watch` the app is further down.
It then looks up the running Visual Studio instances in the Windows running object table (`!VisualStudio.DTE.*`),
picks one as described above and calls its debugger's `Attach()` for that process. Only if none can attach does it
start `vsjitdebugger` with the app's process id.
