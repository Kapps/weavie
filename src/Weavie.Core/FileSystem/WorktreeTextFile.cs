using Weavie.Core.Changes;

namespace Weavie.Core.FileSystem;

/// <summary>A text file inside a worktree that an agent cited, read once with its worktree-relative path.</summary>
public sealed record WorktreeTextFile(string Path, string Text) {
	/// <summary>The file's lines, split as the review splits them.</summary>
	public string[] Lines { get; } = LineDiff.SplitLines(Text);

	/// <summary>The content hash that names this text wherever a copy of it is kept.</summary>
	public string Hash => TextRevision.Of(Text);

	/// <summary>Why lines <paramref name="from"/> to <paramref name="to"/> can't be cited, or null when the file has them.</summary>
	public string? LinesProblem(int from, int to) => from >= 1 && to >= from && to <= Lines.Length
		? null
		: $"{(from == to ? $"Line {from} is" : $"Lines {from}-{to} are")} outside '{Path}', which has {Lines.Length} line(s).";

	/// <summary>
	/// Reads <paramref name="path"/>, resolved against <paramref name="cwd"/> or <paramref name="root"/>; returns why it
	/// can't be cited (not a valid path, outside the worktree, missing, not text), or null with <paramref name="file"/> set.
	/// </summary>
	public static string? TryRead(IFileSystem fileSystem, string root, string path, string? cwd, out WorktreeTextFile file) =>
		TryRead(fileSystem, root, path, cwd, long.MaxValue, out file);

	/// <summary>As <see cref="TryRead(IFileSystem, string, string, string?, out WorktreeTextFile)"/>, refusing a file over <paramref name="maxBytes"/> before reading it.</summary>
	public static string? TryRead(IFileSystem fileSystem, string root, string path, string? cwd, long maxBytes, out WorktreeTextFile file) {
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
		if (fileSystem.TryGetStat(absolute, out var stat) && stat.Size > maxBytes) {
			return $"'{relative}' is {stat.Size:N0} bytes; a cited file is at most {maxBytes:N0}.";
		}
		if (!fileSystem.TryReadAllText(absolute, out string text)) return $"'{relative}' isn't a text file.";
		file = new WorktreeTextFile(relative, text);
		return null;
	}
}
