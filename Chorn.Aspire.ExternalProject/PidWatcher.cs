namespace Chorn.Aspire.ExternalProject;

using System.Collections.Concurrent;

internal class PidWatcher
{
	private readonly ConcurrentDictionary<string, int> pids = [];

	public void Store(string name, int? pid)
	{
		if (pid == null)
		{
			this.pids.TryRemove(name, out _);
		}
		else
		{
			this.pids[name] = pid.Value;
		}
	}

	public int? Get(string name)
	{
		if (this.pids.TryGetValue(name, out int pid))
		{
			return pid;
		}

		return null;
	}
}