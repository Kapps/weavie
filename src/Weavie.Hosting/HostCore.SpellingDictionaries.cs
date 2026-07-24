using Weavie.Core.Spelling;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly CustomDictionary _userDictionary;

	private readonly record struct SpellDictionaryVersions(
		long UserVersion,
		long ProjectVersion);

	private void WireSpellingReactions() {
		_userDictionary.Changed += OnUserDictionaryChanged;
		_userDictionary.LoadErrorChanged += OnUserDictionaryLoadErrorChanged;
	}

	private void DetachSpellingReactions() {
		_userDictionary.Changed -= OnUserDictionaryChanged;
		_userDictionary.LoadErrorChanged -= OnUserDictionaryLoadErrorChanged;
	}

	private void WireSpellingSession(HostSession session) {
		session.ProjectDictionary.Changed += OnProjectDictionaryChanged;
		session.ProjectDictionary.LoadErrorChanged += OnProjectDictionaryLoadErrorChanged;
	}

	private void UnwireSpellingSession(HostSession session) {
		session.ProjectDictionary.Changed -= OnProjectDictionaryChanged;
		session.ProjectDictionary.LoadErrorChanged -= OnProjectDictionaryLoadErrorChanged;
	}

	private void UnwireAllSpellingSessions() {
		foreach (var session in LoadedSessions()) {
			UnwireSpellingSession(session);
		}
	}

	private void OnUserDictionaryChanged(CustomDictionary _) {
		CancelAllSpellOperations();
		_ui.Post(() => {
			if (_session is { } session) {
				PostSpellDictionaryChanged(session);
			}
		});
	}

	private void OnProjectDictionaryChanged(CustomDictionary dictionary) {
		_ui.Post(() => {
			if (SessionForProjectDictionary(dictionary) is { } session) {
				CancelSpellOperationsForSession(session);
				if (IsActiveSession(session)) {
					PostSpellDictionaryChanged(session);
				}
			}
		});
	}

	private void OnUserDictionaryLoadErrorChanged(
		CustomDictionary _,
		SpellDictionaryException? error) =>
		OnDictionaryLoadErrorChanged("user", error);

	private void OnDictionaryLoadErrorChanged(string scope, SpellDictionaryException? error) =>
		_ui.Post(() => NotifyDictionaryLoadState(scope, error));

	private void OnProjectDictionaryLoadErrorChanged(
		CustomDictionary dictionary,
		SpellDictionaryException? error) {
		_ui.Post(() => {
			if (SessionForProjectDictionary(dictionary) is { } session && IsActiveSession(session)) {
				NotifyDictionaryLoadState("project", error);
			}
		});
	}

	private HostSession? SessionForProjectDictionary(CustomDictionary dictionary) =>
		LoadedSessions().FirstOrDefault(session => ReferenceEquals(session.ProjectDictionary, dictionary));

	private void NotifyInitialSpellDictionaryErrors() {
		if (_userDictionary.LastLoadError is { } userError) {
			NotifyDictionaryLoadState("user", userError);
		}
		if (_session is { } session && session.ProjectDictionary.LastLoadError is { } projectError) {
			NotifyDictionaryLoadState("project", projectError);
		}
	}

	private void NotifyDictionaryLoadState(string scope, SpellDictionaryException? error) {
		string key = $"spell-dictionary-{scope}-malformed";
		if (error is null) {
			Notify("info", $"The {scope} spelling dictionary reloaded.", key);
			return;
		}

		Notify(
			"error",
			$"Your {scope} spelling dictionary has errors; its last valid words remain active until you fix it. {error.Message}",
			key);
	}

	private SpellDictionaryVersions CaptureSpellDictionaryVersions(HostSession session) =>
		new(_userDictionary.Revision, session.ProjectDictionary.Revision);

	private bool HasCurrentSpellDictionaryVersions(HostSession session, SpellDictionaryVersions versions) =>
		versions.UserVersion == _userDictionary.Revision
		&& versions.ProjectVersion == session.ProjectDictionary.Revision;
}
