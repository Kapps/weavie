using System.Text.Json;
using Weavie.Core.Theming;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void WireThemeMessages() {
		var themes = _messages.Host.Feature("themes");
		themes.Handle<EmptyMessage, IReadOnlyList<ThemeChoice>>("list", (_, _) => Task.FromResult(ThemeCatalog.List()));
		themes.Handle<ThemeIdRequest, JsonElement>("preview", (message, _) =>
			Task.FromResult(ThemeJson.PreviewSlot(message.Id, _themeOverrides)));
		themes.Handle<ThemeIdRequest, CommandWireResult>("select", (message, _) =>
			Task.FromResult(ToWireResult(ThemeCommands.SelectTheme(JsonSerializer.Serialize(new { id = message.Id }), _settings))));
		themes.HandleConcurrent<ThemeSearchRequest, JsonElement>("search", async (message, ct) => {
			using var http = new HttpClient();
			return await new OpenVsxThemeInstaller(http, OpenVsxThemeInstaller.DefaultRegistry)
				.SearchAsync(message.Query, message.Offset, ct).ConfigureAwait(false);
		});
		themes.HandleConcurrent<ThemeExtensionRequest, IReadOnlyList<ThemePreview>>("previewExtension", async (message, ct) => {
			using var http = new HttpClient();
			return await new OpenVsxThemeInstaller(http, OpenVsxThemeInstaller.DefaultRegistry)
				.PreviewAsync(message.Namespace, message.Name, message.Version, _themeOverrides, ct).ConfigureAwait(false);
		});
	}

	private sealed record ThemeIdRequest(string Id);
	private sealed record ThemeSearchRequest(string Query, int Offset);
	private sealed record ThemeExtensionRequest(string Namespace, string Name, string Version);
}
