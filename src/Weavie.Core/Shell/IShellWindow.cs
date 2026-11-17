namespace Weavie.Core.Shell;

/// <summary>
/// The window a host's page draws chrome for. <see cref="Chrome"/> tells the page what to draw; the operations are the
/// ones that chrome advertises. A host overrides exactly those, and an unadvertised one fails loudly.
/// </summary>
public interface IShellWindow {
	/// <summary>What the page's title bar should draw for this window.</summary>
	WindowChrome Chrome { get; }

	/// <summary>Raised on the UI thread after <see cref="Chrome"/> changes (a new button layout, entering fullscreen).</summary>
	event Action? ChromeChanged;

	/// <summary>Minimizes this window.</summary>
	void Minimize() => throw Unadvertised(nameof(Minimize));

	/// <summary>Toggles this window between maximized and restored.</summary>
	void ToggleMaximize() => throw Unadvertised(nameof(ToggleMaximize));

	/// <summary>Closes this window — the title-bar ✕ button. The host decides last-window behavior.</summary>
	void Close() => throw Unadvertised(nameof(Close));

	/// <summary>
	/// Begins an interactive native resize from <paramref name="edge"/>. A frameless WebView covers the host's real
	/// resize border, so the page draws grab handles and hands off to the OS here.
	/// </summary>
	void StartResize(ResizeEdge edge) => throw Unadvertised(nameof(StartResize));

	/// <summary>Turns a press on empty title-bar space into the system's title-bar gesture, paired with the native press.</summary>
	void PressCaption(CaptionPress press) => throw Unadvertised(nameof(PressCaption));

	private static NotSupportedException Unadvertised(string operation) =>
		new($"This window does not offer {operation}; its chrome never advertised it.");
}
