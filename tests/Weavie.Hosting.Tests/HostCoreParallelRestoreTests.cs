using System.Collections.Concurrent;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreParallelRestoreTests {
	[Fact]
	public async Task RestorationConstructsSessionsConcurrently() {
		await using var host = await TestHost.StartAsync();
		foreach (string branch in new[] { "branch-a", "branch-b" }) {
			Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
				Branch = branch,
				Base = "main",
				AgentProviderId = "structured",
			})).Ok);
		}
		var provider = Assert.IsType<FakeStructuredAgentProvider>(host.AgentProviders.RequireAvailable("structured"));
		using var entered = new CountdownEvent(2);
		using var release = new ManualResetEventSlim();
		var restart = host.RestartAsync(() => provider.CreatingSession = _ => {
			entered.Signal();
			release.Wait();
		});
		try {
			Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))),
				"Both sessions must enter construction before either is released.");
			Assert.False(restart.IsCompleted);
		} finally {
			release.Set();
			await restart;
			provider.CreatingSession = _ => { };
		}
		Assert.NotNull(host.Session("branch-a"));
		Assert.NotNull(host.Session("branch-b"));
	}

	[Fact]
	public async Task RestorationStartsTerminalsConcurrently_AndPersistsEverySession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		using var entered = new CountdownEvent(2);
		using var release = new ManualResetEventSlim();
		var workspaces = new ConcurrentDictionary<string, byte>();
		var restart = host.RestartAsync(() => { }, platform => platform.NoopLauncher.Resolving = launch => {
			if (workspaces.TryAdd(launch.WorkingDirectory, 0)) entered.Signal();
			release.Wait();
		});
		try {
			Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))),
				"Both workspaces must enter terminal launch before either is released.");
			Assert.False(restart.IsCompleted);
		} finally {
			release.Set();
			await restart;
		}
		await host.RestartAsync();
		Assert.NotNull(host.WorkspaceSession);
		Assert.NotNull(host.Session("branch-a"));
	}
}
