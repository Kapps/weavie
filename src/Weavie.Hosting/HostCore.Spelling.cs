using Weavie.Core.Commands;
using Weavie.Core.Spelling;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly SpellDictionary _userDictionary;
	private readonly HttpClient _spellingHttp = new();
	private readonly SpellLanguages _spellLanguages;

	private void WireSpelling(HostSession session) {
		var feature = session.Bus.Feature("spelling");
		session.ProjectDictionary.Changed += () => feature.Publish("changed", WireJson.Default.EmptyPayload, EmptyPayload.Value);
		feature.HandleConcurrent("check", MessageActivity.Droppable("Checking spelling"), WireJson.Default.SpellCheckRequest, WireJson.Default.MisspellingArray, async (message, ct) => {
			var words = await _spellLanguages.ForAsync(message.Locale, ct).ConfigureAwait(false);
			return await Task.Run(() => SpellChecker.Check(words, message.Spans, _userDictionary.Words, session.ProjectDictionary.Words, ct), ct).ConfigureAwait(false);
		});
		feature.HandleConcurrent("suggest", MessageActivity.Droppable("Suggesting spellings"), WireJson.Default.SpellSuggestRequest, WireJson.Default.StringArray, async (message, ct) => {
			if (!SpellChecker.IsWord(message.Word)) throw new ArgumentException("Provide one word for spelling suggestions.");
			var words = await _spellLanguages.ForAsync(message.Locale, ct).ConfigureAwait(false);
			return await Task.Run(() => words.Suggest(SpellChecker.Normalize(message.Word), ct).ToArray(), ct).ConfigureAwait(false);
		});
		feature.Handle("add", MessageActivity.ActionFailed("Adding a word to the dictionary"), WireJson.Default.SpellAddRequest, WireJson.Default.Boolean, (message, ct) => Task.Run(() => {
			var dictionary = message.Scope switch {
				"user" => _userDictionary,
				"project" => session.ProjectDictionary,
				_ => throw new ArgumentException("Choose the user or project dictionary."),
			};
			dictionary.Add(message.Word);
			return true;
		}, ct));
	}

	private void InvalidateSpelling() {
		_ui.Post(() => {
			foreach (var session in LoadedSessions()) {
				session.Bus.Feature("spelling").Publish("changed", WireJson.Default.EmptyPayload, EmptyPayload.Value);
			}
		});
	}

	internal sealed record SpellCheckRequest(SpellSpan[] Spans, string Locale);
	internal sealed record SpellSuggestRequest(string Word, string Locale);
	internal sealed record SpellAddRequest(string Scope, string Word);
}
