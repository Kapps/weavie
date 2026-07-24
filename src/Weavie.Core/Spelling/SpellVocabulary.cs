using System.Collections.Frozen;
using System.Text;

namespace Weavie.Core.Spelling;

internal static class SpellVocabulary {
	private const string ResourcePrefix = "Weavie.Core.Spelling.Resources.CSpell.";
	private static readonly Lazy<FrozenSet<string>> Words = new(
		() => Load(
			"softwareTerms.txt",
			"software-tools.txt",
			"coding-compound-terms.txt",
			"webServices.txt",
			"csharp.txt",
			"typescript.txt",
			"go.txt"),
		LazyThreadSafetyMode.ExecutionAndPublication);

	internal static bool Contains(string word) => Words.Value.Contains(word);

	private static FrozenSet<string> Load(params string[] names) {
		var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var assembly = typeof(SpellVocabulary).Assembly;
		foreach (string name in names) {
			using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
				?? throw new InvalidOperationException($"Missing embedded spelling vocabulary '{name}'.");
			using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			while (reader.ReadLine() is { } line) {
				if (!line.StartsWith('#') && SpellWord.TryNormalize(line, out string word)) {
					words.Add(word);
				}
			}
		}

		return words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
	}
}
