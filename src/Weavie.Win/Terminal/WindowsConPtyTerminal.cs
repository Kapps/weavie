using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Weavie.Core.Terminal;
using static Weavie.Win.Terminal.ConPtyNativeMethods;

namespace Weavie.Win.Terminal;

/// <summary>
/// Real Windows PTY via ConPTY: opens a pseudo console, launches the child attached to it with
/// <c>CreateProcess</c>, reads rendered output on a background thread, and writes keystrokes to the
/// input pipe. The Windows sibling of <see cref="PosixPtyTerminal"/>; both implement
/// <see cref="ITerminal"/> for identical host wiring. <c>CreateProcess</c> searches PATH, so the
/// command need not be absolute.
/// </summary>
internal sealed class WindowsConPtyTerminal : ITerminal {
	// How long Dispose waits for the child to exit after the pseudo console is closed, then how long it
	// waits after force-terminating. Bounded so teardown can't hang; the force step kills rather than abandons.
	private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(3);
	private static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(2);

	private readonly Lock _gate = new();
	private readonly Lock _writeGate = new();
	private nint _hPC;
	private nint _inputWrite;
	private nint _outputRead;
	private nint _hProcess;
	private nint _hThread;
	private Thread? _readThread;
	private volatile bool _running;
	private int _exitRaised;

	public event Action<byte[]>? Output;
	public event Action<int>? Exited;

	public bool IsRunning => _running;

