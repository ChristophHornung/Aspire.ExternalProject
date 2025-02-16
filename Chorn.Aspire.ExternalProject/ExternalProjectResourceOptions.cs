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
	/// This is only relevant if <see cref="SkipGitSupport"/> is not <c>ture</c>.
	/// </remarks>
	public bool EnableGitHealthCheck { get; set; }
}