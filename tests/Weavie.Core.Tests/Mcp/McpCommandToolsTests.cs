using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The command discovery tools over the registry server's HTTP endpoint: <c>listCommands</c> is an index an
/// agent reads whole, and <c>describeCommand</c> adds one command's description and argument schema.
/// </summary>
public sealed class McpCommandToolsTests : IDisposable {
	private const string Token = "0123456789abcdef0123456789abcdef";

	// An agent must read a whole index in one tool result; Claude Code rejects one near 100,000 characters.
	private const int IndexBudget = 40_000;

	private readonly TempDirectory _dir = new("weavie-mcp-command-tests");
	private readonly SettingsStore _settings;
	private readonly McpServer _server;
	private readonly int _port;

	public McpCommandToolsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], _settings);
		_port = _server.Start();
	}

	public void Dispose() {
		_server.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_settings.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public async Task ListCommands_IndexesEveryCommandWithinTheReadBudget() {
		string text = ToolText(await CallAsync("listCommands", "{}"));
		using var index = JsonDocument.Parse(text);
		var rows = index.RootElement.GetProperty("commands").EnumerateArray().ToList();

		Assert.Equal(CoreCommands.CreateRegistry().Definitions.Count, rows.Count);
		Assert.InRange(text.Length, 1, IndexBudget);
		var newSession = rows.Single(row => row.GetProperty("id").GetString() == SessionCommands.NewSession);
		Assert.Equal("New Session", newSession.GetProperty("title").GetString());
		Assert.Equal("Session", newSession.GetProperty("category").GetString());
		Assert.Contains("branch", newSession.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()));
		Assert.False(newSession.TryGetProperty("description", out _));
		Assert.False(newSession.TryGetProperty("keys", out _));
		var palette = rows.Single(row => row.GetProperty("id").GetString() == CoreCommands.FocusOmnibarCommands);
		Assert.NotEmpty(palette.GetProperty("keys").EnumerateArray());
		Assert.False(palette.TryGetProperty("args", out _));
	}

	[Fact]
	public async Task DescribeCommand_ReturnsDescriptionAndArgumentSchema() {
		using var command = JsonDocument.Parse(
			ToolText(await CallAsync("describeCommand", $$"""{"id":"{{SessionCommands.NewSession}}"}""")));

		Assert.Contains("'branch' is required", command.RootElement.GetProperty("description").GetString());
		Assert.NotEmpty(command.RootElement.GetProperty("aliases").EnumerateArray());
		Assert.Equal("string", command.RootElement.GetProperty("argsSchema").GetProperty("branch").GetProperty("type").GetString());
	}

	[Fact]
	public async Task DescribeCommand_UnknownId_Errors() {
		using var response = await CallAsync("describeCommand", """{"id":"weavie.nope"}""");

		Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		Assert.Contains("Unknown command 'weavie.nope'", ToolText(response));
	}

	[Fact]
	public async Task ListSettings_IndexesEverySettingWithinTheReadBudget() {
		string text = ToolText(await CallAsync("listSettings", "{}"));
		using var index = JsonDocument.Parse(text);
		var rows = index.RootElement.GetProperty("settings").EnumerateArray().ToList();

		Assert.Equal(CoreSettings.CreateRegistry().Definitions.Count, rows.Count);
		Assert.InRange(text.Length, 1, IndexBudget);
		var mode = rows.Single(row => row.GetProperty("key").GetString() == "theme.mode");
		Assert.Equal(
			["key", "type", "value", "source", "allowedValues"],
			mode.EnumerateObject().Select(property => property.Name));
	}

	[Fact]
	public async Task GetSetting_ReturnsWhatTheIndexOmits() {
		using var setting = JsonDocument.Parse(ToolText(await CallAsync("getSetting", """{"key":"theme.mode"}""")));

		foreach (string name in new[] { "description", "scope", "aliases", "default", "apply" }) {
			Assert.True(setting.RootElement.TryGetProperty(name, out _), name);
		}
	}

	private async Task<JsonDocument> CallAsync(string tool, string arguments) {
		using var client = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/mcp") {
			Content = new StringContent(
				$$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}"""),
		};
		request.Headers.Authorization = new("Bearer", Token);
		using var response = await client.SendAsync(request);
		return await TestMcp.ReadResponseAsync(response);
	}

	private static string ToolText(JsonDocument response) {
		using (response) {
			return response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
		}
	}
}