	public void Start(TerminalStartInfo startInfo) {
		ArgumentNullException.ThrowIfNull(startInfo);

		lock (_gate) {
			if (_running) {
				throw new InvalidOperationException("Terminal already started.");
			}

			if (!CreatePipe(out nint inputRead, out nint inputWrite, 0, 0)) {
				throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed.");
			}

			if (!CreatePipe(out nint outputRead, out nint outputWrite, 0, 0)) {
				CloseHandle(inputRead);
				CloseHandle(inputWrite);
				throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed.");
			}

			try {
				var size = new Coord {
					X = (short)Math.Clamp(startInfo.Columns, 1, short.MaxValue),
					Y = (short)Math.Clamp(startInfo.Rows, 1, short.MaxValue),
				};

				int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _hPC);
				if (hr < 0) {
					Marshal.ThrowExceptionForHR(hr);
				}

				var pi = SpawnChild(startInfo, _hPC);

				// Drop our copies of the PTY-side pipe ends only AFTER the child is attached: ConHost
				// starts lazily at CreateProcess, so closing outputWrite earlier yields no output.
				// Closing our outputWrite copy also lets the read loop see EOF when the child exits
				// (ConHost holds the surviving write end).
				CloseHandle(inputRead);
				inputRead = 0;
				CloseHandle(outputWrite);
				outputWrite = 0;

				_hProcess = pi.hProcess;
				_hThread = pi.hThread;
				_inputWrite = inputWrite;
				_outputRead = outputRead;
				inputWrite = 0;
				outputRead = 0;
				_running = true;
			} catch {
				if (inputRead != 0) { CloseHandle(inputRead); }
				if (inputWrite != 0) { CloseHandle(inputWrite); }
				if (outputRead != 0) { CloseHandle(outputRead); }
				if (outputWrite != 0) { CloseHandle(outputWrite); }
				if (_hPC != 0) { ClosePseudoConsole(_hPC); _hPC = 0; }
				throw;
			}

			_readThread = new Thread(ReadLoop) { IsBackground = true, Name = "weavie-conpty-read" };
			_readThread.Start();
		}
	}

	private static unsafe ProcessInformation SpawnChild(TerminalStartInfo startInfo, nint hPC) {
		// Size the attribute list (the first call fails with ERROR_INSUFFICIENT_BUFFER and reports the size).
		nint listSize = 0;
		InitializeProcThreadAttributeList(0, 1, 0, ref listSize);

		nint attrList = Marshal.AllocHGlobal(listSize);
		nint envBlock = BuildEnvironmentBlock(startInfo);
		try {
			if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize)) {
				throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
			}

			// For PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE the attribute value IS the HPCON handle itself,
			// passed directly as lpValue (NOT a pointer). Passing &hPC launches the child detached from
			// the pseudo console, so ConHost tears down and the output pipe breaks.
			if (!UpdateProcThreadAttribute(
					attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, IntPtr.Size, 0, 0)) {
				throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
			}

			var startupInfo = new StartupInfoEx { lpAttributeList = attrList };
			startupInfo.StartupInfo.cb = sizeof(StartupInfoEx);

			// Opt out of console standard-handle inheritance. When weavie's stdio is redirected (launched
			// from a terminal, under `dotnet run`, or piped) the console subsystem otherwise duplicates
			// those non-console handles into the child's std slots (even with bInheritHandles=false), where
			// they win over the pseudoconsole — so claude reads a non-TTY pipe, falls back to `--print`, and
			// dumps output onto weavie's stdout. STARTF_USESTDHANDLES with NULL handles forces the child to
			// attach to the pseudoconsole instead and get a real console. A no-op when weavie is console-less.
			startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
			startupInfo.StartupInfo.hStdInput = 0;
			startupInfo.StartupInfo.hStdOutput = 0;
			startupInfo.StartupInfo.hStdError = 0;

			bool ok = CreateProcess(
				lpApplicationName: null,
				lpCommandLine: BuildCommandLine(startInfo),
				lpProcessAttributes: 0,
				lpThreadAttributes: 0,
				bInheritHandles: false,
				dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
				lpEnvironment: envBlock,
				lpCurrentDirectory: string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
				lpStartupInfo: ref startupInfo,
				lpProcessInformation: out var pi);

			if (!ok) {
				throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess('{startInfo.Command}') failed.");
			}

			return pi;
		} finally {
			if (attrList != 0) {
				DeleteProcThreadAttributeList(attrList);
				Marshal.FreeHGlobal(attrList);
			}
			if (envBlock != 0) {
				Marshal.FreeHGlobal(envBlock);
			}
		}
	}

	/// <summary>
	/// Builds the double-null-terminated UTF-16 environment block: current env, minus
	/// <see cref="TerminalStartInfo.RemoveEnvironment"/>, plus <see cref="TerminalStartInfo.Environment"/>.
	/// Names are sorted case-insensitively as Win32 requires.
	/// </summary>
	private static nint BuildEnvironmentBlock(TerminalStartInfo startInfo) {
		var merged = startInfo.BuildEnvironment(StringComparer.OrdinalIgnoreCase);
		var sb = new StringBuilder();
		foreach (string? key in merged.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
			sb.Append(key).Append('=').Append(merged[key]).Append('\0');
		}

		// StringToHGlobalUni appends the final terminator, completing the required "...\0\0".
		return Marshal.StringToHGlobalUni(sb.ToString());
	}

	private static string BuildCommandLine(TerminalStartInfo startInfo) {
		var sb = new StringBuilder();
		AppendArg(sb, startInfo.Command);
		foreach (string arg in startInfo.Arguments) {
			sb.Append(' ');
			AppendArg(sb, arg);
		}

		return sb.ToString();
	}

	// Quote an argument per the Win32 CommandLineToArgvW rules (only when needed).
	private static void AppendArg(StringBuilder sb, string arg) {
		if (arg.Length > 0 && !arg.AsSpan().ContainsAny(" \t\n\v\"")) {
			sb.Append(arg);
			return;
		}

		sb.Append('"');
		for (int i = 0; i < arg.Length; i++) {
			int backslashes = 0;
			while (i < arg.Length && arg[i] == '\\') {
				backslashes++;
				i++;
			}

			if (i == arg.Length) {
				sb.Append('\\', backslashes * 2);
				break;
			}

			if (arg[i] == '"') {
				sb.Append('\\', (backslashes * 2) + 1).Append('"');
			} else {
				sb.Append('\\', backslashes).Append(arg[i]);
			}
		}

		sb.Append('"');
	}

	public void Write(byte[] data) {
		ArgumentNullException.ThrowIfNull(data);
		if (!_running || _inputWrite == 0 || data.Length == 0) {
			return;
		}

		lock (_writeGate) {
			WriteAll(_inputWrite, data);
		}
	}

	private static unsafe void WriteAll(nint handle, byte[] data) {
		fixed (byte* p = data) {
			int offset = 0;
			while (offset < data.Length) {
				if (!WriteFile(handle, p + offset, (uint)(data.Length - offset), out uint written, 0) || written == 0) {
					break;
				}

				offset += (int)written;
			}
		}
	}

	public void Resize(int columns, int rows) {
		if (_hPC == 0) {
			return;
		}

		var size = new Coord {
			X = (short)Math.Clamp(columns, 1, short.MaxValue),
			Y = (short)Math.Clamp(rows, 1, short.MaxValue),
		};
		ResizePseudoConsole(_hPC, size);
	}

	private unsafe void ReadLoop() {
		byte[] buffer = new byte[8192];
		try {
			fixed (byte* p = buffer) {
				while (true) {
					if (!ReadFile(_outputRead, p, (uint)buffer.Length, out uint read, 0) || read == 0) {
						break; // 0 bytes / broken pipe: ConPTY torn down, child gone
					}

					byte[] slice = new byte[read];
					Buffer.BlockCopy(buffer, 0, slice, 0, (int)read);
					Output?.Invoke(slice);
				}
			}
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] conpty read loop error: {ex.Message}");
		} finally {
			RaiseExited();
		}
	}

	private void RaiseExited() {
		if (Interlocked.Exchange(ref _exitRaised, 1) != 0) {
			return;
		}

		nint process = _hProcess;
		int code = 0;
		if (process != 0) {
			WaitForSingleObject(process, 2000);
			if (GetExitCodeProcess(process, out uint exitCode)) {
				code = unchecked((int)exitCode);
			}
		}

		_running = false;
		Exited?.Invoke(code);
	}

	public void Dispose() {
		nint process;
		Thread? readThread;
		lock (_gate) {
			// Closing the pseudo console signals the child to exit gracefully (and tear down its descendants);
			// the read loop then sees EOF and RaiseExited reaps the child.
			if (_hPC != 0) {
				ClosePseudoConsole(_hPC);
				_hPC = 0;
			}

			process = _hProcess;
			readThread = _readThread;
		}

		// Block until the child has exited, so a worktree removal following teardown can't race a process that
		// still has the worktree as its cwd. The read loop's RaiseExited waits on the process and ends the
		// thread; if the child ignores the console close, force it to keep teardown bounded. Done off _gate so
		// the read loop's final OnOutput can run, and skipped if we are the read thread (avoids self-joining).
		if (readThread is { IsAlive: true } && Environment.CurrentManagedThreadId != readThread.ManagedThreadId) {
			if (!readThread.Join(GracefulExitTimeout) && process != 0) {
				TerminateProcess(process, 1);
				readThread.Join(ForcedExitTimeout);
			}
		}

		lock (_gate) {
			if (_hProcess != 0) {
				CloseHandle(_hProcess);
				_hProcess = 0;
			}

			if (_hThread != 0) {
				CloseHandle(_hThread);
				_hThread = 0;
			}

			if (_inputWrite != 0) {
				CloseHandle(_inputWrite);
				_inputWrite = 0;
			}

			if (_outputRead != 0) {
				CloseHandle(_outputRead);
				_outputRead = 0;
			}

			_running = false;
		}
	}
}
