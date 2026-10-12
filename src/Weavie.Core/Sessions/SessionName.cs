namespace Weavie.Core.Sessions;

/// <summary>A session label's readable name: <c>feature/review-front-door</c> → <c>review front door</c>.</summary>
public static class SessionName {
	/// <summary>The branch leaf after the last <c>/</c>, with <c>-</c> and <c>_</c> as spaces.</summary>
	public static string Title(string label) {
		ArgumentException.ThrowIfNullOrEmpty(label);
		string trimmed = label.TrimEnd('/');
		string leaf = trimmed[(trimmed.LastIndexOf('/') + 1)..];
		string title = string.Join(' ', leaf.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries));
		return title.Length == 0 ? label : title;
	}
}
