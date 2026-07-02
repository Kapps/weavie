namespace Weavie.Core.Terminal;

/// <summary>How to launch the PTY child.</summary>
public sealed record TerminalStartInfo {
	/// <summary>The executable to launch in the PTY.</summary>
	public required string Command { get; init; }

	/// <summary>Arguments passed to <see cref="Command"/>.</summary>
	public IReadOnlyList<string> Arguments { get; init; } = [];

	/// <summary>Working directory for the child; the current directory when null.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>Environment variables to set/override on top of the current process environment.</summary>
	public IReadOnlyDictionary<string, string> Environment { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>Environment variable names to remove from the child (e.g. ANTHROPIC_API_KEY).</summary>
	public IReadOnlyList<string> RemoveEnvironment { get; init; } = [];

	/// <summary>Initial PTY width in columns.</summary>
	public int Columns { get; init; } = 80;

	/// <summary>Initial PTY height in rows.</summary>
	public int Rows { get; init; } = 24;

	/// <summary>
	/// The child's full environment: the current process environment, minus <see cref="RemoveEnvironment"/>,
	/// plus <see cref="Environment"/> (overriding). <paramref name="comparer"/> sets key case-sensitivity
	/// (OrdinalIgnoreCase on Windows, Ordinal on POSIX).
	/// </summary>
	public Dictionary<string, string> BuildEnvironment(StringComparer comparer) {
		ArgumentNullException.ThrowIfNull(comparer);
		var merged = new Dictionary<string, string>(comparer);
		foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()) {
			merged[(string)entry.Key] = entry.Value?.ToString() ?? string.Empty;
		}

		foreach (string name in RemoveEnvironment) {
			merged.Remove(name);
		}

		foreach (var (key, value) in Environment) {
			merged[key] = value;
		}

		return merged;
	}
}

/// <summary>
/// The terminal seam. Real implementation = a PTY running a real process; test fake = scripted bytes.
/// </summary>
public interface ITerminal : IDisposable {
	/// <summary>Raised with raw bytes the child wrote to the PTY (off the UI thread).</summary>
	event Action<byte[]>? Output;

	/// <summary>Raised once with the child's exit code when it terminates.</summary>
	event Action<int>? Exited;

	/// <summary>Whether the child process is currently running.</summary>
	bool IsRunning { get; }

	/// <summary>
	/// Whether the PTY's foreground process group differs from the child itself — a job (build, dev
	/// server) is running in the pane. POSIX job-control signal; Windows ConPTY has none and always
	/// reports false. Used by the update drain gate (docs/specs/runner-auto-update.md).
	/// </summary>
	bool HasForegroundJob { get; }

	/// <summary>Launches the child described by <paramref name="startInfo"/> in a fresh PTY.</summary>
	void Start(TerminalStartInfo startInfo);

	/// <summary>Writes input bytes (keystrokes) to the child.</summary>
	void Write(byte[] data);

	/// <summary>Resizes the PTY to <paramref name="columns"/> by <paramref name="rows"/>.</summary>
	void Resize(int columns, int rows);
}
