using Weavie.Core.FileSystem;
using Weavie.Core.Worktrees;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="WorktreeRegistry"/>: persistence, same-path replacement, removal, lookups, malformed-file
/// backup + reset, and the Changed event.
/// </summary>
public sealed class WorktreeRegistryTests {
	private const string RegistryPath = "/weavie-worktree-tests/worktrees.json";

	private static WorktreeRecord Record(string branch, string path) => new() {
		Branch = branch,
		Path = path,
		BaseRef = "main",
		CreatedAtUtc = DateTimeOffset.UnixEpoch,
	};

	[Fact]
	public void Add_PersistsAndReloads() {
		var fs = new InMemoryFileSystem();
		var registry = new WorktreeRegistry(fs, RegistryPath);

		registry.Add(Record("feature", "/wt/feature"));

		Assert.True(fs.FileExists(RegistryPath));
		var reloaded = new WorktreeRegistry(fs, RegistryPath);
		Assert.Single(reloaded.Items);
		Assert.Equal("feature", reloaded.Items[0].Branch);
		Assert.Equal("main", reloaded.Items[0].BaseRef);
	}

	[Fact]
	public void Add_SamePath_Replaces() {
		var fs = new InMemoryFileSystem();
		var registry = new WorktreeRegistry(fs, RegistryPath);

		registry.Add(Record("feature", "/wt/feature"));
		registry.Add(Record("renamed", "/wt/feature"));

		Assert.Single(registry.Items);
		Assert.Equal("renamed", registry.Items[0].Branch);
	}

	[Fact]
	public void Remove_DropsEntry() {
		var fs = new InMemoryFileSystem();
		var registry = new WorktreeRegistry(fs, RegistryPath);
		registry.Add(Record("feature", "/wt/feature"));

		registry.Remove("/wt/feature");

		Assert.Empty(registry.Items);
	}

	[Fact]
	public void FindByBranchAndPath_Work() {
		var fs = new InMemoryFileSystem();
		var registry = new WorktreeRegistry(fs, RegistryPath);
		registry.Add(Record("feature", "/wt/feature"));

		Assert.NotNull(registry.FindByBranch("feature"));
		Assert.NotNull(registry.FindByPath("/wt/feature"));
		Assert.Null(registry.FindByBranch("missing"));
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(RegistryPath, "{ not valid json ");

		var registry = new WorktreeRegistry(fs, RegistryPath);

		Assert.True(fs.FileExists(RegistryPath + ".bad"));
		Assert.Empty(registry.Items);
	}

	[Fact]
	public void Changed_RaisedOnAddAndRemove() {
		var fs = new InMemoryFileSystem();
		var registry = new WorktreeRegistry(fs, RegistryPath);
		int count = 0;
		registry.Changed += () => count++;

		registry.Add(Record("feature", "/wt/feature"));
		registry.Remove("/wt/feature");

		Assert.Equal(2, count);
	}

	[Fact]
	public void Remove_UnknownPath_DoesNotPersistOrNotify() {
		var fs = new InMemoryFileSystem();
		var registry = new WorktreeRegistry(fs, RegistryPath);
		int count = 0;
		registry.Changed += () => count++;

		registry.Remove("/wt/never-added"); // nothing matched: no write, no event

		Assert.Equal(0, count);
		Assert.False(fs.FileExists(RegistryPath));
	}
}
