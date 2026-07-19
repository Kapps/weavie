using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;

namespace Weavie.Hosting.Tests;

/// <summary>Forces the ASP.NET Core framework to load once before xunit's parallel collections race it (see
/// docs/specs/hosting-tests-asp-net-warmup.md).</summary>
internal static class AspNetCoreWarmup {
	[ModuleInitializer]
	internal static void Warm() => _ = WebApplication.CreateBuilder();
}
