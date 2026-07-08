namespace Weavie.Hosting.Agents.Codex;

/// <summary>Resolved process-launch details for a Codex app-server child process.</summary>
internal sealed record CodexAppServerLaunch(
	string Command,
	string WorkingDirectory,
	IReadOnlyList<string> PathEntries) {
	/// <summary>Creates a launch for an unpackaged command whose resources resolve from the environment.</summary>
	public static CodexAppServerLaunch Raw(string command, string workingDirectory) =>
		new(command, workingDirectory, []);
}
