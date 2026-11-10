using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// A host-driven choice picker: the pick runs its continuation on the session that asked, never the selected one.
public sealed partial class HostCore {
	private void ShowPicker(HostSession session, ChoicePicker picker, Action<HostSession, string> onPick) {
		session.OpenPicker.Set(new PendingPick(picker, onPick));
		session.State.Set("picker", "current", "show", WireJson.Default.ChoicePicker, picker);
	}

	private void WirePicker(HostSession session) {
		var picker = session.Bus.Feature("picker");
		picker.Handle("pick", MessageActivity.ActionFailed("Picking an option"), WireJson.Default.PickMessage, (message, _) => {
			session.State.Remove("picker", "current");
			if (session.OpenPicker.Take() is not { } pending) {
				throw new InvalidOperationException("There's no open picker to answer.");
			}

			if (!pending.Picker.Options.Any(option => option.Id == message.Id)) {
				throw new InvalidOperationException($"'{message.Id}' isn't one of the offered options.");
			}

			pending.OnPick(session, message.Id);
			return Task.CompletedTask;
		});
		picker.Handle("dismiss", MessageActivity.ActionFailed("Dismissing a picker"), WireJson.Default.EmptyPayload, (_, _) => {
			session.State.Remove("picker", "current");
			session.OpenPicker.Take();
			return Task.CompletedTask;
		});
	}
}

internal sealed record PendingPick(ChoicePicker Picker, Action<HostSession, string> OnPick);

internal sealed record ChoiceOption(string Id, string Label, string? Description, string? Group);

internal sealed record ChoicePicker(string Title, string Value, ChoiceOption[] Options);

internal sealed record PickMessage(string Id);
