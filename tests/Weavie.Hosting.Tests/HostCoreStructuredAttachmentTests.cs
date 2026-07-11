using System.Text.Json;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreStructuredAttachmentTests {
	private static readonly byte[] PngBytes = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];

	[Fact]
	public async Task UploadThenSubmit_ClaimsTheExactRemoteAttachment() {
		await using var host = await StartCodexAsync("structured-images");

		host.Send(Upload("structured-images", "image-1", "image/png", PngBytes));

		var ready = host.Bridge.LastOfType("agent-attachment-state");
		Assert.True(ready.HasValue);
		Assert.Equal("ready", ready.Value.GetProperty("status").GetString());
		string file = Assert.Single(Directory.GetFiles(host.Core.ActiveSessionForTest()!.PastedImages.Directory));
		Assert.Equal(PngBytes, File.ReadAllBytes(file));
		host.Send(Upload("structured-images", "image-1", "image/png", PngBytes));
		Assert.Single(Directory.GetFiles(host.Core.ActiveSessionForTest()!.PastedImages.Directory));
		Assert.Equal("ready", host.Bridge.LastOfType("agent-attachment-state")?.GetProperty("status").GetString());

		host.Send(Submit("structured-images", "submission-1", "describe it", ["image-1"]));

		var accepted = host.Bridge.LastOfType("agent-submission-state");
		Assert.True(accepted.HasValue);
		Assert.Equal("accepted", accepted.Value.GetProperty("status").GetString());

		host.Send(Submit("structured-images", "submission-1", "describe it", ["image-1"]));
		var replayed = host.Bridge.LastOfType("agent-submission-state");
		Assert.Equal("accepted", replayed?.GetProperty("status").GetString());
		Assert.Equal("image-1", replayed?.GetProperty("attachmentIds")[0].GetString());
		host.Send(Upload("structured-images", "image-1", "image/png", PngBytes));
		Assert.Equal("removed", host.Bridge.LastOfType("agent-attachment-state")?.GetProperty("status").GetString());
		Assert.Single(Directory.GetFiles(host.Core.ActiveSessionForTest()!.PastedImages.Directory));

		host.Send(Submit("structured-images", "submission-2", "again", ["image-1"]));
		var rejected = host.Bridge.LastOfType("agent-submission-state");
		Assert.True(rejected.HasValue);
		Assert.Equal("rejected", rejected.Value.GetProperty("status").GetString());
		Assert.Contains("not ready", rejected.Value.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SubmitBeforeUpload_IsRejectedWithoutConsumingTheLaterAttachment() {
		await using var host = await StartCodexAsync("attachment-race");

		host.Send(Submit("attachment-race", "submission-early", "describe it", ["image-1"]));
		Assert.Equal(
			"rejected",
			host.Bridge.LastOfType("agent-submission-state")?.GetProperty("status").GetString());

		host.Send(Upload("attachment-race", "image-1", "image/png", PngBytes));
		host.Send(Submit("attachment-race", "submission-ready", "describe it", ["image-1"]));
		Assert.Equal(
			"accepted",
			host.Bridge.LastOfType("agent-submission-state")?.GetProperty("status").GetString());
	}

	[Fact]
	public async Task RemoveAttachment_DeletesItsScratchFile() {
		await using var host = await StartCodexAsync("remove-image");
		host.Send(Upload("remove-image", "image-1", "image/png", PngBytes));
		string directory = host.Core.ActiveSessionForTest()!.PastedImages.Directory;
		Assert.Single(Directory.GetFiles(directory));

		host.Send(JsonSerializer.Serialize(new {
			type = "agent-attachment-remove",
			slot = "remove-image",
			id = "image-1",
		}));

		Assert.Empty(Directory.GetFiles(directory));
		Assert.Equal("removed", host.Bridge.LastOfType("agent-attachment-state")?.GetProperty("status").GetString());
		host.Send(Upload("remove-image", "image-1", "image/png", PngBytes));
		Assert.Empty(Directory.GetFiles(directory));
		Assert.Equal("removed", host.Bridge.LastOfType("agent-attachment-state")?.GetProperty("status").GetString());
	}

	private static async Task<TestHost> StartCodexAsync(string branch) {
		var host = await TestHost.StartAsync();
		var result = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(result.Ok, result.Error);
		return host;
	}

	private static string Upload(string slot, string id, string mime, byte[] bytes) =>
		JsonSerializer.Serialize(new {
			type = "agent-attachment-upload",
			slot,
			id,
			mime,
			dataB64 = Convert.ToBase64String(bytes),
		});

	private static string Submit(string slot, string id, string prompt, string[] attachmentIds) =>
		JsonSerializer.Serialize(new { type = "agent-submit", slot, id, prompt, attachmentIds });
}
