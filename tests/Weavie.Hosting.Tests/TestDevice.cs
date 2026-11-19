using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Theming;

namespace Weavie.Hosting.Tests;

/// <summary>A test session's device: this host answering as its own home, over isolated theme overrides.</summary>
internal static class TestDevice {
	internal static HomeDevice Home(SettingsStore settings) => new(
		new SettingsTools(settings, "test-device"),
		new ThemeTools(settings, new ThemeOverridesStore(new InMemoryFileSystem(), "/theme-overrides.json"), new DarkScreen(), "test-device"));

	private sealed class DarkScreen : IThemeScreen {
		public string? Showing => "dark";
	}
}
