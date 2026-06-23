using Weavie.Hosting;

namespace Weavie.Win.Hosting;

/// <summary>
/// The Windows native file dialogs <see cref="HostCore"/> needs — the <c>.vsix</c> open picker and the Save-As
/// picker — shown modally over the owner window, marshaled onto its UI thread when invoked off it.
/// </summary>
internal sealed class WinDialogs : IHostDialogs {
	private readonly Form _owner;

	public WinDialogs(Form owner) {
		ArgumentNullException.ThrowIfNull(owner);
		_owner = owner;
	}

	/// <inheritdoc/>
	public Task<string?> PickVsixFileAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		Run(() => {
			try {
				using var dialog = new OpenFileDialog {
					Title = "Install Theme from .vsix",
					Filter = "VS Code extension (*.vsix)|*.vsix|All files (*.*)|*.*",
					CheckFileExists = true,
				};
				completion.SetResult(dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : null);
			} catch (Exception ex) {
				completion.SetException(ex);
			}
		});
		return completion.Task;
	}

	/// <inheritdoc/>
	public Task<string?> PickSaveAsPathAsync(string suggestedName, string initialDirectory, CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		Run(() => {
			try {
				using var dialog = new SaveFileDialog {
					Title = "Save As",
					InitialDirectory = initialDirectory,
					// A rooted FileName forces the dialog open at the session root (over the OS's remembered folder);
					// the leaf shows in the name box.
					FileName = string.IsNullOrEmpty(initialDirectory) ? suggestedName : Path.Combine(initialDirectory, suggestedName),
					Filter = "All files (*.*)|*.*",
					OverwritePrompt = true,
					RestoreDirectory = true,
				};
				completion.SetResult(dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : null);
			} catch (Exception ex) {
				completion.SetException(ex);
			}
		});
		return completion.Task;
	}

	private void Run(Action show) {
		if (_owner.InvokeRequired) {
			_owner.BeginInvoke(show);
		} else {
			show();
		}
	}
}
