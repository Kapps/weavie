using System.Net;
using Weavie.Hosting.Web;
using Xunit;

namespace Weavie.Remote.Tests;

/// <summary>The runner's authenticated drain request shuts a real remote worker down cleanly.</summary>
public sealed class HeadlessDrainTests {
	[Fact]
	public async Task AuthenticatedDrain_ExitsTheWorkerCleanly() {
		using var workspace = new TempDirectory("weavie-drain-tests");
		int port = Hosts.FreePort();
		await using var host = await HostHandle.StartAsync(
			Hosts.HeadlessDll,
			["--remote", "--bind", "127.0.0.1", "--port", port.ToString(), "--token", Tokens.Correct,
				"--workspace", workspace.Path, "--spawn-contract", WorkspaceControlProtocol.SpawnContract.ToString()],
			port,
			readyMarker: "open  http://",
			timeout: TimeSpan.FromSeconds(60));

		using var http = new HttpClient();
		using var response = await http.PostAsync($"{host.BaseUrl}/control/drain?token={Tokens.Correct}", content: null);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
	}
}
