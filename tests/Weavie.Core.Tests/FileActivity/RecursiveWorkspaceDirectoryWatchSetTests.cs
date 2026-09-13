using System.Collections.Concurrent;
using Weavie.Core.FileActivity;
using Weavie.TestSupport;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The non-Linux (macOS/Windows) directory watch strategy: a single recursive <see cref="FileSystemWatcher"/>
/// rooted at the workspace directory. That primitive does not reliably report the watched directory's own
/// deletion, so this set must also notice it externally, mirroring Linux's IN_DELETE_SELF handling.
/// </summary>
public sealed class RecursiveWorkspaceDirectoryWatchSetTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-recursive-watch");
	private readonly ConcurrentBag<FileSystemEventArgs> _deleted = [];

	private RecursiveWorkspaceDirectoryWatchSet NewWatchSet(string root) =>
		new(
			root,
			created: _ => { },
			changed: _ => { },
			deleted: e => _deleted.Add(e),
			renamed: (_, _) => { },
			error: _ => { });

	private async Task<bool> WaitForAsync(Func<bool> predicate) {
		for (int i = 0; i < 100; i++) {
			if (predicate()) {
				return true;
			}

			await Task.Delay(50);
		}

		return predicate();
	}

	[Fact]
	public async Task ReportsTheWatchedDirectoryItsOwnDeletion() {
		string root = _dir.CreateDirectory("worktree");
		using var watchSet = NewWatchSet(root);
		watchSet.EnsureWatching(root);

		Directory.Delete(root, recursive: true);

		Assert.True(
			await WaitForAsync(() => _deleted.Any(e => string.Equals(e.FullPath, root, StringComparison.Ordinal))),
			"expected the watched directory's own removal to be reported");
	}

	[Fact]
	public async Task ReportsTheWatchedDirectoryBeingRenamedAway() {
		string root = _dir.CreateDirectory("worktree");
		using var watchSet = NewWatchSet(root);
		watchSet.EnsureWatching(root);

		Directory.Move(root, _dir.Combine("moved-away"));

		Assert.True(
			await WaitForAsync(() => _deleted.Any(e => string.Equals(e.FullPath, root, StringComparison.Ordinal))),
			"expected the watched directory being renamed away to be reported as gone");
	}

	public void Dispose() => _dir.Dispose();
}
