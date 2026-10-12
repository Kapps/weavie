using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Weavie.Core.Processes;

public static partial class ChildTemp {
	private const string RootPrefix = "weavie-";

	/// <summary>Deletes the roots, in the system temp path, of Weavie processes that are no longer running.</summary>
	public static void ReclaimOrphans(Action<string> log) => ReclaimOrphans(Path.GetTempPath(), log);

	/// <summary>
	/// Deletes each root in <paramref name="temp"/> whose owner is gone: no process has its pid, or the one that
	/// does started at another time. A root is never judged by age.
	/// </summary>
	internal static void ReclaimOrphans(string temp, Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		foreach (string root in Directory.EnumerateDirectories(temp, RootPrefix + "*")) {
			string[] parts = Path.GetFileName(root)[RootPrefix.Length..].Split('-');
			if (parts.Length != 2
				|| !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
				|| !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long stamp)
				|| IsRunning(pid, stamp)) continue;
			try {
				Directory.Delete(root, recursive: true);
				log($"reclaimed child temp of exited process {pid}: {root}");
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				log($"could not reclaim child temp {root}: {ex.Message}");
			}
		}
	}

	internal static string RootName(int pid, long stamp) =>
		string.Create(CultureInfo.InvariantCulture, $"{RootPrefix}{pid}-{stamp}");

	/// <summary>
	/// When <paramref name="pid"/> started, exactly and stably, or null when no such process exists: Linux's
	/// clock ticks since boot (the runtime's StartTime drifts with its boot-time estimate), else Unix seconds.
	/// </summary>
	internal static long? StartStamp(int pid) {
		if (OperatingSystem.IsLinux()) {
			string stat;
			try {
				stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
			} catch (IOException) {
				return null;
			}
			// Field 22 (starttime), counted after the parenthesized command name, which may itself hold spaces.
			return long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], CultureInfo.InvariantCulture);
		}
		try {
			using var process = Process.GetProcessById(pid);
			return new DateTimeOffset(process.StartTime).ToUnixTimeSeconds();
		} catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
			return null;
		}
	}

	internal static bool IsRunning(int pid, long stamp) {
		try {
			return StartStamp(pid) == stamp;
		} catch (Win32Exception) {
			// The process exists but belongs to someone this user cannot inspect, so its root is not ours to judge.
			return true;
		}
	}
}
