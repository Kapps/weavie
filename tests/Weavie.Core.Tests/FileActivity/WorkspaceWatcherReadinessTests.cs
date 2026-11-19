using System.Collections.Immutable;
using Weavie.Core.FileActivity;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// A reader that follows the inventory's changes gets the first listing as soon as its watches are armed; one
/// that reads once waits for the pass that verifies it.
/// </summary>
public sealed class WorkspaceWatcherReadinessTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-watch-ready");

	[Fact]
	public async Task TheFirstListingIsServedBeforeItsVerificationPassFinishes() {
		var verification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int loads = 0;
		var inventory = new WorkspaceInventory(_dir.Path, async _ => {
			if (Interlocked.Increment(ref loads) == 2) {
				await verification.Task;
			}

			return (IReadOnlyList<string>)["a.ts"];
		});
		using var watcher = new WorkspaceInvalidationWatcher(
			inventory, _ => { }, _ => { }, debounceMs: 1, Task.Delay, path => new FileSystemWatcher(path));
		var run = watcher.RunAsync(CancellationToken.None);

		var listed = await watcher.ListedSnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal([_dir.Combine("a.ts")], listed.Files);
		Assert.False(watcher.Ready.IsCompleted);
		verification.SetResult();
		await watcher.Ready.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Same(listed, await watcher.SnapshotAsync(CancellationToken.None));
		await watcher.StopAsync();
		await run;
	}

	[Fact]
	public void AnEntryIsFoundAsTheInventorySpellsIt() {
		var paths = ImmutableSortedSet.Create(StringComparer.OrdinalIgnoreCase, "/r/foo.cs", "/r/src/a.cs");

		Assert.Equal(["/r/foo.cs"], WorkspaceInventory.AtOrBeneath(paths, "/r/Foo.cs"));
		Assert.Empty(WorkspaceInventory.AtOrBeneath(paths, "/r/bar.cs"));
	}

	public void Dispose() => _dir.Dispose();
}
