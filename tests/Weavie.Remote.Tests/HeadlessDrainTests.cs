using System.Net;
using Xunit;

namespace Weavie.Remote.Tests;

/// <summary>The runner's authenticated drain request shuts a real remote worker down cleanly.</summary>
public sealed class HeadlessDrainTests {
	[Fact]
	public async Task AuthenticatedDrain_ExitsTheWorkerCleanly() {
		string workspace = Path.Combine(Path.GetTempPath(), "weavie-drain-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workspace);
		try {
			int port = Hosts.FreePort();
			await using var host = await HostHandle.StartAsync(
				Hosts.HeadlessDll,
				["--remote", "--bind", "127.0.0.1", "--port", port.ToString(), "--token", Tokens.Correct, "--workspace", workspace],
				port,
				readyMarker: "open  http://",
				timeout: TimeSpan.FromSeconds(60));

			using var http = new HttpClient();
			using var response = await http.PostAsync($"{host.BaseUrl}/control/drain?token={Tokens.Correct}", content: null);

			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			Assert.Equal(0, await host.WaitForExitAsync(TimeSpan.FromSeconds(30)));
		} finally {
			try {
				Directory.Delete(workspace, recursive: true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}
}
