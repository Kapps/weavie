using Weavie.Hosting.Desktop;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The ladder every OS-delivered path obeys. Pure path math plus a directory probe, so each rung is pinned
/// without a window, a git process, or a session.
/// </summary>
public sealed class OpenTargetResolverTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-open");

	public OpenTargetResolverTests() {
		_root.CreateDirectory("repo", "src");
		_root.CreateDirectory("loose");
	}

	[Fact]
	public void FileInsideAnOpenWorkspace_OpensThere() {
		string repo = _root.Combine("repo");
		string file = Path.Combine(repo, "src", "a.ts");

		var target = OpenTargetResolver.Resolve(file, [repo], toplevel: repo);

		Assert.Equal(new OpenTarget(repo, file), target);
	}

	[Fact]
	public void FileInAClosedRepository_OpensThatRepository() {
		string repo = _root.Combine("repo");
		string other = _root.Combine("loose");
		string file = Path.Combine(repo, "src", "a.ts");

		var target = OpenTargetResolver.Resolve(file, [other], toplevel: repo);

		Assert.Equal(new OpenTarget(repo, file), target);
	}

	[Fact]
	public void FileInNoRepository_OpensInAnOpenWorkspaceAsAnOutsideFile() {
		// The case the outside-the-checkout work made possible: no repo above /etc/hosts, but a window is open.
		string repo = _root.Combine("repo");
		string file = _root.Combine("loose", "notes.md");

		var target = OpenTargetResolver.Resolve(file, [repo], toplevel: null);

		Assert.Equal(new OpenTarget(repo, file), target);
	}

	[Fact]
	public void FileInNoRepositoryWithNothingOpen_TakesItsOwnDirectory() {
		string file = _root.Combine("loose", "notes.md");

		var target = OpenTargetResolver.Resolve(file, [], toplevel: null);

		Assert.Equal(new OpenTarget(_root.Combine("loose"), file), target);
	}

	[Fact]
	public void ADirectory_OpensAsItsOwnWorkspaceAndRevealsNothing() {
		string repo = _root.Combine("repo");

		var target = OpenTargetResolver.Resolve(repo, [], toplevel: repo);

		Assert.Equal(new OpenTarget(repo, null), target);
	}

	[Fact]
	public void AnUnnormalizedPath_ResolvesBeforeItIsCompared() {
		string repo = _root.Combine("repo");
		string file = Path.Combine(repo, "src", "..", "src", "a.ts");

		var target = OpenTargetResolver.Resolve(file, [repo], toplevel: null);

		Assert.Equal(Path.Combine(repo, "src", "a.ts"), target.File);
	}

	[Fact]
	public void EveryHandedOverPathMustBelongToTheOpenWorkspace() {
		// One window shows one workspace, so a path from elsewhere is declined and its own window boots it —
		// rather than being opened inside a workspace the first path chose.
		string repo = _root.Combine("repo");
		string elsewhere = _root.Combine("loose");

		var reply = DesktopHandoff.Offer(
			[Path.Combine(repo, "src", "a.ts"), Path.Combine(elsewhere, "b.ts")],
			repo,
			path => path.Contains("repo", StringComparison.Ordinal) ? repo : elsewhere,
			_ => Assert.Fail("A declined handover must open nothing."));

		Assert.False(reply.Accepted);
		Assert.Equal(elsewhere, reply.Root);
	}

	[Fact]
	public void PathsThatAllBelongAreOpened() {
		string repo = _root.Combine("repo");
		List<string> opened = [];

		var reply = DesktopHandoff.Offer(
			[Path.Combine(repo, "src", "a.ts"), Path.Combine(repo, "src", "b.ts")],
			repo,
			_ => repo,
			opened.Add);

		Assert.True(reply.Accepted);
		Assert.Equal([Path.Combine(repo, "src", "a.ts"), Path.Combine(repo, "src", "b.ts")], opened);
	}

	public void Dispose() => _root.Dispose();
}
