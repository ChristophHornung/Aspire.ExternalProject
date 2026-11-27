using DotnetProjectResource = Aspire.Hosting.Dotnet.DotnetProjectResource;

namespace Chorn.Aspire.ExternalProject;

/// <summary>
/// Extensions for adding external projects to the distributed application.
/// </summary>
/// <remarks>
/// Since 2.0 the project is added with Aspire's own <c>AddDotnetProject</c>, which handles the launch profile,
/// Kestrel endpoints, OpenTelemetry and the coordinated build. This library adds what is specific to projects
/// that live outside the AppHost's solution: no IDE launch (Visual Studio cannot launch them), an optional
/// <c>dotnet watch</c> launch, a debugger attach command and git support.
/// </remarks>
public static partial class ExternalProjectBuilderExtensions
{
	private static string GetProjectPath(DotnetProjectResource resource)
	{
		IProjectMetadata metadata = resource.GetProjectMetadata();
		return metadata.ProjectPath;
	}

	/// <summary>
	/// Gets the folder git commands run in: the project folder for .NET projects, otherwise the working directory.
	/// </summary>
	private static string GetProjectFolder(ExecutableResource resource)
	{
		return resource is DotnetProjectResource project
			? Path.GetDirectoryName(ExternalProjectBuilderExtensions.GetProjectPath(project)) ??
			  resource.WorkingDirectory
			: resource.WorkingDirectory;
	}

	/// <param name="builder">The builder to add the project to.</param>
	extension(IDistributedApplicationBuilder builder)
	{
		/// <summary>
		/// Adds a .NET project that lives outside the AppHost's solution (e.g. in another repository) to the
		/// distributed application.
		/// </summary>
		/// <remarks>
		/// This calls Aspire's <c>AddDotnetProject</c> and then applies the extras configured in
		/// <paramref name="configure"/>. A relative <paramref name="csprojPath"/> is resolved against the AppHost
		/// directory, the same way <c>AddDotnetProject</c> resolves it.
		/// </remarks>
		/// <param name="name">The name of the external project resource.</param>
		/// <param name="csprojPath">The path to the project file, or to a directory containing exactly one project.</param>
		/// <param name="configure">A callback to configure the external project resource options.</param>
		/// <returns>The resource builder for the .NET project resource.</returns>
		public IResourceBuilder<DotnetProjectResource> AddExternalProject([ResourceName] string name, string csprojPath,
			Action<ExternalProjectResourceOptions>? configure = null)
		{
			ArgumentNullException.ThrowIfNull(name);
			ArgumentNullException.ThrowIfNull(csprojPath);

			ExternalProjectResourceOptions options = new();
			configure?.Invoke(options);

			string fullPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, csprojPath));
			if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
			{
				throw new ArgumentException($"The project '{fullPath}' was not found.", nameof(csprojPath));
			}

			IResourceBuilder<DotnetProjectResource> project = builder.AddDotnetProject(name, fullPath, o =>
			{
				o.LaunchProfileName = options.LaunchProfileName;
				o.ExcludeLaunchProfile = options.ExcludeLaunchProfile;
				o.ExcludeKestrelEndpoints = options.ExcludeKestrelEndpoints;
			});

			if (!options.LaunchFromIde)
			{
				project.WithoutIdeLaunch();
			}

			if (options.UseWatch)
			{
				project.WithDotnetWatch();
			}

			project.WithDebuggerAttachCommand(d =>
			{
				d.LaunchDebuggerUri = options.LaunchDebuggerUri;
				d.AttachMode = options.DebuggerAttachMode;
				d.LaunchDebuggerCommand = options.LaunchDebuggerCommand;
				d.LaunchDebuggerCommandArguments = options.LaunchDebuggerCommandArguments;
			});

			if (!options.SkipGitSupport)
			{
				project.WithGitPullCommand(allowWhileRunning: options.UseWatch);
				if (options.EnableGitHealthCheck)
				{
					project.WithGitUpToDateHealthCheck();
				}
			}

			if (options.SolutionGroup != null)
			{
				project.WithSolutionGroup(options.SolutionGroup);
			}

