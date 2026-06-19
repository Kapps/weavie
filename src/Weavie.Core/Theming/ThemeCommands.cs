using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;

namespace Weavie.Core.Theming;

/// <summary>
/// Host-supplied native <c>.vsix</c> picker for the install-from-file command. Returns the chosen absolute
/// path, or null if the user cancelled (a host without a picker passes null for the delegate itself).
/// </summary>
public delegate Task<string?> VsixFilePicker(CancellationToken ct);

/// <summary>
/// Wires the Core handlers for the theme <em>verb</em> commands declared in <see cref="CoreCommands"/>:
/// install / install-from-file / select / cycle-mode / undo-override / reset. These are the theming actions
/// that became commands (so they're reachable from the palette, a keybinding, and Claude's <c>runCommand</c>
/// alike), while the data-shaped override editors and the read-only queries stay MCP tools. The handlers act
/// on the app-global stores — appearance is the <c>theme.mode</c> / <c>theme.light</c> / <c>theme.dark</c>
/// settings on <see cref="SettingsStore"/> (select routes by polarity + flips the mode; cycle-mode steps
/// system → light → dark), the per-color tweaks live in <see cref="ThemeOverridesStore"/> — and install reads/writes the themes root
/// via <see cref="OpenVsxThemeInstaller"/>. Both hosts call <see cref="RegisterHandlers"/> after building the
/// session dispatcher, supplying a native file picker for the install-from-file flow.
/// </summary>
public static class ThemeCommands {
	/// <summary>
	/// Registers the Core handlers for the theme verb commands onto <paramref name="dispatcher"/>.
	/// <paramref name="pickVsixFile"/> supplies the native <c>.vsix</c> picker used when install-from-file is
	/// run with no <c>path</c> argument (e.g. from the palette); pass null on a host without one (the command
	/// then requires an explicit <c>path</c>).
	/// </summary>
	public static void RegisterHandlers(
		CommandDispatcher dispatcher,
		SettingsStore settings,
		ThemeOverridesStore overrides,
		VsixFilePicker? pickVsixFile) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(overrides);

