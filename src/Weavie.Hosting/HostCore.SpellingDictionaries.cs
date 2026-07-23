using System.Collections.Concurrent;
using Weavie.Core.Spelling;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly CustomDictionary _userDictionary;
	private readonly ConcurrentDictionary<HostSession, ProjectDictionaryHandlers> _projectDictionaryHandlers = new();
	private Action? _onUserDictionaryChanged;
	private Action<SpellDictionaryException?>? _onUserDictionaryLoadErrorChanged;
	private long _userDictionaryVersion;

	private sealed class ProjectDictionaryHandlers {
		public ProjectDictionaryHandlers(Action changed, Action<SpellDictionaryException?> loadErrorChanged) {
			Changed = changed;
			LoadErrorChanged = loadErrorChanged;
		}

		public Action Changed { get; }
		public Action<SpellDictionaryException?> LoadErrorChanged { get; }
		public long Version;
	}

	private readonly record struct SpellDictionaryVersions(
		long UserVersion,
		ProjectDictionaryHandlers Project,
		long ProjectVersion);

	private void WireSpellingReactions() {
		_onUserDictionaryChanged = OnUserDictionaryChanged;
		_userDictionary.Changed += _onUserDictionaryChanged;
		_onUserDictionaryLoadErrorChanged = error => OnDictionaryLoadErrorChanged("user", error);
		_userDictionary.LoadErrorChanged += _onUserDictionaryLoadErrorChanged;
	}

	private void DetachSpellingReactions() {
		if (_onUserDictionaryChanged is not null) {
			_userDictionary.Changed -= _onUserDictionaryChanged;
			_onUserDictionaryChanged = null;
		}

		if (_onUserDictionaryLoadErrorChanged is not null) {
			_userDictionary.LoadErrorChanged -= _onUserDictionaryLoadErrorChanged;
			_onUserDictionaryLoadErrorChanged = null;
		}
	}

	private void WireSpellingSession(HostSession session) {
		void Changed() => OnProjectDictionaryChanged(session);
		void LoadErrorChanged(SpellDictionaryException? error) => OnProjectDictionaryLoadErrorChanged(session, error);

		var changed = (Action)Changed;
		var loadErrorChanged = (Action<SpellDictionaryException?>)LoadErrorChanged;
		if (!_projectDictionaryHandlers.TryAdd(session, new ProjectDictionaryHandlers(changed, loadErrorChanged))) {
			throw new InvalidOperationException($"Spelling is already wired for session '{session.Id}'.");
		}

		session.ProjectDictionary.Changed += changed;
		session.ProjectDictionary.LoadErrorChanged += loadErrorChanged;
	}

	private void UnwireSpellingSession(HostSession session) {
		if (!_projectDictionaryHandlers.TryRemove(session, out var handlers)) {
			return;
		}

		CancelSpellRequestsForSession(session);
		session.ProjectDictionary.Changed -= handlers.Changed;
		session.ProjectDictionary.LoadErrorChanged -= handlers.LoadErrorChanged;
	}

	private void UnwireAllSpellingSessions() {
		foreach (var session in _projectDictionaryHandlers.Keys.ToArray()) {
			UnwireSpellingSession(session);
		}
	}

	private void OnUserDictionaryChanged() {
		Interlocked.Increment(ref _userDictionaryVersion);
		CancelAllSpellRequests();
		_ui.Post(() => {
			if (_session is { } session) {
				DispatchEditorProjection(session, () => PostSpellDictionaryChanged(session, "user"));
			}
		});
	}

	private void OnProjectDictionaryChanged(HostSession session) {
		if (!_projectDictionaryHandlers.TryGetValue(session, out var handlers)) {
			return;
		}

		Interlocked.Increment(ref handlers.Version);
		CancelSpellRequestsForSession(session);
		_ui.Post(() => {
			if (IsCurrentProjectDictionary(session, handlers)) {
				DispatchEditorProjection(session, () => PostSpellDictionaryChanged(session, "project"));
			}
		});
	}

	private void OnDictionaryLoadErrorChanged(string scope, SpellDictionaryException? error) =>
		_ui.Post(() => NotifyDictionaryLoadState(scope, error));

	private void OnProjectDictionaryLoadErrorChanged(HostSession session, SpellDictionaryException? error) {
		_ui.Post(() => {
			if (IsActiveSession(session)) {
				NotifyDictionaryLoadState("project", error);
			}
		});
	}

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

	private bool TryCaptureSpellDictionaryVersions(HostSession session, out SpellDictionaryVersions versions) {
		if (_projectDictionaryHandlers.TryGetValue(session, out var project)) {
			versions = new SpellDictionaryVersions(
				Volatile.Read(ref _userDictionaryVersion),
				project,
				Volatile.Read(ref project.Version));
			return true;
		}

		versions = default;
		return false;
	}

	private bool HasCurrentSpellDictionaryVersions(HostSession session, SpellDictionaryVersions versions) =>
		versions.UserVersion == Volatile.Read(ref _userDictionaryVersion)
		&& _projectDictionaryHandlers.TryGetValue(session, out var project)
		&& ReferenceEquals(project, versions.Project)
		&& versions.ProjectVersion == Volatile.Read(ref project.Version);

	private bool IsCurrentProjectDictionary(HostSession session, ProjectDictionaryHandlers handlers) =>
		_projectDictionaryHandlers.TryGetValue(session, out var current) && ReferenceEquals(current, handlers);
}
