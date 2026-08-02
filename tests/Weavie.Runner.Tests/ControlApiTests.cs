using System.Text;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Weavie.Runner.Tests;

public sealed class ControlApiTests {
	[Fact]
	public void Worker_handoff_accepts_a_clean_url_on_the_runner_hostname() {
		Assert.True(ControlApi.TryGetWorkerUri(
			new HostString("remdeb.tail7a14e8.ts.net"),
			"https://remdeb.tail7a14e8.ts.net:8443/index.html",
			out var workerUri));
		Assert.Equal(8443, workerUri.Port);
	}

	[Theory]
	[InlineData("https://other.tail7a14e8.ts.net:8443/index.html")]
	[InlineData("https://remdeb.tail7a14e8.ts.net:8443/index.html?token=secret")]
	[InlineData("https://remdeb.tail7a14e8.ts.net:8443/index.html#secret")]
	[InlineData("https://secret@remdeb.tail7a14e8.ts.net:8443/index.html")]
	public void Worker_handoff_rejects_urls_that_cannot_safely_receive_the_cookie(string workerUrl) {
		Assert.False(ControlApi.TryGetWorkerUri(
			new HostString("remdeb.tail7a14e8.ts.net"),
			workerUrl,
			out _));
	}

	[Theory]
	[InlineData("idle", false, false)]
	[InlineData("updating", false, false)]
	[InlineData("error", false, true)]
	[InlineData("failed", false, true)]
	[InlineData("rolled-back", false, true)]
	[InlineData("needs-newer-runner", false, true)]
	[InlineData("idle", true, true)]
	public void Attention_worthy_update_states_hold_the_runner_page(
		string phase,
		bool runnerBehind,
		bool expected) {
		Assert.Equal(expected, ControlApi.UpdateRequiresAttention(new UpdateStatus {
			Enabled = true,
			RunnerBuild = "123",
			Phase = phase,
			RunnerBehind = runnerBehind,
		}));
	}

	[Fact]
	public async Task Attention_page_offers_the_clean_worker_path() {
		var context = new DefaultHttpContext();
		context.Response.Body = new MemoryStream();
		await RunnerStatusPage.WriteAttentionAsync(
			context,
			"/workspace",
			"https://remdeb.tail7a14e8.ts.net:8443/index.html",
			new UpdateStatus {
				Enabled = true,
				RunnerBuild = "123",
				Phase = "error",
				Detail = "update unavailable",
			});
		context.Response.Body.Position = 0;
		using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
		string html = await reader.ReadToEndAsync();

		Assert.Contains("href=\"https://remdeb.tail7a14e8.ts.net:8443/index.html\"", html);
		Assert.DoesNotContain("?token=", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("http-equiv=\"refresh\"", html);
		Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
		Assert.Equal("no-store", context.Response.Headers.CacheControl);
	}
}
