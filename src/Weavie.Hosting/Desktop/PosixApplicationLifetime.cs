using System.Runtime.InteropServices;

namespace Weavie.Hosting.Desktop;

// The native UI loop owns process shutdown; ConsoleLifetime only signals the embedded server.
internal sealed class PosixApplicationLifetime : IDisposable {
	private readonly PosixSignalRegistration _interrupt;
	private readonly PosixSignalRegistration _terminate;
	private int _state;

	internal PosixApplicationLifetime(IUiDispatcher ui, Action quit) {
		ArgumentNullException.ThrowIfNull(ui);
		ArgumentNullException.ThrowIfNull(quit);
		void Stop(PosixSignalContext context) {
			context.Cancel = true;
			if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
			ui.Post(() => {
				if (Volatile.Read(ref _state) != 2) quit();
			});
		}
		_interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
		_terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
	}

	public void Dispose() {
		Interlocked.Exchange(ref _state, 2);
		_interrupt.Dispose();
		_terminate.Dispose();
	}
}
