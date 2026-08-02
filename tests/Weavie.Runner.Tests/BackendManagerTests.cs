using System.Diagnostics;
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
	public async Task Ensure_DoesNotEraseATerminalSupervisionHistory() {
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1");
		var backend = manager.Ensure();
		backend.Supervisor!.Dispose();
		using var terminal = new ProcessSupervisor(
			"worker",
			_ => { },
			() => { },
			new SupervisionOptions { Policy = RestartPolicy.OnFailure, MaxRestartsInWindow = 0 },
			log: null,
			clock: null);
		backend.Supervisor = terminal;
		terminal.Start();
		Assert.True(terminal.ReportUnhealthy(terminal.Generation, "permanent failure"));
		Assert.Equal(SupervisorState.Failed, terminal.State);

		var polled = manager.Ensure();

		Assert.Same(backend, polled);
		Assert.Same(terminal, polled.Supervisor);
		Assert.Equal(SupervisorState.Failed, polled.Supervisor!.State);
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
	public async Task StrictWorkerStatus_ParsesBuildAndSpawnContract() {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
			Content = new StringContent(
				$$"""{"buildNumber":"0.1.42","spawnContract":{{RunnerIdentity.SpawnContract}},"draining":false}"""),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		var probe = await manager.ProbeStatusAsync(Backend(), CancellationToken.None);

		Assert.Equal(WorkerStatusProbeState.Ready, probe.State);
		Assert.Equal(42, probe.Status!.Build);
		Assert.Equal(RunnerIdentity.SpawnContract, probe.Status.SpawnContract);
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("{\"buildNumber\":42}")]
	[InlineData("{\"buildNumber\":\"broken\"}")]
	[InlineData("{\"buildNumber\":\"0.1.42\"}")]
	[InlineData("{\"buildNumber\":\"0.1.42\",\"spawnContract\":\"2\"}")]
	public async Task MalformedWorkerStatus_IsAProtocolFailure(string body) {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
			Content = new StringContent(body),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		var probe = await manager.ProbeStatusAsync(Backend(), CancellationToken.None);

		Assert.Equal(WorkerStatusProbeState.ProtocolFailure, probe.State);
		Assert.Contains("malformed status identity", probe.Failure, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(HttpStatusCode.ServiceUnavailable, "Pending")]
	[InlineData(HttpStatusCode.NotFound, "ProtocolFailure")]
	[InlineData(HttpStatusCode.Unauthorized, "ProtocolFailure")]
	public async Task WorkerStatus_DistinguishesStartupFromProtocolFailure(
		HttpStatusCode status,
		string expected) {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(status)));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);

		Assert.Equal(expected, (await manager.ProbeStatusAsync(Backend(), CancellationToken.None)).State.ToString());
	}

	[Fact]
	public async Task StatusAsync_BoundsANonAnsweringWorkerControlRequest() {
		using var supervisor = Supervisor();
		supervisor.Start();
		using var http = new HttpClient(new HangingHttpHandler());
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);
		var backend = Backend();
		backend.Supervisor = supervisor;
		var elapsed = Stopwatch.StartNew();

		Assert.Equal("starting", await manager.StatusAsync(backend));
		Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"status took {elapsed.Elapsed}");
	}

	[Fact]
	public async Task StatusAsync_RejectsAWorkerFromAnotherContract() {
		using var supervisor = Supervisor();
		supervisor.Start();
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
			Content = new StringContent(
				$$"""{"buildNumber":"0.1.42","spawnContract":{{RunnerIdentity.SpawnContract - 1}}}"""),
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);
		var backend = Backend();
		backend.Supervisor = supervisor;

		Assert.Equal("failed", await manager.StatusAsync(backend));
	}

	[Fact]
	public async Task StatusAsync_DiscardsAReplyFromAReplacedGeneration() {
		using var supervisor = Supervisor();
		supervisor.Start();
		using var http = new HttpClient(new StubHttpHandler(_ => {
			supervisor.ReportUnhealthy(supervisor.Generation, "replace during status request");
			return new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(
					$$"""{"buildNumber":"0.1.42","spawnContract":{{RunnerIdentity.SpawnContract}}}"""),
			};
		}));
		await using var manager = new BackendManager(
			Options(),
			new HeadlessLauncher(() => "headless", "127.0.0.1", log: null),
			"127.0.0.1",
			http);
		var backend = Backend();
		backend.Supervisor = supervisor;

		Assert.Equal("starting", await manager.StatusAsync(backend));
		Assert.Equal(SupervisorState.BackingOff, supervisor.State);
	}

	[Fact]
	public void WorkerIdentityFailure_RequiresTheExactStagedBuildAndContract() {
		Assert.Null(BackendManager.WorkerIdentityFailure(
			new WorkerControlStatus(42, RunnerIdentity.SpawnContract),
			42));
		Assert.Contains("staged build 42", BackendManager.WorkerIdentityFailure(
			new WorkerControlStatus(41, RunnerIdentity.SpawnContract),
			42), StringComparison.Ordinal);
		Assert.Contains("spawn contract", BackendManager.WorkerIdentityFailure(
			new WorkerControlStatus(42, RunnerIdentity.SpawnContract - 1),
			42), StringComparison.Ordinal);
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
		state.MarkReady();
		state.UnreachableFailures = 2;

		state.Observe(second, secondSupervisor, secondStarted);

		Assert.Equal(1, firstSupervisor.Generation);
		Assert.Equal(1, secondSupervisor.Generation);
		Assert.False(state.Ready);
		Assert.Equal(0, state.UnreachableFailures);
		Assert.Equal(secondStarted, state.GenerationStarted);
	}

	[Theory]
	[InlineData(HttpStatusCode.OK, "{\"healthy\":true,\"activeOperations\":[]}", "Healthy")]
	[InlineData(HttpStatusCode.OK,
		"{\"healthy\":true,\"activeOperations\":[{\"id\":\"msg-1\",\"endpoint\":\"host\",\"feature\":\"sessions\",\"name\":\"load\",\"stage\":\"handler\",\"elapsedMs\":11000}]}",
		"Busy")]
	[InlineData(HttpStatusCode.InternalServerError, "{}", "Unreachable")]
	public async Task HealthProbe_ClassifiesHealthyAndUnavailableResponses(
		HttpStatusCode status,
		string body,
		string expected) {
		using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(status) {
			Content = new StringContent(body),
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

	private sealed class HangingHttpHandler : HttpMessageHandler {
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) {
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			throw new InvalidOperationException("unreachable");
		}
	}
}
