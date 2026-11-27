using Chorn.Aspire.ExternalProject;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// The external projects are not part of this solution. Relative paths are resolved against the AppHost directory.

// The all-in-one way: AddExternalProject calls Aspire's AddDotnetProject and adds the extras from the options.
builder.AddExternalProject("WeatherApi", "../External/WeatherApi/WeatherApi.csproj", cfg =>
{
	// Skipping git support will not wire the 'Git pull' command.
	cfg.SkipGitSupport = false;

	// You can enable the health check for the external project to get an indication when there are git changes to pull.
	cfg.EnableGitHealthCheck = false;

	// The external project has an endpoint to start the debugger.
	cfg.LaunchDebuggerUri = "/debug";

	cfg.SolutionGroup = "WeatherSolution";
});

// The building-block way: start from AddDotnetProject and pick the extras you need.
builder.AddDotnetProject("WeatherPredictionApi", "../External/Weather.Prediction.Api/Weather.Prediction.Api.csproj",
		o => o.LaunchProfileName = "PredictionApiProduction")
	// Visual Studio cannot launch projects outside its solution, so let Aspire start it.
	.WithoutIdeLaunch()
	// Recompile/hot reload on source changes.
	.WithDotnetWatch()
	.WithDebuggerAttachCommand()
	// Under watch a pull is picked up live, so allow it while running.
	.WithGitPullCommand(allowWhileRunning: true)
	.WithSolutionGroup("WeatherSolution");

builder.Build().Run();
