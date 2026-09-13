using Weavie.Core.FileActivity;
using Weavie.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Weavie.Core.Tests;

/// <summary>Reports removal of the recursive watch root through its parent directory.</summary>
public sealed class RecursiveWorkspaceDirectoryWatchSetTests(ITestOutputHelper output) {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReportsRootRemoval(bool rename) {
		using var directory = new TempDirectory("weavie-recursive-watch");
		string root = directory.CreateDirectory("worktree");
		var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var watchSet = new RecursiveWorkspaceDirectoryWatchSet(
			root,
			created: _ => { },
			changed: _ => { },
			deleted: e => {
				if (e.FullPath == root) removed.TrySetResult();
			},
			renamed: (_, _) => { },
			error: error => output.WriteLine(error.ToString()));
		watchSet.EnsureWatching(root);

		if (rename) Directory.Move(root, directory.Combine("moved-away"));
		else Directory.Delete(root, recursive: true);

		await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}
}
