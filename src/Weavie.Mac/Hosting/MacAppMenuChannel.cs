using Weavie.Hosting;

namespace Weavie.Mac.Hosting;

/// <summary>One workspace window's cached native-menu state and activation route.</summary>
internal sealed class MacAppMenuChannel : IApplicationMenu, IDisposable {
	private readonly MacAppMenu _owner;
	private event Action<ApplicationMenuActivation>? Activation;
	private bool _closed;

	public MacAppMenuChannel(MacAppMenu owner) {
		ArgumentNullException.ThrowIfNull(owner);
		_owner = owner;
	}

	internal ApplicationMenuState? State { get; private set; }

	event Action<ApplicationMenuActivation> IApplicationMenu.Activated {
		add => Activation += value;
		remove => Activation -= value;
	}

	void IApplicationMenu.Apply(ApplicationMenuState state) {
		ObjectDisposedException.ThrowIf(_closed, this);
		ArgumentNullException.ThrowIfNull(state);
		State = state;
		_owner.Apply(this);
	}

	void IApplicationMenu.Clear() {
		State = null;
		_owner.Apply(this);
	}

	internal void Activate() {
		if (!_closed) {
			_owner.Activate(this);
		}
	}

	internal void Raise(ApplicationMenuActivation activation) => Activation?.Invoke(activation);

	public void Dispose() {
		if (_closed) {
			return;
		}

		_closed = true;
		State = null;
		Activation = null;
		_owner.Close(this);
	}
}
