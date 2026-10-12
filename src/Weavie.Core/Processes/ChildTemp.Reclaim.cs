using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Weavie.Core.Processes;

public static partial class ChildTemp {
	private const string RootPrefix = "wv";
	private const string OwnerFile = "owner";
	private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

	/// <summary>Deletes the roots, in the user's temp, of Weavie processes that are no longer running.</summary>
	public static void ReclaimOrphans(Action<string> log) => ReclaimOrphans(UserTemp, log);

	/// <summary>
	/// Deletes each root in <paramref name="temp"/> whose owner is gone: no process has its pid, or the one that
	/// does started at another time. A directory without a readable owner record is not a root and stays.
	/// </summary>
	internal static void ReclaimOrphans(string temp, Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		foreach (string root in Directory.EnumerateDirectories(temp, RootPrefix + "*")) {
			if (Owner(root) is not (int pid, long stamp) || IsRunning(pid, stamp)) continue;
			try {
				Directory.Delete(root, recursive: true);
				log($"reclaimed child temp of exited process {pid}: {root}");
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				log($"could not reclaim child temp {root}: {ex.Message}");
			}
		}
	}

	// In the user's temp, so an owned Weavie (the runner's worker) doesn't nest its root; short, because children's
	// socket paths sit beneath it. Unpredictable and owner-only like mkdtemp: a directory someone else prepared
	// fails the mode check, or the owner write, loudly.
	private static string CreateRoot() {
		string root = Path.Combine(UserTemp, RootPrefix + RandomNumberGenerator.GetString("0123456789abcdefghijklmnopqrstuvwxyz", 8));
		if (Directory.Exists(root)) throw new IOException($"Child temp root {root} already exists.");
		if (OperatingSystem.IsWindows()) {
			Directory.CreateDirectory(root);
		} else {
			Directory.CreateDirectory(root, OwnerOnly);
			if (File.GetUnixFileMode(root) != OwnerOnly) throw new IOException($"Child temp root {root} is not owner-only.");
		}
		try {
			using var owner = new FileStream(Path.Combine(root, OwnerFile), FileMode.CreateNew, FileAccess.Write);
			owner.Write(Encoding.UTF8.GetBytes(string.Create(
				CultureInfo.InvariantCulture, $"{Environment.ProcessId} {StartStamp(Environment.ProcessId)!.Value}")));
			AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveIfIdle(root);
			return root;
		} catch {
			Directory.Delete(root, recursive: true);
			throw;
		}
	}

	// A clean exit takes the root along unless a scope outlived it; that one waits for the next startup's reclaim.
	private static void RemoveIfIdle(string root) {
		if (Directory.Exists(root) && !Directory.EnumerateDirectories(root).Any()) Delete(root);
	}

	private static (int Pid, long Stamp)? Owner(string root) {
		string text;
		try {
			text = File.ReadAllText(Path.Combine(root, OwnerFile));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return null;
		}
		string[] parts = text.Split(' ');
		return parts.Length == 2
			&& int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
			&& long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long stamp)
				? (pid, stamp)
				: null;
	}

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
		} catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException) {
			// The process exists but belongs to someone this user cannot inspect, so its root is not ours to judge.
			return true;
		}
	}
}
