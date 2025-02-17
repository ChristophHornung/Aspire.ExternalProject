using Chorn.Aspire.ExternalProject;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// We search for the WeatherApi.csproj by going up the directory tree, this is only
// to make the sample work regardless of where you checked it out.
// In a real application you would just use the path to the project file.
string path = Path.Combine(Directory.GetCurrentDirectory(), "..", "External", "WeatherApi", "WeatherApi.csproj");

// Build the canonical path to the project file.
path = Path.GetFullPath(path);
Console.WriteLine($"Path to WeatherApi project: {path}");

// Add the external project to the builder. Note that the project is not included in this solution, it is built and run when the application is started.
builder.AddExternalProject("WeatherApi", path, cfg =>
{
	// Skipping git support will not wire the 'Git pull' command.
	cfg.SkipGitSupport = false;

	// You can enable the health check for the external project to get an indication when there are git changes to pull.
	cfg.EnableGitHealthCheck = false;

	// The external project has an endpoint to start the debugger.
	cfg.LaunchDebuggerUri = "/debug";
});

builder.Build().Run();