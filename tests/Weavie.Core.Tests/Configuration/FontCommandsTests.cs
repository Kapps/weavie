using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The font-zoom command handlers against a real on-disk settings file: the commands must change the
/// *rendered* sizes — stepping per-surface overrides along with the global — and must say so when an
/// environment variable shadows the write, never silently doing nothing.
/// </summary>
[Collection("Settings")]
public sealed class FontCommandsTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-font-tests", Guid.NewGuid().ToString("N"));

	public FontCommandsTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		Environment.SetEnvironmentVariable("WEAVIE_FONT_SIZE", null);
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "settings.toml");

	private static string WorkspaceFile(string root) => Path.Combine(root, ".weavie", "settings.toml");

	private (SettingsStore Store, CommandDispatcher Commands) Harness() {
		var registry = new SettingsRegistry();
		FontSettings.Register(registry);
		var store = new SettingsStore(registry, FilePath, enableWatcher: false, WorkspaceFile);
		var commands = new CommandDispatcher(CoreCommands.CreateRegistry());
		FontCommands.RegisterHandlers(commands, store);
		return (store, commands);
	}

	[Fact]
	public async Task Increase_StepsTheRenderedSizes_WhenSurfaceOverridesAreSet() {
		// A hand-configured machine: both surfaces override the global size, so stepping only the global
		// would change nothing on screen — the exact silent no-op this pins against.
		File.WriteAllText(FilePath, "editor.font.size = 14\nterminal.font.size = 13\n");
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Equal(15, FontSettings.ResolveEditor(store).Size);
			Assert.Equal(14, FontSettings.ResolveTerminal(store).Size);
		}
	}

	[Fact]
	public async Task Decrease_StepsTheRenderedSizes_WhenSurfaceOverridesAreSet() {
		File.WriteAllText(FilePath, "editor.font.size = 14\nterminal.font.size = 13\n");
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.DecreaseFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Equal(13, FontSettings.ResolveEditor(store).Size);
			Assert.Equal(12, FontSettings.ResolveTerminal(store).Size);
		}
	}

	[Fact]
	public async Task Reset_RestoresBothSurfacesToTheDefault() {
		File.WriteAllText(FilePath, "font.size = 22\neditor.font.size = 14\nterminal.font.size = 13\n");
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.ResetFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Equal(FontSettings.DefaultSize, FontSettings.ResolveEditor(store).Size);
			Assert.Equal(FontSettings.DefaultSize, FontSettings.ResolveTerminal(store).Size);
		}
	}

	[Fact]
	public async Task Increase_ReportsTheEnvShadow_InsteadOfSilentlyDoingNothing() {
		Environment.SetEnvironmentVariable("WEAVIE_FONT_SIZE", "16");
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Contains("WEAVIE_FONT_SIZE", result.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task Increase_AtTheMaximum_SaysSo() {
		File.WriteAllText(FilePath, "font.size = 72\n");
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Contains("maximum", result.Message, StringComparison.Ordinal);
			Assert.Equal(72, FontSettings.ResolveEditor(store).Size);
		}
	}

	[Fact]
	public async Task Increase_WithSurfaceOverridesAndAGlobalEnvShadow_IsSilent_BecauseTheFontsStillResize() {
		// The env pins the global, but both rendered sizes come from the overrides and do step — claiming
		// "unset to change the size" here would be false, so the resize itself is the feedback.
		Environment.SetEnvironmentVariable("WEAVIE_FONT_SIZE", "16");
		File.WriteAllText(FilePath, "editor.font.size = 14\nterminal.font.size = 13\n");
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Null(result.Message);
			Assert.Equal(15, FontSettings.ResolveEditor(store).Size);
			Assert.Equal(14, FontSettings.ResolveTerminal(store).Size);
		}
	}

	[Fact]
	public async Task Increase_WithoutOverrides_StepsTheGlobalSize() {
		var (store, commands) = Harness();
		using (store) {
			var result = await commands.InvokeAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

			Assert.True(result.Ok);
			Assert.Null(result.Message);
			Assert.Equal(FontSettings.DefaultSize + 1, FontSettings.ResolveEditor(store).Size);
			Assert.Equal(FontSettings.DefaultSize + 1, FontSettings.ResolveTerminal(store).Size);
		}
	}
}
