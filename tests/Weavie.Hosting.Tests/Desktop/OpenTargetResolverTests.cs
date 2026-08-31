using Weavie.Hosting.Desktop;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The ladder every OS-delivered path obeys. Pure path math plus a directory probe, so each rung is pinned
/// without a window, a git process, or a session.
/// </summary>
public sealed class OpenTargetResolverTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"weavie-open-{Guid.NewGuid():N}");

	public OpenTargetResolverTests() {
		Directory.CreateDirectory(Path.Combine(_root, "repo", "src"));
		Directory.CreateDirectory(Path.Combine(_root, "loose"));
	}

	[Fact]
	public void FileInsideAnOpenWorkspace_OpensThere() {
		string repo = Path.Combine(_root, "repo");
		string file = Path.Combine(repo, "src", "a.ts");

		var target = OpenTargetResolver.Resolve(file, [repo], toplevel: repo);

		Assert.Equal(new OpenTarget(repo, file), target);
	}

	[Fact]
	public void FileInAClosedRepository_OpensThatRepository() {
		string repo = Path.Combine(_root, "repo");
		string other = Path.Combine(_root, "loose");
		string file = Path.Combine(repo, "src", "a.ts");

		var target = OpenTargetResolver.Resolve(file, [other], toplevel: repo);

		Assert.Equal(new OpenTarget(repo, file), target);
	}

	[Fact]
	public void FileInNoRepository_OpensInAnOpenWorkspaceAsAnOutsideFile() {
		// The case the outside-the-checkout work made possible: no repo above /etc/hosts, but a window is open.
		string repo = Path.Combine(_root, "repo");
		string file = Path.Combine(_root, "loose", "notes.md");

		var target = OpenTargetResolver.Resolve(file, [repo], toplevel: null);

		Assert.Equal(new OpenTarget(repo, file), target);
	}

	[Fact]
	public void FileInNoRepositoryWithNothingOpen_TakesItsOwnDirectory() {
		string file = Path.Combine(_root, "loose", "notes.md");

		var target = OpenTargetResolver.Resolve(file, [], toplevel: null);

		Assert.Equal(new OpenTarget(Path.Combine(_root, "loose"), file), target);
	}

	[Fact]
	public void ADirectory_OpensAsItsOwnWorkspaceAndRevealsNothing() {
		string repo = Path.Combine(_root, "repo");

		var target = OpenTargetResolver.Resolve(repo, [], toplevel: repo);

		Assert.Equal(new OpenTarget(repo, null), target);
	}

	[Fact]
	public void AnUnnormalizedPath_ResolvesBeforeItIsCompared() {
		string repo = Path.Combine(_root, "repo");
		string file = Path.Combine(repo, "src", "..", "src", "a.ts");

		var target = OpenTargetResolver.Resolve(file, [repo], toplevel: null);

		Assert.Equal(Path.Combine(repo, "src", "a.ts"), target.File);
	}

	[Fact]
	public void EveryHandedOverPathMustBelongToTheOpenWorkspace() {
		// One window shows one workspace, so a path from elsewhere is declined and its own window boots it —
		// rather than being opened inside a workspace the first path chose.
		string repo = Path.Combine(_root, "repo");
		string elsewhere = Path.Combine(_root, "loose");

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
		string repo = Path.Combine(_root, "repo");
		List<string> opened = [];

		var reply = DesktopHandoff.Offer(
			[Path.Combine(repo, "src", "a.ts"), Path.Combine(repo, "src", "b.ts")],
			repo,
			_ => repo,
			opened.Add);

		Assert.True(reply.Accepted);
		Assert.Equal([Path.Combine(repo, "src", "a.ts"), Path.Combine(repo, "src", "b.ts")], opened);
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
