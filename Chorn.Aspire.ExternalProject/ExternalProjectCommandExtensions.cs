using DotnetProjectResource = Aspire.Hosting.Dotnet.DotnetProjectResource;

namespace Chorn.Aspire.ExternalProject;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public static partial class ExternalProjectBuilderExtensions
{
	private static ResourceCommandState DebugStateChange(UpdateCommandStateContext arg)
	{
		return arg.ResourceSnapshot.State?.Text == "Running"
			? ResourceCommandState.Enabled
			: ResourceCommandState.Hidden;
	}

	private static ResourceCommandState GitUpdateStateChange(UpdateCommandStateContext arg, bool allowWhileRunning)
	{
		// Under 'dotnet watch' a pull is picked up while the app runs (recompile/hot reload), so allow
		// it in any state. Otherwise only allow it when not running, since the app would need a restart
		// to pick up the changes.
		if (allowWhileRunning)
		{
			return ResourceCommandState.Enabled;
		}

		return arg.ResourceSnapshot.State?.Text != "Running"
			? ResourceCommandState.Enabled
			: ResourceCommandState.Disabled;
	}

	private static async Task<ExecuteCommandResult> AttachDebugger(ExecuteCommandContext arg, string resourceName,
		string? projectPath, DebuggerAttachOptions options)
	{
		if (options.LaunchDebuggerUri != null)
		{
			return await ExternalProjectBuilderExtensions.AttachDebuggerViaUrl(arg, resourceName,
				options.LaunchDebuggerUri);
		}

		int? pid = ExternalProjectBuilderExtensions.GetPid(arg.Services, resourceName);
		if (pid == null)
		{
			return new ExecuteCommandResult { Success = false, Message = "No pid found" };
		}

		int? appPid;
		try
		{
			appPid = ExternalProjectBuilderExtensions.FindAppProcess(pid.Value);
		}
		catch (Exception e)
		{
			return new ExecuteCommandResult { Success = false, Message = e.Message };
		}

		if (appPid == null)
		{
			return new ExecuteCommandResult
			{
				Success = false,
				Message = "The application process could not be found. If the app runs as 'dotnet <app>.dll' " +
				          "under 'dotnet run' or 'dotnet watch', use LaunchDebuggerUri instead."
			};
		}

		if (options.AttachMode == DebuggerAttachMode.VisualStudio && OperatingSystem.IsWindows())
		{
			try
			{
				string? attached = await VisualStudioAttacher.TryAttachAsync(appPid.Value, projectPath);
				if (attached != null)
				{
					return new ExecuteCommandResult { Success = true, Message = attached };
				}
			}
			catch (Exception)
			{
				// Fall back to the debugger command below.
			}
		}

		return ExternalProjectBuilderExtensions.AttachDebuggerViaCommand(appPid.Value, options);
	}

