using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SpellCheckTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "weavie-spelling-" + Guid.NewGuid());
	private static readonly HashSet<string> Empty = new(StringComparer.OrdinalIgnoreCase);

	public SpellCheckTests() {
		Directory.CreateDirectory(_directory);
	}

	[Fact]
	public void ChecksExactUtf16RangesAndUnionsDictionaries() {
		var result = SpellChecker.Check(SpellChecker.English.Value,
			[new(4, 7, "😀 This isn't misspelled, but teh is. Weavie Frobulator https://zztypo.test `zzcode` user@zzmail.test")],
			new HashSet<string>(["weavie"], StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(["Frobulator"], StringComparer.OrdinalIgnoreCase),
			CancellationToken.None);
		Assert.Equal([new Misspelling(4, 37, "teh")], result);
	}

	[Fact]
	public void DictionaryAddPersistsAndPreservesExternalAdditions() {
		string path = Path.Combine(_directory, "words");
		using var dictionary = new SpellDictionary(path, confined: true, watch: false);
		Assert.Empty(dictionary.Words);
		Assert.False(File.Exists(path));
		dictionary.Add("Weavie");
		dictionary.Add("weavie");
		File.AppendAllText(path, "Frobulator");
		dictionary.Add("Zorptastic");
		using var reloaded = new SpellDictionary(path, confined: true, watch: false);
		Assert.Equal(3, reloaded.Words.Count);
		Assert.Contains("frobulator", reloaded.Words);
		Assert.Equal(1, File.ReadAllLines(path).Count(line => line.Equals("weavie", StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void CustomWordsUnifyCanonicalAccentsAndApostrophes() {
		using var dictionary = new SpellDictionary(Path.Combine(_directory, "words"), confined: true, watch: false);
		dictionary.Add("Weávíe’s");
		dictionary.Add("Wea\u0301vi\u0301e's");
		Assert.Single(dictionary.Words);
		Assert.Empty(SpellChecker.Check(SpellChecker.English.Value,
			[new(1, 0, "Wea\u0301vi\u0301e’s Weávíe's")], dictionary.Words, Empty, CancellationToken.None));
	}

	[Fact]
	public void InvalidDictionaryAndFailedWritesAreVisible() {
		string path = Path.Combine(_directory, "words");
		File.WriteAllText(path, "not one word\n");
		using var dictionary = new SpellDictionary(path, confined: true, watch: false);
		Assert.Throws<IOException>(() => dictionary.Words);
		Assert.Throws<InvalidDataException>(() => dictionary.Add("Weavie"));
		File.Delete(path);
		Directory.CreateDirectory(path);
		Assert.Throws<UnauthorizedAccessException>(() => dictionary.Add("Weavie"));
	}

	[Fact]
	public void CancelledChecksDoNotReturnPartialResults() {
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();
		Assert.Throws<OperationCanceledException>(() => SpellChecker.Check(SpellChecker.English.Value,
			[new(1, 0, "teh teh teh")], Empty, Empty, cancelled.Token));
	}

	public void Dispose() => Directory.Delete(_directory, recursive: true);
}
