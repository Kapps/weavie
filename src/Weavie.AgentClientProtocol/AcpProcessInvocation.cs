using System.Text.RegularExpressions;
using Weavie.Core.FileSystem;
using Weavie.Core.Processes;

namespace Weavie.AgentClientProtocol;

internal sealed partial record AcpProcessInvocation(string Command, IReadOnlyList<string> Arguments) {
	public static AcpProcessInvocation Resolve(
		AcpAgentDefinition definition,
		string workingDirectory,
		IReadOnlyList<string> additionalArguments) =>
		Resolve(
			definition,
			workingDirectory,
			additionalArguments,
			OperatingSystem.IsWindows(),
			Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

	internal static AcpProcessInvocation Resolve(
		AcpAgentDefinition definition,
		string workingDirectory,
		IReadOnlyList<string> additionalArguments,
		bool windows,
		string pathValue) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentNullException.ThrowIfNull(additionalArguments);
		ArgumentNullException.ThrowIfNull(pathValue);
		string command = definition.Command;
		string[] arguments = [.. definition.Arguments, .. additionalArguments];
		if (definition.Distribution != "npx") return new AcpProcessInvocation(command, arguments);
		if (arguments.Length >= 2 && arguments[0] == "--yes") {
			arguments[1] = BoundNpmPackageSpec(arguments[1]);
		}
		if (windows) {
			command = ResolveNpxOnPath(pathValue, workingDirectory);
		}
		return new AcpProcessInvocation(command, arguments);
	}

	public static AcpProcessInvocation ResolveRedirectedProcess(
		AcpAgentDefinition definition,
		string workingDirectory,
		IReadOnlyList<string> additionalArguments) {
		var invocation = Resolve(definition, workingDirectory, additionalArguments);
		return OperatingSystem.IsWindows() && definition.Distribution == "npx"
			? WrapWindowsNpx(invocation.Command, invocation.Arguments, Environment.SystemDirectory)
			: invocation;
	}

	internal static string BoundNpmPackageSpec(string packageSpec) {
		ArgumentNullException.ThrowIfNull(packageSpec);
		int separator = packageSpec.LastIndexOf('@');
		if (separator <= 0 || !ExactSemanticVersion().IsMatch(packageSpec.AsSpan(separator + 1))) {
			return packageSpec;
		}
		return string.Concat(packageSpec.AsSpan(0, separator + 1), "<=", packageSpec.AsSpan(separator + 1));
	}

	internal static AcpProcessInvocation WrapWindowsNpx(
		string command,
		IReadOnlyList<string> arguments,
		string systemDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(command);
		ArgumentNullException.ThrowIfNull(arguments);
		string commandLine =
			$"\"{command}\" {string.Join(' ', arguments.Select(WindowsCommandLine.EscapeInputRedirection))}";
		return new AcpProcessInvocation(
			SystemCommandProcessor(systemDirectory),
			["/d", "/s", "/v:off", "/c", commandLine]);
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

	[GeneratedRegex(
		@"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
		RegexOptions.CultureInvariant)]
	private static partial Regex ExactSemanticVersion();
}
