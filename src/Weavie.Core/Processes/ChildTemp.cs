using System.Diagnostics;
using System.Globalization;

namespace Weavie.Core.Processes;

/// <summary>
/// Each owned child's private temp directory, under one root per Weavie process. The child sees it as its system
/// temp path, so whatever it strands there — a .NET runtime's diagnostic pipes after a forced stop — goes with the
/// directory once the child is reaped. See docs/specs/process-supervisor.md.
/// </summary>
public static partial class ChildTemp {
	/// <summary>Carries the user's own temp path through owned children, so their terminals can restore it.</summary>
	public const string UserTempVariable = "WEAVIE_USER_TEMP";

	private static readonly string[] Variables = OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];
	private static readonly Lazy<string> OwnRoot = new(() =>
		Path.Combine(Path.GetTempPath(), RootName(Environment.ProcessId, StartStamp(Environment.ProcessId)!.Value)));
	private static long _next;

	/// <summary>This process's root, holding one directory per live owned child.</summary>
	public static string Root => OwnRoot.Value;

	/// <summary>Creates a child's directory and points <paramref name="info"/>'s temp variables at it.</summary>
	internal static string Assign(ProcessStartInfo info) {
		string directory = Directory.CreateDirectory(Path.Combine(
			Root, Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture))).FullName;
		info.Environment.TryAdd(UserTempVariable, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
		foreach (string variable in Variables) info.Environment[variable] = directory;
		return directory;
	}

	/// <summary>Deletes a reaped child's directory with everything it left there.</summary>
	internal static void Release(string directory) {
		try {
			Directory.Delete(directory, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Console.WriteLine($"[process] could not remove child temp {directory}: {ex.Message}");
		}
	}

	/// <summary>Whether a child's variable is one <see cref="Assign"/> gave it, which no other process should adopt.</summary>
	public static bool IsAssigned(string name, string value) {
		ArgumentNullException.ThrowIfNull(value);
		return name == UserTempVariable
			|| (Variables.Contains(name) && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(value)) == Root);
	}

	/// <summary>Gives an interactive terminal the user's temp path rather than this owned process's private one.</summary>
	public static void RestoreUserTemp(IDictionary<string, string> environment) {
		ArgumentNullException.ThrowIfNull(environment);
		if (!environment.Remove(UserTempVariable, out string? user)) return;
		foreach (string variable in Variables) environment[variable] = user;
	}
}
