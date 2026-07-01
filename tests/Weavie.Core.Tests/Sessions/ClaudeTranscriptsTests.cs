using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeTranscripts"/>: it reports a session resumable only when Claude's transcript for
/// that id actually exists under the launch cwd's project folder, and it names that folder the way Claude does
/// (every non-alphanumeric character in the cwd replaced by <c>-</c>).
/// </summary>
public sealed class ClaudeTranscriptsTests {
	private const string ProjectsDir = "/claude/projects";
	private const string Cwd = @"C:\Users\me\src\weavie";
	private const string Id = "4b6b3953-8cfe-4b7b-a8de-5681a5be671b";

	[Fact]
	public void Exists_WhenTranscriptPresent_ReturnsTrue() {
		var fs = new InMemoryFileSystem();
		var transcripts = new ClaudeTranscripts(fs, ProjectsDir);
		fs.WriteAllText(transcripts.TranscriptPath(Cwd, Id), "{}");

		Assert.True(transcripts.Exists(Cwd, Id));
	}

	[Fact]
	public void Exists_WhenTranscriptAbsent_ReturnsFalse() {
		var transcripts = new ClaudeTranscripts(new InMemoryFileSystem(), ProjectsDir);

		Assert.False(transcripts.Exists(Cwd, Id));
	}

	[Fact]
	public void Exists_WhenTranscriptFiledUnderADifferentCwd_ReturnsFalse() {
		// The exact reported bug: the id's transcript exists, but under another directory's project folder, so a
		// resume launched from Cwd can't find it.
		var fs = new InMemoryFileSystem();
		var transcripts = new ClaudeTranscripts(fs, ProjectsDir);
		fs.WriteAllText(transcripts.TranscriptPath(@"C:\Users\me\src\weavie\.claude\worktrees\other", Id), "{}");

		Assert.False(transcripts.Exists(Cwd, Id));
	}

	[Fact]
	public void TranscriptPath_EncodesCwd_ReplacingEveryNonAlphanumericWithDash() {
		var transcripts = new ClaudeTranscripts(new InMemoryFileSystem(), ProjectsDir);

		Assert.Contains("C--Users-me-src-weavie", transcripts.TranscriptPath(Cwd, Id));
	}

	[Fact]
	public void Exists_IgnoresTrailingSeparatorOnCwd() {
		// A cwd with a trailing separator must resolve to the same folder as one without — otherwise it encodes to
		// `…weavie-` and falsely misses the transcript, re-creating a live session.
		var fs = new InMemoryFileSystem();
		var transcripts = new ClaudeTranscripts(fs, ProjectsDir);
		fs.WriteAllText(transcripts.TranscriptPath(Cwd, Id), "{}");

		Assert.True(transcripts.Exists(Cwd + Path.DirectorySeparatorChar, Id));
	}
}
