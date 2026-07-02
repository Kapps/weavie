namespace Weavie.Core.Sources;

/// <summary>
/// A targeted source update whose exact-match op no longer fits the document — the content changed since it was
/// fetched, or the match was ambiguous. Typed so the host can surface "the page changed, re-fetch" at the edited
/// block instead of a generic failure.
/// </summary>
public sealed class SourceConflictException : InvalidOperationException {
	/// <summary>Creates the exception carrying the API's reason.</summary>
	public SourceConflictException(string message) : base(message) {
	}
}
