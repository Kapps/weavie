using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Weavie.Core.Processes;

public sealed partial class OwnedProcess {
	private OwnedProcess(int pid, int input, int output, int error, ProcessStartInfo info) {
		Id = pid;
		StandardInput = new StreamWriter(OpenPipe(input, FileAccess.Write), info.StandardInputEncoding ?? new UTF8Encoding(false)) { AutoFlush = true };
		StandardOutput = new StreamReader(OpenPipe(output, FileAccess.Read), info.StandardOutputEncoding ?? Encoding.UTF8);
		StandardError = new StreamReader(OpenPipe(error, FileAccess.Read), info.StandardErrorEncoding ?? Encoding.UTF8);
		if (!info.RedirectStandardInput) StandardInput.Close();
		new Thread(WaitMac) { IsBackground = true, Name = $"weavie-process-{pid}" }.Start();
	}

	private static OwnedProcess StartMac(ProcessStartInfo info) {
		if (info.Arguments.Length != 0) {
			throw new ArgumentException("Owned children use ArgumentList, not an unparsed argument string.", nameof(info));
		}
		using var argv = new NativeUtf8Array([info.FileName, .. info.ArgumentList]);
		using var envp = new NativeUtf8Array(info.Environment.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}={pair.Value}"));
		int rc = weavie_process_spawn(info.FileName, argv.Pointer, envp.Pointer, info.WorkingDirectory,
			out int input, out int output, out int error, out int pid);
		if (rc != 0) throw new Win32Exception(-rc, $"Could not start '{info.FileName}' (errno {-rc}).");
		return new OwnedProcess(pid, input, output, error, info);
	}

	private static FileStream OpenPipe(int fd, FileAccess access) =>
		new(new SafeFileHandle(fd, ownsHandle: true), access);

	private void WaitMac() {
		int rc = weavie_process_wait(Id);
		lock (_gate) {
			int code = 0;
			if (rc == 0) rc = weavie_process_reap(Id, out code);
			if (rc == 0) _exit.TrySetResult(code);
			else _exit.TrySetException(new Win32Exception(-rc, $"Could not reap child {Id}."));
		}
	}

	[LibraryImport("libweavie_pty", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int weavie_process_spawn(string path, nint argv, nint envp, string cwd,
		out int input, out int output, out int error, out int pid);
	[LibraryImport("libweavie_pty")]
	private static partial int weavie_process_wait(int pid);
	[LibraryImport("libweavie_pty")]
	private static partial int weavie_process_reap(int pid, out int code);
}
