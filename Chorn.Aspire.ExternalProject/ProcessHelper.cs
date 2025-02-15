namespace Chorn.Aspire.ExternalProject;

using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class ProcessHelper
{
	private const int InvalidProcessId = -1;

	internal static Process? GetParent(this Process process)
	{
		try
		{
			var pid = ProcessHelper.GetParentPid(process);
			if (pid == ProcessHelper.InvalidProcessId)
			{
				return null;
			}

			var candidate = Process.GetProcessById(pid);

			// if the candidate was started later than process, the pid has been recycled
			return candidate.StartTime > process.StartTime ? null : candidate;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Returns the parent id of a process or -1 if it fails.
	/// </summary>
	/// <param name="process"></param>
	/// <returns>The pid of the parent process.</returns>
#if UNIX
        internal static int GetParentPid(Process process)
        {
            return Platform.NonWindowsGetProcessParentPid(process.Id);
        }
#else
	internal static int GetParentPid(Process process)
	{
		int res = ProcessHelper.NtQueryInformationProcess(process.Handle, 0, out PROCESS_BASIC_INFORMATION pbi,
			Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out int _);

		return res != 0 ? ProcessHelper.InvalidProcessId : pbi.InheritedFromUniqueProcessId.ToInt32();
	}
#endif

	[DllImport("NTDLL.DLL", SetLastError = true)]
	private static extern int NtQueryInformationProcess(IntPtr hProcess, PROCESSINFOCLASS pic,
		out PROCESS_BASIC_INFORMATION pbi, int cb, out int pSize);

	private enum PROCESSINFOCLASS
	{
		ProcessBasicInformation = 0x00,
		ProcessDebugPort = 0x07,
		ProcessExceptionPort = 0x08,
		ProcessAccessToken = 0x09,
		ProcessWow64Information = 0x1A,
		ProcessImageFileName = 0x1B,
		ProcessDebugObjectHandle = 0x1E,
		ProcessDebugFlags = 0x1F,
		ProcessExecuteFlags = 0x22,
		ProcessInstrumentationCallback = 0x28,
		MaxProcessInfoClass = 0x64
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct PROCESS_BASIC_INFORMATION
	{
		public int ExitStatus;
		public IntPtr PebBaseAddress;
		public IntPtr AffinityMask;
		public int BasePriority;
		public IntPtr UniquePid;
		public IntPtr InheritedFromUniqueProcessId;
	}
}