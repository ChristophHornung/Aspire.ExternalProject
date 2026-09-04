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
	public string? SolutionGroup { get; set; }

	/// <summary>
	/// If set to <c>true</c>, the external project is started with <c>dotnet watch</c> instead of
	/// <c>dotnet run</c>, so source changes trigger a hot reload (or a rebuild/restart for edits
	/// that cannot be hot reloaded).
	/// </summary>
	/// <remarks>
	/// Runs <c>dotnet watch</c> non-interactively, so a rude edit restarts the process automatically
	/// instead of prompting. Note that the process id changes on a restart, which can affect the
	/// <c>Debug</c> command (a re-attach may be required); the URL-based debugger
	/// (<see cref="LaunchDebuggerUri"/>) tolerates restarts better.
	/// </remarks>
	/// <value><c>true</c> to use <c>dotnet watch</c>; otherwise, <c>false</c>.</value>
	public bool UseWatch { get; set; }
}