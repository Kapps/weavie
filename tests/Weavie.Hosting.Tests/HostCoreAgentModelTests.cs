using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreAgentModelTests {
	[Fact]
	public async Task ALiveSessionsReadyControlsReplaceItsProvidersModelSnapshot() {
		await using var host = await TestHost.StartAsync();
		host.AgentModels.Start();
		Assert.Empty(host.AgentModels.Find("structured")!.Models);

		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "models",
			Base = "main",
			AgentProviderId = "structured",
		})).Ok);

		var entry = host.AgentModels.Find("structured")!;
		Assert.Equal(AgentModelSource.Session, entry.Source);
		Assert.Equal(["GPT Test"], entry.Models.Select(model => model.Id));
	}
}
