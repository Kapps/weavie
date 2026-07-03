using System.Runtime.InteropServices;
using static Weavie.Core.Terminal.NativeMethods;

namespace Weavie.Core.Terminal;

/// <summary>
/// Real POSIX PTY (macOS + Linux): opens a master pseudo-terminal, launches the child (via <c>forkpty</c> on
/// macOS, <c>posix_spawn</c> on Linux), reads output on a background thread, and writes input to the master. The
/// command must be an absolute path; callers launch a login shell that execs the real target so env/PATH resolve.
/// </summary>
public sealed class PosixPtyTerminal : ITerminal {
	private readonly Lock _gate = new();
	private int _masterFd = -1;
	private int _pid = -1;
	private Thread? _readThread;
	private volatile bool _running;
	private int _exitRaised;

	/// <inheritdoc/>
	public event Action<byte[]>? Output;
	/// <inheritdoc/>
	public event Action<int>? Exited;

	/// <inheritdoc/>
	public bool IsRunning => _running;

	/// <inheritdoc/>
	// The child is spawned with SETSID, so its pgid equals its pid; any other foreground pgrp is a job.
	public bool HasForegroundJob {
		get {
			lock (_gate) {
				if (!_running || _masterFd < 0 || _pid <= 0) {
					return false;
				}

				int foreground = tcgetpgrp(_masterFd);
				return foreground > 0 && foreground != _pid;
			}
		}
	}

	/// <inheritdoc/>
	public void Start(TerminalStartInfo startInfo) {
		ArgumentNullException.ThrowIfNull(startInfo);
		if (!Path.IsPathRooted(startInfo.Command)) {
			throw new ArgumentException($"Command must be an absolute path: '{startInfo.Command}'.", nameof(startInfo));
		}

		lock (_gate) {
			if (_running) {
				throw new InvalidOperationException("Terminal already started.");
			}

			(_masterFd, _pid) = OpenAndSpawn(startInfo);
			_running = true;

			Resize(startInfo.Columns, startInfo.Rows);

			_readThread = new Thread(ReadLoop) { IsBackground = true, Name = "weavie-pty-read" };
			_readThread.Start();
		}
	}

	/// <summary>
	/// Opens a PTY and launches the child, returning the master fd and child pid. macOS uses the native
	/// <c>weavie_pty_spawn</c> (forkpty) so the child gets a controlling terminal (interactive shells like nushell
	/// require it), falling back to the managed posix_spawn path (Linux's path too) when the shim dylib is absent.
	/// </summary>
	private static (int Master, int Pid) OpenAndSpawn(TerminalStartInfo startInfo) {
		if (OperatingSystem.IsMacOS()) {
			try {
				return SpawnViaForkpty(startInfo);
			} catch (DllNotFoundException) {
				// Shim dylib absent (tests outside the bundle) — fall through to the managed path.
			}
		}

		return SpawnViaPosixSpawn(startInfo);
	}

	/// <summary>macOS: forkpty + execve in the native shim, giving the child a controlling terminal.</summary>
	private static (int Master, int Pid) SpawnViaForkpty(TerminalStartInfo startInfo) {
		var argvItems = new List<string>(1 + startInfo.Arguments.Count) { startInfo.Command };
		argvItems.AddRange(startInfo.Arguments);

		nint[] argv = ToUtf8PtrArray(argvItems);
		nint[] envp = ToUtf8PtrArray(BuildEnvironment(startInfo));
		var argvHandle = GCHandle.Alloc(argv, GCHandleType.Pinned);
		var envpHandle = GCHandle.Alloc(envp, GCHandleType.Pinned);
		try {
			ushort cols = (ushort)Math.Clamp(startInfo.Columns, 1, ushort.MaxValue);
			ushort rows = (ushort)Math.Clamp(startInfo.Rows, 1, ushort.MaxValue);
			int rc = weavie_pty_spawn(
				startInfo.Command,
				argvHandle.AddrOfPinnedObject(),
				envpHandle.AddrOfPinnedObject(),
				string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
				rows,
				cols,
				out int master,
				out int pid);
			if (rc != 0) {
				throw new IOException($"weavie_pty_spawn('{startInfo.Command}') failed (errno {-rc}).");
			}

			return (master, pid);
		} finally {
			argvHandle.Free();
			envpHandle.Free();
			FreeUtf8PtrArray(argv);
			FreeUtf8PtrArray(envp);
		}
	}

