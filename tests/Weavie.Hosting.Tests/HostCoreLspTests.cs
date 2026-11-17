using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

// The configured language-server command is read when a server starts, and applied by Restart Language Servers.
public sealed class HostCoreLspTests {
	private static readonly string Key = LspSettings.CommandKey("csharp");

	[Fact]
	public async Task AConfiguredPathIsResolvedInTheStartingSessionsWorktree() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(Key, JsonSerializer.SerializeToElement("[\"tools/missing-ls\"]"), host.RepoRoot);
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		foreach (var session in new[] { host.WorkspaceSession, host.Session("feature") }) {
			var result = await host.SessionRequestAsync<HostSession.LspStartResult>(
				session, "lsp", "start", new { server = "csharp", channel = "c1" });

			Assert.False(result.Ok);
			Assert.Equal($"C# language server not found (tried {Path.Combine(session.WorkspaceRoot, "tools", "missing-ls")})", result.Error);
		}
	}

	[Fact]
	public async Task EachStartReadsTheCurrentSetting() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;

		foreach (string name in new[] { "first-ls", "second-ls" }) {
			host.Settings.Set(Key, JsonSerializer.SerializeToElement($"[\"{name}\"]"), host.RepoRoot);
			var result = await host.SessionRequestAsync<HostSession.LspStartResult>(
				session, "lsp", "start", new { server = "csharp", channel = name });

			Assert.Equal($"C# language server not found (tried {name})", result.Error);
		}
	}

	[Fact]
	public async Task OnlyRestartRepublishesAndOnlyToTheInvokingSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var session = host.Session("feature");
		host.Bridge.Clear();

		host.Settings.Set(Key, JsonSerializer.SerializeToElement("[\"my-ls\"]"), host.RepoRoot);
		Assert.Empty(host.Bridge.PostedEvents("lsp", "config"));

		Assert.True((await host.InvokeCommandAsync(session.SlotId, CoreCommands.RestartLanguageServers, new { }, CancellationToken.None)).Ok);

		Assert.Equal(1, host.Bridge.LastEvent(session.Address, "lsp", "config")?.GetProperty("revision").GetInt32());
		Assert.Single(host.Bridge.PostedEvents("lsp", "config"));
	}
}
