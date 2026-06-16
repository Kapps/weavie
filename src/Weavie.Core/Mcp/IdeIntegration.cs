using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Mcp;

/// <summary>
/// One-call setup of the IDE-MCP surface: generate a token, start the loopback MCP server on an
/// ephemeral port, and write the <c>~/.claude/ide/&lt;port&gt;.lock</c> file. Expose the env vars to
/// inject into the spawned <c>claude</c> so it connects to <em>this</em> server. Disposal removes
/// the lock file and stops the server.
/// </summary>
public sealed class IdeIntegration : IAsyncDisposable {
	/// <summary>
	/// Mints an auth token, starts the loopback MCP server on an ephemeral port, and writes the
	/// IDE lock file so a spawned <c>claude</c> can discover and connect to this server.
	/// </summary>
	public IdeIntegration(
		IDiffPresenter presenter,
		IFileSystem fileSystem,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie",
		SettingsStore? settings = null) {
		ArgumentNullException.ThrowIfNull(workspaceFolders);

		AuthToken = IdeLockFile.NewAuthToken();
		Server = new McpServer(AuthToken, presenter, fileSystem, workspaceFolders, ideName, settings);
		Port = Server.Start();
		IdeLockFile.Write(Port, workspaceFolders, ideName, AuthToken);
	}

	/// <summary>The running MCP server backing this integration.</summary>
	public McpServer Server { get; }

	/// <summary>The loopback port the server is listening on.</summary>
	public int Port { get; }

	/// <summary>The auth token Claude must present, also written into the lock file.</summary>
	public string AuthToken { get; }

	/// <summary>Path of the lock file written for the current <see cref="Port"/>.</summary>
	public string LockFilePath => IdeLockFile.PathForPort(Port);

	/// <summary>Env vars to inject into the spawned <c>claude</c> so it connects here.</summary>
	public IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>(StringComparer.Ordinal) {
		["CLAUDE_CODE_SSE_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
		["ENABLE_IDE_INTEGRATION"] = "true",
	};

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		IdeLockFile.Delete(Port);
		await Server.DisposeAsync().ConfigureAwait(false);
	}
}
