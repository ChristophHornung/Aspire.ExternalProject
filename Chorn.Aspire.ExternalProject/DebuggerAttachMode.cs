namespace Chorn.Aspire.ExternalProject;

/// <summary>
/// How the <c>Debug</c> dashboard command attaches a debugger.
/// </summary>
public enum DebuggerAttachMode
{
	/// <summary>
	/// Attach through a running Visual Studio (Windows only): preferably the one whose open solution contains the
	/// project, otherwise the one debugging the AppHost. Falls back to
	/// <see cref="DebuggerAttachOptions.LaunchDebuggerCommand"/> when no Visual Studio can attach.
	/// </summary>
	VisualStudio,

	/// <summary>
	/// Only start <see cref="DebuggerAttachOptions.LaunchDebuggerCommand"/> (by default <c>vsjitdebugger</c>),
	/// e.g. to attach Rider.
	/// </summary>
	Command
}