using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Weavie.AgentClientProtocol;
using Weavie.Core.Lsp;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Agents and language servers stop through their own protocol, so a .NET child runs its runtime cleanup instead
/// of being killed with its IPC files left in the temp directory.
/// </summary>
public sealed class GracefulChildStopTests {
	[Fact]
	public async Task AnAgentFloodingStdoutAsItShutsDown_StopsGracefullyAndLeavesNoRuntimeFiles() {
		var log = new ConcurrentQueue<string>();
		int forced = 0;
		var connection = new AcpJsonRpcConnection(new AcpAgentDefinition {
			Id = "flood",
			Name = "Flooding fake",
			Command = FakeAcp(),
			Arguments = ["flood-on-close"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		}, Directory.GetCurrentDirectory(), log.Enqueue);
		connection.ProcessStopped += _ => Interlocked.Increment(ref forced);
		int pid;
		try {
			connection.Start();
			pid = await Wait.ForAsync(() => ReportedPid(log));
			// The runtime's IPC files exist while the child runs, so their absence afterwards is meaningful.
			await Wait.UntilAsync(() => RuntimeFiles(pid).Length > 0);
		} finally {
			var stopping = Stopwatch.StartNew();
			await connection.DisposeAsync();
			Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(4), $"stop took {stopping.Elapsed}");
		}

		Assert.Equal(0, Volatile.Read(ref forced));
		Assert.Empty(RuntimeFiles(pid));
	}

	[Fact]
	public async Task ALanguageServer_IsShutDownWithoutItsShutdownReplyReachingThePage() {
		using var workspace = new TempDirectory("wv-lsp");
		var server = new LspServerLauncher().Start(new ResolvedCommand(FakeAcp(), ["lsp"], FakeAcp()), workspace.Path, _ => { });
		var frames = new ConcurrentQueue<string>();
		server.FrameReceived += frame => frames.Enqueue(Encoding.UTF8.GetString(frame));
		server.Start();
		server.Write("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"""u8.ToArray());
		await Wait.UntilAsync(() => frames.Any(frame => frame.Contains("\"id\":1", StringComparison.Ordinal)));

		Assert.Same(ProcessStopOutcome.Graceful, await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(4)));
		Assert.DoesNotContain(frames, frame => frame.Contains("weavie/shutdown", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ALanguageServerThePageAlreadyShutDown_StillStopsGracefully() {
		using var workspace = new TempDirectory("wv-lsp");
		var server = new LspServerLauncher().Start(new ResolvedCommand(FakeAcp(), ["lsp"], FakeAcp()), workspace.Path, _ => { });
		var frames = new ConcurrentQueue<string>();
		server.FrameReceived += frame => frames.Enqueue(Encoding.UTF8.GetString(frame));
		server.Start();
		server.Write("""{"jsonrpc":"2.0","id":7,"method":"shutdown"}"""u8.ToArray());
		await Wait.UntilAsync(() => frames.Any(frame => frame.Contains("\"id\":7", StringComparison.Ordinal)));

		Assert.Same(ProcessStopOutcome.Graceful, await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(4)));
	}

	private static int? ReportedPid(IEnumerable<string> log) => log
		.Select(line => line.IndexOf("] pid ", StringComparison.Ordinal) is int at and >= 0
			? int.Parse(line.AsSpan(at + 6), CultureInfo.InvariantCulture)
			: (int?)null)
		.FirstOrDefault(pid => pid is not null);

	private static string[] RuntimeFiles(int pid) => [.. Directory.EnumerateFileSystemEntries(Path.GetTempPath())
		.Where(entry => Path.GetFileName(entry).Contains($"-{pid}-", StringComparison.Ordinal))];

	private static string FakeAcp() => AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp");
}
