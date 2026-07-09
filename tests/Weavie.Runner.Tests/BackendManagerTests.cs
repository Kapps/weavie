using System.Net;
using System.Net.Sockets;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Runner.Tests;

public sealed class BackendManagerTests {
	[Fact]
	public async Task StatusAsync_ReturnsStartingUntilWorkerControlEndpointAnswers() {
		using var supervisor = new ProcessSupervisor(
			"worker",
			_ => { },
			() => { },
			new SupervisionOptions { Policy = RestartPolicy.OnFailure },
			log: null,
			clock: null);
		supervisor.Start();

		await using var manager = new BackendManager(
			new RunnerOptions {
				WorkspaceRoot = Path.GetTempPath(),
				HeadlessPath = "headless",
				RunnerToken = "runner",
			},
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1");

		var backend = new WorkspaceBackend {
			WorkspaceRoot = Path.GetTempPath(),
			Port = UnusedPort(),
			Token = "worker",
			Supervisor = supervisor,
		};

		Assert.Equal("starting", await manager.StatusAsync(backend));
	}

	private static int UnusedPort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}
}
