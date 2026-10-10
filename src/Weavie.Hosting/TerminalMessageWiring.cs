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
			messages.Handle("input", "Sending input to the terminal", WireJson.Default.TerminalInputMessage, (message, _) => {
				byte[] data = Convert.FromBase64String(message.DataB64);
				acceptInput(message.UserInitiated, () => terminal.Write(data));
				return Task.CompletedTask;
			}),
			messages.Handle("resize", "Resizing the terminal", WireJson.Default.TerminalSizeMessage, (message, _) => {
				terminal.Resize(message.Columns, message.Rows);
				resized(message.Columns, message.Rows);
				return Task.CompletedTask;
			}),
			messages.HandleOwned("ready", "Starting the terminal", WireJson.Default.TerminalSizeMessage, (message, peer, _) => {
				terminal.OnReady(messages.Target(peer), message.Columns, message.Rows);
				return Task.CompletedTask;
			}),
			messages.Handle("cwd", "Updating the terminal folder", WireJson.Default.TerminalCwdMessage, (message, _) => {
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

	internal sealed record TerminalInputMessage(string DataB64, bool UserInitiated);
	internal sealed record TerminalSizeMessage(int Columns, int Rows);
	internal sealed record TerminalCwdMessage(string Cwd);
}
