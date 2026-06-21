using Weavie.Core.Diffs;
using Weavie.Core.Documents;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The edit-feed contract end to end with test fakes:
/// openDiff-shaped edit -&gt; document model -&gt; user types into it -&gt; save -&gt; in-memory FS.
/// </summary>
public sealed class ExampleFlowTests {
	private const string FilePath = "/workspace/src/greeter.cs";

	[Fact]
	public void OpenDiff_Keep_SavesProposedContentsToFileSystem() {
		// File already on disk with original contents.
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

		// Original presented on the diff's left side.
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

		// User tweaks the proposal before keeping: insert "// reviewed\n" atop the right side.
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
