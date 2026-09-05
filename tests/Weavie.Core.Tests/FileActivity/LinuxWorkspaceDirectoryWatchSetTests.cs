using System.Collections.Concurrent;
using Weavie.Core.FileActivity;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class LinuxWorkspaceDirectoryWatchSetTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-inotify");

	[Fact]
	public async Task OneNativeInstanceWatchesManyFlatDirectories() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		var directories = new List<string> { _root.Path };
		for (int i = 0; i < 300; i++) {
			directories.Add(_root.CreateDirectory(i.ToString()));
		}

		var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		var errors = new ConcurrentBag<Exception>();
		using var watches = new LinuxWorkspaceDirectoryWatchSet(
			e => created.TrySetResult(e.FullPath),
			_ => { },
			_ => { },
			(_, _) => { },
			errors.Add);

		watches.Reconcile(directories);
		Assert.Equal(directories.Count, watches.Count);
		string expected = Path.Combine(directories[^1], "created.ts");
		await File.WriteAllTextAsync(expected, "export {};\n");

		Assert.Equal(expected, await created.Task.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Empty(errors);
	}

	[Fact]
	public async Task PairsDirectoryMoveCookieAsRename() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		string oldPath = _root.Combine("before");
		string newPath = _root.Combine("after");
		string oldNested = _root.CreateDirectory("before", "nested");
		string newNested = Path.Combine(newPath, "nested");
		var renamed = new TaskCompletionSource<(string OldPath, string NewPath)>(TaskCreationOptions.RunContinuationsAsynchronously);
		var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var watches = new LinuxWorkspaceDirectoryWatchSet(
			e => created.TrySetResult(e.FullPath),
			_ => { },
			_ => { },
			(oldName, newName) => renamed.TrySetResult((oldName, newName)),
			_ => { });
		watches.Reconcile([_root.Path, oldPath, oldNested]);

		Directory.Move(oldPath, newPath);

		Assert.Equal((oldPath, newPath), await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
		string file = Path.Combine(newNested, "after.ts");
		await File.WriteAllTextAsync(file, "export {};\n");
		Assert.Equal(file, await created.Task.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public void VanishedDirectoryDoesNotFailWatchRegistration() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		using var watches = new LinuxWorkspaceDirectoryWatchSet(
			_ => { },
			_ => { },
			_ => { },
			(_, _) => { },
			_ => { });

		watches.Reconcile([_root.Path, _root.Combine("gone")]);
		watches.EnsureWatching(_root.Combine("also-gone"));

		Assert.Equal(1, watches.Count);
	}

	[Fact]
	public async Task MovesOutsideWatchedTreeBecomeDeletesDuringContinuousEvents() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		string firstPath = _root.CreateDirectory("first");
		string secondPath = _root.CreateDirectory("second");
		using var outside = new TempDirectory("weavie-inotify-outside");
		string firstOutside = outside.Combine("first");
		string secondOutside = outside.Combine("second");
		string trafficDirectory = _root.CreateDirectory("traffic");
		var deletedPaths = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
		var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var stopTraffic = new CancellationTokenSource();
		Task? traffic = null;
		try {
			using var watches = new LinuxWorkspaceDirectoryWatchSet(
				_ => { },
				_ => { },
				e => {
					deletedPaths.TryAdd(e.FullPath, 0);
					if (deletedPaths.ContainsKey(firstPath) && deletedPaths.ContainsKey(secondPath)) {
						deleted.TrySetResult();
					}
				},
				(_, _) => { },
				_ => { });
			watches.Reconcile([_root.Path]);

			Directory.Move(firstPath, firstOutside);
			Directory.Move(secondPath, secondOutside);
			traffic = Task.Run(() => {
				int index = 0;
				while (!stopTraffic.IsCancellationRequested) {
					File.WriteAllText(Path.Combine(trafficDirectory, $"{index++}.tmp"), string.Empty);
				}
			});

			await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Contains(firstPath, deletedPaths.Keys);
			Assert.Contains(secondPath, deletedPaths.Keys);
		} finally {
			stopTraffic.Cancel();
			if (traffic is not null) {
				await traffic;
			}
		}
	}

	public void Dispose() => _root.Dispose();
}