	private static ExecuteCommandResult AttachDebuggerViaCommand(int appPid, DebuggerAttachOptions options)
	{
		try
		{
			ProcessStartInfo startInfo = new()
			{
				FileName = options.LaunchDebuggerCommand,
				Arguments = options.LaunchDebuggerCommandArguments.Replace("<pid>", appPid.ToString()),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			Process.Start(startInfo);

			// We don't wait for the process to exit, as the debugger will keep running.
			return new ExecuteCommandResult { Success = true };
		}
		catch (Exception e)
		{
			return new ExecuteCommandResult { Success = false, Message = e.Message };
		}
	}

	/// <summary>
	/// Finds the application process for the process Aspire tracks for the resource.
	/// </summary>
	/// <remarks>
	/// What Aspire tracks depends on how the project was launched:
	/// <list type="bullet">
	/// <item>Without an IDE, Aspire builds first and starts the built app directly, so the tracked process is
	/// the app (<c>App.exe</c>, or <c>dotnet App.dll</c> without an apphost).</item>
	/// <item>Under an IDE (with IDE launch turned off), Aspire runs <c>dotnet run</c>; the app is its child.</item>
	/// <item>With <c>dotnet watch</c>, the app is several levels below the tracked process.</item>
	/// </list>
	/// The <c>dotnet run</c> / <c>dotnet watch</c> wrappers are always <c>dotnet</c> processes, so a tracked
	/// process with another name is the app itself.
	/// </remarks>
	private static int? FindAppProcess(int trackedPid)
	{
		using (Process tracked = Process.GetProcessById(trackedPid))
		{
			if (!ExternalProjectBuilderExtensions.IsDotnetHost(tracked))
			{
				return trackedPid;
			}
		}

		Process[] all = Process.GetProcesses();
		try
		{
			// Build a parent-pid -> children lookup (one parent query per process).
			ILookup<int, Process> childrenByParent = all.ToLookup(p => p.GetParent()?.Id ?? -1);

			// A 'dotnet' process without children (other than the console host) is 'dotnet App.dll' itself.
			if (!childrenByParent[trackedPid].Any(c => !ExternalProjectBuilderExtensions.IsConsoleHost(c)))
			{
				return trackedPid;
			}

			// Otherwise it is a 'dotnet run'/'dotnet watch' wrapper: breadth-first search for the shallowest
			// descendant that is not a dotnet process.
			Queue<int> queue = new();
			queue.Enqueue(trackedPid);
			HashSet<int> seen = [trackedPid];
			while (queue.Count > 0)
			{
				int parentId = queue.Dequeue();
				foreach (Process candidate in childrenByParent[parentId])
				{
					// The Windows console host helper is a non-dotnet descendant but never the app.
					if (ExternalProjectBuilderExtensions.IsConsoleHost(candidate))
					{
						continue;
					}

					if (!ExternalProjectBuilderExtensions.IsDotnetHost(candidate))
					{
						return candidate.Id;
					}

					if (seen.Add(candidate.Id))
					{
						queue.Enqueue(candidate.Id);
					}
				}
			}

			return null;
		}
		finally
		{
			// Release the native handles opened by Process.GetProcesses().
			foreach (Process p in all)
			{
				p.Dispose();
			}
		}
	}

	private static bool IsDotnetHost(Process process) =>
		string.Equals(process.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase);

	private static bool IsConsoleHost(Process process) =>
		string.Equals(process.ProcessName, "conhost", StringComparison.OrdinalIgnoreCase);

	private static async Task<ExecuteCommandResult> AttachDebuggerViaUrl(ExecuteCommandContext arg, string resourceName,
		string launchDebuggerUri)
	{
		string? baseUrl = ExternalProjectBuilderExtensions.GetHttpsOrHttpBaseUrl(arg.Services, resourceName);
		if (baseUrl == null)
		{
			return new ExecuteCommandResult { Success = false, Message = "No base url found" };
		}

		string url = $"{baseUrl}{launchDebuggerUri}";

		// No HttpClientFactory or HttpClient is registered by default, so we create a new one.
		using HttpClient httpClient = new();

		try
		{
			HttpResponseMessage result = await httpClient.PostAsync(url, new StringContent(""));

			if (!result.IsSuccessStatusCode)
			{
				return new ExecuteCommandResult { Success = false, Message = result.ReasonPhrase };
			}

			return new ExecuteCommandResult { Success = true };
		}
		catch (Exception e)
		{
			return new ExecuteCommandResult { Success = false, Message = e.Message };
		}
	}

	private static int? GetPid(IServiceProvider serviceProvider, string resourceName)
	{
		ResourceNotificationService notificationService =
			serviceProvider.GetRequiredService<ResourceNotificationService>();

		if (notificationService.TryGetCurrentState(resourceName, out ResourceEvent? resourceEvent))
		{
			ResourcePropertySnapshot? pidProperty =
				resourceEvent.Snapshot.Properties.FirstOrDefault(p => p.Name == "executable.pid");
			if (pidProperty != null && pidProperty.Value is int pid && pid != 0)
			{
				return pid;
			}
		}

		return null;
	}

	private static string? GetHttpsOrHttpBaseUrl(IServiceProvider serviceProvider, string resourceName)
	{
		ResourceNotificationService notificationService =
			serviceProvider.GetRequiredService<ResourceNotificationService>();

		if (notificationService.TryGetCurrentState(resourceName, out ResourceEvent? resourceEvent))
		{
			UrlSnapshot? baseUrl =
				resourceEvent.Snapshot.Urls.FirstOrDefault(p => p.Name == "https target port") ??
				resourceEvent.Snapshot.Urls.FirstOrDefault(p => p.Name == "https") ??
				resourceEvent.Snapshot.Urls.FirstOrDefault(p => p.Name == "http target port") ??
				resourceEvent.Snapshot.Urls.FirstOrDefault(p => p.Name == "http");
			return baseUrl?.Url;
		}

		return null;
	}

	private static async Task<ExecuteCommandResult> GitUpdate(string projectFolder)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = "git",
			Arguments = "pull",
			WorkingDirectory = projectFolder,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		using Process process = Process.Start(startInfo)!;

		// Read both streams while waiting, so a full pipe buffer cannot block git.
		Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> errorTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		string output = await outputTask;
		string error = await errorTask;

		bool success = process.ExitCode == 0;

		// Surface the git output in the dashboard: a short message plus the full text
		// (stdout on success, stderr on failure) as structured command result data.
		string trimmedOutput = output.Trim();
		string trimmedError = error.Trim();
		string message = success
			? (trimmedOutput.Length > 0 ? trimmedOutput : "git pull completed.")
			: (trimmedError.Length > 0 ? trimmedError : "git pull failed.");

		return new ExecuteCommandResult
		{
			Success = success,
			Message = message,
			Data = new CommandResultData
			{
				Value = success ? output : (trimmedError.Length > 0 ? error : output),
				Format = CommandResultFormat.Text
			}
		};
	}

