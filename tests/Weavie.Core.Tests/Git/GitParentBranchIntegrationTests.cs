using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Proves a whole-branch review compares a stacked branch with the branch it was created from.</summary>
public sealed class GitParentBranchIntegrationTests : IDisposable {
	private readonly TempGitRepo _repo = new("weavie-git-parent");
	private readonly GitService _git = new();

	public GitParentBranchIntegrationTests() {
		_repo.Write("base.txt", "base\n");
		_repo.Commit("initial");
	}

	[Fact]
	public async Task BranchOffTheDefaultBranch_ComparesWithIt() {
		Branch("feature-a", "main");

		Assert.Equal("main", await ParentAsync());
	}

	[Fact]
	public async Task StackedBranch_ComparesWithItsParentEvenAfterBothMoveOnAndMainIsMergedIn() {
		Branch("feature-a", "main");
		Branch("feature-b", "feature-a");
		Commit("feature-a", "a2");
		Commit("main", "m2");
		_repo.Git("switch", "--quiet", "feature-b");
		_repo.Git("merge", "--quiet", "--no-edit", "main");
		Branch("feature-c", "feature-b");
		_repo.Git("update-ref", "refs/remotes/origin/feature-c", "feature-c");
		Commit("feature-b", "b2");

		Assert.Equal("feature-a", await ParentAsync());
	}

	[Fact]
	public async Task StackedBranch_ComparesWithItsParentAfterBothMergeMainIn() {
		Branch("feature-a", "main");
		Branch("feature-b", "feature-a");
		Commit("main", "m2");
		foreach (string branch in new[] { "feature-a", "feature-b" }) {
			_repo.Git("switch", "--quiet", branch);
			_repo.Git("merge", "--quiet", "--no-edit", "main");
		}

		Assert.Equal("feature-a", await ParentAsync());
	}

	[Fact]
	public async Task RecreatedBranch_IgnoresWhereItsDeletedNamesakeCameFrom() {
		Branch("feature-a", "main");
		_repo.Git("switch", "--quiet", "-c", "feature-b");
		_repo.Git("switch", "--quiet", "main");
		_repo.Git("branch", "--quiet", "-D", "feature-b");
		// Reflog times are whole seconds; the namesake must be strictly older than the new branch.
		Thread.Sleep(1100);
		_repo.Git("switch", "--quiet", "-c", "feature-b");
		Commit("feature-b", "b1");

		Assert.Equal("main", await ParentAsync());
	}

	[Fact]
	public async Task StackedBranchCreatedFromHead_ComparesWithTheBranchItWasCreatedOn() {
		Branch("feature-a", "main");
		_repo.Git("switch", "--quiet", "-c", "feature-b");
		Commit("feature-b", "b1");
		_repo.Git("switch", "--quiet", "main");
		_repo.Git("switch", "--quiet", "feature-b"); // a later arrival from elsewhere doesn't make main the parent

		Assert.Equal("feature-a", await ParentAsync());
	}

	[Fact]
	public async Task BranchCreatedFromARemoteParent_ComparesWithIt() {
		Branch("feature-a", "main");
		_repo.Git("update-ref", "refs/remotes/origin/feature-a", "feature-a");
		Branch("feature-b", "origin/feature-a");

		Assert.Equal("origin/feature-a", await ParentAsync());
	}

	[Fact]
	public async Task BranchCreatedFromItsOwnRemoteCopy_ComparesWithTheDefaultBranch() {
		Branch("feature-b", "main");
		_repo.Git("update-ref", "refs/remotes/origin/feature-b", "feature-b");
		_repo.Git("switch", "--quiet", "main");
		_repo.Git("branch", "--quiet", "-D", "feature-b");
		_repo.Git("switch", "--quiet", "-c", "feature-b", "origin/feature-b");

		Assert.Equal("main", await ParentAsync());
	}

	[Fact]
	public async Task BranchRebasedOffItsParent_ComparesWithTheDefaultBranch() {
		Branch("feature-a", "main");
		Branch("feature-b", "feature-a");
		Commit("main", "m2");
		_repo.Git("switch", "--quiet", "feature-b");
		_repo.Git("rebase", "--quiet", "--onto", "main", "feature-a");

		Assert.Equal("main", await ParentAsync());
	}

	[Fact]
	public async Task ParentThatAlreadyContainsTheBranch_StaysItsBase() {
		Branch("feature-a", "main");
		Branch("feature-b", "feature-a");
		_repo.Git("switch", "--quiet", "feature-a");
		_repo.Git("merge", "--quiet", "--ff-only", "feature-b");
		_repo.Git("switch", "--quiet", "feature-b");

		Assert.Equal("feature-a", await ParentAsync());
	}

	private Task<string> ParentAsync() => _git.ResolveParentBranchAsync(_repo.Path, "main");

	private void Branch(string name, string from) {
		_repo.Git("switch", "--quiet", "-c", name, from);
		Commit(name, name);
	}

	private void Commit(string branch, string file) {
		_repo.Git("switch", "--quiet", branch);
		_repo.Write(file + ".txt", file + "\n");
		_repo.Commit(file);
	}

	public void Dispose() => _repo.Dispose();
}