		dispatcher.RegisterHandler(CoreCommands.InstallTheme, (argsJson, ct) =>
			InstallFromOpenVsxAsync(argsJson, settings, ct));
		dispatcher.RegisterHandler(CoreCommands.InstallThemeFromFile, (argsJson, ct) =>
			InstallFromFileAsync(argsJson, settings, pickVsixFile, ct));
		dispatcher.RegisterHandler(CoreCommands.SelectTheme, (argsJson, _) =>
			Task.FromResult(SelectTheme(argsJson, settings)));
		dispatcher.RegisterHandler(CoreCommands.CycleThemeMode, (_, _) =>
			Task.FromResult(CycleMode(settings)));
		dispatcher.RegisterHandler(CoreCommands.UndoThemeOverride, (_, _) =>
			Task.FromResult(UndoOverride(settings, overrides)));
		dispatcher.RegisterHandler(CoreCommands.ResetTheme, (_, _) =>
			Task.FromResult(Reset(settings, overrides)));
	}

	private static async Task<CommandResult> InstallFromOpenVsxAsync(string? argsJson, SettingsStore settings, CancellationToken ct) {
		using var args = ParseArgs(argsJson);
		string? ns = GetString(args, "namespace");
		string? name = GetString(args, "name");
		string? version = GetString(args, "version");
		if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(name)) {
			return CommandResult.Failure(
				"Installing from Open VSX needs a 'namespace' (publisher) and 'name' (extension), e.g. dracula-theme / theme-dracula.");
		}

		try {
			var installer = new OpenVsxThemeInstaller(http: null, registry: OpenVsxThemeInstaller.DefaultRegistry);
			var installed = await installer.InstallAsync(ns, name, string.IsNullOrEmpty(version) ? null : version, ct).ConfigureAwait(false);
			return DescribeInstall(installed, $"{ns}.{name}", settings, autoSelectSingle: false);
		} catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) {
			return CommandResult.Failure($"Install failed: {ex.Message}");
		}
	}

	private static async Task<CommandResult> InstallFromFileAsync(
		string? argsJson, SettingsStore settings, VsixFilePicker? pickVsixFile, CancellationToken ct) {
		string? path;
		using (var args = ParseArgs(argsJson)) {
			path = GetString(args, "path");
		}

		// No explicit path → open the native picker (the palette/menu flow); an interactively-chosen theme is
		// almost certainly what the user wants on screen now, so we activate a single contributed theme below.
		bool interactive = false;
		if (string.IsNullOrEmpty(path)) {
			if (pickVsixFile is null) {
				return CommandResult.Failure("Provide a 'path' to a .vsix file (this host has no interactive file picker).");
			}

			path = await pickVsixFile(ct).ConfigureAwait(false);
			if (string.IsNullOrEmpty(path)) {
				return CommandResult.Success("Theme install cancelled.");
			}

			interactive = true;
		}

		try {
			var installer = new OpenVsxThemeInstaller(http: null, registry: OpenVsxThemeInstaller.DefaultRegistry);
			var installed = await installer.InstallFromVsixAsync(path, ct).ConfigureAwait(false);
			return DescribeInstall(installed, Path.GetFileName(path), settings, autoSelectSingle: interactive);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) {
			return CommandResult.Failure($"Install from file failed: {ex.Message}");
		}
	}

	private static CommandResult SelectTheme(string? argsJson, SettingsStore settings) {
		string? id;
		using (var args = ParseArgs(argsJson)) {
			id = GetString(args, "id");
		}

		if (string.IsNullOrEmpty(id)) {
			return CommandResult.Failure("Select needs an 'id' (use the listThemes tool to see available theme ids).");
		}

		// Route by the theme's polarity: a light theme fills the light slot, a dark theme the dark slot — and
		// switch the mode to match, so selecting a theme puts you in that mode showing that theme right now.
		string? polarity = ThemePolarity(id);
		if (polarity is null) {
			return CommandResult.Failure($"Unknown theme '{id}'. Use the listThemes tool to see available themes.");
		}

		try {
			SetString(settings, polarity == "light" ? ThemeSettings.LightKey : ThemeSettings.DarkKey, id);
			SetString(settings, ThemeSettings.ModeKey, polarity);
			return CommandResult.Success($"Active theme is now '{id}' ({polarity} mode).");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}

	// Steps the appearance mode system → light → dark → system. A keybound, palette-visible, no-arg command.
	private static CommandResult CycleMode(SettingsStore settings) {
		string next = ThemeSettings.Mode(settings) switch {
			"system" => "light",
			"light" => "dark",
			_ => "system",
		};

		try {
			SetString(settings, ThemeSettings.ModeKey, next);
			string label = next switch {
				"light" => "Light",
				"dark" => "Dark",
				_ => "System (match OS)",
			};
			return CommandResult.Success($"Theme mode: {label}.");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}

	// The polarity ("light"/"dark") a theme id belongs to: a built-in's declared type, or an installed theme's
	// VS Code uiTheme ("vs"/"hc-light" → light; "vs-dark"/"hc-black" → dark). Null when the id is unknown.
	private static string? ThemePolarity(string id) {
		foreach (var (builtInId, _, type) in BuiltInThemes.All) {
			if (builtInId == id) {
				return type == "light" ? "light" : "dark";
			}
		}

		var installed = OpenVsxThemeInstaller.ListInstalled().FirstOrDefault(theme => theme.Id == id);
		if (installed is null) {
			return null;
		}

		return installed.UiTheme is "vs" or "hc-light" ? "light" : "dark";
	}

	private static CommandResult UndoOverride(SettingsStore settings, ThemeOverridesStore overrides) {
		string active = ActiveThemeId(settings);
		return CommandResult.Success(overrides.UndoLast(active)
			? $"Undid the last override on theme '{active}'."
			: $"Theme '{active}' has no overrides to undo.");
	}

	private static CommandResult Reset(SettingsStore settings, ThemeOverridesStore overrides) {
		string active = ActiveThemeId(settings);
		return CommandResult.Success(overrides.Clear(active)
			? $"Cleared all overrides on theme '{active}'."
			: $"Theme '{active}' had no overrides.");
	}

	// Shared install reporting: report what was added, and — for the interactive picker flow with a single
	// contributed theme — activate it so it's on screen immediately. A path/Open VSX install stays inert.
	private static CommandResult DescribeInstall(
		IReadOnlyList<InstalledTheme> installed, string source, SettingsStore settings, bool autoSelectSingle) {
		if (installed.Count == 0) {
			return CommandResult.Success($"Installed {source}, but it contributed no color themes.");
		}

		if (autoSelectSingle && installed.Count == 1) {
			var only = installed[0];
			string polarity = only.UiTheme is "vs" or "hc-light" ? "light" : "dark";
			SetString(settings, polarity == "light" ? ThemeSettings.LightKey : ThemeSettings.DarkKey, only.Id);
			SetString(settings, ThemeSettings.ModeKey, polarity);
			return CommandResult.Success($"Installed and activated '{only.Id}' ({polarity} mode).");
		}

		string ids = string.Join(", ", installed.Select(theme => $"'{theme.Id}'"));
		return CommandResult.Success(
			$"Installed {installed.Count} theme(s) from {source}: {ids}. Run weavie.theme.select to switch.");
	}

	private static string ActiveThemeId(SettingsStore settings) =>
		ThemeSettings.ResolveActiveThemeId(settings);

	private static void SetString(SettingsStore settings, string key, string value) {
		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
		settings.Set(key, doc.RootElement);
	}

	// Parses the runCommand args object (raw JSON, possibly null/malformed) into a document to read string
	// props from. Returns null on absent/invalid input; disposing a null `using` is a no-op.
	private static JsonDocument? ParseArgs(string? argsJson) {
		if (string.IsNullOrEmpty(argsJson)) {
			return null;
		}

		try {
			return JsonDocument.Parse(argsJson);
		} catch (JsonException) {
			return null;
		}
	}

	private static string? GetString(JsonDocument? args, string key) =>
		args is not null && args.RootElement.ValueKind == JsonValueKind.Object
			&& args.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
