using Weavie.Core.FileSystem;

namespace Weavie.Core.Mcp;

/// <summary>
/// One-call setup of the IDE-MCP surface: generate a token, start the loopback MCP server on an
/// ephemeral port, and write the <c>~/.claude/ide/&lt;port&gt;.lock</c> file. Expose the env vars to
/// inject into the spawned <c>claude</c> so it connects to <em>this</em> server. Disposal removes
/// the lock file and stops the server.
/// </summary>
public sealed class IdeIntegration : IAsyncDisposable
{
    public IdeIntegration(
        IDiffPresenter presenter,
        IFileSystem fileSystem,
        IReadOnlyList<string> workspaceFolders,
        string ideName = "weavie")
    {
        ArgumentNullException.ThrowIfNull(workspaceFolders);

        AuthToken = IdeLockFile.NewAuthToken();
        Server = new McpServer(AuthToken, presenter, fileSystem, workspaceFolders, ideName);
        Port = Server.Start();
        IdeLockFile.Write(Port, workspaceFolders, ideName, AuthToken);
    }

    public McpServer Server { get; }

    public int Port { get; }

    public string AuthToken { get; }

    public string LockFilePath => IdeLockFile.PathForPort(Port);

    /// <summary>Env vars to inject into the spawned <c>claude</c> so it connects here.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CLAUDE_CODE_SSE_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ENABLE_IDE_INTEGRATION"] = "true",
    };

    public async ValueTask DisposeAsync()
    {
        IdeLockFile.Delete(Port);
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}
