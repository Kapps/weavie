using Tomlyn;
using Tomlyn.Model;
using Weavie.Core.TestRunning;

namespace Weavie.Core.Workspaces;

internal static class PythonWorkspacePreset {
	internal static PresetResult Detect(DetectionContext ctx) {
		var environment = ResolveEnvironment(ctx.MarkerFiles);
		if (!PytestConfigured(ctx)) {
			return new PresetResult { SetupCommand = environment?.Setup, TestRules = [] };
		}

		string runner = environment?.Runner ?? (OperatingSystem.IsWindows() ? "py -m pytest" : "python3 -m pytest");
		return new PresetResult {
			SetupCommand = environment?.Setup,
			TestRules = [new TestRule {
				Glob = "**/{test_*.py,*_test.py}",
				Symbol = "^(Test\\w*|test_\\w+)$",
				RunOne = runner + " ${file} -k ${name}",
				RunFile = runner + " ${file}",
				NameSeparator = " and ",
			}],
		};
	}

	private static EnvironmentCommands? ResolveEnvironment(IReadOnlyList<string> files) {
		if (files.Contains("uv.lock", StringComparer.OrdinalIgnoreCase)) {
			return new EnvironmentCommands("uv sync", "uv run pytest");
		}

		if (files.Contains("poetry.lock", StringComparer.OrdinalIgnoreCase)) {
			return new EnvironmentCommands("poetry install", "poetry run pytest");
		}

		return files.Contains("Pipfile.lock", StringComparer.OrdinalIgnoreCase)
			? new EnvironmentCommands("pipenv sync --dev", "pipenv run pytest")
			: null;
	}

	private static bool PytestConfigured(DetectionContext ctx) {
		foreach (string fileName in (string[])["pyproject.toml", "Pipfile"]) {
			string path = Path.Combine(ctx.MarkerDirectory, fileName);
			if (!ctx.FileSystem.FileExists(path)) {
				continue;
			}

			try {
				var parsed = Toml.Parse(ctx.FileSystem.ReadAllText(path), path);
				if (!parsed.HasErrors && ContainsPytest(parsed.ToModel())) {
					return true;
				}
			} catch (IOException) {
				// An unreadable manifest cannot establish the runner.
			}
		}

		foreach (string fileName in (string[])["pytest.ini", "setup.cfg", "tox.ini"]) {
			string path = Path.Combine(ctx.MarkerDirectory, fileName);
			if (!ctx.FileSystem.FileExists(path)) {
				continue;
			}

			try {
				string text = ctx.FileSystem.ReadAllText(path);
				if (fileName == "pytest.ini"
					|| text.Split('\n').Any(line => line.Trim() is "[pytest]" or "[tool:pytest]")) {
					return true;
				}
			} catch (IOException) {
				// An unreadable configuration cannot establish the runner.
			}
		}

		foreach (string fileName in (string[])["requirements.txt", "requirements-dev.txt"]) {
			string path = Path.Combine(ctx.MarkerDirectory, fileName);
			if (!ctx.FileSystem.FileExists(path)) {
				continue;
			}

			try {
				if (ctx.FileSystem.ReadAllText(path).Split('\n').Any(IsPytestRequirement)) {
					return true;
				}
			} catch (IOException) {
				// An unreadable requirements file cannot establish the runner.
			}
		}

		return false;
	}

	private static bool ContainsPytest(object? value) => value switch {
		TomlTable table => table.Any(entry =>
			string.Equals(entry.Key, "pytest", StringComparison.OrdinalIgnoreCase) || ContainsPytest(entry.Value)),
		TomlArray array => array.Any(item => item is string text ? IsPytestRequirement(text) : ContainsPytest(item)),
		_ => false,
	};

	private static bool IsPytestRequirement(string value) {
		string text = value.TrimStart();
		if (!text.StartsWith("pytest", StringComparison.OrdinalIgnoreCase)) {
			return false;
		}

		return text.Length == "pytest".Length
			|| char.IsWhiteSpace(text["pytest".Length])
			|| text["pytest".Length] is '[' or '<' or '>' or '=' or '!' or '~' or ';' or '@';
	}

	private sealed record EnvironmentCommands(string Setup, string Runner);
}
