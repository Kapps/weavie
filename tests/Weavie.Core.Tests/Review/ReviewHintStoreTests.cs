using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Weavie.Core.Review.Hints;
using Xunit;

namespace Weavie.Core.Tests.Review;

public sealed class ReviewHintStoreTests {
	private readonly string _root = Path.Combine(Path.GetTempPath(), "weavie-hints-repo");
	private readonly InMemoryFileSystem _fs = new();
	private readonly MemoryReviewPersistence _persistence = new();
	private readonly List<IReadOnlyList<ReviewHint>> _published = [];
	private readonly List<string> _rejected = [];

	public ReviewHintStoreTests() {
		_fs.WriteAllText(Path.Combine(_root, "src", "a.cs"), "one\ntwo\nthree\n");
		_fs.WriteAllText(Path.Combine(_root, "b.cs"), "red\nblue\n");
	}

	[Fact]
	public void FirstHintOfANewTurnReplacesOlderHints() {
		var store = Store();
		Assert.Empty(store.Add([Finding("src/a.cs", 2)], null));
		Assert.Empty(store.Add([Finding("b.cs", 1)], null));
		Assert.Equal(["src/a.cs", "b.cs"], store.Hints.Select(hint => hint.Path));

		store.Observe(new AgentPromptSubmitted(null, "fix #2"));
		store.Observe(new AgentTurnStopped(false));
		Assert.Equal(2, store.Hints.Count);

		store.Observe(new AgentPromptSubmitted(null, "review again"));
		store.Add([Finding("b.cs", 2)], null);
		var hint = Assert.Single(store.Hints);
		Assert.Equal(("b.cs", 2, "blue"), (hint.Path, hint.Line, hint.Quote));
	}

	[Fact]
	public void SideConversationPromptsDoNotStartATurn() {
		var store = Store();
		store.Add([Finding("b.cs", 1)], null);

		store.Observe(new AgentConversationEvent("side", new AgentPromptSubmitted(null, "aside")));
		store.Add([Finding("b.cs", 2)], null);

		Assert.Equal(2, store.Hints.Count);
	}

	[Fact]
	public void ReportedFindingsResolveAgainstTheToolCwd() {
		var store = Store();

		store.Observe(new AgentReviewFindingsReported([Finding("a.cs", 3)], Path.Combine(_root, "src")));

		Assert.Equal(("src/a.cs", "three"), (Assert.Single(store.Hints).Path, store.Hints[0].Quote));
		Assert.Empty(_rejected);
	}

	[Theory]
	[InlineData("../outside.cs", 1, "isn't a file inside")]
	[InlineData("missing.cs", 1, "doesn't exist")]
	[InlineData("b.cs", 4, "Line 4 is outside")]
	[InlineData("b.cs", 0, "Line 0 is outside")]
	public void InvalidFindingsAreRejectedLoudly(string path, int line, string error) {
		var store = Store();

		store.Observe(new AgentReviewFindingsReported([Finding(path, line), Finding("b.cs", 1)], null));

		Assert.Contains(error, Assert.Single(_rejected), StringComparison.Ordinal);
		Assert.Equal("b.cs", Assert.Single(store.Hints).Path);
	}

	[Fact]
	public void UnreadableFindingsAreSurfaced() {
		Store().Observe(new AgentReviewFindingsUnreadable("bad payload"));

		Assert.Equal(["bad payload"], _rejected);
	}

	[Fact]
	public void DismissAndClearDropUnreferencedSources() {
		var store = Store();
		store.Add([Finding("b.cs", 1), Finding("b.cs", 2), Finding("src/a.cs", 1)], null);
		string hash = store.Hints[0].SourceHash;

		store.Dismiss(store.Hints[0].Id);
		Assert.Equal("red\nblue\n", store.Source("b.cs", hash));
		store.Dismiss(store.Hints[0].Id);
		Assert.Throws<InvalidOperationException>(() => store.Source("b.cs", hash));
		Assert.DoesNotContain(_persistence.Read().Keys, key => key == "source:" + hash);

		store.Clear();
		Assert.Empty(store.Hints);
		Assert.Equal(["metadata"], _persistence.Read().Keys);
		Assert.Empty(_published[^1]);
	}

	[Fact]
	public void HintsAnchorToTheTextTheyWereCapturedAgainst() {
		var store = Store();
		store.Add([Finding("b.cs", 2)], null);
		_fs.WriteAllText(Path.Combine(_root, "b.cs"), "green\nred\nblue\n");

		Assert.Equal("red\nblue\n", store.Source("b.cs", store.Hints[0].SourceHash));
	}

	[Fact]
	public void HintsSurviveAReloadThroughTheDatabase() {
		using var dir = new TempDirectory("weavie-hints-db");
		var store = Store(new ReviewPersistence(dir.Combine("hints.db")));
		store.Add([Finding("b.cs", 2), Finding("src/a.cs", 1)], null);
		store.Dismiss(store.Hints[1].Id);

		var reloaded = Store(new ReviewPersistence(dir.Combine("hints.db")));

		Assert.Equal(store.Hints, reloaded.Hints);
		Assert.Equal(reloaded.Hints, _published[^1]);
		reloaded.Add([Finding("src/a.cs", 3)], null);
		Assert.Equal([1L, 3L], reloaded.Hints.Select(hint => hint.Id));
	}

	[Fact]
	public void CorruptRecordsFailTheRestore() {
		_persistence.Save(new Dictionary<string, string?> { ["metadata"] = """{"NextId":1,"Batch":0}""", ["stray"] = "x" });

		Assert.Throws<IOException>(() => Store());
	}

	private ReviewHintStore Store() => Store(_persistence);

	private ReviewHintStore Store(IReviewPersistence persistence) =>
		new(_fs, _root, persistence, "Claude", _published.Add, _rejected.Add);

	private static ReviewFinding Finding(string path, int line) =>
		ReviewFinding.FromText(path, line, ReviewHintSeverity.Bug, "Summary\nDetails");
}
