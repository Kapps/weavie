using System.Runtime.InteropServices;

namespace Weavie.Win.Hosting;

/// <summary>
/// Ties every child process this host spawns (Vite, ConPTY shells, language servers, the embedded <c>claude</c>)
/// to the host's lifetime via a Windows Job Object with <c>KILL_ON_JOB_CLOSE</c>. Children inherit the job, so
/// when the host dies by <em>any</em> means — clean exit, debugger Stop, crash — the OS closes the job handle and
/// terminates everything still inside it.
///
/// <para>The OS backstop behind <c>ProcessSupervisor</c>'s graceful teardown: managed cleanup runs only on an
/// orderly shutdown, so it can't reap children on a hard kill. Best effort — if the job can't be created it logs
/// loudly and the host still runs, just without the hard-kill safety net.</para>
/// </summary>
internal static partial class ChildProcessJob {
	// SetInformationJobObject class for JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
	private const int JobObjectExtendedLimitInformation = 9;

	// JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags: kill every process in the job when its last handle closes.
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	// Held for the host's entire lifetime: closing it ourselves would trip KILL_ON_JOB_CLOSE immediately (we're
	// a member of the job) and take the host down. The OS closes it on process exit, which is exactly when the
	// surviving children should be reaped.
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

		// Nested jobs are allowed on Windows 8+, so this succeeds even when a debugger has already placed us in
		// its own job — we nest under it, and our kill-on-close still governs our children.
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
