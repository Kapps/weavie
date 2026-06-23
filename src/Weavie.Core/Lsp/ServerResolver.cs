namespace Weavie.Core.Lsp;

/// <summary>
/// A launch command resolved to a concrete executable. Windows cmd/bat shims are wrapped through
/// <c>cmd.exe /c</c> (<c>CreateProcess</c> can't start them directly); native executables launch as-is.
/// </summary>
/// <param name="FileName">The executable to launch (the wrapper for cmd/bat shims, else the server).</param>
/// <param name="Arguments">The full argument list, including any wrapper prefix.</param>
/// <param name="ServerPath">The resolved path of the server itself, for logging/status.</param>
public sealed record ResolvedCommand(string FileName, IReadOnlyList<string> Arguments, string ServerPath);

/// <summary>
/// Bring-your-own server resolution: finds a <see cref="LanguageServerDescriptor"/>'s server on <c>PATH</c>.
/// Resolution follows the descriptor's candidate order; the first one found wins.
/// </summary>
public static class ServerResolver {
	/// <summary>
	/// Resolves the first launchable candidate of <paramref name="descriptor"/> on <c>PATH</c>, or
	/// <see langword="null"/> if none of its candidates are installed.
	/// </summary>
	/// <param name="descriptor">The language server recipe to resolve.</param>
	public static ResolvedCommand? Resolve(LanguageServerDescriptor descriptor) {
		ArgumentNullException.ThrowIfNull(descriptor);

		foreach (var candidate in descriptor.Candidates) {
			string? path = FindOnPath(candidate.Command);
			if (path is not null) {
				return BuildCommand(path, candidate.Arguments);
			}
		}

		return null;
	}

	/// <summary>
	/// Locates <paramref name="command"/> on <c>PATH</c>, returning its full path or <see langword="null"/>. On
	/// Windows probes <c>.exe</c>/<c>.cmd</c>/<c>.bat</c> but not <c>.ps1</c>, which <c>CreateProcess</c> can't launch.
	/// </summary>
	/// <param name="command">A command name (no extension needed) or an explicit path.</param>
	public static string? FindOnPath(string command) {
		if (string.IsNullOrWhiteSpace(command)) {
			return null;
		}

		if (Path.IsPathRooted(command)
			|| command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) {
			return File.Exists(command) ? command : null;
		}

		string? pathVar = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(pathVar)) {
			return null;
		}

		string[] dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (string dir in dirs) {
			foreach (string name in ExtensionVariants(command)) {
				string full;
				try {
					full = Path.Combine(dir, name);
				} catch (ArgumentException) {
					break; // a PATH entry with invalid path characters — skip the whole dir
				}

				if (File.Exists(full)) {
					return full;
				}
			}
		}

		return null;
	}

	private static ResolvedCommand BuildCommand(string serverPath, IReadOnlyList<string> arguments) {
		string ext = Path.GetExtension(serverPath);
		bool isShim = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);

		if (OperatingSystem.IsWindows() && isShim) {
			// CreateProcess can't run .cmd/.bat directly; route through the command interpreter.
			string? comSpec = Environment.GetEnvironmentVariable("ComSpec");
			comSpec = string.IsNullOrEmpty(comSpec) ? "cmd.exe" : comSpec;
			var wrapped = new List<string>(arguments.Count + 2) { "/c", serverPath };
			wrapped.AddRange(arguments);
			return new ResolvedCommand(comSpec, wrapped, serverPath);
		}

		return new ResolvedCommand(serverPath, arguments, serverPath);
	}

	private static IEnumerable<string> ExtensionVariants(string command) {
		if (!OperatingSystem.IsWindows() || Path.HasExtension(command)) {
			yield return command;
			yield break;
		}

		yield return command + ".exe";
		yield return command + ".cmd";
		yield return command + ".bat";
	}
}
