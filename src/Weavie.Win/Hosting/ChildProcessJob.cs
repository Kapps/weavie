using System.Runtime.InteropServices;

namespace Weavie.Win.Hosting;

/// <summary>
/// Ties every child process this host spawns (Vite, ConPTY shells, language servers, the embedded <c>claude</c>)
/// to the host's lifetime via a Windows Job Object with <c>KILL_ON_JOB_CLOSE</c>: children inherit the job, so
/// the OS reaps them when the host dies by any means. The OS backstop behind <c>ProcessSupervisor</c>'s graceful
/// teardown, which can't run on a hard kill. Best effort — if the job can't be created it logs loudly and runs on.
/// </summary>
internal static partial class ChildProcessJob {
	// SetInformationJobObject class for JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
	private const int JobObjectExtendedLimitInformation = 9;

	// JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags: kill every process in the job when its last handle closes.
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	// Held for the host's entire lifetime: closing it ourselves trips KILL_ON_JOB_CLOSE (we're a member) and kills
	// the host. The OS closes it on process exit — exactly when surviving children should be reaped.
	private static nint _job;

	/// <summary>Creates the kill-on-close job and assigns the current process to it. Idempotent; call once at
	/// startup before any child is spawned, so every child inherits the job.</summary>
	public static void Install() {
		if (_job != 0) {
			return;
		}

		nint job = CreateJobObjectW(0, null);
		if (job == 0) {
			Warn("CreateJobObject", Marshal.GetLastWin32Error());
			return;
		}

		var info = new JobObjectExtendedLimitInformationStruct {
			BasicLimitInformation = new JobObjectBasicLimitInformationStruct {
				LimitFlags = JobObjectLimitKillOnJobClose,
			},
		};
		int size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
		nint buffer = Marshal.AllocHGlobal(size);
		try {
			Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
			if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size)) {
				Warn("SetInformationJobObject", Marshal.GetLastWin32Error());
				CloseHandle(job);
				return;
			}
		} finally {
			Marshal.FreeHGlobal(buffer);
		}

		// Nested jobs (Windows 8+) let this succeed even under a debugger's job: we nest, and our kill-on-close
		// still governs our children.
		if (!AssignProcessToJobObject(job, GetCurrentProcess())) {
			Warn("AssignProcessToJobObject", Marshal.GetLastWin32Error());
			CloseHandle(job);
			return;
		}

		_job = job;
	}

	private static void Warn(string api, int error) =>
		Console.Error.WriteLine($"[weavie] orphan guard: {api} failed (win32 {error}); child processes may survive a hard kill of the host");

	[StructLayout(LayoutKind.Sequential)]
	internal struct JobObjectBasicLimitInformationStruct {
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct IoCountersStruct {
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct JobObjectExtendedLimitInformationStruct {
		public JobObjectBasicLimitInformationStruct BasicLimitInformation;
		public IoCountersStruct IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetInformationJobObject(
		nint hJob, int jobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

	[LibraryImport("kernel32.dll")]
	private static partial nint GetCurrentProcess();

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint hObject);
}
