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
	private static readonly Lock SlotGate = new();
	private static readonly HashSet<int> Slots = [];

	/// <summary>The variables that name a process's system temp path on this OS.</summary>
	public static IReadOnlyList<string> TempVariables => Variables;

	/// <summary>This process's root, holding one directory per live scope; created on first use.</summary>
	public static string Root => OwnRoot.Value;

	/// <summary>Whether this process has created its root yet.</summary>
	public static bool RootCreated => OwnRoot.IsValueCreated;

	/// <summary>The user's own temp path: inherited from an owning Weavie, else this process's.</summary>
	public static string UserTemp =>
		Environment.GetEnvironmentVariable(UserTempVariable) ?? Path.TrimEndingDirectorySeparator(Path.GetTempPath());

	// Directories reuse the lowest free slot: names stay one or two characters however many children have run,
	// keeping a child's socket paths inside macOS's 104-byte cap.
	internal static (string Directory, int Slot) CreateDirectory() {
		int slot = 0;
		lock (SlotGate) {
			while (!Slots.Add(slot)) slot++;
		}
		try {
			return (Directory.CreateDirectory(Path.Combine(Root, SlotName(slot))).FullName, slot);
		} catch {
			lock (SlotGate) Slots.Remove(slot);
			throw;
		}
	}

	internal static void Point(ProcessStartInfo info, string directory) {
		info.Environment.TryAdd(UserTempVariable, UserTemp);
		foreach (string variable in Variables) info.Environment[variable] = directory;
	}

	// A directory that could not be deleted keeps its slot, so no later child inherits what it holds.
	internal static void Delete(string directory, int slot) {
		try {
			Directory.Delete(directory, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Console.WriteLine($"[process] could not remove child temp {directory}: {ex.Message}");
			return;
		}
		lock (SlotGate) Slots.Remove(slot);
	}

	private static string SlotName(int slot) {
		string name = string.Empty;
		do {
			name = "0123456789abcdefghijklmnopqrstuvwxyz"[slot % 36] + name;
			slot /= 36;
		} while (slot > 0);
		return name;
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
