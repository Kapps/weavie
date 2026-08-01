using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreEditorViewBindingTests {
	[Fact]
	public async Task OnlyTheBoundPageAuthorsEditorSessionState() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		var owner = new WebPeer("editor-owner");
		var stale = new WebPeer("stale-page");
		var observed = new ConcurrentQueue<string?>();
		session.EditorSessionChanged += state => observed.Enqueue(state.Active);
		Publish(host, owner, session, "view", "attach", new { pageEpoch = "editor-owner" });

		PublishSession(host, owner, session, "/owner-one.cs");
		await Wait.ForAsync<bool>(() => observed.Count == 1 ? true : null);
		PublishSession(host, stale, session, "/stale.cs");
		PublishSession(host, owner, session, "/owner-two.cs");
		await Wait.ForAsync<bool>(() => observed.Count == 2 ? true : null);

		Assert.Collection(
			observed,
			value => Assert.Equal("/owner-one.cs", value),
			value => Assert.Equal("/owner-two.cs", value));
		Assert.Equal("/owner-two.cs", session.EditorSession.Active);
	}

	private static void PublishSession(
		TestHost host,
		WebPeer peer,
		HostSession session,
		string path) =>
		Publish(
			host,
			peer,
			session,
			"editor",
			"sessionChanged",
			new {
				session = new {
					active = path,
					open = new[] { new { path, viewState = (object?)null } },
				},
			});

	private static void Publish(
		TestHost host,
		WebPeer peer,
		HostSession session,
		string feature,
		string name,
		object payload) =>
		host.Bridge.Receive(
			peer,
			MessageEnvelope.SessionEvent(
				session.Address,
				feature,
				name,
				JsonSerializer.SerializeToElement(payload)).ToJson());
}
