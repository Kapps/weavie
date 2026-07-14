using System.Net;
using System.Net.Sockets;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Runner.Tests;

public sealed class BackendManagerTests {
	[Fact]
	public async Task Drain_RetriesStartup503_AndClearsTheDetailWhenAccepted() {
		var responses = new Queue<HttpResponseMessage>([
			new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
			new HttpResponseMessage(HttpStatusCode.Accepted),
		]);
		using var http = new HttpClient(new StubHttpHandler(_ => responses.Dequeue()));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);
		var backend = new WorkspaceBackend {
			WorkspaceRoot = Path.GetTempPath(),
			Port = UnusedPort(),
			Token = "worker",
		};
		var reports = new List<(string Phase, string? Detail)>();

		Assert.False(await manager.TryDrainAsync(backend, Report, CancellationToken.None));
		Assert.Equal(("updating", "worker is still starting; drain will retry"), reports[^1]);
		Assert.True(await manager.TryDrainAsync(backend, Report, CancellationToken.None));
		Assert.Equal(("updating", "waiting for the workspace to go quiet"), reports[^1]);

		void Report(string phase, string? detail) => reports.Add((phase, detail));
	}

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
			Options(),
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

	private static RunnerOptions Options() => new() {
		WorkspaceRoot = Path.GetTempPath(),
		HeadlessPath = "headless",
		RunnerToken = "runner",
	};

	private static int UnusedPort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}

	private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}
}
