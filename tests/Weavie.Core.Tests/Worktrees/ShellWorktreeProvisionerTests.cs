using Weavie.Core.Worktrees;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="ShellWorktreeProvisioner"/> against the real shell: no-op when unconfigured, captured-output
/// success, surfaced non-zero failure, and the lifecycle events.
/// </summary>
public sealed class ShellWorktreeProvisionerTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-prov");

	public void Dispose() => _dir.Dispose();

	[Fact]
	public async Task EmptyCommand_IsNoOp() {
		var provisioner = new ShellWorktreeProvisioner(() => "", () => "   ");

		var setup = await provisioner.RunSetupAsync(_dir.Path, CancellationToken.None);
		var teardown = await provisioner.RunTeardownAsync(_dir.Path, CancellationToken.None);

		Assert.False(setup.Ran);
		Assert.True(setup.Succeeded);
		Assert.False(teardown.Ran);
	}

	[Fact]
	public async Task RunSetup_RunsCommand_CapturesOutput_AndRaisesEvents() {
		var provisioner = new ShellWorktreeProvisioner(() => "echo weavie-ok", () => "");
		WorktreeCommandPhase? started = null;
		WorktreeCommandResult? finished = null;
		provisioner.Starting += e => started = e.Phase;
		provisioner.Finished += e => finished = e.Result;

		var result = await provisioner.RunSetupAsync(_dir.Path, CancellationToken.None);

		Assert.True(result.Ran);
		Assert.Equal(0, result.ExitCode);
		Assert.True(result.Succeeded);
		Assert.Contains("weavie-ok", result.StdOut);
		Assert.Equal(WorktreeCommandPhase.Setup, started);
		Assert.Same(result, finished);
	}

	[Fact]
	public async Task NonZeroExit_IsSurfacedAsFailure() {
		var provisioner = new ShellWorktreeProvisioner(() => "exit 3", () => "");

		var result = await provisioner.RunSetupAsync(_dir.Path, CancellationToken.None);

		Assert.True(result.Ran);
		Assert.Equal(3, result.ExitCode);
		Assert.False(result.Succeeded);
	}
}
