using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class EditorTabPathTests {
	[Fact]
	public void AFilePathFollowsTheCaseRule_AndAnOverlayPathIsExact() {
		var file = new EditorSessionEntry { Path = "/ws/Notes.md", ViewState = null };
		var dashboard = new EditorSessionEntry { Path = "dashboard:Rollout@", Kind = "dashboard", ViewState = null };

		Assert.True(HostSession.IsTab(file, "/ws/notes.md", caseInsensitiveFiles: true));
		Assert.False(HostSession.IsTab(file, "/ws/notes.md", caseInsensitiveFiles: false));
		Assert.False(HostSession.IsTab(dashboard, "dashboard:rollout@", caseInsensitiveFiles: true));
		Assert.True(HostSession.IsTab(dashboard, "dashboard:Rollout@", caseInsensitiveFiles: true));
	}
}
