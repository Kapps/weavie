using Weavie.Core.Changes;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Tests;

internal static class ReviewTestChanges {
	internal static void AddPendingFile(SessionChangeTracker tracker, InMemoryFileSystem files, string path) {
		tracker.CaptureBaseline(path);
		files.WriteAllText(path, "pending\n");
		tracker.RecordChange(path);
	}
}
