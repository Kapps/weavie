using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Git, not a hardcoded list, decides which edited paths are ignored.</summary>
public sealed class GitIgnoredPathsIntegrationTests : IDisposable {
	private readonly TempGitRepo _repo = new("weavie-git-ignored");

	public GitIgnoredPathsIntegrationTests() {
		_repo.Write(".gitignore", ".claude/worktrees/\n*.log\n");
		_repo.Write("tracked.log", "forced\n");
		_repo.Git("add", "-f", "tracked.log");
		_repo.Commit("initial");
	}

	[Fact]
	public async Task FindIgnored_ReportsIgnoredPathsInsideNestedWorktreesButNeverTrackedFiles() {
		_repo.Git("worktree", "add", "--quiet", Path.Combine(".claude", "worktrees", "agent"));
		string nested = _repo.Write(Path.Combine(".claude", "worktrees", "agent", "a.txt"), "agent\n");
		string tracked = Path.Combine(_repo.Path, "tracked.log");
		string fresh = _repo.Write(Path.Combine("src", "new.log"), "log\n");
		string deleted = Path.Combine(_repo.Path, "gone.log");
		string source = _repo.Write(Path.Combine("src", "a.txt"), "source\n");

		var ignored = await new GitService().FindIgnoredAsync(_repo.Path, [nested, tracked, fresh, deleted, source]);

		Assert.Equal(new[] { nested, fresh, deleted }.Order(StringComparer.Ordinal), ignored.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task FindIgnored_PathsInsideASubmodule_FollowTheTrackedSubmodule() {
		using var module = new TempGitRepo("weavie-git-module");
		module.Write("a.txt", "module\n");
		module.Commit("module");
		_repo.Git("-c", "protocol.file.allow=always", "submodule", "add", "--quiet", module.Path, "module");
		_repo.Commit("add module");
		string inModule = Path.Combine(_repo.Path, "module", "x.log");
		string beside = Path.Combine(_repo.Path, "x.log");

		Assert.Equal([beside], await new GitService().FindIgnoredAsync(_repo.Path, [inModule, beside]));
	}

	[Fact]
	public async Task FindIgnored_PathsBeneathASymlink_FollowTheSymlinkEntry() {
		if (OperatingSystem.IsWindows()) return;
		_repo.Write(Path.Combine("real", "a.log"), "log\n");
		Directory.CreateSymbolicLink(Path.Combine(_repo.Path, "link"), Path.Combine(_repo.Path, "real"));
		Directory.CreateSymbolicLink(Path.Combine(_repo.Path, "linked.log"), Path.Combine(_repo.Path, "real"));
		string viaLink = Path.Combine(_repo.Path, "link", "a.log");
		string viaIgnoredLink = Path.Combine(_repo.Path, "linked.log", "a.log");

		Assert.Equal([viaIgnoredLink], await new GitService().FindIgnoredAsync(_repo.Path, [viaLink, viaIgnoredLink]));
	}

	[Fact]
	public async Task FindIgnored_OutsideARepository_IgnoresNothing() {
		using var directory = new TempDirectory("weavie-not-a-repo");
		string file = directory.WriteFile("a.log", "log\n");

		Assert.Empty(await new GitService().FindIgnoredAsync(directory.Path, [file]));
	}

	public void Dispose() => _repo.Dispose();
}
