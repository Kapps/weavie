using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Processes;

namespace Weavie.AcpDistribution;

/// <summary>Installs one npm package into a directory Weavie owns.</summary>
internal interface INpmInstaller {
	/// <summary>Installs <paramref name="package"/> into the empty <paramref name="directory"/>.</summary>
	Task InstallAsync(string package, string directory, CancellationToken ct);
}

/// <summary>Runs the user's npm, isolated from every project's package.json and .npmrc.</summary>
internal sealed class NpmInstaller : INpmInstaller {
	public async Task InstallAsync(string package, string directory, CancellationToken ct) {
		AcpNpmPackage.ValidateSpec(package);
		// An own manifest makes the directory npm's project root, so no enclosing package.json or .npmrc applies.
		await File.WriteAllTextAsync(Path.Combine(directory, "package.json"), "{}", ct).ConfigureAwait(false);
		string[] install = [
			"install", "--location=project", "--save", "--no-audit", "--no-fund", "--no-update-notifier",
			"--min-release-age=0", "--", package,
		];
		var (command, arguments) = OperatingSystem.IsWindows()
			? WindowsCommandLine.BatchInvocation("npm.cmd", install)
			: ("npm", install);
		var result = await ProcessCapture.RunAsync(new ProcessCaptureRequest {
			FileName = command,
			Arguments = arguments,
			WorkingDirectory = directory,
		}, ct).ConfigureAwait(false);
		if (result.StartFailure is { } failure) {
			throw new InvalidOperationException($"npm could not be started: {failure.Message}", failure);
		}
		if (result.ExitCode != 0) {
			throw new InvalidOperationException($"npm install {package} failed (exit {result.ExitCode}):\n{result.StdErr.Trim()}");
		}
	}
}

/// <summary>Resolves and launches the executable an installed npm package runs, the way <c>npx</c> does.</summary>
internal static class AcpNpmPackage {
	/// <summary>Rejects a package spec that could break out of a Windows command line.</summary>
	public static void ValidateSpec(string package) {
		if (string.IsNullOrEmpty(package) || package[0] == '-' || package.Any(character => !char.IsAsciiLetterOrDigit(character)
			&& character is not '@' and not '_' and not '.' and not '/' and not ':' and not '=' and not '+'
				and not '-')) {
			throw new InvalidDataException($"The npm package '{package}' cannot be installed safely.");
		}
	}

	/// <summary>The absolute executable of the one package installed in <paramref name="directory"/>.</summary>
	public static string Executable(string directory) {
		using var manifest = Read(Path.Combine(directory, "package.json"));
		string name = manifest.RootElement.TryGetProperty("dependencies", out var dependencies)
			&& dependencies.ValueKind == JsonValueKind.Object
			&& dependencies.EnumerateObject().ToArray() is [var only]
				? only.Name
				: throw new InvalidDataException("npm did not record exactly one installed package.");
		string root = Path.Combine(directory, "node_modules", name);
		using var package = Read(Path.Combine(root, "package.json"));
		string unscoped = name[(name.LastIndexOf('/') + 1)..];
		string? script = package.RootElement.TryGetProperty("bin", out var bin) ? bin.ValueKind switch {
			JsonValueKind.String => bin.GetString(),
			JsonValueKind.Object when bin.EnumerateObject().Select(entry => entry.Value.GetString()).Distinct().ToArray()
				is [var alias] => alias,
			JsonValueKind.Object when bin.TryGetProperty(unscoped, out var named) => named.GetString(),
			_ => null,
		} : null;
		if (string.IsNullOrWhiteSpace(script)) {
			throw new InvalidDataException($"The npm package '{name}' declares no single executable.");
		}
		string executable = Path.GetFullPath(Path.Combine(root, script));
		if (!PathBoundary.Contains(root, executable, PathIdentity.Comparison) || !File.Exists(executable)) {
			throw new InvalidDataException($"The npm package '{name}' executable '{script}' is not inside the package.");
		}
		return executable;
	}

	/// <summary>
	/// The command and leading arguments that run <paramref name="executable"/>: its <c>#!</c> interpreter, as npm's
	/// Windows shims honor it, or the file itself when it is a native binary.
	/// </summary>
	public static (string Command, IReadOnlyList<string> Arguments) Launch(string executable) {
		string line;
		using (var reader = new StreamReader(executable)) {
			if ((reader.Read(), reader.Read()) is not ('#', '!')) return (executable, []);
			line = reader.ReadLine() ?? string.Empty;
		}
		string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (words.Length > 1 && Path.GetFileName(words[0]) == "env") words = words[1] == "-S" ? words[2..] : words[1..];
		return words is [var interpreter, .. var options]
			? (interpreter, [.. options, executable])
			: throw new InvalidDataException($"The npm executable '{executable}' has an empty #! line.");
	}

	private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
}
