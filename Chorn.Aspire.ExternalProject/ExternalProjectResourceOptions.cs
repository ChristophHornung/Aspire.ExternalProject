namespace Chorn.Aspire.ExternalProject;

/// <summary>
/// Options for adding an external project to the distributed application.
/// </summary>
public class ExternalProjectResourceOptions : ProjectResourceOptions
{
	/// <summary>
	///If set to <c>true</c>, the external project will not have Git support.
	/// </summary>
	/// <value><c>true</c> if without Git support; otherwise, <c>false</c>.</value>
	public bool SkipGitSupport { get; set; }

	/// <summary>
	/// If set to <c>true</c>, the external project will have a Git health check.
	/// </summary>
	/// <remarks>
	/// This is only relevant if <see cref="SkipGitSupport"/> is not <c>true</c>.
	/// </remarks>
	/// <value><c>true</c> if enable Git health check; otherwise, <c>false</c>.</value>
	public bool EnableGitHealthCheck { get; set; }

	/// <summary>
	/// The URI to launch the debugger for the external project. If <c>null</c> uses
	/// the <see cref="LaunchDebuggerCommand"/>.
	/// </summary>
	/// <value>The launch debugger URI.</value>
	public string? LaunchDebuggerUri { get; set; }

	/// <summary>
	/// How the <c>Debug</c> command attaches when <see cref="LaunchDebuggerUri"/> is not set.
	/// Defaults to <see cref="ExternalProject.DebuggerAttachMode.VisualStudio"/>: attach through the Visual Studio that
	/// has the project's solution open, falling back to <see cref="LaunchDebuggerCommand"/>.
	/// </summary>
	public DebuggerAttachMode DebuggerAttachMode { get; set; } = DebuggerAttachMode.VisualStudio;

	/// <summary>
	/// The command line command to launch the debugger for the external project. Defaults to "vsjitdebugger".
	/// </summary>
	public string LaunchDebuggerCommand { get; set; } = "vsjitdebugger";

	/// <summary>
	/// The arguments to pass to the debugger when launching the external project.
	/// "&lt;pid&gt;" will be replaced with the process id of the external project.
	/// </summary>
	public string LaunchDebuggerCommandArguments { get; set; } = "-p <pid>";


	/// <summary>
	/// An optional solution group for the external project.
	/// Projects inside the same solution group will wait for each other to start in
	/// order to avoid conflicts during build.
	/// </summary>
	/// <remarks>
	/// Aspire already builds path-based projects together before launching them, so this is rarely needed.
	/// It can still help when projects rebuild themselves at launch, e.g. under an IDE or with <see cref="UseWatch"/>.
	/// </remarks>
	public string? SolutionGroup { get; set; }

	/// <summary>
	/// If set to <c>true</c>, the project is handed to the IDE to launch when the AppHost runs under one,
	/// the way Aspire does for its own projects. Defaults to <c>false</c>.
	/// </summary>
	/// <remarks>
	/// Visual Studio cannot launch projects that are not part of its solution (the resource fails to start),
	/// so by default Aspire starts external projects itself. Only turn this on if your IDE can launch the
	/// project; you then get the IDE debugger attached automatically, but <see cref="UseWatch"/> is ignored
	/// under the IDE.
	/// </remarks>
	public bool LaunchFromIde { get; set; }

	/// <summary>
	/// If set to <c>true</c>, the external project is started with <c>dotnet watch</c> instead of
	/// <c>dotnet run</c>, so source changes trigger a hot reload (or a rebuild/restart for edits
	/// that cannot be hot reloaded).
	/// </summary>
	/// <remarks>
	/// Runs <c>dotnet watch</c> non-interactively, so a rude edit restarts the process automatically
	/// instead of prompting. Note that the process id changes on a restart, which can affect the
	/// <c>Debug</c> command (a re-attach may be required); the URL-based debugger
	/// (<see cref="LaunchDebuggerUri"/>) tolerates restarts better. Has no effect when the project is
	/// launched by the IDE (<see cref="LaunchFromIde"/>).
	/// </remarks>
	/// <value><c>true</c> to use <c>dotnet watch</c>; otherwise, <c>false</c>.</value>
	public bool UseWatch { get; set; }
}