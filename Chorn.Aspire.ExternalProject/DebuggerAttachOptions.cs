namespace Chorn.Aspire.ExternalProject;

/// <summary>
/// Options for the <c>Debug</c> dashboard command that attaches a debugger to a running resource.
/// </summary>
public class DebuggerAttachOptions
{
	/// <summary>
	/// How the debugger is attached when <see cref="LaunchDebuggerUri"/> is not set.
	/// Defaults to <see cref="DebuggerAttachMode.VisualStudio"/>.
	/// </summary>
	public DebuggerAttachMode AttachMode { get; set; } = DebuggerAttachMode.VisualStudio;

	/// <summary>
	/// A path on the resource (e.g. <c>/debug</c>) that is POSTed to instead of starting
	/// <see cref="LaunchDebuggerCommand"/>. The application is expected to call <c>Debugger.Launch()</c> there.
	/// If <c>null</c>, <see cref="LaunchDebuggerCommand"/> is used.
	/// </summary>
	public string? LaunchDebuggerUri { get; set; }

	/// <summary>
	/// The command that attaches the debugger. Defaults to "vsjitdebugger".
	/// </summary>
	public string LaunchDebuggerCommand { get; set; } = "vsjitdebugger";

	/// <summary>
	/// The arguments for <see cref="LaunchDebuggerCommand"/>.
	/// "&lt;pid&gt;" is replaced with the process id of the application.
	/// </summary>
	public string LaunchDebuggerCommandArguments { get; set; } = "-p <pid>";
}