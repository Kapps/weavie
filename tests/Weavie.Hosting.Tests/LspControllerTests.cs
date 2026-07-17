using System.Text;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Unit tests for <see cref="LspController"/> at the <see cref="ILspServerLauncher"/> seam: a fake launcher
/// returns a scripted server, so the host's multiplex/lifecycle logic is exercised without spawning a process or
/// opening a socket. The recipe's candidate is <c>dotnet</c> (on PATH wherever tests run) so
/// <see cref="ServerResolver"/> resolves a command and the fake launcher is reached.
/// </summary>
public sealed class LspControllerTests {
	private static readonly LanguageServerDescriptor FakeRecipe = new() {
		Id = "fake",
		DisplayName = "Fake",
		LanguageIds = ["fake"],
		FileExtensions = [".fake"],
		Candidates = [new("dotnet", [])],
	};

	private static LanguageServerDescriptor? Resolve(string selector) => selector == "fake" ? FakeRecipe : null;

	private static LspController NewController(FakeHostBridge bridge, ILspServerLauncher launcher) =>
		new(bridge, Path.GetTempPath(), launcher, Resolve, _ => { });

	[Fact]
	public void Start_then_data_round_trips_through_the_server() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);

		controller.Start("s1", "fake", "ch1");
		Assert.Single(launcher.Servers);

		controller.Data("ch1", Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"x"}"""));
		Assert.Equal("""{"jsonrpc":"2.0","id":1,"method":"x"}""", launcher.Servers[0].LastWrittenText());

		launcher.Servers[0].RaiseFrame(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":42}"""));
		var data = bridge.LastOfType("lsp-data");
		Assert.True(data.HasValue);
		Assert.Equal("ch1", data!.Value.GetProperty("channel").GetString());
		Assert.Equal(42, data.Value.GetProperty("payload").GetProperty("result").GetInt32());
	}

	[Fact]
	public void Server_exit_posts_lsp_exit_and_reaps() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);
		controller.Start("s1", "fake", "ch1");

		launcher.Servers[0].RaiseExited(3);

		var exit = bridge.LastOfType("lsp-exit");
		Assert.True(exit.HasValue);
		Assert.Equal("ch1", exit!.Value.GetProperty("channel").GetString());
		Assert.Equal(3, exit.Value.GetProperty("code").GetInt32());
		Assert.True(launcher.Servers[0].Disposed);
	}

	[Fact]
	public void Duplicate_channel_posts_lsp_exit_and_leaves_the_live_server_alone() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);
		controller.Start("s1", "fake", "ch1");

		controller.Start("s1", "fake", "ch1");

		Assert.Single(launcher.Servers);
		Assert.False(launcher.Servers[0].Disposed);
		var exit = bridge.LastOfType("lsp-exit");
		Assert.True(exit.HasValue);
		Assert.Contains("already bound", exit!.Value.GetProperty("reason").GetString());
	}

	[Fact]
	public async Task DropOtherEpochs_reaps_only_channels_from_other_page_instances() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);
		controller.Start("s1", "fake", "lsp1-oldpage");
		controller.Start("s1", "fake", "lsp2-newpage");

		controller.DropOtherEpochs("newpage");

		// The reaped channel routes nothing; the current page's channel still round-trips.
		controller.Data("lsp1-oldpage", Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":9,"method":"x"}"""));
		controller.Data("lsp2-newpage", Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":9,"method":"x"}"""));
		Assert.Empty(launcher.Servers[0].WrittenTexts());
		Assert.Equal("""{"jsonrpc":"2.0","id":9,"method":"x"}""", launcher.Servers[1].LastWrittenText());

		// The reap posts lsp-exit (after disposing) so a still-live sibling page's client learns and reconnects.
		for (int i = 0; i < 200 && !bridge.LastOfType("lsp-exit").HasValue; i++) {
			await Task.Delay(10);
		}

		var exit = bridge.LastOfType("lsp-exit");
		Assert.True(exit.HasValue);
		Assert.Equal("lsp1-oldpage", exit!.Value.GetProperty("channel").GetString());
		Assert.True(launcher.Servers[0].Disposed);
		Assert.False(launcher.Servers[1].Disposed);
	}

	[Fact]
	public void Unknown_recipe_posts_lsp_exit_without_spawning() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);
		var unresolved = new List<LanguageServerDescriptor>();
		controller.Unresolved += unresolved.Add;

		controller.Start("s1", "nope", "ch1");

		Assert.Empty(launcher.Servers);
		var exit = bridge.LastOfType("lsp-exit");
		Assert.True(exit.HasValue);
		Assert.Contains("recipe", exit!.Value.GetProperty("reason").GetString());
		// An unknown selector is a protocol bug, not an installable miss — no install offer.
		Assert.Empty(unresolved);
	}

	[Fact]
	public void Server_not_on_path_posts_lsp_exit_and_raises_Unresolved() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = new LspController(
			bridge, Path.GetTempPath(), launcher,
			_ => new LanguageServerDescriptor {
				Id = "ghost",
				DisplayName = "Ghost",
				LanguageIds = ["ghost"],
				FileExtensions = [".ghost"],
				Candidates = [new("weavie-no-such-language-server-xyz", [])],
			},
			_ => { });
		var unresolved = new List<LanguageServerDescriptor>();
		controller.Unresolved += unresolved.Add;

		controller.Start("s1", "ghost", "ch1");

		Assert.Empty(launcher.Servers);
		var exit = bridge.LastOfType("lsp-exit");
		Assert.True(exit.HasValue);
		Assert.Contains("PATH", exit!.Value.GetProperty("reason").GetString());
		Assert.Equal("ghost", Assert.Single(unresolved).Id);
	}

	[Fact]
	public void NotifyWatchedFileChanges_fans_a_didChange_to_every_server() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);
		controller.Start("s1", "fake", "ch1");

		controller.NotifyWatchedFileChanges([new WatchedFileChange("file:///x.fake", FileChangeKind.Changed)]);

		Assert.Contains("workspace/didChangeWatchedFiles", launcher.Servers[0].LastWrittenText());
	}

	[Fact]
	public async Task DisposeAsync_reaps_every_channel() {
		var bridge = new FakeHostBridge();
		var launcher = new FakeLauncher();
		var controller = NewController(bridge, launcher);
		controller.Start("s1", "fake", "ch1");
		controller.Start("s1", "fake", "ch2");

		await controller.DisposeAsync();

		Assert.Equal(2, launcher.Servers.Count);
		Assert.All(launcher.Servers, s => Assert.True(s.Disposed));
	}

	private sealed class FakeLauncher : ILspServerLauncher {
		public List<FakeServer> Servers { get; } = [];

		public ILspServerProcess Start(ResolvedCommand command, string workspaceRoot, Action<string> log) {
			var server = new FakeServer();
			Servers.Add(server);
			return server;
		}
	}

	private sealed class FakeServer : ILspServerProcess {
		private readonly List<byte[]> _written = [];

		public event Action<byte[]>? FrameReceived;
		public event Action<int>? Exited;

		public bool Disposed { get; private set; }

		public void Start() { }

		public void Write(ReadOnlyMemory<byte> payload) => _written.Add(payload.ToArray());

		public string LastWrittenText() => Encoding.UTF8.GetString(_written[^1]);

		public IReadOnlyList<string> WrittenTexts() => _written.Select(Encoding.UTF8.GetString).ToArray();

		public void RaiseFrame(byte[] frame) => FrameReceived?.Invoke(frame);

		public void RaiseExited(int code) => Exited?.Invoke(code);

		public void Dispose() => Disposed = true;
	}
}
