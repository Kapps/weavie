using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="FileOpener"/> pushes a file to the editor through the session's <see cref="SessionEditorChannel"/>,
/// so a background session's <c>openFile</c> is held (not posted into the foreground) and a missing file is a
/// skipped no-op rather than an error. The gate goes through the validated <see cref="FileProviderService"/>,
/// so an out-of-workspace path is refused, not revealed. No content rides on the push — the web reads disk.
/// A relative reference that doesn't resolve is recovered by suffix match against the workspace index: one hit
/// opens, several preload Go-to-File, none toasts.
/// </summary>
public sealed class FileOpenerTests {
	// A real worktree root is always fully rooted; "/ws" is drive-relative on Windows, where Path.GetFullPath
	// would root it to the current drive (C:\ws) and a relative-path resolution would diverge from Path.Combine.
	private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
	private static readonly string Scratch = OperatingSystem.IsWindows() ? @"C:\scratch" : "/scratch";

	private static (FileOpener opener, SessionEditorChannel channel, FakeHostBridge bridge, InMemoryFileSystem fs) New() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		var fs = new InMemoryFileSystem();
		var files = new FileProviderService(fs, Workspace, Scratch);
		return (new FileOpener(channel, files, bridge, new WorkspaceFileIndex(fs, Workspace)), channel, bridge, fs);
	}

	[Fact]
	public void Active_PostsOpenFileWithoutContent() {
		var (opener, channel, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");
		channel.Activate();

		opener.Open(path, line: 3, preview: false, scratch: false);

		var msg = bridge.LastOfType("open-file");
		Assert.True(msg.HasValue);
		Assert.Equal(path, msg!.Value.GetProperty("path").GetString());
		Assert.False(msg.Value.TryGetProperty("content", out _)); // the working copy reads disk — nothing rides along
		Assert.Equal(3, msg.Value.GetProperty("line").GetInt32());
	}

	[Fact]
	public void Muted_HoldsOpenFile_UntilActivate() {
		var (opener, channel, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");

		opener.Open(path, line: 1, preview: false, scratch: false); // session is background → held
		Assert.Empty(bridge.Posted);

		channel.Activate();
		Assert.Equal("open-file", JsonDocument.Parse(Assert.Single(bridge.Posted)).RootElement.GetProperty("type").GetString());
	}

	[Fact]
	public void NonPositiveLine_IsClampedToOne() {
		var (opener, channel, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");
		channel.Activate();

		opener.Open(path, line: 0, preview: false, scratch: false); // a 0/negative line must reveal line 1, not 0

		Assert.Equal(1, bridge.LastOfType("open-file")!.Value.GetProperty("line").GetInt32());
	}

	[Fact]
	public void RelativePath_ResolvesAgainstTheWorkspace() {
		var (opener, channel, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "a.cs"), "hello");
		channel.Activate();

		opener.Open("a.cs", line: 1, preview: false, scratch: false); // relative → resolved under the workspace

		Assert.Equal(Path.Combine(Workspace, "a.cs"), bridge.LastOfType("open-file")!.Value.GetProperty("path").GetString());
	}

	[Fact]
	public void PreviewAndScratch_FlagsArePropagated() {
		var (opener, channel, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");
		channel.Activate();

		opener.Open(path, line: 1, preview: true, scratch: true);

		var msg = bridge.LastOfType("open-file")!.Value;
		Assert.True(msg.GetProperty("preview").GetBoolean());
		Assert.True(msg.GetProperty("scratch").GetBoolean());
	}

	[Fact]
	public void MissingFile_ToastsAWarningInsteadOfOpening() {
		var (opener, channel, bridge, _) = New();
		channel.Activate();

		opener.Open(Path.Combine(Workspace, "ghost.cs"), line: 1, preview: false, scratch: false);

		Assert.Null(bridge.LastOfType("open-file")); // not found → no open-file, no crash
		Assert.Contains("ghost.cs", bridge.LastOfType("notify")!.Value.GetProperty("message").GetString()); // …but the user hears why
	}

	[Fact]
	public async Task RelativePathMissingItsLeadingFolders_OpensTheSuffixMatch() {
		var (opener, channel, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "src", "web", "foo.ts"), "");
		channel.Activate();

		await opener.OpenAsync("web/foo.ts", line: 7, preview: true, scratch: false);

		var msg = bridge.LastOfType("open-file")!.Value;
		Assert.Equal(Path.Combine(Workspace, "src", "web", "foo.ts"), msg.GetProperty("path").GetString());
		Assert.Equal(7, msg.GetProperty("line").GetInt32()); // the requested line survives the recovery
		Assert.True(msg.GetProperty("preview").GetBoolean());
	}

	[Fact]
	public async Task BareFilename_UniqueInTheWorkspace_Opens() {
		var (opener, channel, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "src", "deep", "unique.cs"), "");
		channel.Activate();

		await opener.OpenAsync("unique.cs", line: 1, preview: false, scratch: false);

		Assert.Equal(
			Path.Combine(Workspace, "src", "deep", "unique.cs"),
			bridge.LastOfType("open-file")!.Value.GetProperty("path").GetString());
	}

	[Fact]
	public async Task AmbiguousReference_OpensGoToFilePreloadedInsteadOfGuessing() {
		var (opener, channel, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "a", "foo.ts"), "");
		fs.WriteAllText(Path.Combine(Workspace, "b", "foo.ts"), "");
		channel.Activate();

		await opener.OpenAsync("./foo.ts", line: 1, preview: false, scratch: false);

		Assert.Null(bridge.LastOfType("open-file")); // two candidates — never open one arbitrarily
		Assert.Null(bridge.LastOfType("notify"));
		Assert.Equal("foo.ts", bridge.LastOfType("focus-omnibar")!.Value.GetProperty("query").GetString());
	}

	[Fact]
	public async Task RelativePathWithNoSuffixMatch_ToastsAWarning() {
		var (opener, channel, bridge, _) = New();
		channel.Activate();

		await opener.OpenAsync("nowhere/ghost.cs", line: 1, preview: false, scratch: false);

		Assert.Null(bridge.LastOfType("open-file"));
		Assert.Contains("ghost.cs", bridge.LastOfType("notify")!.Value.GetProperty("message").GetString());
	}

	[Fact]
	public async Task Muted_HoldsTheAmbiguousReferenceOmnibarOpen_UntilActivate() {
		var (opener, channel, bridge, fs) = New();
		fs.WriteAllText(Path.Combine(Workspace, "a", "foo.ts"), "");
		fs.WriteAllText(Path.Combine(Workspace, "b", "foo.ts"), "");

		await opener.OpenAsync("foo.ts", line: 1, preview: false, scratch: false); // background session → held
		Assert.Empty(bridge.Posted);

		channel.Activate();
		Assert.NotNull(bridge.LastOfType("focus-omnibar")); // surfaces with the session, not over the foreground
	}

	[Fact]
	public void OutOfWorkspaceFile_IsRefused() {
		var (opener, channel, bridge, fs) = New();
		fs.WriteAllText("/etc/secret.txt", "top secret"); // exists on disk, but outside the worktree + scratch
		channel.Activate();

		opener.Open("/etc/secret.txt", line: 1, preview: false, scratch: false);

		Assert.Null(bridge.LastOfType("open-file")); // containment refuses it before any read → never revealed in the editor
		Assert.NotNull(bridge.LastOfType("notify")); // refused loudly, not silently ignored
	}
}
