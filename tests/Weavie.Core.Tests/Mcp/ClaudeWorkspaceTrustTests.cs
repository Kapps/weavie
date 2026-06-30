using System.Text.Json.Nodes;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests.Mcp;

public sealed class ClaudeWorkspaceTrustTests : IDisposable {
	private readonly string _dir;
	private readonly string _config;

	public ClaudeWorkspaceTrustTests() {
		_dir = Path.Combine(Path.GetTempPath(), "weavie-trust-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(_dir);
		_config = Path.Combine(_dir, ".claude.json");
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		}
	}

	private JsonObject Project(string path) =>
		(JsonObject)JsonNode.Parse(File.ReadAllText(_config))!["projects"]![path]!;

	[Fact]
	public void CreatesConfigAndTrustsWorkspace_WhenFileMissing() {
		Assert.True(ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo"));

		Assert.True((bool)Project("/work/repo")["hasTrustDialogAccepted"]!);
	}

	[Fact]
	public void DoesNotEnableProjectMcpServers() {
		ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo");

		// Only the trust flag is set — the repo's own .mcp.json servers keep Claude's per-server consent.
		Assert.Null(Project("/work/repo")["enableAllProjectMcpServers"]);
	}

	[Fact]
	public void PreservesOtherProjectsAndTopLevelKeys() {
		File.WriteAllText(_config, """
			{"oauthAccount":{"email":"x@y.z"},"projects":{"/other":{"history":[1,2],"hasTrustDialogAccepted":true}}}
			""");

		ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo");

		var root = (JsonObject)JsonNode.Parse(File.ReadAllText(_config))!;
		Assert.Equal("x@y.z", (string)root["oauthAccount"]!["email"]!);
		Assert.Equal(2, ((JsonArray)Project("/other")["history"]!).Count); // untouched
		Assert.True((bool)Project("/work/repo")["hasTrustDialogAccepted"]!);
	}

	[Fact]
	public void MergesIntoExistingProjectEntry_KeepingItsOtherFields() {
		File.WriteAllText(_config, """
			{"projects":{"/work/repo":{"disabledMcpjsonServers":["sentry"],"hasTrustDialogAccepted":false}}}
			""");

		Assert.True(ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo"));

		var project = Project("/work/repo");
		Assert.True((bool)project["hasTrustDialogAccepted"]!); // flipped
		// The user's explicit per-server disable is preserved, not overridden.
		Assert.Equal("sentry", (string)((JsonArray)project["disabledMcpjsonServers"]!)[0]!);
	}

	[Fact]
	public void IsIdempotent_ReturnsTrueWithoutRewriting_WhenAlreadyTrusted() {
		ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo");
		var firstWrite = File.GetLastWriteTimeUtc(_config);

		Assert.True(ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo"));
		Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(_config)); // file untouched
	}

	[Fact]
	public void LeavesMalformedConfigUntouched_AndReportsFailure() {
		File.WriteAllText(_config, "{ this is not json ");

		Assert.False(ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo"));
		Assert.Equal("{ this is not json ", File.ReadAllText(_config)); // never clobbered
	}

	[Fact]
	public void LeavesValidNonObjectConfigUntouched_AndReportsFailure() {
		File.WriteAllText(_config, "[1,2,3]"); // valid JSON, but not an object

		Assert.False(ClaudeWorkspaceTrust.EnsureTrusted(_config, "/work/repo"));
		Assert.Equal("[1,2,3]", File.ReadAllText(_config));
	}
}
