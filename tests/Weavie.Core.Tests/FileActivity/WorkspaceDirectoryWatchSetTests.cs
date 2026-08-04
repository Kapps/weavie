using Weavie.Core.FileActivity;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceDirectoryWatchSetTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"weavie-flat-watch-{Guid.NewGuid():N}");

	public WorkspaceDirectoryWatchSetTests() {
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void VanishedDirectoryDoesNotFailWatchRegistration() {
		using var watches = Create(path => new FileSystemWatcher(path));

		watches.Reconcile([_root, Path.Combine(_root, "gone")]);
		watches.EnsureWatching(Path.Combine(_root, "also-gone"));

		Assert.Equal(1, watches.Count);
	}

	[Fact]
	public void AccessFailureIsNotClassifiedAsVanishedDirectory() {
		using var watches = Create(_ => throw new UnauthorizedAccessException("denied"));

		Assert.Throws<UnauthorizedAccessException>(() => watches.Reconcile([_root]));
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private static FileSystemWorkspaceDirectoryWatchSet Create(Func<string, FileSystemWatcher> factory) =>
		new(factory, _ => { }, _ => { }, _ => { }, (_, _) => { }, _ => { });
}
