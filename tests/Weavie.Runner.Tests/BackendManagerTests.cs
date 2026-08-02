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
			PortIsPinned = false,
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
	public async Task Ensure_PinsThePort_OnlyWhenWorkerPortIsConfigured() {
		await using var pinned = new BackendManager(
			Options() with { WorkerPort = UnusedPort() },
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1");
		Assert.True(pinned.Ensure().PortIsPinned);

		await using var unpinned = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1");
		Assert.False(unpinned.Ensure().PortIsPinned);
	}

	[Fact]
	public async Task Fresh_managers_keep_the_worker_token_for_the_same_runner_identity() {
		var options = Options();
		var launcher = new HeadlessLauncher(() => "headless", "127.0.0.1", log: null);
		await using var first = new BackendManager(options, launcher, "127.0.0.1");
		await using var second = new BackendManager(options, launcher, "127.0.0.1");
		await using var rotated = new BackendManager(
			options with { RunnerToken = "another-runner" },
			launcher,
			"127.0.0.1");

		Assert.Equal(first.Ensure().Token, second.Ensure().Token);
		Assert.NotEqual(first.Ensure().Token, rotated.Ensure().Token);
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
			PortIsPinned = false,
			Token = "worker",
			Supervisor = supervisor,
		};

		Assert.Equal("starting", await manager.StatusAsync(backend));
	}

	[Fact]
	public async Task PreHealthWorkerStatus_IsReadyWithoutAdvertisingMessageHealth() {
		using var http = new HttpClient(new StubHttpHandler(request => {
			Assert.Contains("/control/status", request.RequestUri?.AbsolutePath, StringComparison.Ordinal);
			return new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent("""{"buildNumber":"0.1.42","draining":false}"""),
			};
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		var status = await manager.TryReadStatusAsync(Backend(), CancellationToken.None);

		Assert.NotNull(status);
		Assert.Equal(42, status.Build);
		Assert.False(status.SupportsMessageHealth);
		var state = new WorkerHealthMonitorState();
		state.MarkReady(status);
		Assert.True(state.Ready);
		Assert.False(state.SupportsMessageHealth);
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("{\"buildNumber\":42}")]
	[InlineData("{\"buildNumber\":\"broken\"}")]
	[InlineData("{\"buildNumber\":\"0.1.42\",\"capabilities\":{}}")]
	public async Task MalformedWorkerStatus_RemainsNotReady(string body) {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
			Content = new StringContent(body),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		Assert.Null(await manager.TryReadStatusAsync(Backend(), CancellationToken.None));
	}

	[Fact]
	public async Task HealthProbe_ReportsTheExactTimedOutMessageOperation() {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
			Content = new StringContent(
				"""{"healthy":false,"lastFailure":{"id":"msg-7","endpoint":"session:mobile/i2","feature":"lifecycle","name":"sync","stage":"handler","elapsedMs":60001}}"""),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);
		var backend = Backend();

		var health = await manager.ProbeHealthAsync(backend, CancellationToken.None);

		Assert.Equal(WorkerHealthState.Unhealthy, health.State);
		Assert.Equal("msg-7 session:mobile/i2 lifecycle.sync stuck in handler for 60001 ms", health.Detail);
	}

	[Fact]
	public async Task HealthProbe_ReportsIngressAndOldestActiveOperationBeforeTimeout() {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
			Content = new StringContent(
				"""{"healthy":false,"ingressResponsive":false,"activeOperations":[{"id":"msg-9","endpoint":"session:mobile/i2","feature":"lifecycle","name":"sync","stage":"handler-dispatch","elapsedMs":4100}],"lastFailure":null}"""),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		var health = await manager.ProbeHealthAsync(Backend(), CancellationToken.None);

		Assert.Equal(WorkerHealthState.Unhealthy, health.State);
		Assert.Equal(
			"worker message ingress is unresponsive; oldest active operation "
				+ "msg-9 session:mobile/i2 lifecycle.sync stuck in handler-dispatch for 4100 ms",
			health.Detail);
	}

	[Fact]
	public async Task HealthProbe_RejectsAValidJsonPayloadWithTheWrongShape() {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
			Content = new StringContent("[]"),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		var health = await manager.ProbeHealthAsync(Backend(), CancellationToken.None);

		Assert.Equal(WorkerHealthState.Unhealthy, health.State);
		Assert.Equal("worker reported unhealthy with a malformed health payload", health.Detail);
	}

	[Fact]
	public void HealthMonitorState_ResetsAcrossBackendsWhoseSupervisorsShareAGenerationNumber() {
		using var firstSupervisor = Supervisor();
		using var secondSupervisor = Supervisor();
		firstSupervisor.Start();
		secondSupervisor.Start();
		var first = Backend();
		var second = Backend();
		first.Supervisor = firstSupervisor;
		second.Supervisor = secondSupervisor;
		var state = new WorkerHealthMonitorState();
		var firstStarted = DateTimeOffset.UnixEpoch;
		var secondStarted = firstStarted.AddMinutes(3);
		state.Observe(first, firstSupervisor, firstStarted);
		state.MarkReady(new WorkerControlStatus(42, SupportsMessageHealth: true));
		state.UnreachableFailures = 2;

		state.Observe(second, secondSupervisor, secondStarted);

		Assert.Equal(1, firstSupervisor.Generation);
		Assert.Equal(1, secondSupervisor.Generation);
		Assert.False(state.Ready);
		Assert.False(state.SupportsMessageHealth);
		Assert.Equal(0, state.UnreachableFailures);
		Assert.Equal(secondStarted, state.GenerationStarted);
	}

	[Theory]
	[InlineData(HttpStatusCode.OK, "Healthy")]
	[InlineData(HttpStatusCode.InternalServerError, "Unreachable")]
	public async Task HealthProbe_ClassifiesHealthyAndUnavailableResponses(
		HttpStatusCode status,
		string expected) {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(status) {
			Content = new StringContent("{}"),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		var health = await manager.ProbeHealthAsync(Backend(), CancellationToken.None);

		Assert.Equal(expected, health.State.ToString());
	}

	private static RunnerOptions Options() => new() {
		WorkspaceRoot = Path.GetTempPath(),
		HeadlessPath = "headless",
		RunnerToken = "runner",
	};

	private static WorkspaceBackend Backend() => new() {
		WorkspaceRoot = Path.GetTempPath(),
		Port = UnusedPort(),
		PortIsPinned = false,
		Token = "worker",
	};

	private static ProcessSupervisor Supervisor() => new(
		"worker",
		_ => { },
		() => { },
		new SupervisionOptions { Policy = RestartPolicy.OnFailure },
		log: null,
		clock: null);

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
