using Weavie.Core.Commands;
using Weavie.Core.Spelling;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly SpellDictionary _userDictionary;
	private readonly HttpClient _spellingHttp = new();
	private readonly SpellLanguages _spellLanguages;

	private void WireSpelling(HostSession session) {
		session.Commands.RegisterHandler(CoreCommands.SpellSetLocale, _spellLanguages.SetLocaleAsync);
		var feature = session.Bus.Feature("spelling");
		session.ProjectDictionary.Changed += () => feature.Publish("changed", WireJson.Default.EmptyPayload, EmptyPayload.Value);
		feature.HandleConcurrent("check", WireJson.Default.SpellCheckRequest, WireJson.Default.MisspellingArray, (message, ct) => Task.Run(() => SpellChecker.Check(_spellLanguages.Current, message.Spans, _userDictionary.Words, session.ProjectDictionary.Words, ct), ct));
		feature.HandleConcurrent("suggest", WireJson.Default.SpellSuggestRequest, WireJson.Default.StringArray, (message, ct) => Task.Run(() => {
			if (!SpellChecker.IsWord(message.Word)) throw new ArgumentException("Provide one word for spelling suggestions.");
			return _spellLanguages.Current.Suggest(SpellChecker.Normalize(message.Word), ct).ToArray();
		}, ct));
		feature.Handle("add", WireJson.Default.SpellAddRequest, WireJson.Default.Boolean, (message, ct) => Task.Run(() => {
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

	internal sealed record SpellCheckRequest(SpellSpan[] Spans);
	internal sealed record SpellSuggestRequest(string Word);
	internal sealed record SpellAddRequest(string Scope, string Word);
}
