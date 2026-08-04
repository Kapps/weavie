using System.Collections.Concurrent;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class LinuxWorkspaceDirectoryWatchSetTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"weavie-inotify-{Guid.NewGuid():N}");

	public LinuxWorkspaceDirectoryWatchSetTests() {
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public async Task OneNativeInstanceWatchesManyFlatDirectories() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		var directories = new List<string> { _root };
		for (int i = 0; i < 300; i++) {
			string directory = Path.Combine(_root, i.ToString());
			Directory.CreateDirectory(directory);
			directories.Add(directory);
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

		string oldPath = Path.Combine(_root, "before");
		string newPath = Path.Combine(_root, "after");
		string oldNested = Path.Combine(oldPath, "nested");
		string newNested = Path.Combine(newPath, "nested");
		Directory.CreateDirectory(oldNested);
		var renamed = new TaskCompletionSource<(string OldPath, string NewPath)>(TaskCreationOptions.RunContinuationsAsynchronously);
		var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var watches = new LinuxWorkspaceDirectoryWatchSet(
			e => created.TrySetResult(e.FullPath),
			_ => { },
			_ => { },
			(oldName, newName) => renamed.TrySetResult((oldName, newName)),
			_ => { });
		watches.Reconcile([_root, oldPath, oldNested]);

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

		watches.Reconcile([_root, Path.Combine(_root, "gone")]);
		watches.EnsureWatching(Path.Combine(_root, "also-gone"));

		Assert.Equal(1, watches.Count);
	}

	[Fact]
	public async Task MovesOutsideWatchedTreeBecomeDeletesDuringContinuousEvents() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		string firstPath = Path.Combine(_root, "first");
		string secondPath = Path.Combine(_root, "second");
		string outside = Path.Combine(Path.GetTempPath(), $"weavie-inotify-outside-{Guid.NewGuid():N}");
		string firstOutside = Path.Combine(outside, "first");
		string secondOutside = Path.Combine(outside, "second");
		string trafficDirectory = Path.Combine(_root, "traffic");
		Directory.CreateDirectory(firstPath);
		Directory.CreateDirectory(secondPath);
		Directory.CreateDirectory(outside);
		Directory.CreateDirectory(trafficDirectory);
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
			watches.Reconcile([_root]);

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

			if (Directory.Exists(outside)) {
				Directory.Delete(outside, recursive: true);
			}
		}
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
