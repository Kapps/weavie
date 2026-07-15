using Weavie.Win.Hosting;

namespace Weavie.Win;

internal sealed partial class WorkspaceWindow {
	private Task _initializationTask = Task.CompletedTask;
	private bool _closing;
	private bool _closeCommitted;

	private void OnFormClosing(object? sender, FormClosingEventArgs e) {
		SaveWindowState();
		if (_closeCommitted) {
			return;
		}

		e.Cancel = true;
		if (_closing) {
			return;
		}

		_closing = true;
		// A web title-bar close arrives inside WebView2's callback; unwind it before tearing the view down.
		BeginInvoke((Action)FinishCloseAsync);
	}

	private async void FinishCloseAsync() {
		Exception? failure = null;
		try {
			await _initializationTask;
		} catch (Exception ex) {
			failure = ex;
		}

		_core.Ready -= OnPageReady;
		try {
			await _core.DisposeAsync();
		} catch (Exception ex) {
			failure = ShutdownFailure.Add(failure, ex);
		}

#if DEBUG
		try {
			_devBringUp?.Dispose();
		} catch (Exception ex) {
			failure = ShutdownFailure.Add(failure, ex);
		}
#endif
		try {
			_bridge.Dispose();
		} catch (Exception ex) {
			failure = ShutdownFailure.Add(failure, ex);
		}

		_dispatcher.Close();
		try {
			_webView.Dispose();
		} catch (Exception ex) {
			failure = ShutdownFailure.Add(failure, ex);
		}

		_closeCommitted = true;
		Close();
		ShutdownFailure.ThrowIfAny(failure);
	}
}
