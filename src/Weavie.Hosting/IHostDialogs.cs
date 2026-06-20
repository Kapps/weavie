namespace Weavie.Hosting;

/// <summary>
/// The native modal dialogs <see cref="HostCore"/> needs but can't draw itself: the <c>.vsix</c> open picker
/// (install-theme-from-file) and the Save-As picker (naming an untitled scratch buffer). Implemented per host
/// with the OS file panels (WinForms <c>OpenFileDialog</c>/<c>SaveFileDialog</c>, Cocoa <c>NSOpenPanel</c>/
/// <c>NSSavePanel</c>); hosts with no native UI (Linux/Headless) pass <c>null</c> and the few commands that
/// need a dialog become no-ops. Kept tiny on purpose — everything around the picked path stays in Core.
/// </summary>
public interface IHostDialogs {
	/// <summary>Shows a native open picker for a <c>.vsix</c> theme file; returns the chosen path or <c>null</c> if cancelled.</summary>
	Task<string?> PickVsixFileAsync(CancellationToken ct);

	/// <summary>
	/// Shows a native Save-As picker defaulting to <paramref name="suggestedName"/> in
	/// <paramref name="initialDirectory"/>; returns the chosen path or <c>null</c> if cancelled.
	/// </summary>
	Task<string?> PickSaveAsPathAsync(string suggestedName, string initialDirectory, CancellationToken ct);
}
