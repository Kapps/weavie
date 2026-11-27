using System.Security.Cryptography;
using System.Text;
using Weavie.Core.Changes;

namespace Weavie.Core.FileSystem;

/// <summary>A text file inside a worktree that an agent cited, read once with its worktree-relative path.</summary>
public sealed record WorktreeTextFile(string Path, string Text) {
	/// <summary>The file's lines, split as the review splits them.</summary>
	public string[] Lines { get; } = LineDiff.SplitLines(Text);

	/// <summary>The content hash that names this text wherever a copy of it is kept.</summary>
	public string Hash { get; } = HashOf(Text);

	/// <summary>The SHA-256 of <paramref name="text"/>'s UTF-8 bytes, as lowercase hex.</summary>
	public static string HashOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	/// <summary>
	/// Reads <paramref name="path"/>, resolved against <paramref name="cwd"/> or <paramref name="root"/>; returns why it
	/// can't be cited (not a valid path, outside the worktree, missing, not text), or null with <paramref name="file"/> set.
	/// </summary>
	public static string? TryRead(IFileSystem fileSystem, string root, string path, string? cwd, out WorktreeTextFile file) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(root);
		file = new WorktreeTextFile(string.Empty, string.Empty);
		string absolute;
		try {
			absolute = AgentPath.Resolve(path, cwd, root);
		} catch (ArgumentException) {
			return $"'{path}' is not a valid path.";
		}
		string relative = System.IO.Path.GetRelativePath(root, absolute).Replace('\\', '/');
		if (!PathBoundary.Contains(root, absolute) || relative == ".") return $"'{path}' isn't a file inside {root}.";
		if (!fileSystem.FileExists(absolute)) return $"'{relative}' doesn't exist.";
		if (!fileSystem.TryReadAllText(absolute, out string text)) return $"'{relative}' isn't a text file.";
		file = new WorktreeTextFile(relative, text);
		return null;
	}
}
