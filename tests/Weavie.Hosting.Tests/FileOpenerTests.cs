using System.Text.Json;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="FileOpener"/> pushes a file to the editor through the session's <see cref="SessionEditorChannel"/>,
/// so a background session's <c>openFile</c> is held (not posted into the foreground) and a missing file is a
/// skipped no-op rather than an error.
/// </summary>
public sealed class FileOpenerTests {
	private const string Workspace = "/ws";

	private static (FileOpener opener, SessionEditorChannel channel, FakeHostBridge bridge, InMemoryFileSystem fs) New() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		var fs = new InMemoryFileSystem();
		return (new FileOpener(channel, fs, Workspace), channel, bridge, fs);
	}

	[Fact]
	public void Active_PostsOpenFileWithContent() {
		var (opener, channel, bridge, fs) = New();
		string path = Path.Combine(Workspace, "a.cs");
		fs.WriteAllText(path, "hello");
		channel.Activate();

		opener.Open(path, line: 3, preview: false, scratch: false);

		var msg = bridge.LastOfType("open-file");
		Assert.True(msg.HasValue);
		Assert.Equal(path, msg!.Value.GetProperty("path").GetString());
		Assert.Equal("hello", msg.Value.GetProperty("content").GetString());
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
	public void MissingFile_IsSkipped() {
		var (opener, channel, bridge, _) = New();
		channel.Activate();

		opener.Open(Path.Combine(Workspace, "ghost.cs"), line: 1, preview: false, scratch: false);

		Assert.Empty(bridge.Posted); // not found → no open-file, no crash
	}
}
