using Weavie.Core.FileActivity;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WorkspaceDirectoryWatchSetTests : IDisposable {
	private readonly TempDirectory _root = new("weavie-flat-watch");

	[Fact]
	public void VanishedDirectoryDoesNotFailWatchRegistration() {
		using var watches = Create(path => new FileSystemWatcher(path));

		watches.Reconcile([_root.Path, _root.Combine("gone")]);
		watches.EnsureWatching(_root.Combine("also-gone"));

		Assert.Equal(1, watches.Count);
	}

	[Fact]
	public void AccessFailureIsNotClassifiedAsVanishedDirectory() {
		using var watches = Create(_ => throw new UnauthorizedAccessException("denied"));

		Assert.Throws<UnauthorizedAccessException>(() => watches.Reconcile([_root.Path]));
	}

	public void Dispose() => _root.Dispose();

	private static FileSystemWorkspaceDirectoryWatchSet Create(Func<string, FileSystemWatcher> factory) =>
		new(factory, _ => { }, _ => { }, _ => { }, (_, _) => { }, _ => { });
}
