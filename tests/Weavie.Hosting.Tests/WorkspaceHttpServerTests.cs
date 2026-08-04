using Microsoft.Extensions.Configuration;
using Weavie.Hosting.Web;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class WorkspaceHttpServerTests {
	[Fact]
	public void ApplicationBuilderDoesNotWatchConfigurationFiles() {
		var builder = WorkspaceHttpServer.CreateApplicationBuilder();

		Assert.DoesNotContain(
			builder.Configuration.Sources,
			static source => source is FileConfigurationSource);
	}
}
