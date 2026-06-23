namespace Weavie.Core.Sessions;

/// <summary>
/// A session's stable identity within its workspace — a short random token, distinct from
/// <see cref="Weavie.Core.Workspaces.WorkspaceId"/> (which identifies the folder/window).
/// </summary>
/// <param name="Value">The short token identifying the session.</param>
public readonly record struct SessionId(string Value) {
	/// <summary>Mints a new random session id (an 8-character hex token).</summary>
	public static SessionId New() => new(Guid.NewGuid().ToString("n")[..8]);
}
