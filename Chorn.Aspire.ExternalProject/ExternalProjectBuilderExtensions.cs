namespace Chorn.Aspire.ExternalProject;

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
		ExternalProjectResourceOptions options = new();
		configure?.Invoke(options);

		string folder = Path.GetDirectoryName(csprojPath)!;
		// Check if the folder contains a launchSettings.json file.
		string propertiesFolder = Path.Combine(folder, "Properties");
		if (File.Exists(Path.Combine(propertiesFolder, "launchSettings.json")))
		{
			// Read the contents of the launchSettings.json file.
			string launchSettingsJson = File.ReadAllText(Path.Combine(propertiesFolder, "launchSettings.json"));

			// If a launchSettings.json file is found, add the project to the builder.
			return ExternalProjectBuilderExtensions.AddProject(builder, name, csprojPath, launchSettingsJson, options);
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
		try
		{
			LaunchSettings launchSettings =
				JsonSerializer.Deserialize<LaunchSettings>(launchSettingsJson,
					ExternalProjectBuilderExtensions.jsonOptions)!;

			KeyValuePair<string, LaunchProfile> launchProfileKeyPair =
				launchSettings.Profiles.FirstOrDefault(f =>
					(options.LaunchProfileName != null && f.Key == options.LaunchProfileName)
					|| f.Value.CommandName == "Project");
			if (launchProfileKeyPair.Key == null)
			{
				throw new InvalidOperationException(
					options.LaunchProfileName != null
						? $"No launch profile found with name '{options.LaunchProfileName}'"
						: "No launch profile found with commandName 'Project'");
			}

			launchProfile = launchProfileKeyPair.Value;
			launchProfileName = launchProfileKeyPair.Key;
		}
		catch (JsonException e)
		{
			throw new InvalidOperationException("Error parsing launchSettings.json", e);
		}

		IResourceBuilder<ExecutableResource> execBuilder = builder.AddExecutable(
				name, "dotnet",
				projectFolder,
				"run", "--project", projectFileName, "--no-launch-profile")
			.WithExecutableProjectDefaults(launchProfile, launchProfileName)
			.WithCommand("Debug", "Debug", ExternalProjectBuilderExtensions.AttachDebuger,
				ExternalProjectBuilderExtensions.DebugStateChange,
				iconName: "Bug");

		if (!options.WithoutGitSupport)
		{
			string healthCheckKey = $"{name}_git_check";

			execBuilder.WithCommand("GitPull", "Git Pull",
				ctx => ExternalProjectBuilderExtensions.GitUpdate(ctx, projectFolder),
				ExternalProjectBuilderExtensions.GitUpdateStateChange, iconName: "BranchRequest");
			if (!options.WithoutGitHealthCheck)
			{
				execBuilder.WithHealthCheck(healthCheckKey);
				builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(healthCheckKey,
					_ => new GitUpToDateHealthCheck(projectFolder), null, null));
			}
		}

		return execBuilder;
	}

	private static ResourceCommandState DebugStateChange(UpdateCommandStateContext arg)
	{
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

	private static Task<ExecuteCommandResult> AttachDebuger(ExecuteCommandContext arg)
	{
		try
		{
			// Get the Aspire.Hosting.Dcp.IDcpExecutor type via reflection (is internal) from all currently loaded assemblies.
			Assembly hostinAssembly = Assembly.GetAssembly(typeof(IDistributedApplicationBuilder))!;
			Type dcpExecutorType = hostinAssembly.GetTypes().FirstOrDefault(t => t.Name == "ApplicationExecutor")!;
			Type executableType = hostinAssembly.GetTypes().FirstOrDefault(t => t.Name == "Executable")!;

			// Via reflection get the _executablesMap private field
			FieldInfo executablesMapField =
				dcpExecutorType.GetField("_executablesMap", BindingFlags.Instance | BindingFlags.NonPublic)!;

			object dcpExecutor = arg.ServiceProvider.GetRequiredService(dcpExecutorType);

			// Get the _executablesMap field value
			IDictionary executablesMap = (IDictionary)executablesMapField.GetValue(dcpExecutor)!;

			// Get the ExecutableResource
			object o = executablesMap[arg.ResourceName];

			// o is an executableType we get the "Status" property
			PropertyInfo statusProperty = executableType.GetProperty("Status")!;
			object status = statusProperty.GetValue(o)!;

			// Serialize the status object to JSON and get the "pid" property
			string json = JsonSerializer.Serialize(status);
			JsonElement jsonElement = JsonSerializer.Deserialize<JsonElement>(json)!;
			int pid = jsonElement.GetProperty("pid").GetInt32()!;

			// The pid is the pid of dotnet.exe, we need to attach the debugger to the first child process that is not a dotnet process.
			Process? child = Process.GetProcesses()
				.FirstOrDefault(p => p.ProcessName != "dotnet" && p.GetParent()?.Id == pid);

			if (child == null)
			{
				return Task.FromResult(new ExecuteCommandResult()
					{ Success = false, ErrorMessage = "No child process found for dotnet.exe" });
			}

			// Attach the debugger via vsjitdebugger
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = "vsjitdebugger",
				Arguments = $"-p {child.Id}",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};


			Process.Start(startInfo);

			// We don't wait for the process to exit, as the debugger will keep running.
			return Task.FromResult(new ExecuteCommandResult() { Success = true });
		}
		catch (Exception e)
		{
			return Task.FromResult(new ExecuteCommandResult() { Success = false, ErrorMessage = e.Message });
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