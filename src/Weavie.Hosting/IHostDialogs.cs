namespace Weavie.Hosting;

/// <summary>
/// The native modal dialogs <see cref="HostCore"/> needs but can't draw: the <c>.vsix</c> open picker and the
/// Save-As picker (naming a scratch buffer), each implemented per host with the OS file panels. Hosts with no
/// native UI (Linux/Headless) pass <c>null</c> and the few commands needing a dialog become no-ops.
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
