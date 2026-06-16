using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Mcp;

/// <summary>
/// Writes/removes the Claude Code IDE lock file at <c>~/.claude/ide/&lt;port&gt;.lock</c> (or under
/// <c>$CLAUDE_CONFIG_DIR/ide</c>). Claude reads this to discover the IDE's auth token for the port
/// named by <c>CLAUDE_CODE_SSE_PORT</c>. Schema (reverse-engineered from coder/claudecode.nvim):
/// <c>{ pid, workspaceFolders, ideName, transport: "ws", authToken }</c>.
/// </summary>
public static class IdeLockFile {
	/// <summary>Directory holding the lock files: <c>$CLAUDE_CONFIG_DIR/ide</c>, else <c>~/.claude/ide</c>.</summary>
	public static string DirectoryPath {
		get {
			var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
			if (string.IsNullOrEmpty(configDir)) {
				configDir = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					".claude");
			}

			return Path.Combine(configDir, "ide");
		}
	}

	/// <summary>Returns the lock file path for a given port: <c>&lt;DirectoryPath&gt;/&lt;port&gt;.lock</c>.</summary>
	public static string PathForPort(int port) => Path.Combine(DirectoryPath, $"{port}.lock");

	/// <summary>Generates a 32-char lowercase-hex auth token (128 bits), matching the CLI's format.</summary>
	public static string NewAuthToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

	/// <summary>Writes the lock file for <paramref name="port"/> with the workspace, IDE name, and auth token Claude needs to connect.</summary>
	public static void Write(int port, IReadOnlyList<string> workspaceFolders, string ideName, string authToken) {
		ArgumentNullException.ThrowIfNull(workspaceFolders);
		ArgumentException.ThrowIfNullOrEmpty(ideName);
		ArgumentException.ThrowIfNullOrEmpty(authToken);

		Directory.CreateDirectory(DirectoryPath);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteNumber("pid", Environment.ProcessId);
			writer.WriteStartArray("workspaceFolders");
			foreach (var folder in workspaceFolders) {
				writer.WriteStringValue(folder);
			}

			writer.WriteEndArray();
			writer.WriteString("ideName", ideName);
			writer.WriteString("transport", "ws");
			writer.WriteString("authToken", authToken);
			writer.WriteEndObject();
		}

		File.WriteAllBytes(PathForPort(port), stream.ToArray());
	}

	/// <summary>Removes the lock file for <paramref name="port"/> if present; best-effort (ignores I/O errors).</summary>
	public static void Delete(int port) {
		try {
			var path = PathForPort(port);
			if (File.Exists(path)) {
				File.Delete(path);
			}
		} catch (IOException) {
			// Best-effort cleanup.
		}
	}

	internal static string ComputeWebSocketAccept(string secWebSocketKey) {
		const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
		var hash = SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey + magic));
		return Convert.ToBase64String(hash);
	}
}
