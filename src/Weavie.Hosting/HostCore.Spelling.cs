using Weavie.Core.Spelling;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly SpellDictionary _userDictionary;

	private void WireSpelling(HostSession session) {
		var feature = session.Bus.Feature("spelling");
		session.ProjectDictionary.Changed += () => feature.Publish("changed", new { });
		feature.HandleConcurrent<SpellCheckRequest, Misspelling[]>("check", (message, ct) => Task.Run(() => SpellChecker.Check(message.Spans, _userDictionary.Words, session.ProjectDictionary.Words, ct), ct));
		feature.Handle<SpellAddRequest, bool>("add", (message, ct) => Task.Run(() => {
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
				session.Bus.Feature("spelling").Publish("changed", new { });
			}
		});
	}

	private sealed record SpellCheckRequest(SpellSpan[] Spans);
	private sealed record SpellAddRequest(string Scope, string Word);
}
