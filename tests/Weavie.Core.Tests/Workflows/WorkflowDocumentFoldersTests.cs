using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowDocumentFoldersTests {
	[Fact]
	public void InFlightChildKeepsReplacedRunAliveUntilItIsPersistedOrAbandoned() {
		using var dir = new TempDirectory("workflow-folder-owners");
		var folders = new WorkflowDocumentFolders(dir.Path, error => Assert.Fail(error));
		string original = folders.ForRun("original");
		string replacement = folders.ForRun("replacement");
		Directory.CreateDirectory(original);
		folders.Restore([new("parent", original)]);
		using (var child = folders.Acquire("parent")) {
			folders.Bind("parent", replacement);
			Assert.True(Directory.Exists(original));
			folders.Bind("child", child.Directory);
		}
		Assert.True(Directory.Exists(original));
		using (folders.Acquire("child")) {
			folders.Bind("child", null);
			Assert.True(Directory.Exists(original));
		}
		Assert.False(Directory.Exists(original));
	}

	[Fact]
	public void RestoreCollectsOnlyManagedOrphansAndPreservesDormantSessionReferences() {
		using var dir = new TempDirectory("workflow-folder-restore");
		string root = dir.Combine("managed");
		string used = Path.Combine(root, "used");
		string orphan = Path.Combine(root, "orphan");
		string unrelated = dir.Combine("unrelated");
		foreach (string path in new[] { used, orphan, unrelated }) Directory.CreateDirectory(path);
		var errors = new List<string>();
		var folders = new WorkflowDocumentFolders(root, errors.Add);
		folders.Restore([new("dormant", Path.Combine(used, "."))]);
		Assert.True(Directory.Exists(used));
		Assert.False(Directory.Exists(orphan));
		folders.Bind("invalid", unrelated);
		folders.Bind("invalid", null);
		Assert.True(Directory.Exists(unrelated));
		Assert.Contains("outside the managed", Assert.Single(errors));
		foreach (string traversal in new[] { ".", ".." }) {
			folders.Bind("invalid", Path.Combine(root, traversal));
			folders.Bind("invalid", null);
			Assert.True(Directory.Exists(used));
			Assert.True(Directory.Exists(root));
		}
		Assert.Equal(3, errors.Count);
	}

	[Fact]
	public void IncompleteOwnershipRestorePreventsDestructiveCollection() {
		using var dir = new TempDirectory("workflow-folder-failure");
		var folders = new WorkflowDocumentFolders(dir.Path, error => Assert.Fail(error));
		string owned = folders.ForRun("owned");
		Directory.CreateDirectory(owned);
		IEnumerable<KeyValuePair<string, string>> Broken() {
			yield return new("session", owned);
			throw new IOException("unreadable ownership");
		}
		Assert.Throws<IOException>(() => folders.Restore(Broken()));
		folders.Bind("session", null);
		Assert.True(Directory.Exists(owned));
	}
}
