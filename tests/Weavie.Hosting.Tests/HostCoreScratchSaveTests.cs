using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreScratchSaveTests {
	private static string OpenScratch(HostSession session, string content) {
		session.OpenNewScratch();
		string path = Assert.Single(session.EditorSession.Open).Path;
		session.FileSystem.WriteAllText(path, content);
		return path;
	}

	[Fact]
	public async Task NamedSaveReadsTheExactOwnersFlushedScratchFromDisk() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string scratch = OpenScratch(session, "authoritative disk content");

		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"editor",
			"saveScratchNamed",
			new { path = scratch, name = "saved.txt", content = "untrusted duplicate" });

		Assert.Equal("saved", result.GetProperty("status").GetString());
		string saved = result.GetProperty("savedPath").GetString()!;
		Assert.Equal(Path.Combine(host.RepoRoot, "saved.txt"), saved);
		Assert.Equal("authoritative disk content", File.ReadAllText(saved));
		Assert.False(File.Exists(scratch));
	}

	[Fact]
	public async Task NamedSaveRejectsTraversalAndKeepsTheScratch() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string scratch = OpenScratch(session, "keep me");
		string escaped = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "escaped.txt");

		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"editor",
			"saveScratchNamed",
			new { path = scratch, name = "../escaped.txt" });

		Assert.Equal("failed", result.GetProperty("status").GetString());
		Assert.Contains("inside the workspace", result.GetProperty("error").GetString());
		Assert.True(File.Exists(scratch));
		Assert.False(File.Exists(escaped));
	}

	[Fact]
	public async Task NamedSaveCannotEscapeThroughACaseVariantSiblingOnCaseSensitiveSystems() {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string scratch = OpenScratch(session, "keep me");
		string siblingName = Path.GetFileName(host.RepoRoot).ToUpperInvariant();
		string sibling = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, siblingName);
		Directory.CreateDirectory(sibling);

		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"editor",
			"saveScratchNamed",
			new { path = scratch, name = $"../{siblingName}/escaped.txt" });

		Assert.Equal("failed", result.GetProperty("status").GetString());
		Assert.Contains("inside the workspace", result.GetProperty("error").GetString());
		Assert.True(File.Exists(scratch));
		Assert.False(File.Exists(Path.Combine(sibling, "escaped.txt")));
	}

	[Fact]
	public async Task ScratchSaveRejectsAPathOwnedByAnotherSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		string foreignScratch = OpenScratch(host.Session("feature"), "feature draft");

		var result = await host.SessionRequestAsync<JsonElement>(
			host.WorkspaceSession,
			"editor",
			"saveScratchNamed",
			new { path = foreignScratch, name = "stolen.txt" });

		Assert.Equal("failed", result.GetProperty("status").GetString());
		Assert.Contains("does not belong", result.GetProperty("error").GetString());
		Assert.True(File.Exists(foreignScratch));
		Assert.False(File.Exists(Path.Combine(host.RepoRoot, "stolen.txt")));
	}

	[Fact]
	public async Task NativeSaveCancellationIsTaggedAndKeepsTheScratch() {
		await using var host = await TestHost.StartWithDialogsAsync(new CancellingDialogs());
		var session = host.WorkspaceSession;
		string scratch = OpenScratch(session, "keep me");

		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"editor",
			"saveScratchAs",
			new { path = scratch, suggestedName = "draft.txt" });

		Assert.Equal("cancelled", result.GetProperty("status").GetString());
		Assert.True(File.Exists(scratch));
	}

	[Fact]
	public async Task NativeDialogFailureIsTaggedAndKeepsTheScratch() {
		await using var host = await TestHost.StartWithDialogsAsync(new FailingDialogs());
		var session = host.WorkspaceSession;
		string scratch = OpenScratch(session, "keep me");

		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"editor",
			"saveScratchAs",
			new { path = scratch, suggestedName = "draft.txt" });

		Assert.Equal("failed", result.GetProperty("status").GetString());
		Assert.Contains("dialog failed", result.GetProperty("error").GetString());
		Assert.True(File.Exists(scratch));
	}

	[Fact]
	public async Task NativeSaveRejectsEveryDestinationInsideTheScratchStore() {
		string? target = null;
		await using var host = await TestHost.StartWithDialogsAsync(new TargetDialogs(() => target!));
		var session = host.WorkspaceSession;
		string scratch = OpenScratch(session, "keep me");
		target = Path.Combine(session.Scratch.Directory, "renamed.txt");

		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"editor",
			"saveScratchAs",
			new { path = scratch, suggestedName = "draft.txt" });

		Assert.Equal("failed", result.GetProperty("status").GetString());
		Assert.Contains("outside the scratch directory", result.GetProperty("error").GetString());
		Assert.True(File.Exists(scratch));
		Assert.False(File.Exists(target));
	}

	private sealed class CancellingDialogs : IHostDialogs {
		public Task<string?> PickVsixFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

		public Task<string?> PickSaveAsPathAsync(
			string suggestedName,
			string initialDirectory,
			CancellationToken ct) => Task.FromResult<string?>(null);
	}

	private sealed class FailingDialogs : IHostDialogs {
		public Task<string?> PickVsixFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

		public Task<string?> PickSaveAsPathAsync(
			string suggestedName,
			string initialDirectory,
			CancellationToken ct) => throw new InvalidOperationException("dialog failed");
	}

	private sealed class TargetDialogs(Func<string> target) : IHostDialogs {
		public Task<string?> PickVsixFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

		public Task<string?> PickSaveAsPathAsync(
			string suggestedName,
			string initialDirectory,
			CancellationToken ct) => Task.FromResult<string?>(target());
	}
}
