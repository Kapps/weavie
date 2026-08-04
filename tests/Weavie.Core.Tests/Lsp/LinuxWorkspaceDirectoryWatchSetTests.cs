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
	public async Task MoveOutOfWatchedTreeBecomesDelete() {
		if (!OperatingSystem.IsLinux()) {
			return;
		}

		string source = Path.Combine(_root, "source");
		string destination = Path.Combine(Path.GetTempPath(), $"weavie-inotify-moved-{Guid.NewGuid():N}");
		Directory.CreateDirectory(source);
		var deleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		try {
			using var watches = new LinuxWorkspaceDirectoryWatchSet(
				_ => { },
				_ => { },
				e => deleted.TrySetResult(e.FullPath),
				(_, _) => { },
				_ => { });
			watches.Reconcile([_root]);

			Directory.Move(source, destination);

			Assert.Equal(source, await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
		} finally {
			if (Directory.Exists(destination)) {
				Directory.Delete(destination, recursive: true);
			}
		}
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

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
