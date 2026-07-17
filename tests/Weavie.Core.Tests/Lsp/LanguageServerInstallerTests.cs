using Weavie.Core.Lsp;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The installer at its fake seams (PATH lookup, resolution, process run): install targeting into Weavie's
/// tools folder, loud failures carrying the tool's own words, and the post-install resolution check. No test
/// touches the network or a real package manager.
/// </summary>
public sealed class LanguageServerInstallerTests {
	private static readonly ResolvedCommand Resolved = new("/fake/server", [], "/fake/server");

	private static ServerInstallOffer OfferFor(LanguageServerDescriptor descriptor) {
		var candidate = descriptor.Candidates.Single(c => c.Install is not null);
		return new ServerInstallOffer(descriptor, candidate, candidate.Install!);
	}

	[Fact]
	public async Task ToolchainMissing_FailsWithoutRunning() {
		var runner = new FakeRunner();
		var installer = new LanguageServerInstaller(_ => null, _ => null, runner.RunAsync, _ => { });

		var result = await installer.InstallAsync(OfferFor(LanguageServerCatalog.Go), "/repo", CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("not on PATH", result.Message);
		Assert.Empty(runner.Requests);
	}

	[Fact]
	public async Task InstallFails_MessageCarriesTheToolsWords_AndOutputIsLogged() {
		var log = new List<string>();
		var runner = new FakeRunner { Reply = _ => new ToolProcessResult(1, "", "boom\nreal error") };
		var installer = new LanguageServerInstaller(cmd => $"/fake/bin/{cmd}", _ => null, runner.RunAsync, log.Add);

		var result = await installer.InstallAsync(OfferFor(LanguageServerCatalog.Go), "/repo", CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("exit 1", result.Message);
		Assert.Contains("real error", result.Message);
		Assert.Contains(log, line => line.Contains("real error"));
	}

	[Fact]
	public async Task GoInstall_TargetsWeavieBinDir_ViaGobin() {
		var runner = new FakeRunner();
		var installer = new LanguageServerInstaller(cmd => $"/fake/bin/{cmd}", _ => Resolved, runner.RunAsync, _ => { });

		var result = await installer.InstallAsync(OfferFor(LanguageServerCatalog.Go), "/repo", CancellationToken.None);

		Assert.True(result.Ok);
		Assert.Contains("ready", result.Message);
		var request = Assert.Single(runner.Requests);
		Assert.Equal("/fake/bin/go", request.FileName);
		Assert.Equal(["install", "golang.org/x/tools/gopls@latest"], request.Arguments);
		Assert.Equal(ToolchainInstall.InstallDir("go"), request.Environment["GOBIN"]);
		Assert.Equal("/repo", request.WorkingDirectory);
	}

	[Fact]
	public async Task NpmInstall_TargetsWeaviePrefix() {
		var runner = new FakeRunner();
		var installer = new LanguageServerInstaller(cmd => $"/fake/bin/{cmd}", _ => Resolved, runner.RunAsync, _ => { });

		await installer.InstallAsync(OfferFor(LanguageServerCatalog.TypeScript), "/repo", CancellationToken.None);

		var request = Assert.Single(runner.Requests);
		Assert.Equal("/fake/bin/npm", request.FileName);
		Assert.Equal(
			["install", "--global", "--prefix", ToolchainInstall.InstallDir("npm"), "@typescript/native-preview"],
			request.Arguments);
		Assert.Empty(request.Environment);
	}

	[Fact]
	public async Task InstallSucceeds_ButServerStillUnresolved_FailsLoudly() {
		var runner = new FakeRunner();
		var installer = new LanguageServerInstaller(cmd => $"/fake/bin/{cmd}", _ => null, runner.RunAsync, _ => { });

		var result = await installer.InstallAsync(OfferFor(LanguageServerCatalog.Go), "/repo", CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains(ToolchainInstall.BinDir("go"), result.Message);
	}

	private sealed class FakeRunner {
		public List<ToolProcessRequest> Requests { get; } = [];

		public Func<ToolProcessRequest, ToolProcessResult> Reply { get; init; } =
			_ => new ToolProcessResult(0, "installed", "");

		public Task<ToolProcessResult> RunAsync(ToolProcessRequest request, CancellationToken _) {
			Requests.Add(request);
			return Task.FromResult(Reply(request));
		}
	}
}
