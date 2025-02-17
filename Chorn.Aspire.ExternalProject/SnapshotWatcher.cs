namespace Chorn.Aspire.ExternalProject;

using System.Collections.Concurrent;

// TODO: Unsure if this is the best way to get a snapshot of the custom resource.
// Should look into getting a snapshot when the command is executed instead of constantly watching.
internal class SnapshotWatcher
{
	private readonly ConcurrentDictionary<string, CustomResourceSnapshot> snapshots = [];

	public void Store(string name, CustomResourceSnapshot? snapshot)
	{
		if (snapshot == null)
		{
			this.snapshots.TryRemove(name, out _);
		}
		else
		{
			this.snapshots[name] = snapshot;
		}
	}

	public int? GetPid(string name)
	{
		if (this.snapshots.TryGetValue(name, out CustomResourceSnapshot? snapshot))
		{
			ResourcePropertySnapshot? pidProperty =
				snapshot.Properties.FirstOrDefault(p => p.Name == "executable.pid");
			if (pidProperty != null && pidProperty.Value is int pid && pid != 0)
			{
				return pid;
			}
		}

		return null;
	}

	public string? GetHttpsOrHttpBaseUrl(string name)
	{
		if (this.snapshots.TryGetValue(name, out CustomResourceSnapshot? snapshot))
		{
			UrlSnapshot? baseUrl =
				snapshot.Urls.FirstOrDefault(p => p.Name == "https target port") ??
				snapshot.Urls.FirstOrDefault(p => p.Name == "https") ??
				snapshot.Urls.FirstOrDefault(p => p.Name == "http target port") ??
				snapshot.Urls.FirstOrDefault(p => p.Name == "http");
			if (baseUrl != null)
			{
				return baseUrl.Url;
			}
		}

		return null;
	}
}