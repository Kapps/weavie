using Weavie.Core.Commands;
using Weavie.Core.Shell;

namespace Weavie.Hosting;

/// <summary>
/// Everything native a <see cref="HostCore"/> needs from its platform shell, behind one seam so the core stays
/// platform-agnostic. Required members (bridge, dispatcher, PTY launcher, chrome identity) every host supplies;
/// optional ones (<see cref="Window"/>, <see cref="HotkeyRegistrar"/>, <see cref="Dialogs"/>) are <c>null</c>
/// where unsupported (e.g. headless), and the core degrades the matching feature to a no-op.
/// </summary>
public interface IHostPlatform {
	/// <summary>The web message bridge (post to / receive from the page).</summary>
	IHostBridge Bridge { get; }

	/// <summary>Marshals work onto the host's UI thread (inline when there is none).</summary>
	IUiDispatcher Dispatcher { get; }

	/// <summary>Creates PTY backends + resolves how to launch claude / the shell on this OS.</summary>
	IPtyLauncher PtyLauncher { get; }

	/// <summary>Short platform id the web renders against (<c>win</c> / <c>mac</c> / <c>linux</c> / <c>web</c>).</summary>
	string ChromePlatform { get; }

	/// <summary>Title-bar mode the web should render (<c>custom</c> for a web title bar, <c>mac</c>, or <c>null</c> for native chrome).</summary>
	string? TitleBar { get; }

	/// <summary>Recent workspace paths for File ▸ Open Recent (a start-time snapshot; empty when unsupported).</summary>
	IReadOnlyList<string> Recents { get; }

	/// <summary>The OS window primitives the web title bar drives, or <c>null</c> when the host uses native chrome.</summary>
	IShellWindow? Window { get; }

	/// <summary>The OS global-hotkey registrar, or <c>null</c> when the host registers no global hotkeys.</summary>
	IGlobalHotkeyRegistrar? HotkeyRegistrar { get; }

	/// <summary>The native modal file dialogs, or <c>null</c> when the host has no native UI.</summary>
	IHostDialogs? Dialogs { get; }

	/// <summary>Toggles the app window's visibility/focus — the <c>weavie.window.toggle</c> action (no-op when unsupported).</summary>
	void ToggleWindow();
}