	/// <summary>Linux (and the macOS test fallback): posix_openpt + posix_spawn with POSIX_SPAWN_SETSID.</summary>
	private static (int Master, int Pid) SpawnViaPosixSpawn(TerminalStartInfo startInfo) {
		int master = posix_openpt(O_RDWR | O_NOCTTY);
		if (master < 0) {
			throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");
		}

		try {
			// macOS gets this via POSIX_SPAWN_CLOEXEC_DEFAULT; Linux has no such flag, so mark the master
			// close-on-exec explicitly to keep it from leaking into the spawned child.
			if (!OperatingSystem.IsMacOS()) {
				fcntl(master, F_SETFD, FD_CLOEXEC);
			}

			if (grantpt(master) != 0) {
				throw new IOException($"grantpt failed (errno {Marshal.GetLastPInvokeError()}).");
			}
			if (unlockpt(master) != 0) {
				throw new IOException($"unlockpt failed (errno {Marshal.GetLastPInvokeError()}).");
			}

			nint slavePtr = ptsname(master);
			string? slavePath = slavePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(slavePtr);
			if (string.IsNullOrEmpty(slavePath)) {
				throw new IOException("ptsname returned null.");
			}

			int pid = SpawnChild(startInfo, slavePath);
			return (master, pid);
		} catch {
			close(master);
			throw;
		}
	}

	private static int SpawnChild(TerminalStartInfo startInfo, string slavePath) {
		// posix_spawn_file_actions_t / posix_spawnattr_t are init'd in place (large struct on glibc, opaque
		// pointer on macOS). Hand each a zeroed, oversized native buffer passed by value; an 8-byte managed slot
		// would let glibc's *_init overrun it ("stack smashing detected").
		nint fileActions = Marshal.AllocHGlobal(SpawnObjectSize);
		nint attr = Marshal.AllocHGlobal(SpawnObjectSize);
		try {
			Zero(fileActions, SpawnObjectSize);
			Zero(attr, SpawnObjectSize);
			posix_spawn_file_actions_init(fileActions);
			posix_spawnattr_init(attr);

			// Child: chdir into the workspace (if any), then make the slave tty fd 0/1/2.
			if (!string.IsNullOrEmpty(startInfo.WorkingDirectory)) {
				posix_spawn_file_actions_addchdir_np(fileActions, startInfo.WorkingDirectory);
			}

			posix_spawn_file_actions_addopen(fileActions, 0, slavePath, O_RDWR, 0);
			posix_spawn_file_actions_adddup2(fileActions, 0, 1);
			posix_spawn_file_actions_adddup2(fileActions, 0, 2);
			// POSIX_SPAWN_CLOEXEC_DEFAULT is Apple-only; on Linux the master is made close-on-exec in Start.
			short spawnFlags = POSIX_SPAWN_SETSID;
			if (OperatingSystem.IsMacOS()) {
				spawnFlags |= POSIX_SPAWN_CLOEXEC_DEFAULT;
			}

			posix_spawnattr_setflags(attr, spawnFlags);

			var argvItems = new List<string>(1 + startInfo.Arguments.Count) { startInfo.Command };
			argvItems.AddRange(startInfo.Arguments);
			var envItems = BuildEnvironment(startInfo);

			nint[] argv = ToUtf8PtrArray(argvItems);
			nint[] envp = ToUtf8PtrArray(envItems);
			var argvHandle = GCHandle.Alloc(argv, GCHandleType.Pinned);
			var envpHandle = GCHandle.Alloc(envp, GCHandleType.Pinned);
			try {
				int rc = posix_spawn(
					out int pid,
					startInfo.Command,
					fileActions,
					attr,
					argvHandle.AddrOfPinnedObject(),
					envpHandle.AddrOfPinnedObject());
				if (rc != 0) {
					throw new IOException($"posix_spawn('{startInfo.Command}') failed with code {rc}.");
				}

				return pid;
			} finally {
				argvHandle.Free();
				envpHandle.Free();
				FreeUtf8PtrArray(argv);
				FreeUtf8PtrArray(envp);
			}
		} finally {
			posix_spawn_file_actions_destroy(fileActions);
			posix_spawnattr_destroy(attr);
			Marshal.FreeHGlobal(fileActions);
			Marshal.FreeHGlobal(attr);
		}
	}

	private static unsafe void Zero(IntPtr buffer, int length) =>
		new Span<byte>((void*)buffer, length).Clear();

