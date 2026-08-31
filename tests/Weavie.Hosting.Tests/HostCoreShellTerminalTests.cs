using System.Text.Json;
using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreShellTerminalTests {
	[Fact]
	public async Task NewTerminal_CreatesAnIndependentExactMessageChannel() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.EnsureStarted();
		var first = host.SelectedSession.Shells.Primary!;
		var firstProcess = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeClientCommandAsync(CoreCommands.NewTerminal, new { });

		Assert.True(result.Ok, result.Error);
		using var data = JsonDocument.Parse(result.DataJson!);
		string secondId = data.RootElement.GetProperty("terminalId").GetString()!;
		Assert.True(data.RootElement.GetProperty("activateTerminal").GetBoolean());
		Assert.Equal(2, host.SelectedSession.Shells.Items.Count);
		Assert.Equal(2, host.Platform.NoopLauncher.Created.Count);
		var secondProcess = host.Platform.NoopLauncher.Created[1];

		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(secondId),
			"input",
			new { dataB64 = "c2Vjb25k", userInitiated = true });

		Assert.Equal(0, firstProcess.WriteCount);
		Assert.Equal("second", secondProcess.WrittenText);
		Assert.Equal(first.Id, host.SelectedSession.Shells.Primary!.Id);
	}

	[Fact]
	public async Task CloseTerminal_RemovesOnlyTheAddressedTab() {
		await using var host = await TestHost.StartAsync();
		var first = host.SelectedSession.Shells.Primary!;
		string firstScrollback = first.Controller.ScrollbackLogPath!;
		Assert.True(File.Exists(firstScrollback));
		var created = await host.InvokeClientCommandAsync(CoreCommands.NewTerminal, new { });
		using var data = JsonDocument.Parse(created.DataJson!);
		string secondId = data.RootElement.GetProperty("terminalId").GetString()!;

		var closed = await host.InvokeClientCommandAsync(
			CoreCommands.CloseTerminal,
			new { id = first.Id, force = false });

		Assert.True(closed.Ok, closed.Error);
		Assert.False(File.Exists(firstScrollback));
		Assert.Equal(secondId, Assert.Single(host.SelectedSession.Shells.Items).Id);
		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(secondId),
			"input",
			new { dataB64 = "c3RpbGwtaGVyZQ==", userInitiated = true });
		Assert.Equal("still-here", Assert.Single(host.Platform.NoopLauncher.Created).WrittenText);
	}

	[Fact]
	public async Task CloseTerminal_RequiresForceForAForegroundJob() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.EnsureStarted();
		var terminal = host.SelectedSession.Shells.Primary!;
		Assert.Single(host.Platform.NoopLauncher.Created).HasForegroundJob = true;

		var rejected = await host.InvokeClientCommandAsync(
			CoreCommands.CloseTerminal,
			new { id = terminal.Id, force = false });

		Assert.False(rejected.Ok);
		using var detail = JsonDocument.Parse(rejected.DataJson!);
		Assert.True(detail.RootElement.GetProperty("busy").GetBoolean());
		Assert.Single(host.SelectedSession.Shells.Items);

		var forced = await host.InvokeClientCommandAsync(
			CoreCommands.CloseTerminal,
			new { id = terminal.Id, force = true });
		Assert.True(forced.Ok, forced.Error);
		Assert.Empty(host.SelectedSession.Shells.Items);
	}

	[Fact]
	public async Task ExitedTerminal_StaysVisibleAndCanBeReopened() {
		await using var host = await TestHost.StartAsync();
		host.SelectedSession.Shells.EnsureStarted();
		var terminal = host.SelectedSession.Shells.Primary!;
		string feature = ShellTerminalSet.FeatureName(terminal.Id);
		Assert.Single(host.Platform.NoopLauncher.Created).Exit(0);
		await Wait.UntilAsync(() => host.Bridge.LastEvent(feature, "exit") is not null);

		var reopened = await host.InvokeClientCommandAsync(
			CoreCommands.ReopenTerminal,
			new { id = terminal.Id });
		Assert.True(reopened.Ok, reopened.Error);
		host.SessionEvent(
			host.WorkspaceSession,
			feature,
			"ready",
			new { columns = 100, rows = 30 });

		Assert.Equal(2, host.Platform.NoopLauncher.Created.Count);
		Assert.Equal(terminal.Id, Assert.Single(host.SelectedSession.Shells.Items).Id);
	}

	[Fact]
	public async Task TerminalIdsAndOrderSurviveHostRestart() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.InvokeClientCommandAsync(CoreCommands.NewTerminal, new { })).Ok);
		Assert.True((await host.InvokeClientCommandAsync(CoreCommands.NewTerminal, new { })).Ok);
		string[] ids = [.. host.SelectedSession.Shells.Items.Select(terminal => terminal.Id)];

		await host.RestartAsync();

		Assert.Equal(ids, host.SelectedSession.Shells.Items.Select(terminal => terminal.Id));
	}
}
