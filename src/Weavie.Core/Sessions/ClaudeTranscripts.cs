using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// Answers whether Claude Code has a stored transcript for a session id launched from a given working directory
/// — i.e. whether a <c>--resume</c> of that id will actually find its conversation. Consulted before launch so a
/// dead id (its conversation cleared, or filed under a different directory) is re-created fresh instead of
/// greeting the user with "No conversation found".
/// </summary>
public interface IClaudeTranscripts {
	/// <summary>True when a resumable transcript exists for <paramref name="sessionId"/> under <paramref name="workingDirectory"/>.</summary>
	bool Exists(string workingDirectory, string sessionId);
}

/// <summary>
/// Locates Claude Code transcripts under its projects directory. Claude files each conversation at
/// <c>&lt;projects&gt;/&lt;encoded-cwd&gt;/&lt;id&gt;.jsonl</c>, where the folder is the launch cwd with every
/// non-alphanumeric character replaced by <c>-</c>.
/// </summary>
public sealed class ClaudeTranscripts : IClaudeTranscripts {
	private readonly IFileSystem _fileSystem;
	private readonly string _projectsDirectory;

	/// <summary>Creates a locator over <paramref name="projectsDirectory"/> (Claude's <c>&lt;config&gt;/projects</c>).</summary>
	public ClaudeTranscripts(IFileSystem fileSystem, string projectsDirectory) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(projectsDirectory);
		_fileSystem = fileSystem;
		_projectsDirectory = projectsDirectory;
	}

	/// <inheritdoc/>
	public bool Exists(string workingDirectory, string sessionId) {
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		return _fileSystem.FileExists(TranscriptPath(workingDirectory, sessionId));
	}

	/// <summary>The on-disk path Claude would store <paramref name="sessionId"/>'s transcript at for <paramref name="workingDirectory"/>.</summary>
	public string TranscriptPath(string workingDirectory, string sessionId) =>
		Path.Combine(_projectsDirectory, EncodeCwd(workingDirectory), sessionId + ".jsonl");

	// Claude names a project's transcript folder after its launch cwd with every non-alphanumeric char → '-'. Trim
	// a trailing separator first (as ClaudeSessionStore.Normalize does) so `…\weavie\` and `…\weavie` don't diverge
	// into `…weavie-` vs `…weavie` — a mismatch would falsely report "no transcript" and re-create a live session.
	private static string EncodeCwd(string cwd) {
		string trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		char[] chars = new char[trimmed.Length];
		for (int i = 0; i < trimmed.Length; i++) {
			chars[i] = char.IsAsciiLetterOrDigit(trimmed[i]) ? trimmed[i] : '-';
		}

		return new string(chars);
	}
}
