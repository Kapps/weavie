using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

// The theme tools a device's home runs: they edit the theme on that device's screen and say where.
public sealed class ThemeToolsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-theme-tools-tests");
	private readonly SettingsStore _settings;
	private readonly ThemeOverridesStore _overrides;
	private readonly TestScreen _screen = new() { Showing = "light" };
	private readonly ThemeTools _tools;

	public ThemeToolsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_overrides = new ThemeOverridesStore(new InMemoryFileSystem(), _dir.Combine("theme-overrides.json"));
		_tools = new ThemeTools(_settings, _overrides, _screen, "laptop");
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	private ToolReply Run(string tool, string args) => _tools.Run(new ThemeRequest(tool, JsonDocument.Parse(args).RootElement));

	[Fact]
	public void SystemMode_EditsTheThemeForThePolarityOnScreen_AndSaysWhere() {
		var reply = Run("applyThemeTransform", """{"op":"darken","amount":0.6,"target":"colors"}""");

		Assert.Equal("Applied darken 0.6 (colors) to theme 'weavie-light' on this device (laptop).", reply.Text);
		Assert.Single(_overrides.Get("weavie-light"));
		Assert.Empty(_overrides.Get("weavie-dark"));
	}

	[Fact]
	public void AFixedMode_IgnoresTheScreen() {
		_settings.Set(ThemeSettings.ModeKey, JsonSerializer.SerializeToElement("dark"));

		Run("setThemeOverride", """{"key":"editor.background","value":"#000000"}""");

		Assert.Single(_overrides.Get("weavie-dark"));
		Assert.Empty(_overrides.Get("weavie-light"));
	}

	[Fact]
	public void SystemMode_FailsWithNoPageOpen_UnlessAThemeIsNamed() {
		_screen.Showing = null;

		var unknown = Run("setThemeOverride", """{"key":"editor.background","value":"#000000"}""");
		var named = Run("setThemeOverride", """{"key":"editor.background","value":"#000000","theme":"weavie-dark"}""");

		Assert.True(unknown.IsError);
		Assert.StartsWith("No page from laptop is open", unknown.Text, StringComparison.Ordinal);
		Assert.False(named.IsError);
		Assert.Single(_overrides.Get("weavie-dark"));
	}

	[Fact]
	public void ListThemes_MarksTheThemeOnScreen() {
		using var list = JsonDocument.Parse(Run("listThemes", "{}").Text);

		var active = list.RootElement.GetProperty("themes").EnumerateArray().Single(theme => theme.GetProperty("active").GetBoolean());
		Assert.Equal("weavie-light", active.GetProperty("id").GetString());
		Assert.Equal("laptop", list.RootElement.GetProperty("device").GetString());
	}

	[Fact]
	public void DescribeTheme_ReportsThePolarityOnScreen() {
		using var described = JsonDocument.Parse(Run("describeTheme", "{}").Text);

		Assert.Equal(("weavie-light", "system", "light"), (
			described.RootElement.GetProperty("active").GetString(),
			described.RootElement.GetProperty("mode").GetString(),
			described.RootElement.GetProperty("showing").GetString()));
	}

	[Theory]
	[InlineData("1", false)]
	[InlineData("\"0.2\"", false)]
	[InlineData("2", true)]
	[InlineData("0", true)]
	public void ApplyThemeTransform_TakesAnAmountUpToOne(string amount, bool rejected) =>
		Assert.Equal(rejected, Run("applyThemeTransform", $$"""{"op":"darken","amount":{{amount}}}""").IsError);

	[Theory]
	[InlineData("italic", false)]
	[InlineData("", false)]
	[InlineData("wiggly", true)]
	public void SetThemeOverride_TakesAKnownFontStyle(string fontStyle, bool rejected) =>
		Assert.Equal(rejected, Run("setThemeOverride", $$"""{"key":"comment","table":"tokenColors","fontStyle":"{{fontStyle}}"}""").IsError);
}
