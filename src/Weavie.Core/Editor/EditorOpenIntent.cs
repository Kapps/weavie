namespace Weavie.Core.Editor;

/// <summary>Why a file is being opened in the editor, which decides whether it may take over the pane.</summary>
public enum EditorOpenIntent {
	/// <summary>The user navigated here: the file takes the pane, leaving whatever surface covered it.</summary>
	Navigation,

	/// <summary>Something revealed the file on the user's behalf: it opens behind a review they are in.</summary>
	Reveal,
}
