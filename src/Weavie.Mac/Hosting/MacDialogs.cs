using AppKit;
using Foundation;
using UniformTypeIdentifiers;
using Weavie.Hosting;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The macOS native file dialogs <see cref="HostCore"/> needs: the <c>.vsix</c> open picker (install theme
/// from file) and the Save-As picker (naming an untitled scratch buffer). Both run modally on the main thread
/// (<see cref="NSOpenPanel"/> / <see cref="NSSavePanel"/>); calls marshal there when invoked off it.
/// </summary>
internal sealed class MacDialogs : IHostDialogs {
	/// <inheritdoc/>
	public Task<string?> PickVsixFileAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		RunOnMain(() => {
			var panel = NSOpenPanel.OpenPanel;
			panel.Title = "Install Theme from .vsix";
			panel.CanChooseFiles = true;
			panel.CanChooseDirectories = false;
			panel.AllowsMultipleSelection = false;
			// .vsix has no system-declared UTI, so synthesize a content type from the extension.
			if (UTType.CreateFromExtension("vsix") is { } vsixType) {
				panel.AllowedContentTypes = [vsixType];
			}

			completion.SetResult(panel.RunModal() == 1 && panel.Url is { Path: { } path } ? path : null);
		});
		return completion.Task;
	}

	/// <inheritdoc/>
	public Task<string?> PickSaveAsPathAsync(string suggestedName, string initialDirectory, CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		RunOnMain(() => {
			var panel = NSSavePanel.SavePanel;
			panel.Title = "Save As";
			panel.NameFieldStringValue = suggestedName;
			if (!string.IsNullOrEmpty(initialDirectory)) {
				panel.DirectoryUrl = NSUrl.FromFilename(initialDirectory);
			}

			completion.SetResult(panel.RunModal() == 1 && panel.Url is { Path: { } chosen } ? chosen : null);
		});
		return completion.Task;
	}

	private static void RunOnMain(Action action) {
		if (NSThread.IsMain) {
			action();
		} else {
			NSApplication.SharedApplication.InvokeOnMainThread(action);
		}
	}
}
