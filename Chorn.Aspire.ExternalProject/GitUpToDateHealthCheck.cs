namespace Chorn.Aspire.ExternalProject;

using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

internal class GitUpToDateHealthCheck : IHealthCheck
{
	private readonly string projectFolder;

	public GitUpToDateHealthCheck(string projectFolder)
	{
		this.projectFolder = projectFolder;
	}

	/// <inheritdoc />
	public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
	{
		// Check if the git repository is up to date
		// If not, return HealthCheckResult.Unhealthy("Git repository is not up to date")
		// Otherwise, return HealthCheckResult.Healthy("Git repository is up to date")

		// Call git status in the project folder.
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = "git",
			Arguments = "fetch",
			WorkingDirectory = this.projectFolder,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		Process process = Process.Start(startInfo)!;
		await process.WaitForExitAsync(cancellationToken);

		// Read the output and error streams.
		string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
		string error = await process.StandardError.ReadToEndAsync(cancellationToken);

		if (process.ExitCode != 0)
		{
			return HealthCheckResult.Unhealthy("Git repository check failed.");
		}

		startInfo.Arguments = "status --porcelain=v2 --branch";
		process = Process.Start(startInfo)!;
		await process.WaitForExitAsync(cancellationToken);

		output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
		error = await process.StandardError.ReadToEndAsync(cancellationToken);

		if (process.ExitCode != 0)
		{
			return HealthCheckResult.Unhealthy("Git repository is not up to date");
		}

		// We are parsing the # branch.ab +x -y output returned by git status --porcelain=v2 --branch
		// to check if the branch is up to date with the remote branch.

		// Find the line that starts with branch.ab
		string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		string? branchLine = lines.FirstOrDefault(l => l.StartsWith("# branch.ab"));

		if (branchLine == null)
		{
			return HealthCheckResult.Healthy();
		}

		// Check how much we are ahead or behind.
		string[] parts = branchLine!.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		// ahead will be +x and behind will be -y
		int ahead = int.Parse(parts[2]);
		int behind = int.Parse(parts[3]);

		if (behind < 0)
		{
			return HealthCheckResult.Unhealthy("Git repository is not up to date");
		}

		return HealthCheckResult.Healthy("Git repository is up to date");
	}
}