using System.Threading.Channels;
using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpProcessDrainTests {
	[Fact]
	public void AcpConnection_ResolvesWindowsNpxFromPathWithoutConsultingTheWorkspace() {
		string root = Path.Combine(Path.GetTempPath(), $"weavie-npx-path-{Guid.NewGuid():N}");
		string workspace = Path.Combine(root, "workspace");
		string trusted = Path.Combine(root, "trusted");
		Directory.CreateDirectory(workspace);
		Directory.CreateDirectory(trusted);
		File.WriteAllText(Path.Combine(workspace, "npx.cmd"), "shadow");
		string expected = Path.Combine(trusted, "npx.cmd");
		File.WriteAllText(expected, "trusted");
		try {
			string resolved = AcpProcessInvocation.ResolveNpxOnPath(
				string.Join(Path.PathSeparator, workspace, ".", trusted),
				workspace);

			Assert.Equal(expected, resolved);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task AcpConnection_DeliversFinalResponseBeforeExitFault() {
		var definition = Definition("echo-and-exit");
		await using var connection = new AcpJsonRpcConnection(definition, Directory.GetCurrentDirectory(), _ => { });
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ProcessStarted += _ => started.TrySetResult();
		connection.ProtocolFaulted += (_, error) => faulted.TrySetResult(error);
		connection.Start();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

		var response = await connection.RequestAsync(
			"final",
			new { },
			CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal("final", response.GetProperty("value").GetString());
		await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task AcpConnection_TerminallyRejectsAMalformedErrorEnvelope() {
		var definition = Definition("malformed-error");
		await using var connection = new AcpJsonRpcConnection(definition, Directory.GetCurrentDirectory(), _ => { });
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ProcessStarted += _ => started.TrySetResult();
		connection.ProtocolFaulted += (_, error) => faulted.TrySetResult(error);
		connection.Start();
		await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

		var request = connection.RequestAsync("invalid", new { }, CancellationToken.None);
		var fault = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
		await Assert.ThrowsAsync<AcpProtocolException>(() => request);

		Assert.Contains("integer code", fault.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AcpConnection_SurfacesAnExecutableLaunchFailure() {
		string executable = Path.Combine(Path.GetTempPath(), $"weavie-invalid-acp-{Guid.NewGuid():N}");
		await File.WriteAllTextAsync(executable, "not an executable");
		try {
			var definition = Definition(executable, []);
			await using var connection = new AcpJsonRpcConnection(
				definition,
				Directory.GetCurrentDirectory(),
				_ => { });
			var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
			connection.ProtocolFaulted += (_, error) => faulted.TrySetResult(error);

			connection.Start();

			var fault = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Contains("could not start", fault.Message, StringComparison.OrdinalIgnoreCase);
		} finally {
			File.Delete(executable);
		}
	}

	[Fact]
	public async Task AcpConnection_RestartAndDisposeTerminateBlockedStdinWrites() {
		string marker = Path.Combine(Path.GetTempPath(), $"weavie-acp-stdin-{Guid.NewGuid():N}");
		var definition = Definition(FakeExecutable(), ["stdin-stall", marker]);
		await using var connection = new AcpJsonRpcConnection(
			definition,
			Directory.GetCurrentDirectory(),
			_ => { });
		var started = Channel.CreateUnbounded<AcpProcessGeneration>();
		connection.ProcessStarted += generation => started.Writer.TryWrite(generation);
		connection.Start();
		await ReadGenerationAsync(started.Reader);
		string payload = new('x', 16 * 1024 * 1024);

		var first = Task.Run(() => connection.RequestAsync("stall", new { payload }, CancellationToken.None));
		await Wait.UntilAsync(() => File.Exists(marker));
		await Task.Run(connection.Restart).WaitAsync(TimeSpan.FromSeconds(10));
		await ReadGenerationAsync(started.Reader);
		await Assert.ThrowsAnyAsync<Exception>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));

		File.Delete(marker);
		var second = Task.Run(() => connection.RequestAsync("stall", new { payload }, CancellationToken.None));
		await Wait.UntilAsync(() => File.Exists(marker));
		await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
		await Assert.ThrowsAnyAsync<Exception>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
		File.Delete(marker);
	}

	[Fact]
	public async Task AcpConnection_RejectsOperationsOwnedByAReplacedGeneration() {
		var definition = Definition(FakeExecutable(), []);
		await using var connection = new AcpJsonRpcConnection(
			definition,
			Directory.GetCurrentDirectory(),
			_ => { });
		var started = Channel.CreateUnbounded<AcpProcessGeneration>();
		connection.ProcessStarted += generation => started.Writer.TryWrite(generation);
		connection.Start();
		var first = await ReadGenerationAsync(started.Reader);
		connection.Restart();
		var second = await ReadGenerationAsync(started.Reader);

		await Assert.ThrowsAsync<InvalidOperationException>(() => connection.RequestAsync(
			"initialize",
			new { protocolVersion = 1, clientCapabilities = new { plan = new { } } },
			first.Generation,
			CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => connection.NotifyAsync(
			"session/cancel",
			new { sessionId = "old" },
			first.Generation));
		var initialized = await connection.RequestAsync(
			"initialize",
			new { protocolVersion = 1, clientCapabilities = new { plan = new { } } },
			second.Generation,
			CancellationToken.None);
		Assert.Equal(1, initialized.GetProperty("protocolVersion").GetInt32());
	}

	[Fact]
	public async Task AcpConnection_PublishesStartedBeforeAnImmediateProtocolFault() {
		var definition = new AcpAgentDefinition {
			Id = "immediate-malformed",
			Name = "Immediate malformed ACP",
			Command = FakeExecutable(),
			Arguments = [],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal) {
				["WEAVIE_FAKE_ACP_MODE"] = "immediate-malformed",
			},
			Distribution = "custom",
		};
		await using var connection = new AcpJsonRpcConnection(
			definition,
			Directory.GetCurrentDirectory(),
			_ => { });
		var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.ProcessStarted += _ => order.Enqueue("started");
		connection.ProtocolFaulted += (_, _) => {
			order.Enqueue("faulted");
			faulted.TrySetResult();
		};

		connection.Start();
		await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal(["started", "faulted"], order);
	}

	private static AcpAgentDefinition Definition(string argument) => Definition(FakeExecutable(), [argument]);

	private static async Task<AcpProcessGeneration> ReadGenerationAsync(
		ChannelReader<AcpProcessGeneration> reader) {
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		return await reader.ReadAsync(timeout.Token);
	}

	private static AcpAgentDefinition Definition(string executable, IReadOnlyList<string> arguments) => new() {
		Id = "drain",
		Name = "Drain fake",
		Command = executable,
		Arguments = arguments,
		Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		Distribution = "custom",
	};

	private static string FakeExecutable() => AcpAgentSessionFixture.ExecutablePath(
		"tools",
		"Weavie.FakeAcp",
		"weavie-fake-acp");
}
