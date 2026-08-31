using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreStructuredAttachmentTests {
	private static readonly byte[] PngBytes = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];

	[Fact]
	public async Task NewSession_SubmitsItsTextAndImageAsOneInitialInput() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "new-session-image",
			Base = "main",
			AgentProviderId = "structured",
			Prompt = "describe this",
			Attachments = [new NewSessionAttachment {
				Id = "image-1",
				Mime = "image/png",
				DataB64 = Convert.ToBase64String(PngBytes),
			}],
		});

		Assert.True(result.Ok, result.Error);
		var session = host.Session("new-session-image");
		await session.Agent.DrainPaneAsync(CancellationToken.None);
		string file = Assert.Single(Directory.GetFiles(session.PastedImages.Directory));
		Assert.Equal(PngBytes, File.ReadAllBytes(file));
		Assert.Contains(
			host.Bridge.PostedEvents(session.Address, "agent", "pane"),
			message => message.GetProperty("type").GetString() == "user-message"
				&& message.GetProperty("text").GetString() == "describe this");
	}

	[Fact]
	public async Task NewSession_RejectsInvalidImageBeforeCreatingAWorktree() {
		await using var host = await TestHost.StartAsync();

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "invalid-image",
			Base = "main",
			AgentProviderId = "structured",
			Attachments = [new NewSessionAttachment {
				Id = "image-1",
				Mime = "image/tiff",
				DataB64 = Convert.ToBase64String(PngBytes),
			}],
		});

		Assert.False(result.Ok);
		Assert.Contains("Can't paste that image type", result.Error, StringComparison.Ordinal);
		Assert.Null(host.Core.SessionForTest("invalid-image"));
	}

	[Fact]
	public async Task UploadThenSubmit_ClaimsTheExactRemoteAttachment() {
		await using var host = await StartStructuredAsync("structured-images");
		var session = host.SelectedSession;

		Upload(host, session, "image-1", "image/png", PngBytes);

		var ready = host.Bridge.LastEvent(session.Address, "agent", "attachmentState");
		Assert.True(ready.HasValue);
		Assert.Equal("ready", ready.Value.GetProperty("status").GetString());
		string file = Assert.Single(Directory.GetFiles(host.SelectedSession.PastedImages.Directory));
		Assert.Equal(PngBytes, File.ReadAllBytes(file));
		Upload(host, session, "image-1", "image/png", PngBytes);
		Assert.Single(Directory.GetFiles(host.SelectedSession.PastedImages.Directory));
		Assert.Equal(
			"ready",
			host.Bridge.LastEvent(session.Address, "agent", "attachmentState")?.GetProperty("status").GetString());

		Submit(host, session, "submission-1", "describe it", ["image-1"]);

		var accepted = host.Bridge.LastEvent(session.Address, "agent", "submissionState");
		Assert.True(accepted.HasValue);
		Assert.Equal("accepted", accepted.Value.GetProperty("status").GetString());

		Submit(host, session, "submission-1", "describe it", ["image-1"]);
		var replayed = host.Bridge.LastEvent(session.Address, "agent", "submissionState");
		Assert.Equal("accepted", replayed?.GetProperty("status").GetString());
		Assert.Equal("image-1", replayed?.GetProperty("attachmentIds")[0].GetString());
		Upload(host, session, "image-1", "image/png", PngBytes);
		Assert.Equal(
			"removed",
			host.Bridge.LastEvent(session.Address, "agent", "attachmentState")?.GetProperty("status").GetString());
		Assert.Single(Directory.GetFiles(host.SelectedSession.PastedImages.Directory));

		Submit(host, session, "submission-2", "again", ["image-1"]);
		var rejected = host.Bridge.LastEvent(session.Address, "agent", "submissionState");
		Assert.True(rejected.HasValue);
		Assert.Equal("rejected", rejected.Value.GetProperty("status").GetString());
		Assert.Contains("not ready", rejected.Value.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SubmitBeforeUpload_IsRejectedWithoutConsumingTheLaterAttachment() {
		await using var host = await StartStructuredAsync("attachment-race");
		var session = host.SelectedSession;

		Submit(host, session, "submission-early", "describe it", ["image-1"]);
		Assert.Equal(
			"rejected",
			host.Bridge.LastEvent(session.Address, "agent", "submissionState")?.GetProperty("status").GetString());

		Upload(host, session, "image-1", "image/png", PngBytes);
		Submit(host, session, "submission-ready", "describe it", ["image-1"]);
		Assert.Equal(
			"accepted",
			host.Bridge.LastEvent(session.Address, "agent", "submissionState")?.GetProperty("status").GetString());
	}

	[Fact]
	public async Task RemoveAttachment_DeletesItsScratchFile() {
		await using var host = await StartStructuredAsync("remove-image");
		var session = host.SelectedSession;
		Upload(host, session, "image-1", "image/png", PngBytes);
		string directory = host.SelectedSession.PastedImages.Directory;
		Assert.Single(Directory.GetFiles(directory));

		host.SessionEvent(session, "agent", "removeAttachment", new { id = "image-1" });

		Assert.Empty(Directory.GetFiles(directory));
		Assert.Equal(
			"removed",
			host.Bridge.LastEvent(session.Address, "agent", "attachmentState")?.GetProperty("status").GetString());
		Upload(host, session, "image-1", "image/png", PngBytes);
		Assert.Empty(Directory.GetFiles(directory));
		Assert.Equal(
			"removed",
			host.Bridge.LastEvent(session.Address, "agent", "attachmentState")?.GetProperty("status").GetString());
	}

	private static async Task<TestHost> StartStructuredAsync(string branch) {
		var host = await TestHost.StartAsync();
		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AgentProviderId = "structured",
		});
		Assert.True(result.Ok, result.Error);
		return host;
	}

	private static void Upload(
		TestHost host,
		HostSession session,
		string id,
		string mime,
		byte[] bytes) =>
		host.SessionEvent(
			session,
			"agent",
			"uploadAttachment",
			new { id, mime, dataB64 = Convert.ToBase64String(bytes) });

	private static void Submit(
		TestHost host,
		HostSession session,
		string id,
		string prompt,
		string[] attachmentIds) =>
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id, prompt, kind = "prompt", commandName = "", attachmentIds });
}
