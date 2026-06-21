using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises the theme verb commands (select / undo / reset / install-from-file guards) through the command
/// dispatcher — the same path runCommand and the palette take — over an in-memory overrides store and a
/// temp-file settings store. The networked Open VSX install isn't reached here; install-from-file is covered
/// only on its guard paths (no picker, missing file), since a real .vsix would touch the user's themes root.
/// </summary>
public sealed class ThemeCommandsTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-theme-commands-tests", Guid.NewGuid().ToString("N"));
	private readonly SettingsStore _settings;
	private readonly ThemeOverridesStore _overrides;
	private readonly CommandDispatcher _dispatcher;

	public ThemeCommandsTests() {
		Directory.CreateDirectory(_dir);
		_settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		_overrides = new ThemeOverridesStore(new InMemoryFileSystem(), Path.Combine(_dir, "theme-overrides.json"));
		_dispatcher = new CommandDispatcher(CoreCommands.CreateRegistry());
		ThemeCommands.RegisterHandlers(_dispatcher, _settings, _overrides, pickVsixFile: null);
	}

	public void Dispose() {
		_settings.Dispose();
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private Task<CommandResult> Run(string id, string? argsJson = null) =>
		_dispatcher.InvokeAsync(id, argsJson, CancellationToken.None);

	[Fact]
	public async Task Select_DarkBuiltIn_SetsDarkSlotAndMode() {
		var result = await Run(CoreCommands.SelectTheme, "{\"id\":\"weavie-dark\"}");
		Assert.True(result.Ok);
		Assert.Equal("weavie-dark", _settings.GetString("theme.dark"));
		Assert.Equal("dark", _settings.GetString("theme.mode"));
	}

	[Fact]
	public async Task Select_LightBuiltIn_SetsLightSlotAndMode() {
		var result = await Run(CoreCommands.SelectTheme, "{\"id\":\"weavie-light\"}");
		Assert.True(result.Ok);
		Assert.Equal("weavie-light", _settings.GetString("theme.light"));
		Assert.Equal("light", _settings.GetString("theme.mode"));
		// The dark slot is left untouched at its default.
		Assert.Equal("weavie-dark", _settings.GetString("theme.dark"));
	}

	[Fact]
	public async Task CycleMode_StepsSystemLightDarkSystem() {
		// Default mode is system; cycle steps system → light → dark → system.
		Assert.Equal("system", _settings.GetString("theme.mode"));

		await Run(CoreCommands.CycleThemeMode);
		Assert.Equal("light", _settings.GetString("theme.mode"));

		await Run(CoreCommands.CycleThemeMode);
		Assert.Equal("dark", _settings.GetString("theme.mode"));

		var back = await Run(CoreCommands.CycleThemeMode);
		Assert.True(back.Ok);
		Assert.Equal("system", _settings.GetString("theme.mode"));
	}

	[Fact]
	public async Task Select_UnknownId_Fails() {
		var result = await Run(CoreCommands.SelectTheme, "{\"id\":\"no-such-theme-xyz\"}");
		Assert.False(result.Ok);
		Assert.Contains("Unknown theme", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Select_NoId_Fails() {
		var result = await Run(CoreCommands.SelectTheme);
		Assert.False(result.Ok);
		Assert.Contains("needs an 'id'", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Reset_ClearsOverrides_ThenReportsEmpty() {
		_overrides.Append("weavie-dark", new ThemeOverrideSet { Key = "editor.background", Value = "#000000" });

		var cleared = await Run(CoreCommands.ResetTheme);
		Assert.True(cleared.Ok);
		Assert.Contains("Cleared all overrides", cleared.Message, StringComparison.Ordinal);
		Assert.Empty(_overrides.Get("weavie-dark"));

		var again = await Run(CoreCommands.ResetTheme);
		Assert.True(again.Ok);
		Assert.Contains("had no overrides", again.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Undo_PopsLastOverride() {
		_overrides.Append("weavie-dark", new ThemeOverrideSet { Key = "editor.background", Value = "#000000" });
		_overrides.Append("weavie-dark", new ThemeOverrideSet { Key = "focusBorder", Value = "#ffffff" });

		var undone = await Run(CoreCommands.UndoThemeOverride);
		Assert.True(undone.Ok);
		Assert.Contains("Undid the last override", undone.Message, StringComparison.Ordinal);
		Assert.Single(_overrides.Get("weavie-dark"));
	}

	[Fact]
	public async Task InstallFromFile_NoPathNoPicker_Fails() {
		// The constructor wired a null picker, so with no 'path' the command must fail loudly, not hang.
		var result = await Run(CoreCommands.InstallThemeFromFile);
		Assert.False(result.Ok);
		Assert.Contains("Provide a 'path'", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task InstallFromFile_MissingFile_Fails() {
		string missing = Path.Combine(_dir, "nope.vsix").Replace("\\", "\\\\", StringComparison.Ordinal);
		var result = await Run(CoreCommands.InstallThemeFromFile, $"{{\"path\":\"{missing}\"}}");
		Assert.False(result.Ok);
		Assert.Contains("Install from file failed", result.Error, StringComparison.Ordinal);
	}
}
