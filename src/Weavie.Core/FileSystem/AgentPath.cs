namespace Weavie.Core.FileSystem;

/// <summary>Resolves file paths an agent reports in its tool calls.</summary>
public static class AgentPath {
	/// <summary>
	/// Resolves <paramref name="path"/>: absolute as itself, relative against the tool's <paramref name="cwd"/>
	/// (where the agent ran it), or against <paramref name="root"/> when the call carries no cwd.
	/// </summary>
	public static string Resolve(string path, string? cwd, string root) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.GetFullPath(path, string.IsNullOrEmpty(cwd) ? root : cwd));
	}
}
