using System.Text;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Corrections;

/// <summary>
/// Exercises <see cref="CorrectionCorpus"/>: JSONL persistence across reload, FIFO byte eviction, the
/// per-entry ceilings (prompt/delta truncation + trailing-file drops), counted clears, and load tolerance.
/// </summary>
public sealed class CorrectionCorpusTests {
	private const string CorpusPath = "/state/corrections.jsonl";

	[Fact]
	public void Append_PersistsAcrossReload_OldestFirst() {
		var fs = new InMemoryFileSystem();
		var corpus = new CorrectionCorpus(fs, CorpusPath);
		corpus.Append(Record("first", 10));
		corpus.Append(Record("second", 10));

		var reloaded = new CorrectionCorpus(fs, CorpusPath);

		Assert.Equal(2, reloaded.Count);
		Assert.Equal(["first", "second"], reloaded.ReadAll().Select(r => r.Prompt));
	}

	[Fact]
	public void Append_PastMaxBytes_EvictsOldestWholeEntries() {
		var fs = new InMemoryFileSystem();
		var corpus = new CorrectionCorpus(fs, CorpusPath);
		// Each entry ~16 KB (two file deltas at the 8 KB per-file cap); seven of them exceed the 96 KB ring.
		for (int i = 1; i <= 7; i++) {
			corpus.Append(Record($"p{i}", 20_000));
		}

		var records = corpus.ReadAll();

		Assert.True(records.Count < 7);
		Assert.Equal($"p{8 - records.Count}", records[0].Prompt); // oldest evicted first, the rest intact in order
		Assert.Equal("p7", records[^1].Prompt);
	}

	[Fact]
	public void Append_OversizedEntry_TruncatesInsteadOfEvictingAllHistory() {
		var fs = new InMemoryFileSystem();
		var corpus = new CorrectionCorpus(fs, CorpusPath);
		corpus.Append(Record("small", 10));
		var monster = new CorrectionRecord {
			Prompt = new string('p', 10_000),
			Files = [.. Enumerable.Range(1, 10).Select(i => new CorrectionFile {
				Path = $"f{i}.cs",
				Delta = new string('d', 100_000),
			})],
		};

		corpus.Append(monster);

		var records = corpus.ReadAll();
		Assert.Equal("small", records[0].Prompt); // prior history survived the monster
		var bounded = records[1];
		Assert.True(Encoding.UTF8.GetByteCount(bounded.Prompt!) <= 2 * 1024);
		Assert.All(bounded.Files, f => Assert.True(Encoding.UTF8.GetByteCount(f.Delta) <= 8 * 1024));
		Assert.True(bounded.Files.Count < 10);
		Assert.Equal(10 - bounded.Files.Count, bounded.DroppedFiles);
	}

	[Fact]
	public void Clear_Count_RemovesOnlyThatManyOldest() {
		var fs = new InMemoryFileSystem();
		var corpus = new CorrectionCorpus(fs, CorpusPath);
		corpus.Append(Record("a", 10));
		corpus.Append(Record("b", 10));
		corpus.Append(Record("c", 10));

		corpus.Clear(2);

		Assert.Equal(1, corpus.Count);
		Assert.Equal("c", Assert.Single(corpus.ReadAll()).Prompt);
		Assert.Equal("c", Assert.Single(new CorrectionCorpus(fs, CorpusPath).ReadAll()).Prompt);
	}

	[Fact]
	public void Append_StripsControlCharacters_KeepingNewlinesAndTabs() {
		// A hostile file could smuggle a bracketed-paste terminator (ESC[201~) into its delta; the ring strips
		// every control char except \n/\t so /learn's paste can never be escaped into typed PTY input.
		var corpus = new CorrectionCorpus(new InMemoryFileSystem(), CorpusPath);
		corpus.Append(new CorrectionRecord {
			Prompt = "do \x1b[31mthings\x07",
			Files = [new CorrectionFile { Path = "a\x1b.cs", Delta = "-\x1b[201~!rm -rf /\r\n+\tsafe\n" }],
		});

		var record = Assert.Single(corpus.ReadAll());
		Assert.Equal("do [31mthings", record.Prompt);
		var file = Assert.Single(record.Files);
		Assert.Equal("a.cs", file.Path);
		Assert.Equal("-[201~!rm -rf /\n+\tsafe\n", file.Delta);
	}

	[Fact]
	public void Append_SingleFileWhoseEscapedJsonExceedsCeiling_ShrinksTheDelta() {
		// Non-ASCII escapes ~3× in JSON, so an at-cap delta can serialize past the per-entry ceiling; the line
		// must shrink rather than evict most of the ring behind it.
		var fs = new InMemoryFileSystem();
		var corpus = new CorrectionCorpus(fs, CorpusPath);

		corpus.Append(new CorrectionRecord {
			Prompt = new string('é', 4_000),
			Files = [new CorrectionFile { Path = "a.cs", Delta = new string('é', 8_000) }],
		});

		string line = fs.ReadAllText(CorpusPath).TrimEnd('\n');
		Assert.True(Encoding.UTF8.GetByteCount(line) <= CorrectionCorpus.MaxBytes / 4);
	}

	[Fact]
	public void Load_MalformedLine_IsDropped() {
		var fs = new InMemoryFileSystem();
		new CorrectionCorpus(fs, CorpusPath).Append(Record("valid", 10));
		fs.WriteAllText(CorpusPath, "not json at all\n" + fs.ReadAllText(CorpusPath));

		var corpus = new CorrectionCorpus(fs, CorpusPath);

		Assert.Equal("valid", Assert.Single(corpus.ReadAll()).Prompt);
	}

	[Fact]
	public void AppendAndClear_RaiseChanged() {
		var corpus = new CorrectionCorpus(new InMemoryFileSystem(), CorpusPath);
		int changes = 0;
		corpus.Changed += () => changes++;

		corpus.Append(Record("a", 10));
		corpus.Clear(1);
		corpus.Clear(5); // nothing left — no change, no event

		Assert.Equal(2, changes);
	}

	private static CorrectionRecord Record(string prompt, int deltaChars) => new() {
		Prompt = prompt,
		Files = [
			new CorrectionFile { Path = "a.cs", Delta = new string('x', deltaChars) },
			new CorrectionFile { Path = "b.cs", Delta = new string('y', deltaChars) },
		],
	};
}
