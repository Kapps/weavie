namespace Weavie.Core.Diffs;

/// <summary>
/// A proposed edit, shaped like Claude Code's IDE-MCP <c>openDiff</c> tool call (old_file_path /
/// new_file_path / new_file_contents / tab_name). The sole edit feed: built from an inbound MCP request.
/// </summary>
public sealed record DiffProposal {
	/// <summary>All fields required and non-empty, except contents, which may be empty.</summary>
	public DiffProposal(string oldFilePath, string newFilePath, string newFileContents, string tabName) {
		ArgumentException.ThrowIfNullOrEmpty(oldFilePath);
		ArgumentException.ThrowIfNullOrEmpty(newFilePath);
		ArgumentNullException.ThrowIfNull(newFileContents);
		ArgumentException.ThrowIfNullOrEmpty(tabName);

		OldFilePath = oldFilePath;
		NewFilePath = newFilePath;
		NewFileContents = newFileContents;
		TabName = tabName;
	}

	/// <summary>The file Claude read; the left/original side of the diff.</summary>
	public string OldFilePath { get; }

	/// <summary>Where a kept diff is saved; the right/proposed side. Usually equal to <see cref="OldFilePath"/>.</summary>
	public string NewFilePath { get; }

	/// <summary>The full proposed contents of the new file.</summary>
	public string NewFileContents { get; }

	/// <summary>Human-facing tab label Claude supplies (e.g. "✻ [Claude Code] foo.cs").</summary>
	public string TabName { get; }
}
