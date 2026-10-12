using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Core.Review.Hints;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>addReviewHint on the registry server.</summary>
[Collection("Settings")]
public sealed class McpReviewHintToolsTests : IAsyncLifetime {
	private const string Token = "abcdef0123456789abcdef0123456789";
	private readonly TempDirectory _dir = new("weavie-review-hint-tools-tests");
	private readonly SettingsStore _settings;
	private readonly ReviewHintStore _hints;
	private readonly McpServer _registry;
	private readonly McpServer _ide;
	private readonly HttpClient _client = new();

	public McpReviewHintToolsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Path.Combine(_dir.Path, "a.cs"), "one\ntwo\n");
		_hints = new ReviewHintStore(fs, _dir.Path, new MemoryReviewPersistence(), "Codex", _ => { }, _ => { });
		_registry = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", _settings, registryMode: true, reviewHints: _hints);
		_registry.Start();
		_ide = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", exposeIdeTools: true);
		_ide.Start();
	}

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync() {
		_client.Dispose();
		await _registry.DisposeAsync();
		await _ide.DisposeAsync();
		_settings.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public async Task OnlyTheRegistryAdvertisesTheTool() {
		Assert.Contains("\"addReviewHint\"", await ToolsAsync(_registry), StringComparison.Ordinal);
		Assert.DoesNotContain("\"addReviewHint\"", await ToolsAsync(_ide), StringComparison.Ordinal);
	}

	[Fact]
	public async Task AddsAHintAtTheLine() {
		using var response = await CallAsync(_registry, """{"path":"a.cs","line":2,"severity":"warn","text":"Risky\nBecause."}""");

		Assert.False(IsError(response));
		var hint = Assert.Single(_hints.Hints);
		Assert.Equal(("a.cs", 2, ReviewHintSeverity.Warn, "Risky", "Because.", "two", "Codex"),
			(hint.Path, hint.Line, hint.Severity, hint.Title, hint.Body, hint.Quote, hint.Agent));
	}

	[Theory]
	[InlineData("""{"path":"a.cs","line":2,"severity":"major","text":"x"}""", "bug, warn, or nit")]
	[InlineData("""{"path":"a.cs","line":"2","severity":"bug","text":"x"}""", "integer 'line'")]
	[InlineData("""{"path":"a.cs","line":9,"severity":"bug","text":"x"}""", "Line 9 is outside")]
	public async Task InvalidHintsAreToolErrors(string arguments, string error) {
		using var response = await CallAsync(_registry, arguments);

		Assert.True(IsError(response));
		Assert.Contains(error, Text(response), StringComparison.Ordinal);
		Assert.Empty(_hints.Hints);
	}

	[Fact]
	public async Task TheIdeServerHasNoHintSink() {
		using var response = await CallAsync(_ide, """{"path":"a.cs","line":1,"severity":"bug","text":"x"}""");

		Assert.True(IsError(response));
		Assert.Contains("not available", Text(response), StringComparison.Ordinal);
	}

	private async Task<string> ToolsAsync(McpServer server) {
		using var response = await PostAsync(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
		return response.RootElement.GetRawText();
	}

	private Task<JsonDocument> CallAsync(McpServer server, string arguments) =>
		PostAsync(server, $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"addReviewHint","arguments":{{{arguments}}}}}""");

	private async Task<JsonDocument> PostAsync(McpServer server, string json) {
		using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/mcp") {
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
		using var response = await _client.SendAsync(request);
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
	}

	private static bool IsError(JsonDocument response) =>
		response.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();

	private static string Text(JsonDocument response) =>
		response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
}
