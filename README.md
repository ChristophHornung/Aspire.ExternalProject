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

as well as a health check to check if your branch is up to date with the remote.

You can remove the GIT integration or the health check by setting the `WithoutGitSupport` and `WithoutGitHealthCheck` properties to in the resource options.
```csharp
builder.AddExternalProject("resourcename", "path/to/external/project.csproj", options => {
	options.WithoutGitSupport = true;
	options.WithoutGitHealthCheck = true;
});
```

## Limitations and Known Issues
* Currently the `ExcludeLaunchProfile` and `ExcludeKestrelEndpoint` properties on the resource options are ignored.
* Publish operations probably won't work as expected.
* Starting the debugger via the 'Debug' command in the dashboard might not attach correctly.

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