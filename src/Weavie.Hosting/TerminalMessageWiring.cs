using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

internal static class TerminalMessageWiring {
	public static IDisposable Wire(
		MessageFeatureChannel messages,
		TerminalController terminal,
		Action<bool, Action> acceptInput,
		Action<int, int> resized) {
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentNullException.ThrowIfNull(terminal);
		ArgumentNullException.ThrowIfNull(acceptInput);
		ArgumentNullException.ThrowIfNull(resized);
		return new TerminalMessageHandlers([
			messages.Handle<TerminalInputMessage>("input", (message, _) => {
				byte[] data = Convert.FromBase64String(message.DataB64);
				acceptInput(message.UserInitiated, () => terminal.Write(data));
				return Task.CompletedTask;
			}),
			messages.Handle<TerminalSizeMessage>("resize", (message, _) => {
				terminal.Resize(message.Columns, message.Rows);
				resized(message.Columns, message.Rows);
				return Task.CompletedTask;
			}),
			messages.HandleOwned<TerminalSizeMessage>("ready", (message, peer, _) => {
				terminal.OnReady(messages.Target(peer), message.Columns, message.Rows);
				return Task.CompletedTask;
			}),
			messages.Handle<TerminalCwdMessage>("cwd", (message, _) => {
				terminal.OnCwdReported(message.Cwd);
				return Task.CompletedTask;
			}),
		]);
	}

	private sealed class TerminalMessageHandlers(IReadOnlyList<IDisposable> handlers) : IDisposable {
		public void Dispose() {
			foreach (var handler in handlers) {
				handler.Dispose();
			}
		}
	}

	private sealed record TerminalInputMessage(string DataB64, bool UserInitiated);
	private sealed record TerminalSizeMessage(int Columns, int Rows);
	private sealed record TerminalCwdMessage(string Cwd);
}
