using System.Diagnostics;

namespace Weavie.Core.Processes;

/// <summary>
/// Owned children's private temp directories, under one root per Weavie process. A child sees its directory as its
/// system temp path, so whatever it strands there — a .NET runtime's diagnostic pipes after a forced stop — goes
/// with the directory once its <see cref="ChildTempScope"/> is done. See docs/specs/process-supervisor.md.
/// </summary>
public static partial class ChildTemp {
	/// <summary>Carries the user's own temp path through owned children, so their terminals can restore it.</summary>
	public const string UserTempVariable = "WEAVIE_USER_TEMP";

	private static readonly string[] Variables = OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];
	private static readonly Lazy<string> OwnRoot = new(CreateRoot);
	private static long _next = -1;

	/// <summary>The variables that name a process's system temp path on this OS.</summary>
	public static IReadOnlyList<string> TempVariables => Variables;

	/// <summary>This process's root in the user's temp, holding one directory per live scope; created on first use.</summary>
	public static string Root => OwnRoot.Value;

	/// <summary>Whether this process has created its root yet.</summary>
	public static bool RootCreated => OwnRoot.IsValueCreated;

	/// <summary>The user's own temp path: inherited from an owning Weavie, else this process's.</summary>
	public static string UserTemp =>
		Environment.GetEnvironmentVariable(UserTempVariable) ?? Path.TrimEndingDirectorySeparator(Path.GetTempPath());

	// Base-36 names stay a few characters however many children have run, keeping a child's socket paths inside
	// macOS's 104-byte cap; a name is never reused, so a straggler a child left running can't share a successor's.
	internal static string CreateDirectory() {
		long next = Interlocked.Increment(ref _next);
		string name = string.Empty;
		do {
			name = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(next % 36)] + name;
			next /= 36;
		} while (next > 0);
		return Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
	}

	// A temp path the caller chose for the child, rather than inherited from this process, stays the child's.
	internal static void Point(ProcessStartInfo info, string directory) {
		info.Environment.TryAdd(UserTempVariable, UserTemp);
		foreach (string variable in Variables) {
			if (info.Environment.TryGetValue(variable, out string? value) && value != Environment.GetEnvironmentVariable(variable)) continue;
			info.Environment[variable] = directory;
		}
	}

	internal static void Delete(string directory) {
		try {
			Directory.Delete(directory, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Console.WriteLine($"[process] could not remove child temp {directory}: {ex.Message}");
		}
	}

	/// <summary>Whether a child's variable is one Weavie gave it, which no other process should adopt.</summary>
	public static bool IsAssigned(string name, string value) {
		ArgumentNullException.ThrowIfNull(value);
		return name == UserTempVariable
			|| (Variables.Contains(name) && RootCreated
				&& Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(value)) == Root);
	}

	/// <summary>Gives an interactive terminal the user's temp path rather than this owned process's private one.</summary>
	public static void RestoreUserTemp(IDictionary<string, string> environment) {
		ArgumentNullException.ThrowIfNull(environment);
		if (!environment.Remove(UserTempVariable, out string? user)) return;
		foreach (string variable in Variables) environment[variable] = user;
	}
}
