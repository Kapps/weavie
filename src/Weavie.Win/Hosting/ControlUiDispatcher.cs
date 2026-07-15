using Weavie.Hosting;

namespace Weavie.Win.Hosting;

/// <summary>Marshals work to a WinForms control until shutdown closes the dispatch lane.</summary>
internal sealed class ControlUiDispatcher : IUiDispatcher {
	private readonly Control _control;
	private readonly object _gate = new();
	private bool _closed;

	public ControlUiDispatcher(Control control) {
		ArgumentNullException.ThrowIfNull(control);
		_control = control;
	}

	public void Post(Action action) {
		ArgumentNullException.ThrowIfNull(action);
		lock (_gate) {
			if (_closed) {
				return;
			}

			if (_control.InvokeRequired) {
				_control.BeginInvoke(() => Run(action));
			} else {
				action();
			}
		}
	}

	public void Close() {
		lock (_gate) {
			_closed = true;
		}
	}

	private void Run(Action action) {
		lock (_gate) {
			if (!_closed) {
				action();
			}
		}
	}
}
