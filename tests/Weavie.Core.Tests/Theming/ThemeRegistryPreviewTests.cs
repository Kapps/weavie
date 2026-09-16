using System.IO.Compression;
using System.Net;
using System.Text;
using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ThemeRegistryPreviewTests {
	[Fact]
	public async Task Preview_ResolvesJsoncAndPolarity_WithoutInstalling() {
		byte[] package = Package();
		using var handler = new RegistryHandler(package);
		using var http = new HttpClient(handler);
		var installer = new OpenVsxThemeInstaller(http, "https://registry.test");
		using var directory = new TempDirectory("theme-preview-test");
		var overrides = new ThemeOverridesStore(new InMemoryFileSystem(), directory.Combine("overrides.json"));
		string? indexBefore = File.Exists(OpenVsxThemeInstaller.IndexPath) ? File.ReadAllText(OpenVsxThemeInstaller.IndexPath) : null;

		var previews = await installer.PreviewAsync("publisher", "themes", "1.2.3", overrides, CancellationToken.None);

		Assert.Equal(2, previews.Count);
		Assert.Equal("light", previews[0].Choice.Type);
		Assert.Equal("light", previews[0].Slot.GetProperty("theme").GetProperty("type").GetString());
		Assert.Equal("#abcdef", previews[0].Slot.GetProperty("theme").GetProperty("colors").GetProperty("editor.background").GetString());
		Assert.Equal("dark", previews[1].Choice.Type);
		Assert.Equal("1.2.3", previews[1].Choice.Version);
		Assert.Equal("/api/publisher/themes/1.2.3", handler.Requests[0]);
		Assert.Equal(indexBefore, File.Exists(OpenVsxThemeInstaller.IndexPath) ? File.ReadAllText(OpenVsxThemeInstaller.IndexPath) : null);
	}

	[Theory]
	[InlineData("../outside")]
	[InlineData("..")]
	[InlineData("x/y")]
	public async Task Install_RejectsUnsafeCoordinates_BeforeNetwork(string name) {
		using var handler = new RegistryHandler([]);
		using var http = new HttpClient(handler);
		var installer = new OpenVsxThemeInstaller(http, "https://registry.test");
		await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync("publisher", name, "1", CancellationToken.None));
		Assert.Empty(handler.Requests);
	}

	private static byte[] Package() {
		using var bytes = new MemoryStream();
		using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true)) {
			Write(archive, "extension/package.json", """
				{"contributes":{"themes":[
				{"label":"Light","uiTheme":"vs","path":"light.json"},
				{"label":"Dark","uiTheme":"vs-dark","path":"dark.json"}]}}
				""");
			Write(archive, "extension/light.json", """
				{ // VS Code themes accept JSONC
				  "colors":{"editor.background":"#abcdef",},
				}
				""");
			Write(archive, "extension/dark.json", "{}");
		}
		return bytes.ToArray();
	}

	private static void Write(ZipArchive archive, string name, string content) {
		using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
		writer.Write(content);
	}

	private sealed class RegistryHandler(byte[] package) : HttpMessageHandler {
		public List<string> Requests { get; } = [];
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			string path = request.RequestUri!.AbsolutePath;
			Requests.Add(path);
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
				Content = path.EndsWith(".vsix", StringComparison.Ordinal)
					? new ByteArrayContent(package)
					: new StringContent("""{"version":"1.2.3","files":{"download":"https://registry.test/themes.vsix"}}"""),
			});
		}
	}
}
