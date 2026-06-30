using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Mcp;

/// <summary>
/// Marks a workspace directory trusted in Claude Code's config (<c>$CLAUDE_CONFIG_DIR/.claude.json</c>, else
/// <c>~/.claude.json</c>) so the embedded interactive <c>claude</c> skips the first-run workspace-trust dialog.
/// In an untrusted directory that blocking dialog disrupts the <c>ws://</c> handshake to Weavie's own IDE +
/// registry servers, so <c>openDiff</c> and the <c>mcp__weavie__*</c> tools never connect. A freshly-created
/// worktree is always untrusted, which is why a secondary session lost the Weavie MCP integration the primary
/// checkout kept. Opening a workspace in Weavie is itself the trust decision (Weavie drives <c>claude</c> there
/// with its own hooks + MCP), so the per-folder prompt is redundant; we pre-accept it for every session's root.
/// Only the trust flag is set — the repo's own <c>.mcp.json</c> servers keep following Claude's normal per-server
/// consent, never auto-enabled.
/// </summary>
public static class ClaudeWorkspaceTrust {
	/// <summary>The Claude Code config file: <c>$CLAUDE_CONFIG_DIR/.claude.json</c>, else <c>~/.claude.json</c>.</summary>
	public static string ConfigFilePath {
		get {
			string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
			string directory = string.IsNullOrEmpty(configDir)
				? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
				: configDir;
			return Path.Combine(directory, ".claude.json");
		}
	}

	/// <summary>Trusts <paramref name="workspacePath"/> in the default Claude config (see the path overload).</summary>
	public static bool EnsureTrusted(string workspacePath) => EnsureTrusted(ConfigFilePath, workspacePath);

	/// <summary>
	/// Ensures <c>projects[workspacePath].hasTrustDialogAccepted</c> is true, preserving every other key. Returns
	/// true when the directory is trusted afterwards (already was, or written now), false when trust could NOT be
	/// ensured — a malformed/non-object or unreadable config is left untouched rather than clobbered (so the caller
	/// can surface it; Claude then falls back to its own trust dialog). Idempotent: no write when already trusted,
	/// keeping the concurrent-write window with a running <c>claude</c> to first launch only.
	/// </summary>
	public static bool EnsureTrusted(string configFilePath, string workspacePath) {
		ArgumentException.ThrowIfNullOrEmpty(configFilePath);
		ArgumentException.ThrowIfNullOrEmpty(workspacePath);

		JsonObject root;
		if (File.Exists(configFilePath)) {
			try {
				if (JsonNode.Parse(File.ReadAllText(configFilePath)) is not JsonObject parsed) {
					return false; // valid JSON but not an object: not ours to rewrite
				}

				root = parsed;
			} catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
				return false;
			}
		} else {
			root = [];
		}

		if (root["projects"] is not JsonObject projects) {
			projects = [];
			root["projects"] = projects;
		}

		if (projects[workspacePath] is not JsonObject project) {
			project = [];
			projects[workspacePath] = project;
		}

		if (IsTrue(project["hasTrustDialogAccepted"])) {
			return true; // already trusted; skip the write (and the concurrent-write window)
		}

		project["hasTrustDialogAccepted"] = true;
		string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		try {
			// Create a missing config dir (a custom CLAUDE_CONFIG_DIR may not exist yet) so the first session's write
			// lands instead of no-op-warning; never chmod an existing dir (it can be the user's home).
			if (Path.GetDirectoryName(configFilePath) is { Length: > 0 } directory) {
				Directory.CreateDirectory(directory);
			}

			// Atomic replace so a concurrent reader (the user's other claude) never sees a half-written config; the
			// temp inherits owner-only perms (the file holds an OAuth token), preserved across the rename.
			string temp = configFilePath + ".weavie.tmp";
			SecureFile.WriteAllText(temp, json);
			File.Move(temp, configFilePath, overwrite: true);
			return true;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) && flag;
}
