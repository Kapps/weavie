using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Spelling;

namespace Weavie.Hosting;

// The editor owns manual-edit provenance; this host projection routes its resulting lines to the Core checker.
public sealed partial class HostCore {
	private readonly SpellCatalog _spellCatalog;
	private long _spellSettingsVersion;

	private void PushSpellSettingsToWeb() =>
		_bridge.PostToWeb(SpellSettings.BuildJson(_settings, "spell-settings"));

	private void HandleSpellCheck(JsonElement root) {
		if (EditorMessageTarget(root, "spell-check") is not { } session) {
			return;
		}

		if (!TryReadSpellCheckRequest(root, out var request, out string error)) {
			PostSpellCheckError(session, root, error);
			return;
		}

		string locale = _settings.RequireString(SpellSettings.Locale);
		if (!_settings.RequireBool(SpellSettings.Enabled)) {
			PostSpellCheckResult(session, root, request, locale, [], error: null);
			return;
		}

		var key = new SpellRequestKey(session.Id, "check", request.RequestId);
		var cancellation = BeginSpellRequest(key);
		long settingsVersion = Volatile.Read(ref _spellSettingsVersion);
		if (!TryCaptureSpellDictionaryVersions(session, out var dictionaryVersions)) {
			EndSpellRequest(key, cancellation);
			return;
		}

		_ = CheckSpellAsync(session, root, request, locale, settingsVersion, dictionaryVersions, key, cancellation);
	}

	private async Task CheckSpellAsync(
		HostSession session,
		JsonElement root,
		SpellCheckRequest request,
		string locale,
		long settingsVersion,
		SpellDictionaryVersions dictionaryVersions,
		SpellRequestKey key,
		CancellationTokenSource cancellation) {
		try {
			var results = await Task.Run(
				() => CheckLines(session.SpellChecker, request, locale, cancellation.Token), cancellation.Token).ConfigureAwait(false);
			CompleteSpellRequest(key, cancellation, () => {
				if (settingsVersion != Volatile.Read(ref _spellSettingsVersion)
					|| !HasCurrentSpellDictionaryVersions(session, dictionaryVersions)
					|| !IsCurrentSpellTarget(session, root, "spell-check")) {
					return;
				}

				PostSpellCheckResult(session, root, request, locale, results, error: null);
			});
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
			EndSpellRequest(key, cancellation);
		} catch (Exception ex) {
			CompleteSpellRequest(key, cancellation, () => {
				if (settingsVersion == Volatile.Read(ref _spellSettingsVersion)
					&& HasCurrentSpellDictionaryVersions(session, dictionaryVersions)
					&& IsCurrentSpellTarget(session, root, "spell-check")) {
					PostSpellCheckResult(session, root, request, locale, [], ex.Message);
				}
			});
		}
	}

	private static IReadOnlyList<SpellCheckLineResult> CheckLines(
		SpellChecker checker,
		SpellCheckRequest request,
		string locale,
		CancellationToken cancellationToken) {
		var results = new List<SpellCheckLineResult>(request.Lines.Count);
		foreach (var line in request.Lines) {
			cancellationToken.ThrowIfCancellationRequested();
			results.Add(new SpellCheckLineResult(
				line.AnchorId,
				checker.Check(line.Text, request.LanguageId, locale, cancellationToken)));
		}

		return results;
	}

	private void HandleSpellSuggest(JsonElement root) {
		if (EditorMessageTarget(root, "spell-suggest") is not { } session) {
			return;
		}

		if (!TryReadSpellSuggestRequest(root, out var request, out string error)) {
			PostSpellSuggestError(session, root, error);
			return;
		}

		string locale = _settings.RequireString(SpellSettings.Locale);
		var key = new SpellRequestKey(session.Id, "suggest", request.RequestId);
		var cancellation = BeginSpellRequest(key);
		long settingsVersion = Volatile.Read(ref _spellSettingsVersion);
		if (!TryCaptureSpellDictionaryVersions(session, out var dictionaryVersions)) {
			EndSpellRequest(key, cancellation);
			return;
		}

		_ = SuggestSpellAsync(session, root, request, locale, settingsVersion, dictionaryVersions, key, cancellation);
	}

	private async Task SuggestSpellAsync(
		HostSession session,
		JsonElement root,
		SpellSuggestRequest request,
		string locale,
		long settingsVersion,
		SpellDictionaryVersions dictionaryVersions,
		SpellRequestKey key,
		CancellationTokenSource cancellation) {
		try {
			var suggestions = await Task.Run(
				() => session.SpellChecker.Suggest(request.Word, locale, cancellation.Token), cancellation.Token).ConfigureAwait(false);
			CompleteSpellRequest(key, cancellation, () => {
				if (settingsVersion == Volatile.Read(ref _spellSettingsVersion)
					&& HasCurrentSpellDictionaryVersions(session, dictionaryVersions)
					&& IsCurrentSpellTarget(session, root, "spell-suggest")) {
					PostSpellSuggestResult(session, root, request, locale, suggestions, error: null);
				}
			});
		} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
			EndSpellRequest(key, cancellation);
		} catch (Exception ex) {
			CompleteSpellRequest(key, cancellation, () => {
				if (settingsVersion == Volatile.Read(ref _spellSettingsVersion)
					&& HasCurrentSpellDictionaryVersions(session, dictionaryVersions)
					&& IsCurrentSpellTarget(session, root, "spell-suggest")) {
					PostSpellSuggestResult(session, root, request, locale, [], ex.Message);
				}
			});
		}
	}

	private void HandleSpellAddWord(JsonElement root) {
		if (EditorMessageTarget(root, "spell-add-word") is not { } session) {
			return;
		}

		if (!TryReadSpellAddWordRequest(root, out var request, out string error)) {
			PostSpellAddWordError(session, root, error);
			return;
		}

		_ = AddSpellWordAsync(session, root, request);
	}

	private void HandleSpellRestore(JsonElement root) {
		if (EditorMessageTarget(root, "spell-restore") is not { } session
			|| !TryReadSpellRestoreRequest(root, out var request, out _)) {
			return;
		}

		if (!session.FileProvider.Allows(request.Path)) {
			Log("[weavie] spell-restore refused outside file scope");
			return;
		}

		var snapshot = session.AuthoredLines.Snapshot(request.Path);
		PostSpellRestoreResult(session, root, request, snapshot);
	}

	private async Task AddSpellWordAsync(HostSession session, JsonElement root, SpellAddWordRequest request) {
		try {
			await Task.Run(() => DictionaryFor(session, request.Scope).Add(request.Word)).ConfigureAwait(false);
			_ui.Post(() => {
				if (IsCurrentSpellTarget(session, root, "spell-add-word")) {
					PostSpellAddWordResult(session, root, request, ok: true, error: null);
				}
			});
		} catch (Exception ex) {
			_ui.Post(() => {
				if (IsCurrentSpellTarget(session, root, "spell-add-word")) {
					PostSpellAddWordResult(session, root, request, ok: false, ex.Message);
				}
			});
		}
	}

	private CustomDictionary DictionaryFor(HostSession session, string scope) => scope switch {
		"project" => session.ProjectDictionary,
		"user" => _userDictionary,
		_ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown spelling dictionary scope."),
	};

	private bool IsCurrentSpellTarget(HostSession session, JsonElement root, string kind) =>
		ReferenceEquals(EditorMessageTarget(root, kind), session);
}
