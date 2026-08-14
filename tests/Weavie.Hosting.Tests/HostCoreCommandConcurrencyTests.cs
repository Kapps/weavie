using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreCommandConcurrencyTests {
	[Fact]
	public async Task ThemePickerDoesNotBlockAnotherHostCommand() {
		var dialogs = new BlockingDialogs();
		await using var host = await TestHost.StartWithDialogsAsync(dialogs);
		try {
			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				HostCommandRequest("install-theme", CoreCommands.InstallThemeFromFile));
			await dialogs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				HostCommandRequest("increase-font", CoreCommands.IncreaseFontSize));

			var response = await Wait.ForReferenceAsync(() => Response(host, "increase-font"));
			Assert.Null(response.Error);
			Assert.True(response.Payload.GetProperty("ok").GetBoolean());
			Assert.Null(Response(host, "install-theme"));

			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				HostCommandRequest("reset-theme", CoreCommands.ResetTheme));
			host.Bridge.Receive(
				new WebPeer(TestHost.TestPageId),
				HostCommandRequest("decrease-font", CoreCommands.DecreaseFontSize));
			await Wait.ForReferenceAsync(() => Response(host, "decrease-font"));
			Assert.Null(Response(host, "reset-theme"));
		} finally {
			dialogs.Release.TrySetResult();
		}

		var installResponse = await Wait.ForReferenceAsync(() => Response(host, "install-theme"));
		Assert.Null(installResponse.Error);
		Assert.True(installResponse.Payload.GetProperty("ok").GetBoolean());
		var themeResponse = await Wait.ForReferenceAsync(() => Response(host, "reset-theme"));
		Assert.Null(themeResponse.Error);
		Assert.True(themeResponse.Payload.GetProperty("ok").GetBoolean());
	}

	private static string HostCommandRequest(string requestId, string commandId) =>
		MessageEnvelope.Request(
			MessageScope.Host,
			null,
			requestId,
			"commands",
			"invoke",
			JsonSerializer.SerializeToElement(new { id = commandId, args = new { } })).ToJson();

	private static MessageEnvelope? Response(TestHost host, string requestId) =>
		host.Bridge.Posted
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.LastOrDefault(envelope => envelope is { Kind: MessageKind.Response }
				&& envelope.RequestId == requestId);

	private sealed class BlockingDialogs : IHostDialogs {
		public TaskCompletionSource Entered { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<string?> PickVsixFileAsync(CancellationToken ct) {
			Entered.TrySetResult();
			await Release.Task.WaitAsync(ct);
			return null;
		}

		public Task<string?> PickSaveAsPathAsync(
			string suggestedName,
			string initialDirectory,
			CancellationToken ct) =>
			Task.FromResult<string?>(null);
	}
}
