namespace Weavie.Core.Sessions;

/// <summary>A session label split for display: <c>feature/review-front-door</c> → prefix <c>feature</c>, title <c>review front door</c>.</summary>
/// <param name="Prefix">The branch type before the last <c>/</c>, or <c>null</c> when there is none.</param>
/// <param name="Title">The readable branch leaf, with <c>-</c> and <c>_</c> as spaces.</param>
public sealed record SessionName(string? Prefix, string Title) {
	/// <summary>Splits <paramref name="label"/> into its prefix and readable title.</summary>
	public static SessionName Parse(string label) {
		ArgumentException.ThrowIfNullOrEmpty(label);
		string trimmed = label.TrimEnd('/');
		int slash = trimmed.LastIndexOf('/');
		string leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
		string title = string.Join(' ', leaf.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries));
		return new SessionName(slash > 0 ? trimmed[..slash] : null, title.Length == 0 ? label : title);
	}
}
