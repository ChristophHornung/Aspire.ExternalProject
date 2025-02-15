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
	public bool WithoutGitSupport { get; set; }

	/// <summary>
	/// If set to <c>true</c>, the external project will not have a Git health check.
	/// </summary>
	/// <value><c>true</c> if without Git health check; otherwise, <c>false</c>.</value>
	/// <remarks>
	/// This is only relevant if <see cref="WithoutGitSupport"/> is <c>false</c>.
	/// </remarks>
	/// <seealso cref="WithoutGitSupport"/>
	public bool WithoutGitHealthCheck { get; set; }
}