using Weavie.Core.FileSystem;
using Weavie.Core.Remote;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="RemoteAgentStore"/> over the in-memory filesystem: add/replace-by-name
/// (case-insensitive), remove, change notifications, token round-trip across reloads, blank-name rejection,
/// load-time filtering of incomplete entries, and malformed-file backup + reset.
/// </summary>
public sealed class RemoteAgentStoreTests {
	private const string StorePath = "/weavie-remote-agent-tests/remote-agents.json";

	private static RemoteAgent Agent(string name) => new(name, $"http://{name}:8800", $"tok-{name}");

	[Fact]
	public void Add_ThenAgents_ContainsIt() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);

		store.Add(Agent("devbox"));

		var only = Assert.Single(store.Agents);
		Assert.Equal("devbox", only.Name);
		Assert.Equal("http://devbox:8800", only.Url);
		Assert.Equal("tok-devbox", only.Token);
	}

	[Fact]
	public void Add_SameName_ReplacesCaseInsensitively() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);
		store.Add(new RemoteAgent("devbox", "http://old:8800", "old"));

		store.Add(new RemoteAgent("DEVBOX", "http://new:8800", "new"));

		var only = Assert.Single(store.Agents);
		Assert.Equal("DEVBOX", only.Name);
		Assert.Equal("http://new:8800", only.Url);
	}

	[Fact]
	public void Remove_DropsAgent_CaseInsensitive() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);
		store.Add(Agent("devbox"));

		store.Remove("DEVBOX");

		Assert.Empty(store.Agents);
	}

	[Fact]
	public void Remove_Unknown_IsNoOp() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);
		store.Add(Agent("devbox"));

		store.Remove("absent"); // must not throw or drop the existing one

		Assert.Single(store.Agents);
	}

	[Fact]
	public void Add_BlankName_IsNoOp() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);

		store.Add(new RemoteAgent("  ", "http://x:8800", "tok"));

		Assert.Empty(store.Agents);
	}

	[Fact]
	public void Add_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new RemoteAgentStore(fs, StorePath).Add(Agent("devbox"));

		var only = Assert.Single(new RemoteAgentStore(fs, StorePath).Agents);

		Assert.Equal("tok-devbox", only.Token); // tokens round-trip
	}

	[Fact]
	public void Changed_FiresOnAddAndRemove() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.Add(Agent("devbox"));
		store.Remove("devbox");

		Assert.Equal(2, changes);
	}

	[Fact]
	public void Remove_Unknown_DoesNotFireChanged() {
		var store = new RemoteAgentStore(new InMemoryFileSystem(), StorePath);
		store.Add(Agent("devbox"));
		int changes = 0;
		store.Changed += () => changes++;

		store.Remove("absent");

		Assert.Equal(0, changes);
	}

	[Fact]
	public void Load_SkipsEntriesMissingUrlOrToken() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath,
			"{\"version\":1,\"agents\":["
			+ "{\"name\":\"ok\",\"url\":\"http://ok:8800\",\"token\":\"t\"},"
			+ "{\"name\":\"notoken\",\"url\":\"http://x:8800\",\"token\":\"\"},"
			+ "{\"name\":\"nourl\",\"url\":\"\",\"token\":\"t\"},"
			+ "{\"name\":\"\",\"url\":\"http://y:8800\",\"token\":\"t\"}]}");

		var only = Assert.Single(new RemoteAgentStore(fs, StorePath).Agents);

		Assert.Equal("ok", only.Name);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new RemoteAgentStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Empty(store.Agents);
	}
}
