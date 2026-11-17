using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly CommandUsageStore _commandUsage;

	private CommandUsageSnapshot BuildCommandUsage() => new(
		_settings.RequireBool(CoreSettings.CommandLearning),
		CommandUsageStore.HalfLifeMilliseconds,
		_commandUsage.Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

	private void PushCommandUsage() => _messages.Host.Feature("commandUsage")
		.Publish("changed", WireJson.Default.CommandUsageSnapshot, BuildCommandUsage());

	private CommandResult ResetCommandLearning() {
		_commandUsage.Clear();
		PushCommandUsage();
		return CommandResult.Success("Command learning reset");
	}

	private void WireCommandUsage() {
		var feature = _messages.Host.Feature("commandUsage");
		feature.Handle("record", MessageActivity.ActionFailed("Saving command learning"),
			WireJson.Default.RecordCommandUsage, (message, _) => {
				if (_settings.RequireBool(CoreSettings.CommandLearning) && message.Id != CoreCommands.ResetCommandLearning) {
					_commandUsage.Record(message.Id, message.Query, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
					PushCommandUsage();
				}
				return Task.CompletedTask;
			});
	}
}

internal sealed record RecordCommandUsage(string Id, string Query);
internal sealed record CommandUsageSnapshot(bool Enabled, double HalfLifeMilliseconds, IReadOnlyList<CommandUsage> Entries);
