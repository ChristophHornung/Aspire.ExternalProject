namespace Chorn.Aspire.ExternalProject;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Microsoft.CSharp.RuntimeBinder;

/// <summary>
/// Attaches the debugger of a running Visual Studio instance to a process via Visual Studio's automation (DTE)
/// interface, the API behind Debug -> Attach to Process.
/// </summary>
/// <remarks>
/// Every running Visual Studio registers itself in the Windows running object table as
/// <c>!VisualStudio.DTE.&lt;version&gt;:&lt;pid&gt;</c>. The attacher prefers an instance whose open solution contains
/// the project, so the debugger stops where its source is open, then falls back to the instance that debugs the
/// AppHost. Visual Studio instances running at a different elevation than the AppHost are not visible.
/// The DTE is used late-bound (<c>dynamic</c>) to avoid a dependency on the Visual Studio interop assemblies.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class VisualStudioAttacher
{
	private const string DtePrefix = "!VisualStudio.DTE.";
	private const string SolutionFolderKind = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";
	private const int DebuggerDesignMode = 1;

	/// <summary>
	/// Attaches a Visual Studio debugger to <paramref name="processId"/>.
	/// </summary>
	/// <param name="processId">The process to attach to.</param>
	/// <param name="projectPath">The project file used to find the Visual Studio with the right solution open, if known.</param>
	/// <returns>A description of the Visual Studio that attached, or <c>null</c> if none could.</returns>
	public static Task<string?> TryAttachAsync(int processId, string? projectPath)
	{
		// DTE calls go to another process and Visual Studio rejects them while it is busy. A message filter
		// retries rejected calls, and message filters only work on an STA thread.
		TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new(() =>
		{
			VisualStudioAttacher.CoRegisterMessageFilter(new RetryMessageFilter(), out _);
			try
			{
				completion.SetResult(VisualStudioAttacher.TryAttach(processId, projectPath));
			}
			catch (Exception e)
			{
				completion.SetException(e);
			}
			finally
			{
				VisualStudioAttacher.CoRegisterMessageFilter(null, out _);
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.IsBackground = true;
		thread.Start();
		return completion.Task;
	}

	private static string? TryAttach(int processId, string? projectPath)
	{
		List<(int Pid, object Dte)> instances = VisualStudioAttacher.GetRunningInstances();
		if (instances.Count == 0)
		{
			return null;
		}

		// 1. A Visual Studio whose open solution contains the project. Prefer one that is not debugging yet.
		if (projectPath != null)
		{
			string fullProjectPath = Path.GetFullPath(projectPath);
			IEnumerable<(int Pid, object Dte)> matches = instances
				.Where(i => VisualStudioAttacher.SolutionContainsProject(i.Dte, fullProjectPath))
				.OrderBy(i => VisualStudioAttacher.IsDebugging(i.Dte) ? 1 : 0);
			foreach ((int _, object dte) in matches)
			{
				if (VisualStudioAttacher.Attach(dte, processId))
				{
					return $"Attached Visual Studio with '{VisualStudioAttacher.GetSolutionName(dte)}' open.";
				}
			}
		}

		// 2. The Visual Studio that debugs the AppHost.
		int? appHostVisualStudio = VisualStudioAttacher.FindAncestorVisualStudio();
		if (appHostVisualStudio != null)
		{
			foreach ((int _, object dte) in instances.Where(i => i.Pid == appHostVisualStudio))
			{
				if (VisualStudioAttacher.Attach(dte, processId))
				{
					return "Attached the Visual Studio that runs the AppHost.";
				}
			}
		}

		return null;
	}

	private static string GetSolutionName(dynamic dte)
	{
		try
		{
			return Path.GetFileName((string)dte.Solution.FullName);
		}
		catch (Exception e) when (VisualStudioAttacher.IsComFailure(e))
		{
			return "the solution";
		}
	}

	private static bool Attach(dynamic dte, int processId)
	{
		try
		{
			dynamic processes = dte.Debugger.LocalProcesses;
			int count = processes.Count;
			for (int i = 1; i <= count; i++)
			{
				dynamic process = processes.Item(i);
				if ((int)process.ProcessID == processId)
				{
					process.Attach();
					VisualStudioAttacher.TryActivate(dte);
					return true;
				}
			}
		}
		catch (Exception e) when (VisualStudioAttacher.IsComFailure(e))
		{
			// The instance went away or refused the attach; try the next one.
		}

		return false;
	}

	private static void TryActivate(dynamic dte)
	{
		try
		{
			dte.MainWindow.Activate();
		}
		catch (Exception e) when (VisualStudioAttacher.IsComFailure(e))
		{
			// Bringing the window to the front is a nicety only.
		}
	}

	private static bool IsDebugging(dynamic dte)
	{
		try
		{
			return (int)dte.Debugger.CurrentMode != VisualStudioAttacher.DebuggerDesignMode;
		}
		catch (Exception e) when (VisualStudioAttacher.IsComFailure(e))
		{
			return false;
		}
	}

	private static bool SolutionContainsProject(dynamic dte, string fullProjectPath)
	{
		try
		{
			string solution = dte.Solution.FullName;
			if (string.IsNullOrEmpty(solution))
			{
				return false;
			}

			dynamic projects = dte.Solution.Projects;
			int count = projects.Count;
			for (int i = 1; i <= count; i++)
			{
				if (VisualStudioAttacher.ProjectMatches(projects.Item(i), fullProjectPath))
				{
					return true;
				}
			}
		}
		catch (Exception e) when (VisualStudioAttacher.IsComFailure(e))
		{
			// The instance is busy or went away.
		}

		return false;
	}

	private static bool ProjectMatches(dynamic project, string fullProjectPath)
	{
		try
		{
			// Solution folders contain their projects as sub projects of their items.
			if (string.Equals((string)project.Kind, VisualStudioAttacher.SolutionFolderKind,
				    StringComparison.OrdinalIgnoreCase))
			{
				dynamic items = project.ProjectItems;
				int count = items.Count;
				for (int i = 1; i <= count; i++)
				{
					dynamic subProject = items.Item(i).SubProject;
					if (subProject != null && VisualStudioAttacher.ProjectMatches(subProject, fullProjectPath))
					{
						return true;
					}
				}

				return false;
			}

			string path = project.FullName;
			return !string.IsNullOrEmpty(path) &&
			       string.Equals(Path.GetFullPath(path), fullProjectPath, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception e) when (VisualStudioAttacher.IsComFailure(e) ||
		                          e is ArgumentException or NotSupportedException)
		{
			// Unloaded or non-file projects have no usable path.
			return false;
		}
	}

	/// <summary>
	/// Finds the Visual Studio (<c>devenv</c>) among the ancestors of this AppHost process, i.e. the one debugging it.
	/// </summary>
	private static int? FindAncestorVisualStudio()
	{
		Process? current = Process.GetCurrentProcess();
		for (int depth = 0; depth < 5 && current != null; depth++)
		{
			Process? parent = current.GetParent();
			current.Dispose();
			current = parent;
			if (current != null && string.Equals(current.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase))
			{
				int id = current.Id;
				current.Dispose();
				return id;
			}
		}

		current?.Dispose();
		return null;
	}

	/// <summary>
	/// COM call failures, and late-bound members the Visual Studio instance does not have.
	/// </summary>
	private static bool IsComFailure(Exception e) =>
		e is COMException or InvalidCastException or RuntimeBinderException;

	private static List<(int Pid, object Dte)> GetRunningInstances()
	{
		List<(int Pid, object Dte)> result = [];
		if (VisualStudioAttacher.GetRunningObjectTable(0, out IRunningObjectTable table) != 0 ||
		    VisualStudioAttacher.CreateBindCtx(0, out IBindCtx bindContext) != 0)
		{
			return result;
		}

		table.EnumRunning(out IEnumMoniker monikers);
		monikers.Reset();
		IMoniker[] moniker = new IMoniker[1];
		while (monikers.Next(1, moniker, IntPtr.Zero) == 0)
		{
			try
			{
				moniker[0].GetDisplayName(bindContext, null, out string name);
				if (!name.StartsWith(VisualStudioAttacher.DtePrefix, StringComparison.Ordinal))
				{
					continue;
				}

				int separator = name.LastIndexOf(':');
				if (separator < 0 || !int.TryParse(name.AsSpan(separator + 1), out int pid))
				{
					continue;
				}

				table.GetObject(moniker[0], out object dte);
				result.Add((pid, dte));
			}
			catch (Exception e) when (VisualStudioAttacher.IsComFailure(e))
			{
				// The entry went away while enumerating.
			}
		}

		return result;
	}

	[DllImport("ole32.dll")]
	private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

	[DllImport("ole32.dll")]
	private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

	[DllImport("ole32.dll")]
	private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);

	[ComImport]
	[Guid("00000016-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IOleMessageFilter
	{
		[PreserveSig]
		int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);

		[PreserveSig]
		int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);

		[PreserveSig]
		int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
	}

	/// <summary>
	/// Retries calls Visual Studio rejects because it is busy (SERVERCALL_RETRYLATER) for up to 10 seconds.
	/// </summary>
	private sealed class RetryMessageFilter : IOleMessageFilter
	{
		private const int ServerCallIsHandled = 0;
		private const int ServerCallRetryLater = 2;
		private const int PendingMessageWaitDefProcess = 2;

		public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) =>
			RetryMessageFilter.ServerCallIsHandled;

		public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType) =>
			rejectType == RetryMessageFilter.ServerCallRetryLater && tickCount < 10_000 ? 100 : -1;

		public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) =>
			RetryMessageFilter.PendingMessageWaitDefProcess;
	}
}