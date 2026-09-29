using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class FileReferenceResolutionTests {
	[Fact]
	public async Task BackgroundAcceptedOpens_AreDurableWithoutAuthoringTheBoundViewsSnapshot() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("background-open")).Ok);
		var session = host.Session("background-open");
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		host.SelectWorkspaceSession();
		var foreground = host.WorkspaceSession.EditorSession;
		host.Bridge.Clear();

		Assert.True(await host.SessionRequestAsync<bool>(session, "editor", "commitFileOpens", new {
			files = new[] { new { path, preview = false } },
			activePath = path,
			originPageEpoch = "background-client",
		}));
		host.SessionEvent(session, "editor", "sessionChanged", new { session = new { active = (string?)null, open = Array.Empty<object>() } });
		Assert.Equal(path, session.EditorSession.Active);
		Assert.Same(foreground, host.WorkspaceSession.EditorSession);
		Assert.Null(host.Bridge.LastEvent("editor", "openFile"));
		var published = host.Bridge.LastEvent("editor", "filesOpened")!.Value;
		Assert.Equal("background-client", published.GetProperty("originPageEpoch").GetString());
		Assert.Equal(path, Assert.Single(published.GetProperty("paths").EnumerateArray()).GetString());

		await host.RestartAsync();
		var restored = host.Session("background-open").EditorSession;
		Assert.Equal(path, restored.Active);
		Assert.Equal(path, Assert.Single(restored.Open).Path);
	}

	[Fact]
	public async Task LateFileBatch_MergesEveryPathWithoutReplacingTheActiveFile() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string current = Path.Combine(host.RepoRoot, "readme.txt");
		string first = Path.Combine(host.RepoRoot, "first.txt");
		string second = Path.Combine(host.RepoRoot, "second.txt");
		File.WriteAllText(first, "first");
		File.WriteAllText(second, "second");
		await session.FileOpener.OpenAsync(current, null, false, false, Weavie.Core.Editor.EditorOpenIntent.Navigation);
		host.Bridge.Clear();

		Assert.True(await host.SessionRequestAsync<bool>(session, "editor", "commitFileOpens", new {
			files = new[] { new { path = first, preview = false }, new { path = second, preview = false } },
			activePath = (string?)null,
			originPageEpoch = "late-batch",
		}));

		Assert.Equal(current, session.EditorSession.Active);
		Assert.Equal([current, first, second], session.EditorSession.Open.Select(entry => entry.Path));
		Assert.Null(host.Bridge.LastEvent("editor", "openFile"));
	}

	[Fact]
	public async Task ClientResolution_ReturnsToItsRequesterWithoutCommittingAnEditorOpen() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		var previous = session.EditorSession;
		host.Bridge.Clear();

		var result = await host.SessionRequestAsync<FileReferenceResolution>(
			session, "files", "resolveReference", new { path, line = 0 });

		Assert.Equal(new FileReferenceResolution.File(path, 1), result);
		Assert.Same(previous, session.EditorSession);
		Assert.Null(host.Bridge.LastEvent("editor", "openFile"));
		Assert.Null(host.Bridge.LastEvent("view", "focusOmnibar"));
	}
}
