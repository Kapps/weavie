namespace Weavie.Hosting.Agents.Codex;

/// <summary>Resolved process-launch details for an installed Codex CLI.</summary>
internal sealed record CodexCliLaunch(
	string Command,
	string WorkingDirectory,
	IReadOnlyList<string> PathEntries) {
	/// <summary>Creates a launch for an unpackaged command whose resources resolve from the environment.</summary>
	public static CodexCliLaunch Raw(string command, string workingDirectory) =>
		new(command, workingDirectory, []);
}
