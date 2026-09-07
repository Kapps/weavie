using System.ComponentModel;
using System.Diagnostics;

namespace Weavie.Core.Processes;

public sealed partial class OwnedProcess {
	private static OwnedProcess StartUnix(ProcessStartInfo info) {
		string command = info.FileName;
		string directory = Path.Combine(Environment.CurrentDirectory, info.WorkingDirectory);
		if (command.Contains('/', StringComparison.Ordinal)) {
			return StartUnixCommand(info, Path.Combine(directory, command));
		}
		Win32Exception? denied = null;
		if (command.Length != 0 && info.Environment.TryGetValue("PATH", out string? path) && path is not null) {
			foreach (string entry in path.Split(':')) {
				string candidate = Path.Combine(directory, entry, command);
				try {
					return StartUnixCommand(info, candidate);
				} catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 13 or 20) {
					if (ex.NativeErrorCode == 13) denied = ex;
				}
			}
		}
		throw denied ?? new Win32Exception(2, $"Could not start '{command}': executable not found on the child PATH.");
	}

	// Both .NET and posix_spawnp otherwise search the parent PATH instead of the supplied child environment.
	private static OwnedProcess StartUnixCommand(ProcessStartInfo info, string command) {
		string original = info.FileName;
		try {
			info.FileName = command;
			return OperatingSystem.IsMacOS()
				? StartMac(info)
				: new OwnedProcess(Process.Start(info) ?? throw new IOException($"Could not start '{command}'."));
		} finally {
			info.FileName = original;
		}
	}
}
