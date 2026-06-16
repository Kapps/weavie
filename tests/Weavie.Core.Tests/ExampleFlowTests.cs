using Weavie.Core.Diffs;
using Weavie.Core.Documents;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The spine (Headless &amp; Testing: "the example flow = the first test"):
/// an openDiff-shaped edit -&gt; document model -&gt; (user types into it) -&gt; save -&gt; in-memory FS.
/// This is the whole edit-feed contract exercised end to end with the test fakes.
/// </summary>
public sealed class ExampleFlowTests {
	private const string FilePath = "/workspace/src/greeter.cs";

	[Fact]
	public void OpenDiff_Keep_SavesProposedContentsToFileSystem() {
		// The file already exists on disk with original contents.
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(FilePath, "public static string Greet() => \"hi\";\n");
		var factory = new InMemoryDocumentModelFactory(fs);

		// Claude proposes a rewrite via openDiff.
		var proposal = new DiffProposal(
			oldFilePath: FilePath,
			newFilePath: FilePath,
			newFileContents: "public static string Greet() => \"hello\";\n",
			tabName: "✻ [Claude Code] greeter.cs");

		var session = DiffSession.Open(proposal, fs, factory);

		// The original is presented on the left for the diff.
		Assert.Equal("public static string Greet() => \"hi\";\n", session.OriginalContents);

		var outcome = session.Keep();

		Assert.Equal(DiffResult.Kept, outcome.Result);
		Assert.Equal("public static string Greet() => \"hello\";\n", outcome.FinalContents);
		Assert.Equal("public static string Greet() => \"hello\";\n", fs.ReadAllText(FilePath));
	}

	[Fact]
	public void OpenDiff_UserTypesIntoProposedDiff_ThenKeep_SavesEditedContents() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(FilePath, "// original\n");
		var factory = new InMemoryDocumentModelFactory(fs);

		var proposal = new DiffProposal(
			oldFilePath: FilePath,
			newFilePath: FilePath,
			newFileContents: "// Claude's edit\nvar x = 1;\n",
			tabName: "✻ [Claude Code] greeter.cs");

		var session = DiffSession.Open(proposal, fs, factory);

		// The user tweaks Claude's proposed edit in the diff before keeping:
		// insert "// reviewed\n" at the top of the proposed (right) side.
		session.ProposedDocument.ApplyEdit(TextEdit.Insert(Position.Start, "// reviewed\n"));
		Assert.True(session.ProposedDocument.IsDirty);

		var outcome = session.Keep();

		const string expected = "// reviewed\n// Claude's edit\nvar x = 1;\n";
		Assert.Equal(DiffResult.Kept, outcome.Result);
		Assert.Equal(expected, outcome.FinalContents);
		Assert.Equal(expected, fs.ReadAllText(FilePath));
	}

	[Fact]
	public void OpenDiff_Reject_LeavesFileSystemUntouched() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(FilePath, "// keep me\n");
		var factory = new InMemoryDocumentModelFactory(fs);

		var proposal = new DiffProposal(FilePath, FilePath, "// unwanted\n", "tab");
		var session = DiffSession.Open(proposal, fs, factory);

		var outcome = session.Reject();

		Assert.Equal(DiffResult.Rejected, outcome.Result);
		Assert.Null(outcome.FinalContents);
		Assert.Equal("// keep me\n", fs.ReadAllText(FilePath));
	}

	[Fact]
	public void OpenDiff_ForNewFile_KeepCreatesItOnTheFileSystem() {
		var fs = new InMemoryFileSystem();
		var factory = new InMemoryDocumentModelFactory(fs);
		const string newPath = "/workspace/src/created.cs";

		var proposal = new DiffProposal(newPath, newPath, "namespace N;\n", "tab");
		var session = DiffSession.Open(proposal, fs, factory);

		Assert.Equal(string.Empty, session.OriginalContents);
		session.Keep();

		Assert.True(fs.FileExists(newPath));
		Assert.Equal("namespace N;\n", fs.ReadAllText(newPath));
	}

	[Fact]
	public void DiffSession_ResolvingTwice_Throws() {
		var fs = new InMemoryFileSystem();
		var factory = new InMemoryDocumentModelFactory(fs);
		var proposal = new DiffProposal("/f", "/f", "x", "tab");
		var session = DiffSession.Open(proposal, fs, factory);

		session.Keep();

		Assert.Throws<InvalidOperationException>(() => session.Reject());
		Assert.Throws<InvalidOperationException>(() => session.Keep());
	}
}
