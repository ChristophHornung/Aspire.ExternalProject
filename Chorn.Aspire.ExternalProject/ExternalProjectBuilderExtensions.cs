namespace Chorn.Aspire.ExternalProject;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Extensions for adding external projects to the distributed application.
/// </summary>
public static class ExternalProjectBuilderExtensions
{
	private static readonly JsonSerializerOptions jsonOptions = new()
	{
		AllowTrailingCommas = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <summary>
	/// Adds the csproj file as an external project to the distributed application and tries to wire it up
	/// the same way as AddProject would.
	/// </summary>
	/// <param name="builder">The builder to add the project to.</param>
	/// <param name="name">The name of the external project resource.</param>
	/// <param name="csprojPath">The path to the csproj file.</param>
	/// <param name="configure">A callback to configure the external project resource options.</param>
	/// <returns>The resource builder for the executable resource.</returns>
	public static IResourceBuilder<ExecutableResource> AddExternalProject(this IDistributedApplicationBuilder builder,
		[ResourceName] string name, string csprojPath, Action<ExternalProjectResourceOptions>? configure = null)
	{
		// Make sure we register the PidWatcher service if not already registered.
		builder.Services.TryAddSingleton<SnapshotWatcher>();

		ExternalProjectResourceOptions options = new();
		configure?.Invoke(options);

		if (!File.Exists(csprojPath))
		{
			throw new ArgumentException($"The csproj file '{csprojPath}' was not found.", nameof(csprojPath));
		}

		string folder = Path.GetDirectoryName(csprojPath)!;
		// Check if the folder contains a launchSettings.json file.
		string propertiesFolder = Path.Combine(folder, "Properties");
		if (File.Exists(Path.Combine(propertiesFolder, "launchSettings.json")))
		{
			// Read the contents of the launchSettings.json file.
			string launchSettingsJson = File.ReadAllText(Path.Combine(propertiesFolder, "launchSettings.json"));

			// If a launchSettings.json file is found, add the project to the builder.
			IResourceBuilder<ExecutableResource> execBuilder =
				ExternalProjectBuilderExtensions.AddProject(builder, name, csprojPath, launchSettingsJson, options);
			return execBuilder;
		}
		else
		{
			throw new InvalidOperationException(
				"Properties/launchSettings.json file not found. This is required for now.");
		}
	}

	internal static bool ShouldInjectEndpointEnvironment(this Resource r, EndpointReference e)
	{
		EndpointAnnotation? endpoint = e.GetEndpointAnnotation();

		if (endpoint?.UriScheme is not ("http" or "https")) // Only process http and https endpoints
			//|| endpoint.TargetPortEnvironmentVariable is not null) // Skip if target port env variable was set
		{
			return false;
		}

		return true;
	}

	private static IResourceBuilder<ExecutableResource> AddProject(IDistributedApplicationBuilder builder, string name,
		string csprojFile, string launchSettingsJson, ExternalProjectResourceOptions options)
	{
		string projectFolder = Path.GetDirectoryName(csprojFile)!;
		string projectFileName = Path.GetFileName(csprojFile);

		// Use system.text.json to Parse the launchSettings.json file to get the launch profile. The profile is the first one with a "commandName" of "Project".
		LaunchProfile? launchProfile;
		string? launchProfileName;
		List<string> launchProfileCommandLineArgs;
		try
		{
			LaunchSettings launchSettings =
				JsonSerializer.Deserialize<LaunchSettings>(launchSettingsJson,
					ExternalProjectBuilderExtensions.jsonOptions)!;

			KeyValuePair<string, LaunchProfile> launchProfileKeyPair =
				launchSettings.Profiles.FirstOrDefault(f =>
					options.LaunchProfileName != null && f.Key == options.LaunchProfileName);
			if (launchProfileKeyPair.Value == null)
			{
				launchProfileKeyPair = launchSettings.Profiles.FirstOrDefault(f => f.Value.CommandName == "Project");
			}

			if (launchProfileKeyPair.Key == null)
			{
				throw new InvalidOperationException(
					options.LaunchProfileName != null
						? $"No launch profile found with name '{options.LaunchProfileName}'"
						: "No launch profile found with commandName 'Project'");
			}

			launchProfile = launchProfileKeyPair.Value;
			launchProfileName = launchProfileKeyPair.Key;
			launchProfileCommandLineArgs = ExternalProjectBuilderExtensions.GetLaunchProfileArgs(launchProfile);
		}
		catch (JsonException e)
		{
			throw new InvalidOperationException("Error parsing launchSettings.json", e);
		}

		List<string> launchParameters=["run", "--project", projectFileName, "--no-launch-profile"];
		launchParameters.AddRange(launchProfileCommandLineArgs);

		IResourceBuilder<ExecutableResource> execBuilder = builder.AddExecutable(
				name, "dotnet",
				projectFolder, launchParameters.ToArray())
			.WithExecutableProjectDefaults(launchProfile, launchProfileName)
			.WithCommand("Debug", "Debug",
				ctx => ExternalProjectBuilderExtensions.AttachDebugger(ctx, name, options),
				ctx => ExternalProjectBuilderExtensions.DebugStateChange(ctx, name),
				iconName: "Bug");

		if (!options.SkipGitSupport)
		{
			string healthCheckKey = $"{name}_git_check";

			execBuilder.WithCommand("GitPull", "Git Pull",
				ctx => ExternalProjectBuilderExtensions.GitUpdate(ctx, projectFolder),
				ExternalProjectBuilderExtensions.GitUpdateStateChange, iconName: "BranchRequest");
			if (options.EnableGitHealthCheck)
			{
				execBuilder.WithHealthCheck(healthCheckKey);
				builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(healthCheckKey,
					_ => new GitUpToDateHealthCheck(projectFolder), null, null));
			}
		}

		if (options.SolutionGroup != null)
		{
			execBuilder.WithAnnotation<ExternalProjectSolutionGroupAnnotation>(
				new ExternalProjectSolutionGroupAnnotation(options.SolutionGroup));

			// Find all other resources with the same solution group and add a dependency to them.
			foreach (IResource resource in builder.Resources.Where(r=>r!=execBuilder.Resource))
			{
				if (resource.Annotations.OfType<ExternalProjectSolutionGroupAnnotation>()
				    .Any(a => a.SolutionGroup == options.SolutionGroup))
				{
					IResourceBuilder<IResource> resourceBuilder = builder.CreateResourceBuilder(resource);
					execBuilder
						.WaitFor(resourceBuilder);
				}
			}
		}

		return execBuilder;
	}
	private static List<string> GetLaunchProfileArgs(LaunchProfile? launchProfile)
	{
		List<string> args = [];
		if (launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs))
		{
			List<string> cmdArgs = CommandLineArgsParser.Parse(launchProfile.CommandLineArgs);
			if (cmdArgs.Count > 0)
			{
				args.Add("--");
				args.AddRange(cmdArgs);
			}
		}
		return args;
	}
	private static ResourceCommandState DebugStateChange(UpdateCommandStateContext arg, string name)
	{
		// We seem to be unable to get the current resource snapshot when executing the command, so we need to store the pid when the resource state changes.
		SnapshotWatcher snapshotWatcher = arg.ServiceProvider.GetRequiredService<SnapshotWatcher>();
		snapshotWatcher.Store(name, arg.ResourceSnapshot);

		return arg.ResourceSnapshot.State?.Text == "Running"
			? ResourceCommandState.Enabled
			: ResourceCommandState.Hidden;
	}

	private static ResourceCommandState GitUpdateStateChange(UpdateCommandStateContext arg)
	{
		return arg.ResourceSnapshot.State?.Text != "Running"
			? ResourceCommandState.Enabled
			: ResourceCommandState.Disabled;
	}

	private static async Task<ExecuteCommandResult> AttachDebugger(ExecuteCommandContext arg, string resourceName,
		ExternalProjectResourceOptions externalProjectOptions)
	{
		SnapshotWatcher snapshotWatcher = arg.ServiceProvider.GetRequiredService<SnapshotWatcher>();

		if (externalProjectOptions.LaunchDebuggerUri != null)
		{
			return await ExternalProjectBuilderExtensions.AttachDebuggerViaUrl(arg, resourceName, externalProjectOptions.LaunchDebuggerUri,
				snapshotWatcher);
		}

		return ExternalProjectBuilderExtensions.AttachDebuggerViaVsjit(resourceName, snapshotWatcher,
			externalProjectOptions);
	}

	private static ExecuteCommandResult AttachDebuggerViaVsjit(string resourceName, SnapshotWatcher snapshotWatcher,
		ExternalProjectResourceOptions externalProjectOptions)
	{
		int? pid = snapshotWatcher.GetPid(resourceName);
		if (pid == null)
		{
			return new ExecuteCommandResult() { Success = false, ErrorMessage = "No pid found" };
		}

		try
		{
			// The pid is the pid of dotnet.exe, we need to attach the debugger to the first child process that is not a dotnet process.
			Process? child = Process.GetProcesses()
				.FirstOrDefault(p => p.ProcessName != "dotnet" && p.GetParent()?.Id == pid);

			if (child == null)
			{
				return new ExecuteCommandResult()
					{ Success = false, ErrorMessage = "No child process found for dotnet.exe" };
			}

			// Attach the debugger via vsjitdebugger
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = externalProjectOptions.LaunchDebuggerCommand,
				Arguments = externalProjectOptions.LaunchDebuggerCommandArguments.Replace("<pid>", child.Id.ToString()),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};


			Process.Start(startInfo);

			// We don't wait for the process to exit, as the debugger will keep running.
			return new ExecuteCommandResult() { Success = true };
		}
		catch (Exception e)
		{
			return new ExecuteCommandResult() { Success = false, ErrorMessage = e.Message };
		}
	}

	private static async Task<ExecuteCommandResult> AttachDebuggerViaUrl(ExecuteCommandContext arg, string resourceName,
		string launchDebuggerUri, SnapshotWatcher snapshotWatcher)
	{
		// If the launch debugger uri is set we call that instead of trying to attach the debugger via vsjitdebugger.
		string? baseUrl = snapshotWatcher.GetHttpsOrHttpBaseUrl(resourceName);
		if (baseUrl == null)
		{
			return new ExecuteCommandResult() { Success = false, ErrorMessage = "No base url found" };
		}

		string url = $"{baseUrl}{launchDebuggerUri}";
		// TODO: Should we register a HttpClient as a service instead of creating a new one each time?
		// No HttpClientFactory or HttpClient is registered by default, so we create a new one.
		HttpClient httpClient = new HttpClient();

		// POST to the launch debugger uri.
		try
		{
			HttpResponseMessage result = await httpClient.PostAsync(url, new StringContent(""));

			if (!result.IsSuccessStatusCode)
			{
				return new ExecuteCommandResult() { Success = false, ErrorMessage = result.ReasonPhrase };
			}

			return new ExecuteCommandResult() { Success = true };
		}
		catch (Exception e)
		{
			return new ExecuteCommandResult() { Success = false, ErrorMessage = e.Message };
		}
	}

	private static async Task<ExecuteCommandResult> GitUpdate(ExecuteCommandContext arg, string projectFolder)
	{
		// Call git pull in the project folder.
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = "git",
			Arguments = "pull",
			WorkingDirectory = projectFolder,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		Process process = Process.Start(startInfo)!;
		await process.WaitForExitAsync();

		// Read the output and error streams.
		string output = await process.StandardOutput.ReadToEndAsync();
		string error = await process.StandardError.ReadToEndAsync();

		ExecuteCommandResult commandResult = new ExecuteCommandResult
		{
			Success = process.ExitCode == 0,
			ErrorMessage = error
		};

		return commandResult;
	}

	private static IResourceBuilder<ExecutableResource> WithExecutableProjectDefaults(
		this IResourceBuilder<ExecutableResource> builder, LaunchProfile launchProfile, string launchProfileName)
	{
		// Taken mainly from the WithProjectDefaults of Aspire base.

		// We only want to turn these on for .NET projects, ConfigureOtlpEnvironment works for any resource type that
		// implements IDistributedApplicationResourceWithEnvironment.
		builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EXCEPTION_LOG_ATTRIBUTES", "true");
		builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_OTLP_EMIT_EVENT_LOG_ATTRIBUTES", "true");
		// .NET SDK has experimental support for retries. Enable with env var.
		// https://github.com/open-telemetry/opentelemetry-dotnet/pull/5495
		// Remove once retry feature in opentelemetry-dotnet is enabled by default.
		builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY", "in_memory");

		// OTEL settings that are used to improve local development experience.
		if (builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
		    builder.ApplicationBuilder.Environment.IsDevelopment())
		{
			// Disable URL query redaction, e.g. ?myvalue=Redacted
			builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true");
			builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "true");
		}

		builder.WithOtlpExporter();

		ExecutableResource projectResource = builder.Resource;

		// Get all the endpoints from the Kestrel configuration
		IConfiguration config = ExternalProjectBuilderExtensions.GetConfiguration(projectResource);
		IEnumerable<IConfigurationSection> kestrelEndpoints = config.GetSection("Kestrel:Endpoints").GetChildren();

		// Helper to change the transport to http2 if needed
		bool isHttp2ConfiguredInKestrelEndpointDefaults =
			config["Kestrel:EndpointDefaults:Protocols"] == nameof(HttpProtocols.Http2);
		var adjustTransport = (EndpointAnnotation e, string? bindingLevelProtocols = null) =>
		{
			if (bindingLevelProtocols != null)
			{
				// If the Kestrel endpoint has an explicit protocol, use that and ignore any EndpointDefaults
				e.Transport = bindingLevelProtocols == nameof(HttpProtocols.Http2) ? "http2" : e.Transport;
			}
			else if (isHttp2ConfiguredInKestrelEndpointDefaults)
			{
				// Fall back to honoring Http2 specified at EndpointDefaults level
				e.Transport = "http2";
			}
		};

		if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
		{
			// We don't need to set ASPNETCORE_URLS if we have Kestrel endpoints configured
			// as Kestrel will get everything it needs from the config.
			//if (!kestrelEndpointsByScheme.Any())
			{
				builder.SetAspNetCoreUrls();
			}

			// If we had found any Kestrel endpoints, we ignore the launch profile endpoints,
			// to match the Kestrel runtime behavior.
			string[] urlsFromApplicationUrl =
				launchProfile.ApplicationUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
			Dictionary<string, int> endpointCountByScheme = [];
			foreach (string url in urlsFromApplicationUrl)
			{
				BindingAddress bindingAddress = BindingAddress.Parse(url);

				// Keep track of how many endpoints we have for each scheme
				endpointCountByScheme.TryGetValue(bindingAddress.Scheme, out int count);
				endpointCountByScheme[bindingAddress.Scheme] = count + 1;

				// If we have multiple for the same scheme, we differentiate them by appending a number.
				// We only do this starting with the second endpoint, so that the first stays just http/https.
				// This allows us to keep the same behavior as "dotnet run".
				// Also, note that we only do this in Run mode, as in Publish mode those extra endpoints
				// with generic names would not be easily usable.
				string endpointName = bindingAddress.Scheme;
				if (endpointCountByScheme[bindingAddress.Scheme] > 1)
				{
					endpointName += endpointCountByScheme[bindingAddress.Scheme];
				}

				builder.WithEndpoint(endpointName, e =>
					{
						e.Port = bindingAddress.Port;
						e.TargetHost = bindingAddress.Host;
						e.UriScheme = bindingAddress.Scheme;
						adjustTransport(e);
					},
					createIfNotExists: true);
			}

			builder.WithEnvironment(context =>
			{
				// Populate DOTNET_LAUNCH_PROFILE environment variable for consistency with "dotnet run" and "dotnet watch".
				context.EnvironmentVariables.TryAdd("DOTNET_LAUNCH_PROFILE", launchProfileName);

				foreach (KeyValuePair<string, string> envVar in launchProfile.EnvironmentVariables)
				{
					string value = Environment.ExpandEnvironmentVariables(envVar.Value);
					context.EnvironmentVariables.TryAdd(envVar.Key, value);
				}
			});
		}

		return builder;
	}

	private static void SetAspNetCoreUrls(this IResourceBuilder<ExecutableResource> builder)
	{
		builder.WithEnvironment(context =>
		{
			if (context.EnvironmentVariables.ContainsKey("ASPNETCORE_URLS"))
			{
				// If the user has already set ASPNETCORE_URLS, we don't want to override it.
				return;
			}

			ReferenceExpressionBuilder aspnetCoreUrls = new ReferenceExpressionBuilder();

			bool processedHttpsPort = false;
			bool first = true;

			// Turn http and https endpoints into a single ASPNETCORE_URLS environment variable.
			foreach (EndpointReference e in builder.Resource.GetEndpoints()
				         .Where(builder.Resource.ShouldInjectEndpointEnvironment))
			{
				if (!first)
				{
					aspnetCoreUrls.AppendLiteral(";");
				}

				if (!processedHttpsPort && e.GetEndpointAnnotation().UriScheme == "https")
				{
					// Add the environment variable for the HTTPS port if we have an HTTPS service. This will make sure the
					// HTTPS redirection middleware avoids redirecting to the internal port.
					context.EnvironmentVariables["ASPNETCORE_HTTPS_PORT"] = e.Property(EndpointProperty.Port);

					processedHttpsPort = true;
				}

				// If the endpoint is proxied, we will use localhost as the target host since DCP will be forwarding the traffic
				string targetHost = e.GetEndpointAnnotation().IsProxied
					? "localhost"
					: e.GetEndpointAnnotation().TargetHost;

				aspnetCoreUrls.Append(
					$"{e.Property(EndpointProperty.Scheme)}://{targetHost}:{e.Property(EndpointProperty.TargetPort)}");
				first = false;
			}

			if (!aspnetCoreUrls.IsEmpty)
			{
				// Combine into a single expression
				context.EnvironmentVariables["ASPNETCORE_URLS"] = aspnetCoreUrls.Build();
			}
		});
	}

	private static EndpointAnnotation? GetEndpointAnnotation(this EndpointReference e) =>
		e.Resource.Annotations.OfType<EndpointAnnotation>()
			.SingleOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.Name, e.EndpointName));

	private static IConfiguration GetConfiguration(ExecutableResource projectResource)
	{
		// Was: var projectMetadata = projectResource.GetProjectMetadata();

		string projectDirectoryPath = Path.GetDirectoryName(projectResource.WorkingDirectory)!;
		string appSettingsPath = Path.Combine(projectDirectoryPath, "appsettings.json");
		string? env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
		              Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
		string appSettingsEnvironmentPath = Path.Combine(projectDirectoryPath, $"appsettings.{env}.json");

		ConfigurationBuilder configBuilder = new ConfigurationBuilder();
		configBuilder.AddJsonFile(appSettingsPath, optional: true);
		configBuilder.AddJsonFile(appSettingsEnvironmentPath, optional: true);
		return configBuilder.Build();
	}
}