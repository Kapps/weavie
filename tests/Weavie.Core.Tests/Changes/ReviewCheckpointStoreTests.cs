using Weavie.Core.Changes;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ReviewCheckpointStoreTests {
	[Fact]
	public void Save_AtomicallyReplacesAnOwnerOnlyDocument() {
		string directory = Path.Combine(Path.GetTempPath(), "weavie-review-store-" + Guid.NewGuid().ToString("n"));
		string path = Path.Combine(directory, "review.json");
		try {
			var store = new FileReviewCheckpointStore(path);

			store.Save("first");
			store.Save("second");

			Assert.Equal("second", store.Load());
			Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
			if (!OperatingSystem.IsWindows()) {
				Assert.Equal(
					UnixFileMode.UserRead | UnixFileMode.UserWrite,
					File.GetUnixFileMode(path));
			}

			store.Clear();
			Assert.Null(store.Load());
		} finally {
			if (Directory.Exists(directory)) {
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	[Fact]
	public void FailedSave_RemovesItsUniqueTemporaryFile() {
		string directory = Path.Combine(Path.GetTempPath(), "weavie-review-store-" + Guid.NewGuid().ToString("n"));
		string path = Path.Combine(directory, "occupied");
		try {
			Directory.CreateDirectory(path);
			var store = new FileReviewCheckpointStore(path);

			Assert.Throws<IOException>(() => store.Save("checkpoint"));

			Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
		} finally {
			if (Directory.Exists(directory)) {
				Directory.Delete(directory, recursive: true);
			}
		}
	}
}
