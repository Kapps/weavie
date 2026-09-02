using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreApplicationMenuTests {
	[Fact]
	public async Task StateAndActivationStayOnTheOwningPageRoute() {
		var menu = new RecordingApplicationMenu();
		await using var host = TestHost.CreateUnstarted(menu);
		await host.Core.StartAsync();
		await host.ConnectAsync();
		host.Bridge.Clear();

		host.HostEvent("applicationMenu", "state", new {
			revision = 7,
			menus = new[] {
				new {
					label = "File",
					entries = new[] {
						new {
							kind = "command",
							label = "Save",
							enabled = true,
							token = "file/0",
							keys = new[] { "$mod+s" },
							toolTip = (string?)null,
							entries = Array.Empty<object>(),
						},
					},
				},
			},
		});

		var state = Assert.IsType<ApplicationMenuState>(menu.State);
		Assert.Equal(7, state.Revision);
		Assert.Equal("Save", Assert.Single(Assert.Single(state.Menus).Entries).Label);

		menu.Activate(new ApplicationMenuActivation(7, "file/0"));

		var (Peer, Json) = Assert.Single(host.Bridge.Sent, item => IsInvocation(item.Json));
		Assert.Equal(new WebPeer(TestHost.TestPageId), Peer);
		Assert.DoesNotContain(host.Bridge.Broadcasts, IsInvocation);
		Assert.True(MessageEnvelope.TryParse(Json, out var envelope));
		var activation = Assert.IsType<MessageEnvelope>(envelope).Payload;
		Assert.Equal(7, activation.GetProperty("revision").GetInt64());
		Assert.Equal("file/0", activation.GetProperty("token").GetString());
	}

	private static bool IsInvocation(string json) =>
		MessageEnvelope.TryParse(json, out var envelope)
		&& envelope is { Feature: "applicationMenu", Name: "invoke" };

	private sealed class RecordingApplicationMenu : IApplicationMenu {
		public event Action<ApplicationMenuActivation>? Activated;

		public ApplicationMenuState? State { get; private set; }

		public void Apply(ApplicationMenuState state) => State = state;

		public void Clear() => State = null;

		public void Activate(ApplicationMenuActivation activation) => Activated?.Invoke(activation);
	}
}
