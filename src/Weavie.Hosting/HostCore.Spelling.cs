using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Spelling;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void PushSpellSettingsToWeb() =>
		_bridge.PostToWeb(SpellSettings.BuildJson(_settings, "spell-settings"));

	private void HandleSpellDocumentChanged(JsonElement root) {
		if (EditorMessageTarget(root, "spell-document-changed") is not { } session) {
			return;
		}

		if (!TryReadSpellDocumentChanged(root, out var request, out string error)) {
			Log($"[weavie] ignored malformed spell document update: {error}");
			return;
		}

		if (!TryCanonicalSpellPath(request.Path, out string path)) {
			Log("[weavie] ignored spell document update with an invalid path");
			return;
		}

		request = request with { Path = path };
		if (!ReferenceEquals(ResolveFsSession(request.Path), session)) {
			Log("[weavie] ignored spell document update outside its editor session");
			return;
		}

		StartSpellDocumentCheck(session, request, ProjectionFor(session, root));
	}

	private void StartSpellDocumentCheck(
		HostSession session,
		SpellDocumentChangedRequest request,
		SpellProjection projection) {
		string locale = _settings.RequireString(SpellSettings.Locale);
		if (!_settings.RequireBool(SpellSettings.Enabled)) {
			return;
		}

		var dictionaryVersions = CaptureSpellDictionaryVersions(session);
		var operation = _spellOperationRegistry.Begin(session, "document", request.Path);
		if (operation is null) {
			return;
		}

		_ = CheckSpellDocumentAsync(
			session,
			request,
			projection,
			locale,
			dictionaryVersions,
			operation);
	}

	private async Task CheckSpellDocumentAsync(
		HostSession session,
		SpellDocumentChangedRequest request,
		SpellProjection projection,
		string locale,
		SpellDictionaryVersions dictionaryVersions,
		SpellOperationRegistry.Operation operation) {
		var cancellationToken = operation.Token;
		try {
			var diagnostics = await Task.Run(
				() => CheckDocument(session.SpellChecker, request.Content, locale, cancellationToken),
				cancellationToken).ConfigureAwait(false);
			if (!_spellOperationRegistry.IsCurrent(operation) || cancellationToken.IsCancellationRequested) {
				return;
			}

			_ui.Post(() => {
				if (!cancellationToken.IsCancellationRequested) {
					PublishSpellDiagnostics(
						session,
						request,
						projection,
						locale,
						dictionaryVersions,
						diagnostics,
						error: null);
				}
			});
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			// Superseded document content has no result to publish.
		} catch (Exception ex) {
			if (_spellOperationRegistry.IsCurrent(operation)) {
				_ui.Post(() => {
					if (!cancellationToken.IsCancellationRequested) {
						PublishSpellDiagnostics(
							session,
							request,
							projection,
							locale,
							dictionaryVersions,
							[],
							ex.Message);
					}
				});
			}
		} finally {
			_spellOperationRegistry.End(operation);
		}
	}

	private static IReadOnlyList<SpellDiagnostic> CheckDocument(
		SpellChecker checker,
		string content,
		string locale,
		CancellationToken cancellationToken) {
		var diagnostics = new List<SpellDiagnostic>();
		using var reader = new StringReader(content);
		int lineNumber = 1;
		while (reader.ReadLine() is { } line) {
			cancellationToken.ThrowIfCancellationRequested();
			foreach (var issue in checker.Check(line, locale, cancellationToken)) {
				diagnostics.Add(new SpellDiagnostic(
					lineNumber,
					issue.Start + 1,
					issue.Start + issue.Length + 1,
					issue.Word));
			}
			lineNumber++;
		}

		return diagnostics;
	}

	private static bool TryCanonicalSpellPath(string path, out string canonicalPath) {
		try {
			if (Path.IsPathFullyQualified(path)) {
				canonicalPath = WorkspacePaths.CanonicalFsPath(Path.GetFullPath(path));
				return true;
			}
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
		}

		canonicalPath = string.Empty;
		return false;
	}

	private void PublishSpellDiagnostics(
		HostSession session,
		SpellDocumentChangedRequest request,
		SpellProjection projection,
		string locale,
		SpellDictionaryVersions dictionaryVersions,
		IReadOnlyList<SpellDiagnostic> diagnostics,
		string? error) {
		if (!SpellDiagnosticsAreCurrent(session, projection, locale, dictionaryVersions)) {
			return;
		}

		DispatchEditorProjection(session, () => {
			if (SpellDiagnosticsAreCurrent(session, projection, locale, dictionaryVersions)) {
				PostSpellDiagnostics(request, projection, locale, diagnostics, error);
			}
		});
	}

	private bool SpellDiagnosticsAreCurrent(
		HostSession session,
		SpellProjection projection,
		string locale,
		SpellDictionaryVersions dictionaryVersions) =>
		SpellSettingsMatch(locale)
		&& HasCurrentSpellProjection(session, projection)
		&& HasCurrentSpellDictionaryVersions(session, dictionaryVersions);

	private bool SpellSettingsMatch(string locale) =>
		_settings.RequireBool(SpellSettings.Enabled)
		&& string.Equals(_settings.RequireString(SpellSettings.Locale), locale, StringComparison.Ordinal);

	private void HandleSpellSuggest(JsonElement root) {
		if (EditorMessageTarget(root, "spell-suggest") is not { } session) {
			return;
		}

		if (!TryReadSpellSuggestRequest(root, out var request, out string error)) {
			PostSpellSuggestError(session, root, error);
			return;
		}

		string locale = _settings.RequireString(SpellSettings.Locale);
		var dictionaryVersions = CaptureSpellDictionaryVersions(session);
		var operation = _spellOperationRegistry.Begin(session, "suggest", string.Empty);
		if (operation is null) {
			return;
		}

		_ = SuggestSpellAsync(session, root, request, locale, dictionaryVersions, operation);
	}

	private async Task SuggestSpellAsync(
		HostSession session,
		JsonElement root,
		SpellSuggestRequest request,
		string locale,
		SpellDictionaryVersions dictionaryVersions,
		SpellOperationRegistry.Operation operation) {
		var cancellationToken = operation.Token;
		try {
			var suggestions = await Task.Run(
				() => session.SpellChecker.Suggest(request.Word, locale, cancellationToken),
				cancellationToken).ConfigureAwait(false);
			if (!_spellOperationRegistry.IsCurrent(operation) || cancellationToken.IsCancellationRequested) {
				return;
			}

			_ui.Post(() => {
				if (!cancellationToken.IsCancellationRequested
					&& SpellSettingsMatch(locale)
					&& HasCurrentSpellDictionaryVersions(session, dictionaryVersions)
					&& IsCurrentSpellTarget(session, root, "spell-suggest")) {
					PostSpellSuggestResult(session, root, request, locale, suggestions, error: null);
				}
			});
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			// A newer suggestion request owns the response surface.
		} catch (Exception ex) {
			if (_spellOperationRegistry.IsCurrent(operation)) {
				_ui.Post(() => {
					if (!cancellationToken.IsCancellationRequested
						&& SpellSettingsMatch(locale)
						&& HasCurrentSpellDictionaryVersions(session, dictionaryVersions)
						&& IsCurrentSpellTarget(session, root, "spell-suggest")) {
						PostSpellSuggestResult(session, root, request, locale, [], ex.Message);
					}
				});
			}
		} finally {
			_spellOperationRegistry.End(operation);
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

		_spellDictionaryWrites.TryRun(session, () => AddSpellWordAsync(session, root, request));
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
