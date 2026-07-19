using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Forces the Microsoft.AspNetCore.App shared framework to load once, single-threaded, before xunit's
/// parallel test collections can race each other into <see cref="Weavie.Hosting.Web.WorkspaceHttpServer"/>'s
/// first <c>WebApplication.CreateBuilder()</c> call. Concurrent first-time loads of the same framework
/// assembly across threads are a known CLR race (flaked as FileNotFoundException on
/// Microsoft.AspNetCore.Http, 2026-07-19T16:05Z:
/// https://github.com/Kapps/weavie/actions/runs/29694172917/job/88211947278 — 141/318 hosting tests failed
/// identically; the assembly is cached after one successful load, so this warm-up settles the race).
/// </summary>
internal static class AspNetCoreWarmup {
	[ModuleInitializer]
	internal static void Warm() {
		using var app = WebApplication.CreateBuilder().Build();
	}
}
