using Weavie.Core.FileSystem;

namespace Weavie.AgentClientProtocol;

internal sealed record AcpProcessInvocation(string Command, IReadOnlyList<string> Arguments) {
	public static AcpProcessInvocation Resolve(
		AcpAgentDefinition definition,
		string workingDirectory,
		IReadOnlyList<string> additionalArguments) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentNullException.ThrowIfNull(additionalArguments);
		string command = definition.Command;
		if (OperatingSystem.IsWindows() && definition.Distribution == "npx") {
			command = ResolveNpxOnPath(
				Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
				workingDirectory);
		}
		return new AcpProcessInvocation(command, [.. definition.Arguments, .. additionalArguments]);
	}

	internal static string ResolveNpxOnPath(string pathValue, string workingDirectory) {
		ArgumentNullException.ThrowIfNull(pathValue);
		string workspace = Path.GetFullPath(workingDirectory);
		foreach (string rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
			string directory = rawDirectory.Trim().Trim('"');
			if (!Path.IsPathFullyQualified(directory)) continue;
			string candidate;
			try {
				candidate = Path.GetFullPath(Path.Combine(directory, "npx.cmd"));
			} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
				continue;
			}
			if (PathBoundary.Contains(workspace, candidate, StringComparison.OrdinalIgnoreCase)) continue;
			if (File.Exists(candidate)) return candidate;
		}
		throw new FileNotFoundException("The npx.cmd dependency was not found on PATH.", "npx.cmd");
	}

	internal static string SystemCommandProcessor(string systemDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(systemDirectory);
		return Path.Combine(Path.GetFullPath(systemDirectory), "cmd.exe");
	}
}
