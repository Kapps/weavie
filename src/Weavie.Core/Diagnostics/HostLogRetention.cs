using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Weavie.Core.Diagnostics;

internal static class HostLogRetention {
	internal const string Prefix = "host-";
	internal const string TimestampFormat = "yyyyMMdd-HHmmss-fffffff";

	internal static void Prune(string directory) => Prune(directory, IsRunning);

	internal static void Prune(string directory, Func<int, bool?> isRunning) {
		foreach (var file in new DirectoryInfo(directory).EnumerateFiles($"{Prefix}*.log")
			.Where(file => HasExited(file.Name, isRunning))
			.OrderByDescending(file => file.LastWriteTimeUtc).Skip(20)) {
			file.Delete();
		}
	}

	private static bool HasExited(string name, Func<int, bool?> isRunning) {
		string stem = Path.GetFileNameWithoutExtension(name);
		int separator = stem.LastIndexOf('-');
		return separator > Prefix.Length
			&& DateTime.TryParseExact(stem.AsSpan(Prefix.Length, separator - Prefix.Length), TimestampFormat,
				CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
			&& int.TryParse(stem.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
			&& pid > 0 && isRunning(pid) == false;
	}

	private static bool? IsRunning(int pid) {
		try {
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		} catch (ArgumentException) {
			return false;
		} catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or NotSupportedException) {
			return null;
		}
	}
}
