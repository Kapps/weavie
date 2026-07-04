using System.Text.RegularExpressions;

namespace Weavie.Core.TestRunning;

/// <summary>Which template of a <see cref="TestRule"/> to compose.</summary>
public enum TestCommandKind {
	/// <summary>Run a single named test (<see cref="TestRule.RunOne"/>).</summary>
	RunOne,

	/// <summary>Run every test in a file (<see cref="TestRule.RunFile"/>).</summary>
	RunFile,
}

/// <summary>How to quote substituted values for the shell that will receive the command.</summary>
public enum ShellQuoting {
	/// <summary>POSIX shells: single-quote wrap, <c>'\''</c> for an embedded quote.</summary>
	Posix,

	/// <summary>PowerShell: single-quote wrap, <c>''</c> doubling (no variable expansion).</summary>
	PowerShell,
}

/// <summary>
/// Composes a runnable shell command from a <see cref="TestRule"/> template by substituting shell-quoted
/// placeholders — <c>${file}</c>, <c>${fileDir}</c> (absolute paths), and <c>${name}</c> (run-one only). Every
/// value is quoted, never interpolated raw; an unknown or unavailable placeholder is a loud failure, never a
/// silent pass-through. Pure: it builds the string, it does not run anything.
/// </summary>
public static partial class TestCommandComposer {
	/// <summary>
	/// Builds the command for <paramref name="rule"/>/<paramref name="kind"/> against the absolute
	/// <paramref name="absoluteFilePath"/> (and <paramref name="testName"/> for <see cref="TestCommandKind.RunOne"/>),
	/// quoting substitutions for <paramref name="quoting"/>. Returns false with a human-readable
	/// <paramref name="error"/> on an unknown placeholder or a missing test name.
	/// </summary>
	public static bool TryCompose(
		TestRule rule,
		TestCommandKind kind,
		string absoluteFilePath,
		string? testName,
		ShellQuoting quoting,
		out string command,
		out string error) {
		ArgumentNullException.ThrowIfNull(rule);
		ArgumentException.ThrowIfNullOrEmpty(absoluteFilePath);
		command = string.Empty;
		error = string.Empty;

		string template = kind == TestCommandKind.RunOne ? rule.RunOne : rule.RunFile;
		string file = Quote(absoluteFilePath, quoting);
		string fileDir = Quote(Path.GetDirectoryName(absoluteFilePath) ?? string.Empty, quoting);

		string? failure = null;
		command = PlaceholderPattern().Replace(template, match => {
			string key = match.Groups[1].Value;
			switch (key) {
				case "file":
					return file;
				case "fileDir":
					return fileDir;
				case "name" when kind == TestCommandKind.RunOne && testName is not null:
					return Quote(testName, quoting);
				case "name":
					failure ??= "the ${name} placeholder is only available when running a single test.";
					return match.Value;
				default:
					failure ??= $"unknown placeholder ${{{key}}} in the test command template.";
					return match.Value;
			}
		});

		if (failure is not null) {
			error = failure;
			command = string.Empty;
			return false;
		}

		return true;
	}

	private static string Quote(string value, ShellQuoting quoting) => quoting switch {
		ShellQuoting.PowerShell => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
		_ => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
	};

	[GeneratedRegex(@"\$\{([^}]*)\}")]
	private static partial Regex PlaceholderPattern();
}