	private static List<string> BuildEnvironment(TerminalStartInfo startInfo) =>
		[.. startInfo.BuildEnvironment(StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")];

	private void ReadLoop() {
		byte[] buffer = new byte[8192];
		try {
			while (true) {
				nint n = read(_masterFd, buffer, (nuint)buffer.Length);
				if (n <= 0) {
					break; // 0 = EOF, -1 = EIO when the child closed the slave / exited
				}

				byte[] slice = new byte[n];
				Buffer.BlockCopy(buffer, 0, slice, 0, (int)n);
				Output?.Invoke(slice);
			}
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] pty read loop error: {ex.Message}");
		} finally {
			RaiseExited();
		}
	}

	private void RaiseExited() {
		if (Interlocked.Exchange(ref _exitRaised, 1) != 0) {
			return;
		}

		int code = 0;
		if (_pid > 0 && waitpid(_pid, out int status, 0) == _pid) {
			int low = status & 0x7f;
			code = low == 0 ? (status >> 8) & 0xff : 128 + low;
		}

		_running = false;
		Exited?.Invoke(code);
	}

	/// <inheritdoc/>
	public void Write(byte[] data) {
		ArgumentNullException.ThrowIfNull(data);
		if (!_running || _masterFd < 0 || data.Length == 0) {
			return;
		}

		byte[] remaining = data;
		while (remaining.Length > 0) {
			nint written = write(_masterFd, remaining, (nuint)remaining.Length);
			if (written <= 0) {
				break;
			}
			if (written < remaining.Length) {
				remaining = remaining[(int)written..];
			} else {
				break;
			}
		}
	}

	/// <inheritdoc/>
	public void Resize(int columns, int rows) {
		if (_masterFd < 0) {
			return;
		}

		ushort cols = (ushort)Math.Clamp(columns, 1, ushort.MaxValue);
		ushort rowCount = (ushort)Math.Clamp(rows, 1, ushort.MaxValue);
		SetWindowSize(_masterFd, rowCount, cols);
	}

	/// <summary>
	/// Sets the pty's window size. macOS uses the native <c>weavie_set_winsize</c> shim because libc's variadic
	/// <c>ioctl</c> can't be P/Invoked correctly on arm64-apple (the resize is silently dropped, blanking a TUI
	/// like claude); falls back to managed <c>ioctl</c> elsewhere and when the shim dylib is absent.
	/// </summary>
	private static void SetWindowSize(int fd, ushort rows, ushort cols) {
		if (OperatingSystem.IsMacOS()) {
			try {
				weavie_set_winsize(fd, rows, cols);
				return;
			} catch (DllNotFoundException) {
				// The shim dylib isn't present (e.g. tests run outside the bundle); fall back to managed ioctl.
			}
		}

		var ws = new Winsize { ws_row = rows, ws_col = cols };
		ioctl(fd, TIOCSWINSZ, ref ws);
	}

	/// <inheritdoc/>
	public void Dispose() {
		int pid;
		bool running;
		Thread? readThread;
		lock (_gate) {
			pid = _pid;
			running = _running;
			readThread = _readThread;
			_running = false;
		}

		// Kill the child's whole process group (kill(-pid, …)), not just the leader: subprocesses (e.g. claude's
		// node children) inherit the slave PTY, and if they keep it open the master read() never sees EOF and
		// macOS's close() blocks forever on that in-flight read. SIGTERM first, escalating to SIGKILL on timeout.
		if (pid > 0 && running) {
			kill(-pid, SIGTERM);
		}

		bool readThreadDone = readThread is null || readThread.Join(TimeSpan.FromMilliseconds(500));
		if (!readThreadDone && pid > 0 && running) {
			kill(-pid, SIGKILL);
			readThreadDone = readThread!.Join(TimeSpan.FromSeconds(1));
		}

		lock (_gate) {
			// Close only after the read thread has exited (macOS close() blocks on a concurrent read() of the
			// same fd). If a stray holder kept the slave open, skip the close rather than hang teardown — the
			// leaked fd is reclaimed at process exit.
			if (_masterFd >= 0 && readThreadDone) {
				close(_masterFd);
				_masterFd = -1;
			}
		}
	}

	private static IntPtr[] ToUtf8PtrArray(IReadOnlyList<string> items) {
		nint[] array = new IntPtr[items.Count + 1];
		for (int i = 0; i < items.Count; i++) {
			array[i] = Marshal.StringToCoTaskMemUTF8(items[i]);
		}
		array[items.Count] = IntPtr.Zero;
		return array;
	}

	private static void FreeUtf8PtrArray(IntPtr[] array) {
		foreach (nint ptr in array) {
			if (ptr != IntPtr.Zero) {
				Marshal.FreeCoTaskMem(ptr);
			}
		}
	}
}