			return project;
		}
	}

	/// <param name="builder">The .NET project resource builder.</param>
	extension(IResourceBuilder<DotnetProjectResource> builder)
	{
		/// <summary>
		/// Makes Aspire start the project itself instead of asking the IDE to launch it.
		/// </summary>
		/// <remarks>
		/// When the AppHost runs under an IDE such as Visual Studio, Aspire normally hands .NET projects to the
		/// IDE to launch. Visual Studio rejects projects that are not part of its solution, so the resource fails
		/// to start. Without IDE launch, Aspire runs <c>dotnet run</c> (or the built app) itself and the IDE
		/// debugger is not attached automatically; use <see cref="WithDebuggerAttachCommand{T}"/> instead.
		/// This relies on the experimental <c>SupportsDebuggingAnnotation</c> (ASPIREEXTENSION001).
		/// </remarks>
		/// <returns>The same builder for chaining.</returns>
		public IResourceBuilder<DotnetProjectResource> WithoutIdeLaunch()
		{
#pragma warning disable ASPIREEXTENSION001 // SupportsDebuggingAnnotation is experimental
			foreach (SupportsDebuggingAnnotation annotation in
			         builder.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().ToList())
			{
				builder.Resource.Annotations.Remove(annotation);
			}
#pragma warning restore ASPIREEXTENSION001

			return builder;
		}

		/// <summary>
		/// Starts the project with <c>dotnet watch</c> instead of <c>dotnet run</c>, so source changes trigger a
		/// hot reload (or a rebuild/restart for edits that cannot be hot reloaded).
		/// </summary>
		/// <remarks>
		/// Aspire 17 has no watch support for path-based projects, so this replaces the launch command.
		/// <c>dotnet watch</c> runs non-interactively, so a rude edit restarts the process instead of prompting.
		/// Aspire's coordinated build still runs before the launch and <c>dotnet watch</c> builds again.
		/// The process id changes on a restart, so the debugger has to be re-attached.
		/// When the project is launched by the IDE (see <see cref="WithoutIdeLaunch"/>) this has no effect.
		/// </remarks>
		/// <returns>The same builder for chaining.</returns>
		public IResourceBuilder<DotnetProjectResource> WithDotnetWatch()
		{
			DotnetProjectResource resource = builder.Resource;
			string projectPath = ExternalProjectBuilderExtensions.GetProjectPath(resource);
			string? configuration = builder.ApplicationBuilder.Configuration["AppHost:BuildConfiguration"];

			// A later WithLaunchToolArgs call replaces the 'dotnet run' invocation set up by AddDotnetProject.
			// The launch profile's commandLineArgs are added by AddDotnetProject as ordinary arguments after these,
			// so the trailing '--' hands them to the app.
			// WithLaunchToolArgs is experimental (ASPIREEXTENSION001).
#pragma warning disable ASPIREEXTENSION001
			builder.WithLaunchToolArgs(ctx =>
			{
				// Without an IDE, Aspire switches the command to the built app after its coordinated build.
				// Watch needs the 'dotnet' host, so make sure it is used.
				ExecutableAnnotation executable = ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
				executable.Command = "dotnet";

				ctx.Args.Add("watch");
				ctx.Args.Add("--non-interactive");
				ctx.Args.Add("--project");
				ctx.Args.Add(projectPath);
				ctx.Args.Add("run");
				if (!string.IsNullOrEmpty(configuration))
				{
					ctx.Args.Add("--configuration");
					ctx.Args.Add(configuration);
				}

				// The launch profile is applied by Aspire through the environment, like AddDotnetProject does.
				ctx.Args.Add("--no-launch-profile");
				ctx.Args.Add("--");
			});
#pragma warning restore ASPIREEXTENSION001

			return builder;
		}

		/// <summary>
		/// Makes this project wait for every other resource in the same solution group, so projects that share
		/// dependencies do not build at the same time.
		/// </summary>
		/// <remarks>
		/// Aspire already builds path-based projects together before launching them, so this is rarely needed.
		/// It can still help when projects rebuild themselves at launch, e.g. under an IDE or with
		/// <see cref="WithDotnetWatch"/>.
		/// </remarks>
		/// <param name="solutionGroup">The name of the solution group.</param>
		/// <returns>The same builder for chaining.</returns>
		public IResourceBuilder<DotnetProjectResource> WithSolutionGroup(string solutionGroup)
		{
			ArgumentNullException.ThrowIfNull(solutionGroup);

			builder.WithAnnotation(new ExternalProjectSolutionGroupAnnotation(solutionGroup));

			foreach (IResource resource in builder.ApplicationBuilder.Resources.Where(r => r != builder.Resource))
			{
				if (resource.Annotations.OfType<ExternalProjectSolutionGroupAnnotation>()
				    .Any(a => a.SolutionGroup == solutionGroup))
				{
					builder.WaitFor(builder.ApplicationBuilder.CreateResourceBuilder(resource));
				}
			}

			return builder;
		}
	}
}