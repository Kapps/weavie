using System.Text.Json;
using Weavie.Core.Lsp;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Configuration;

/// <summary>Registers one launch-command setting per <see cref="LanguageServerCatalog"/> recipe.</summary>
public static class LspSettings {
	/// <summary>The setting key holding <paramref name="serverId"/>'s launch command.</summary>
	public static string CommandKey(string serverId) => $"lsp.{serverId}.command";

	/// <summary>Registers every recipe's command setting into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		foreach (var descriptor in LanguageServerCatalog.All) {
			string builtIn = descriptor.Candidates is [var only]
				? only.Command
				: $"the first of {string.Join(", ", descriptor.Candidates.Select(c => c.Command))} found";
			registry.Register(new SettingDefinition {
				Key = CommandKey(descriptor.Id),
				Kind = SettingKind.Json,
				Owner = SettingOwner.Repo,
				Description = $"The {descriptor.DisplayName} language server to run: a JSON array of the executable and its "
					+ "arguments, e.g. [\"node_modules/.bin/server\",\"--stdio\"]. A bare name is found on PATH; a path is "
					+ $"relative to the session's worktree. Empty runs {builtIn} on PATH. setSetting stores it for this "
					+ "repository; a value in the user settings.toml applies to every repository without its own. Running "
					+ "servers keep their command until Restart Language Servers.",
				Aliases = [$"{descriptor.DisplayName} language server", $"{descriptor.Id} lsp", $"{descriptor.Id} server path"],
				Apply = ApplyMode.NextLanguageServerStart,
				Default = "",
				Validate = static value => value is string { Length: 0 } || TryParse(value as string ?? "", out _, out string error)
					? ValidationResult.Success
					: ValidationResult.Failure(error),
			});
		}
	}

	/// <summary>
	/// <paramref name="descriptor"/> as <paramref name="settingsRoot"/>'s workspace launches it in
	/// <paramref name="worktree"/>: a configured command replaces the built-in candidates.
	/// </summary>
	public static LanguageServerDescriptor Effective(
		LanguageServerDescriptor descriptor, SettingsStore settings, string settingsRoot, string worktree) {
		ArgumentNullException.ThrowIfNull(descriptor);
		ArgumentNullException.ThrowIfNull(settings);
		string configured = settings.RequireString(CommandKey(descriptor.Id), settingsRoot);
		if (configured.Length == 0) {
			return descriptor;
		}

		if (!TryParse(configured, out string[] argv, out string error)) {
			throw new InvalidOperationException($"{CommandKey(descriptor.Id)}: {error}");
		}

		return descriptor with { Candidates = [new(Anchor(argv[0], worktree), argv[1..])] };
	}

	// A bare name stays a PATH lookup; anything path-shaped is anchored at the worktree.
	private static string Anchor(string command, string worktree) {
		string expanded = WorkspacePaths.ExpandHome(command);
		return expanded.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
			? expanded
			: Path.GetFullPath(expanded, worktree);
	}

	private static bool TryParse(string json, out string[] argv, out string error) {
		argv = [];
		error = "expected a JSON array of strings starting with the executable";
		try {
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Array
				|| document.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) {
				return false;
			}

			argv = [.. document.RootElement.EnumerateArray().Select(item => item.GetString()!)];
		} catch (JsonException ex) {
			error = $"not valid JSON ({ex.Message})";
			return false;
		}

		return argv.Length > 0 && !string.IsNullOrWhiteSpace(argv[0]);
	}
}
