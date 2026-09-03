using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="FileOpener"/> publishes state to its owning session bus without consulting client selection. An
/// open is gated on existence alone, wherever the file lives. Relative references recover by suffix match:
/// one hit opens, several preload Go-to-File, and none toast.
/// </summary>
public sealed class FileOpenerTests {
	// A real worktree root is always fully rooted; "/ws" is drive-relative on Windows, where Path.GetFullPath
	// would root it to the current drive (C:\ws) and a relative-path resolution would diverge from Path.Combine.
	private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
	private static readonly string Scratch = OperatingSystem.IsWindows() ? @"C:\scratch" : "/scratch";

	private static (FileOpener opener, FakeHostBridge bridge, InMemoryFileSystem fs) New() {
		var bridge = new FakeHostBridge();
		var fs = new InMemoryFileSystem();
		var files = new FileProviderService(fs);
		return (
			new FileOpener(
				bridge.SessionViewFeature("view"),
				bridge.SessionFeature("notifications"),
				files,
				new WorkspaceFileIndex(fs, Workspace),
				(path, line, preview, scratch) =>
					bridge.SessionFeature("editor").Publish(
						"openFile",
						new { path, line, preview, scratch })),
			bridge,
			fs);
	}

	[Fact]
	public async Task PublishesOpenFileWithoutContent() {
		var (opener, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");

		await opener.OpenAsync(path, line: 3, preview: false, scratch: false);

		var msg = bridge.LastEvent("editor", "openFile");
		Assert.True(msg.HasValue);
		Assert.Equal(path, msg!.Value.GetProperty("path").GetString());
		Assert.False(msg.Value.TryGetProperty("content", out _)); // the working copy reads disk — nothing rides along
		Assert.Equal(3, msg.Value.GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task BackgroundSelectionCannotSuppressTheOwningSessionEvent() {
		var (opener, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");

		await opener.OpenAsync(path, line: 1, preview: false, scratch: false);

		Assert.NotNull(bridge.LastEvent("editor", "openFile"));
	}

	[Fact]
	public async Task NonPositiveLine_IsClampedToOne() {
		var (opener, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");

		await opener.OpenAsync(path, line: 0, preview: false, scratch: false); // a 0/negative line must reveal line 1, not 0

		Assert.Equal(1, bridge.LastEvent("editor", "openFile")!.Value.GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task NullLine_PublishesNoTarget() {
		var (opener, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");

		// No target line: the web leaves an already-open tab where the user left it.
		await opener.OpenAsync(path, line: null, preview: false, scratch: false);

		Assert.Equal(
			JsonValueKind.Null,
			bridge.LastEvent("editor", "openFile")!.Value.GetProperty("line").ValueKind);
	}

	[Fact]
	public async Task RelativePath_ResolvesAgainstTheWorkspace() {
		var (opener, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "a.cs"), "hello");

		await opener.OpenAsync("a.cs", line: 1, preview: false, scratch: false); // relative → resolved under the workspace

		Assert.Equal(Path.Combine(Workspace, "a.cs"), bridge.LastEvent("editor", "openFile")!.Value.GetProperty("path").GetString());
	}

	[Fact]
	public async Task PreviewAndScratch_FlagsArePropagated() {
		var (opener, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");

		await opener.OpenAsync(path, line: 1, preview: true, scratch: true);

		var msg = bridge.LastEvent("editor", "openFile")!.Value;
		Assert.True(msg.GetProperty("preview").GetBoolean());
		Assert.True(msg.GetProperty("scratch").GetBoolean());
	}

	[Fact]
	public async Task MissingFile_ToastsAWarningInsteadOfOpening() {
		var (opener, bridge, _) = New();

		await opener.OpenAsync(
			Path.Combine(Workspace, "ghost.cs"),
			line: 1,
			preview: false,
			scratch: false);

		Assert.Null(bridge.LastEvent("editor", "openFile")); // not found → no open-file, no crash
		Assert.Contains("ghost.cs", bridge.LastEvent("notifications", "show")!.Value.GetProperty("message").GetString()); // …but the user hears why
	}

	[Fact]
	public async Task RelativePathMissingItsLeadingFolders_OpensTheSuffixMatch() {
		var (opener, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "src", "web", "foo.ts"), "");

		await opener.OpenAsync("web/foo.ts", line: 7, preview: true, scratch: false);

		var msg = bridge.LastEvent("editor", "openFile")!.Value;
		Assert.Equal(Path.Combine(Workspace, "src", "web", "foo.ts"), msg.GetProperty("path").GetString());
		Assert.Equal(7, msg.GetProperty("line").GetInt32()); // the requested line survives the recovery
		Assert.True(msg.GetProperty("preview").GetBoolean());
	}

	[Fact]
	public async Task BareFilename_UniqueInTheWorkspace_Opens() {
		var (opener, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "src", "deep", "unique.cs"), "");

		await opener.OpenAsync("unique.cs", line: 1, preview: false, scratch: false);

		Assert.Equal(
			Path.Combine(Workspace, "src", "deep", "unique.cs"),
			bridge.LastEvent("editor", "openFile")!.Value.GetProperty("path").GetString());
	}

	[Fact]
	public async Task AmbiguousReference_OpensGoToFilePreloadedInsteadOfGuessing() {
		var (opener, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "a", "foo.ts"), "");
		fs.WriteAllText(Path.Combine(Workspace, "b", "foo.ts"), "");

		await opener.OpenAsync("./foo.ts", line: 12, preview: false, scratch: false);

		Assert.Null(bridge.LastEvent("editor", "openFile")); // two candidates — never open one arbitrarily
		Assert.Null(bridge.LastEvent("notifications", "show"));
		var focus = bridge.LastEvent("view", "focusOmnibar")!.Value;
		Assert.Equal("foo.ts", focus.GetProperty("query").GetString());
		Assert.Equal(12, focus.GetProperty("line").GetInt32()); // the link's line applies to whichever candidate is picked
	}

	[Fact]
	public async Task RelativePathWithNoSuffixMatch_ToastsAWarning() {
		var (opener, bridge, _) = New();

		await opener.OpenAsync("nowhere/ghost.cs", line: 1, preview: false, scratch: false);

		Assert.Null(bridge.LastEvent("editor", "openFile"));
		Assert.Contains("ghost.cs", bridge.LastEvent("notifications", "show")!.Value.GetProperty("message").GetString());
	}

	[Fact]
	public async Task AmbiguousReferencePublishesToItsOwningSessionImmediately() {
		var (opener, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "a", "foo.ts"), "");
		fs.WriteAllText(Path.Combine(Workspace, "b", "foo.ts"), "");

		await opener.OpenAsync("foo.ts", line: 1, preview: false, scratch: false);

		Assert.NotNull(bridge.LastEvent("view", "focusOmnibar"));
	}

	[Fact]
	public async Task FileOutsideTheWorktree_IsOpened() {
		var (opener, bridge, fs) = New();
		string outside = OperatingSystem.IsWindows() ? @"C:\elsewhere\notes.md" : "/elsewhere/notes.md";
		fs.WriteAllText(outside, "notes");

		await opener.OpenAsync(outside, line: 1, preview: false, scratch: false);

		Assert.NotNull(bridge.LastEvent("editor", "openFile"));
		Assert.Null(bridge.LastEvent("notifications", "show"));
	}

	[Fact]
	public async Task MissingFile_IsRefusedLoudly() {
		var (opener, bridge, _) = New();

		await opener.OpenAsync("/elsewhere/ghost.md", line: 1, preview: false, scratch: false);

		Assert.Null(bridge.LastEvent("editor", "openFile"));
		Assert.NotNull(bridge.LastEvent("notifications", "show")); // refused loudly, not silently ignored
	}
}
