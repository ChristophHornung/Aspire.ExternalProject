﻿namespace Chorn.Aspire.ExternalProject;

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
	/// the 'vsjitdebugger' command.
	/// </summary>
	/// <value>The launch debugger URI.</value>
	public string? LaunchDebuggerUri { get; set; }

	/// <summary>
	/// An optional solution group for the external project.
	/// Projects inside the same solution group will wait for each other to start in
	/// order to avoid conflicts during build.
	/// </summary>
	public string? SolutionGroup { get; set; }
}