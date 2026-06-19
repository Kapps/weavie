using System.Runtime.InteropServices;
using static Weavie.Core.Terminal.NativeMethods;

namespace Weavie.Core.Terminal;

/// <summary>
/// Real POSIX PTY (macOS + Linux). Opens a master pseudo-terminal and launches the child with
/// <c>posix_spawn</c> (+ <c>POSIX_SPAWN_SETSID</c>), reads child output on a background
/// thread, and writes input to the master. The command must be an absolute path
/// (<c>posix_spawn</c> does not search PATH) — callers launch a login shell that execs the
/// real target so env/PATH resolve regardless of how the app was started.
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
	public void Start(TerminalStartInfo startInfo) {
		ArgumentNullException.ThrowIfNull(startInfo);
		if (!Path.IsPathRooted(startInfo.Command)) {
			throw new ArgumentException($"Command must be an absolute path: '{startInfo.Command}'.", nameof(startInfo));
		}

		lock (_gate) {
			if (_running) {
				throw new InvalidOperationException("Terminal already started.");
			}

			int master = posix_openpt(O_RDWR | O_NOCTTY);
			if (master < 0) {
				throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()}).");
			}

			try {
				// macOS gets this via POSIX_SPAWN_CLOEXEC_DEFAULT; Linux has no such spawn flag, so mark
				// the master close-on-exec explicitly to keep it from leaking into the spawned child.
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

				_pid = SpawnChild(startInfo, slavePath);
				_masterFd = master;
				_running = true;
			} catch {
				close(master);
				throw;
			}

			Resize(startInfo.Columns, startInfo.Rows);

			_readThread = new Thread(ReadLoop) { IsBackground = true, Name = "weavie-pty-read" };
			_readThread.Start();
		}
	}

	private static int SpawnChild(TerminalStartInfo startInfo, string slavePath) {
		// posix_spawn_file_actions_t / posix_spawnattr_t are objects *_init initializes in place: on glibc
		// they are large structs, on macOS an opaque pointer. Hand each a zeroed, generously sized native
		// buffer and pass that pointer by value (see NativeMethods) — passing the address of an 8-byte
		// managed slot would let glibc's *_init overrun it ("stack smashing detected").
		nint fileActions = Marshal.AllocHGlobal(SpawnObjectSize);
		nint attr = Marshal.AllocHGlobal(SpawnObjectSize);
		try {
			Zero(fileActions, SpawnObjectSize);
			Zero(attr, SpawnObjectSize);
			posix_spawn_file_actions_init(fileActions);
			posix_spawnattr_init(attr);

			// Child: chdir into the workspace (if any), then make the slave tty fd 0/1/2
			// (after setsid -> controlling terminal).
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

	private static List<string> BuildEnvironment(TerminalStartInfo startInfo) {
		var merged = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()) {
			merged[(string)entry.Key] = entry.Value?.ToString() ?? string.Empty;
		}

		foreach (string name in startInfo.RemoveEnvironment) {
			merged.Remove(name);
		}

		foreach (var (key, value) in startInfo.Environment) {
			merged[key] = value;
		}

		return [.. merged.Select(kv => $"{kv.Key}={kv.Value}")];
	}

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

		var ws = new Winsize {
			ws_col = (ushort)Math.Clamp(columns, 1, ushort.MaxValue),
			ws_row = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
		};
		ioctl(_masterFd, TIOCSWINSZ, ref ws);
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			if (_pid > 0 && _running) {
				kill(_pid, 15); // SIGTERM — child dies, read loop sees EOF and exits
			}
			if (_masterFd >= 0) {
				close(_masterFd);
				_masterFd = -1;
			}
			_running = false;
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
