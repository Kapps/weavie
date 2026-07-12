using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Resolves configured Codex binaries into package-aware app-server launch details.</summary>
internal static class CodexInstallResolver {
	public static CodexAppServerLaunch Resolve(string command, string workspace) {
		ArgumentException.ThrowIfNullOrEmpty(command);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		string? executable = ExecutableFinder.FindOnPath(command);
		if (executable is null) {
			if (!OperatingSystem.IsWindows() && !Path.IsPathRooted(command) && !command.Contains(Path.DirectorySeparatorChar)) {
				// Match Claude's POSIX behavior: a bare command may only exist on the login shell's PATH (version
				// managers like nvm/asdf/mise); the app-server launcher's login-shell wrapper resolves it.
				return CodexAppServerLaunch.Raw(command, workspace);
			}

			throw new InvalidOperationException($"Weavie could not find the configured Codex binary: {command}");
		}

		string realExecutable = ResolveFinalTarget(executable);
		if (TryPackageLaunch(realExecutable, out var launch)) {
			return launch;
		}

		return CodexAppServerLaunch.Raw(executable, workspace);
	}

	private static bool TryPackageLaunch(string executable, out CodexAppServerLaunch launch) {
		launch = CodexAppServerLaunch.Raw(executable, Path.GetDirectoryName(executable) ?? Directory.GetCurrentDirectory());
		var packageRoot = Directory.GetParent(executable)?.Parent;
		if (packageRoot is null) {
			return false;
		}

		string manifestPath = Path.Combine(packageRoot.FullName, "codex-package.json");
		if (!File.Exists(manifestPath)) {
			return false;
		}

		using var manifest = ReadManifest(manifestPath);
		string entrypoint = manifest.RootElement.GetStringOrEmpty("entrypoint");
		if (entrypoint.Length == 0) {
			throw new InvalidOperationException($"The Codex package manifest is missing entrypoint: {manifestPath}");
		}

		string command = Path.GetFullPath(Path.Combine(packageRoot.FullName, entrypoint));
		if (!File.Exists(command)) {
			throw new InvalidOperationException($"The Codex package entrypoint does not exist: {command}");
		}

		List<string> pathEntries = [];
		AddPackageDirectory(manifest.RootElement, packageRoot.FullName, "resourcesDir", pathEntries);
		AddPackageDirectory(manifest.RootElement, packageRoot.FullName, "pathDir", pathEntries);
		RequireWindowsSandboxHelper(packageRoot.FullName, pathEntries, manifestPath);
		launch = new CodexAppServerLaunch(command, packageRoot.FullName, pathEntries);
		return true;
	}

	private static void AddPackageDirectory(JsonElement manifest, string packageRoot, string key, List<string> pathEntries) {
		string relative = manifest.GetStringOrEmpty(key);
		if (relative.Length == 0) {
			return;
		}

		string path = Path.GetFullPath(Path.Combine(packageRoot, relative));
		if (!Directory.Exists(path)) {
			throw new InvalidOperationException($"Codex package directory does not exist: {path}");
		}

		pathEntries.Add(path);
	}

	private static JsonDocument ReadManifest(string manifestPath) {
		try {
			return JsonDocument.Parse(File.ReadAllText(manifestPath));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
			throw new InvalidOperationException($"The Codex package manifest could not be read: {manifestPath}. {ex.Message}", ex);
		}
	}

	private static void RequireWindowsSandboxHelper(string packageRoot, IReadOnlyList<string> pathEntries, string manifestPath) {
		if (!OperatingSystem.IsWindows()) {
			return;
		}

		foreach (string path in pathEntries) {
			if (File.Exists(Path.Combine(path, "codex-windows-sandbox-setup.exe"))) {
				return;
			}
		}

		throw new InvalidOperationException(
			$"The Codex package at {packageRoot} is missing codex-windows-sandbox-setup.exe from its resources declared by {manifestPath}.");
	}

	private static string ResolveFinalTarget(string path) {
		try {
			var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
			if (target is not null) {
				return target.FullName;
			}

			var parentTarget = Directory.GetParent(path)?.ResolveLinkTarget(returnFinalTarget: true);
			if (parentTarget is not null) {
				string candidate = Path.Combine(parentTarget.FullName, Path.GetFileName(path));
				return File.Exists(candidate) ? candidate : path;
			}

			return path;
		} catch (IOException) {
			return path;
		} catch (UnauthorizedAccessException) {
			return path;
		}
	}
}
