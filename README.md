# Aspire.ExternalProject
Aspire.ExternalProject is a .NET Aspire package to add a .NET project that is external to the current solution.

[![NuGet version (Chorn.Aspire.ExternalProject)](https://img.shields.io/nuget/v/Chorn.Aspire.ExternalProject.svg?style=flat-square)](https://www.nuget.org/packages/Chorn.Aspire.ExternalProject/)


## Usage
Install the package from NuGet:
```
dotnet add package Chorn.Aspire.ExternalProject
```

You can now add an external project via its csproj file in your app host:
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj");
```

This will add the project as an executable resource and wire up all the magic that the normal Aspire `AddProject` method does.
In addition it will use 'dotnet run' to start the external project so automatic build and run should work as expected.

To debug the external project, you can use the `Debug` command in the dashboard or just attach the debugger manually
from your IDE.

It will also add two commands to the dashboard:

* 'Debug' - Starts the debugger for the external project by starting the manual 'attach to VisualStudio' process.
* 'Git Pull" - Pulls the latest changes from the git repository of the external project.

You can remove the GIT integration by setting the `SkipGitSupport` in the resource options.
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.SkipGitSupport = true;
});
```

You can add a GIT up-to-date health check to the external project by setting the `EnableGitHealthCheck` in the resource options.
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.EnableGitHealthCheck = true;
});
```

By default we use `vsjitdebugger` to attach the debugger to the external project. This does not seem to work reliably, as a viable alternative
you can set the `DebuggerUri` in the resource options to call an endpoint in your external project to start the debugger via
`Debugger.Launch()`. (See the sample for the external project code)
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.LaunchDebuggerUri = "/debugger";
});
```

## Limitations and Known Issues
* Currently the `ExcludeLaunchProfile` and `ExcludeKestrelEndpoint` properties on the resource options are ignored.
* Publish operations probably won't work as expected.
* Starting the debugger via the default 'Debug' command in the dashboard might not attach correctly via vsjitdebugger.
* Running multiple external projects at the same time might lead to build problems if they have common dependencies.
If you run into any issues you can set the `options.SolutionGroup` property, which will make sure the projects in the same
solution group wait for each other, make sure to have at least one health check that indicates
the project started. Or try an explicit start for the external projects to avoid this issue. (Via `WithExplicitStart()`)

## Motivation
Often it is not feasible to have a single solution for all projects, especially when working across multiple repositories.
With this package you can temporarily add and remove external projects to your Aspire app host without having to add them to your solution while
still being able to debug them.

## Technical Details
We bascially replicate the work done in the `AddProject` method, but with a few tweaks to make it work with external projects.

Starting the debugger is done by starting the `vsjitdebugger` process with the pid of the external project.

## Future Work
* Add support for the `ExcludeLaunchProfile` and `ExcludeKestrelEndpoint` properties.
* Replicate the publish functionality if possible.
* Maybe switch to a custom resource type to make the external project more visible in the dashboard.