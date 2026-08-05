using System.Diagnostics;
using System.Text;

namespace Weavie.Hosting.Agents;

/// <summary>Builds redirected CLI launches that preserve login-shell and packaged-install resolution.</summary>
internal static class AgentCliProcessStartInfo {
	private static readonly Encoding StdioEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	public static ProcessStartInfo Create(
		string command,
		string workingDirectory,
		IReadOnlyList<string> arguments,
		IReadOnlyList<string> pathEntries,
		IReadOnlyDictionary<string, string> environment,
		IReadOnlyList<string> removeEnvironment) {
		ArgumentException.ThrowIfNullOrWhiteSpace(command);
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(pathEntries);
		ArgumentNullException.ThrowIfNull(environment);
		ArgumentNullException.ThrowIfNull(removeEnvironment);

		var processArguments = arguments;
		if (!OperatingSystem.IsWindows() && !Path.IsPathRooted(command)) {
			processArguments = ["-l", "-c", $"exec {ShellQuote(command)} {string.Join(' ', arguments.Select(ShellQuote))}"];
			command = LoginShellEnvironment.LoginShell();
		}

		var info = new ProcessStartInfo(command) {
			WorkingDirectory = workingDirectory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = StdioEncoding,
			StandardOutputEncoding = StdioEncoding,
			StandardErrorEncoding = StdioEncoding,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden,
		};
		foreach (string argument in processArguments) {
			info.ArgumentList.Add(argument);
		}
		foreach (var entry in environment) {
			info.Environment[entry.Key] = entry.Value;
		}
		foreach (string name in removeEnvironment) {
			info.Environment.Remove(name);
		}

		PrependPath(info, pathEntries);
		return info;
	}

	private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

	private static void PrependPath(ProcessStartInfo info, IReadOnlyList<string> entries) {
		if (entries.Count == 0) {
			return;
		}

		string key = PathKey(info.Environment);
		string existing = info.Environment.TryGetValue(key, out string? path) && path is not null
			? path
			: Environment.GetEnvironmentVariable(key) ?? string.Empty;
		var parts = entries.Where(entry => entry.Length > 0).ToList();
		if (existing.Length > 0) {
			parts.Add(existing);
		}

		info.Environment[key] = string.Join(Path.PathSeparator, parts);
	}

	private static string PathKey(IDictionary<string, string?> environment) =>
		environment.Keys.FirstOrDefault(key => string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
}
