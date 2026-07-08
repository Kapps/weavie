using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for resolving a clicked terminal file link. A link from the shell pane carries its pane
/// identity so the host resolves a relative path (e.g. a filename printed by <c>ls</c> in a subdir) against that
/// shell's live OSC 7 cwd; a link with no pane, or from the Claude pane, resolves against the worktree root. The
/// worktree confinement gate stays authoritative either way.
/// </summary>
[Collection("host-integration")]
public sealed class RevealFileResolutionTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	/// <summary>Starts a host whose repo has a nested source file, with the shell pane ready and reporting the nested dir.</summary>
	private static async Task<(TestHost Host, string Nested)> StartWithNestedShellAsync() {
		var host = await TestHost.StartAsync(repo =>
			Directory.CreateDirectory(Path.Combine(repo, "src", "nested"))).ConfigureAwait(false);
		File.WriteAllText(Path.Combine(host.RepoRoot, "src", "nested", "foo.ts"), "export const x = 1;\n");
		string nested = Path.Combine(host.RepoRoot, "src", "nested");

		host.Send(Msg(new { type = "term-ready", slot = host.PrimaryId, session = "shell", cols = 80, rows = 24 }));
		host.Send(Msg(new { type = "term-cwd", slot = host.PrimaryId, session = "shell", cwd = nested }));
		return (host, nested);
	}

	[Fact]
	public async Task ShellLink_ResolvesARelativePathAgainstThePaneCwd() {
		var (host, nested) = await StartWithNestedShellAsync();
		await using var _ = host;

		host.Send(Msg(new { type = "reveal-file", path = "foo.ts", line = 3, slot = host.PrimaryId, session = "shell" }));

		var open = host.Bridge.LastOfType("open-file");
		Assert.True(open.HasValue);
		Assert.Equal(Path.Combine(nested, "foo.ts"), open!.Value.GetProperty("path").GetString());
		Assert.Equal(3, open.Value.GetProperty("line").GetInt32());
	}

	[Fact]
	public async Task LinkWithoutAPane_ResolvesAgainstTheWorktreeRoot() {
		await using var host = await TestHost.StartAsync();

		// readme.txt exists at the worktree root (TestHost's initial commit), not in any subdir.
		host.Send(Msg(new { type = "reveal-file", path = "readme.txt", line = 1 }));

		var open = host.Bridge.LastOfType("open-file");
		Assert.True(open.HasValue);
		Assert.Equal(Path.Combine(host.RepoRoot, "readme.txt"), open!.Value.GetProperty("path").GetString());
	}

	[Fact]
	public async Task ShellLink_EscapingTheWorktree_IsRefusedNotOpened() {
		var (host, _) = await StartWithNestedShellAsync();
		await using var _ = host;

		host.Send(Msg(new {
			type = "reveal-file", path = "../../../../etc/passwd", line = 1, slot = host.PrimaryId, session = "shell",
		}));

		Assert.Null(host.Bridge.LastOfType("open-file")); // the confinement gate refused it
		Assert.True(host.Bridge.LastOfType("notify").HasValue); // and told the user
	}
}
