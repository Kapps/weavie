using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Covers the capability-registry MCP surface — the model-facing server the spawned <c>claude</c>
/// reaches via <c>--mcp-config</c> (a <c>ws://</c> + Bearer entry), distinct from the IDE server.
/// Verifies registry-only tool advertisement, Bearer auth, and the generated port-scoped config file.
/// </summary>
[Collection("Settings")]
public sealed class RegistryMcpTests : IDisposable {
	private const string Token = "abcdef0123456789abcdef0123456789";
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-registry-tests", Guid.NewGuid().ToString("N"));

	public RegistryMcpTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private SettingsStore NewStore() =>
		CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);

	[Fact]
	public async Task RegistryMode_AdvertisesOnlySettingsTools() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var names = response.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Contains("listSettings", names);
		Assert.Contains("getSetting", names);
		Assert.Contains("setSetting", names);
		Assert.DoesNotContain("openDiff", names);   // IDE RPC not on the registry server
		Assert.DoesNotContain("openFile", names);
	}

	[Fact]
	public async Task RegistryMode_BearerAuth_AcceptsCorrect_RejectsWrong() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", store, registryMode: true);
		int port = server.Start();

		await Assert.ThrowsAsync<WebSocketException>(() => ConnectBearerAsync(port, "wrong-token"));
		using var ws = await ConnectBearerAsync(port, Token); // correct token connects
		Assert.Equal(WebSocketState.Open, ws.State);
	}

	[Fact]
	public async Task RegistryMode_ThemeTools_AdvertiseQueriesAndEditors_NotVerbs() {
		using var store = NewStore();
		var overrides = new ThemeOverridesStore(new InMemoryFileSystem(), Path.Combine(_dir, "theme-overrides.json"));
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", store, registryMode: true, themeOverrides: overrides);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var names = response.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		// Data-shaped operations stay MCP tools: read-only queries and per-color override editors.
		Assert.Contains("listThemes", names);
		Assert.Contains("describeTheme", names);
		Assert.Contains("setThemeOverride", names);
		Assert.Contains("applyThemeTransform", names);
		Assert.Contains("removeThemeOverride", names);
		// Verb actions are commands (reached via runCommand), not advertised as theme tools.
		Assert.DoesNotContain("installTheme", names);
		Assert.DoesNotContain("selectTheme", names);
		Assert.DoesNotContain("resetTheme", names);
		Assert.DoesNotContain("undoThemeOverride", names);
	}

	[Fact]
	public async Task RegistryMode_SetSetting_OverBearer_Persists() {
		using var store = NewStore();
		await using var server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", store, registryMode: true);
		int port = server.Start();
		using var ws = await ConnectBearerAsync(port, Token);

		string shell = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";
		await SendAsync(ws, Request(2, "tools/call",
			$"{{\"name\":\"setSetting\",\"arguments\":{{\"key\":\"terminal.shell\",\"value\":\"{shell}\"}}}}"));
		using var response = await ReceiveAsync(ws);

		Assert.False(response.RootElement.GetProperty("result").TryGetProperty("isError", out var e) && e.GetBoolean());
		Assert.Equal(shell, store.Resolve("terminal.shell").Value);
	}

	[Fact]
	public void RegistryMode_RequiresStore() {
		Assert.Throws<ArgumentNullException>(() => TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", settings: null, registryMode: true));
	}

	[Fact]
	public async Task IdeIntegration_WithSettings_WritesPortScopedConfig() {
		using var store = NewStore();
		await using var ide = TestMcp.Ide(FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", store);

		Assert.NotNull(ide.RegistryServer);
		Assert.True(ide.RegistryPort > 0);

		string? path = ide.WriteMcpConfigFile();
		Assert.NotNull(path);
		Assert.Contains(ide.RegistryPort.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(path!), StringComparison.Ordinal);

		using var doc = JsonDocument.Parse(File.ReadAllText(path!));
		var weavie = doc.RootElement.GetProperty("mcpServers").GetProperty("weavie");
		Assert.Equal("ws", weavie.GetProperty("type").GetString());
		Assert.Equal($"ws://127.0.0.1:{ide.RegistryPort}", weavie.GetProperty("url").GetString());
		Assert.Equal($"Bearer {ide.AuthToken}", weavie.GetProperty("headers").GetProperty("Authorization").GetString());
	}

	[Fact]
	public async Task IdeIntegration_WithoutSettings_HasNoRegistry() {
		await using var ide = TestMcp.Ide(FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie");
		Assert.Null(ide.RegistryServer);
		Assert.Equal(0, ide.RegistryPort);
		Assert.Null(ide.WriteMcpConfigFile());
	}

	private static async Task<ClientWebSocket> ConnectBearerAsync(int port, string token) {
		var client = new ClientWebSocket();
		client.Options.SetRequestHeader("Authorization", $"Bearer {token}");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);
		return client;
	}

	private static async Task SendAsync(ClientWebSocket ws, string json) =>
		await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

	private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws) {
		byte[] buffer = new byte[64 * 1024];
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var ms = new MemoryStream();
		WebSocketReceiveResult result;
		do {
			result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
			ms.Write(buffer, 0, result.Count);
		}
		while (!result.EndOfMessage);

		return JsonDocument.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
	}

	private static string Request(int id, string method, string paramsJson) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
}