	/// <param name="builder">The executable resource builder.</param>
	/// <typeparam name="T">The executable resource type, e.g. a .NET project resource.</typeparam>
	extension<T>(IResourceBuilder<T> builder) where T : ExecutableResource
	{
		/// <summary>
		/// Adds a <c>Debug</c> dashboard command that attaches a debugger to the running application.
		/// </summary>
		/// <remarks>
		/// By default the command starts <c>vsjitdebugger -p &lt;pid&gt;</c> for the application process. If
		/// <see cref="DebuggerAttachOptions.LaunchDebuggerUri"/> is set, it POSTs to that path on the resource
		/// instead, and the application is expected to call <c>Debugger.Launch()</c> there.
		/// </remarks>
		/// <param name="configure">A callback to configure how the debugger is attached.</param>
		/// <returns>The same builder for chaining.</returns>
		public IResourceBuilder<T> WithDebuggerAttachCommand(Action<DebuggerAttachOptions>? configure = null)
		{
			DebuggerAttachOptions options = new();
			configure?.Invoke(options);
			string resourceName = builder.Resource.Name;

			// Used to find the Visual Studio that has the project's solution open.
			string? projectPath = builder.Resource is DotnetProjectResource project
				? ExternalProjectBuilderExtensions.GetProjectPath(project)
				: null;

			return builder.WithCommand("Debug", "Debug",
				ctx => ExternalProjectBuilderExtensions.AttachDebugger(ctx, resourceName, projectPath, options),
				new CommandOptions
				{
					UpdateState = ExternalProjectBuilderExtensions.DebugStateChange,
					IconName = "Bug",
					// Attaching a debugger is an interactive, local action, so keep it out of
					// automation/MCP clients and only expose it in the dashboard.
					Visibility = ResourceCommandVisibility.UI
				});
		}

		/// <summary>
		/// Adds a <c>Git Pull</c> dashboard command that runs <c>git pull</c> in the project folder.
		/// </summary>
		/// <param name="allowWhileRunning">
		/// <c>true</c> to allow the pull while the resource runs, e.g. under <c>dotnet watch</c> where the change
		/// is picked up live. By default the command is only enabled while the resource is not running.
		/// </param>
		/// <returns>The same builder for chaining.</returns>
		public IResourceBuilder<T> WithGitPullCommand(bool allowWhileRunning = false)
		{
			string projectFolder = ExternalProjectBuilderExtensions.GetProjectFolder(builder.Resource);
			string resourceName = builder.Resource.Name;

			return builder.WithCommand("GitPull", "Git Pull",
				_ => ExternalProjectBuilderExtensions.GitUpdate(projectFolder),
				new CommandOptions
				{
					UpdateState = ctx => ExternalProjectBuilderExtensions.GitUpdateStateChange(ctx, allowWhileRunning),
					IconName = "BranchRequest",
					// git pull mutates the external project's working tree, so confirm first.
					ConfirmationMessage = $"Pull the latest changes for '{resourceName}' from git?"
				});
		}

		/// <summary>
		/// Adds a health check that reports the resource as unhealthy while its git branch is behind the remote.
		/// </summary>
		/// <remarks>The check runs <c>git fetch</c> and <c>git status</c> in the project folder.</remarks>
		/// <returns>The same builder for chaining.</returns>
		public IResourceBuilder<T> WithGitUpToDateHealthCheck()
		{
			string projectFolder = ExternalProjectBuilderExtensions.GetProjectFolder(builder.Resource);
			string healthCheckKey = $"{builder.Resource.Name}_git_check";

			builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(healthCheckKey,
				_ => new GitUpToDateHealthCheck(projectFolder), null, null));
			return builder.WithHealthCheck(healthCheckKey);
		}
	}
}