using System.Collections.Frozen;
using System.Text;

namespace Weavie.Core.Spelling;

internal static class SpellVocabulary {
	private const string ResourcePrefix = "Weavie.Core.Spelling.Resources.CSpell.";
	private static readonly Lazy<FrozenSet<string>> Common = new(
		() => Load("softwareTerms.txt", "software-tools.txt", "coding-compound-terms.txt", "webServices.txt"),
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<FrozenSet<string>> CSharp = new(
		() => Load("csharp.txt"), LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<FrozenSet<string>> TypeScript = new(
		() => Load("typescript.txt"), LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly Lazy<FrozenSet<string>> Go = new(
		() => Load("go.txt"), LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly FrozenSet<string> Empty = Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	internal static bool Contains(string languageId, string word) =>
		Common.Value.Contains(word) || LanguageWords(languageId).Contains(word);

	private static FrozenSet<string> LanguageWords(string languageId) => languageId.ToLowerInvariant() switch {
		"csharp" or "cs" => CSharp.Value,
		"typescript" or "javascript" or "typescriptreact" or "javascriptreact" => TypeScript.Value,
		"go" => Go.Value,
		_ => Empty,
	};

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
