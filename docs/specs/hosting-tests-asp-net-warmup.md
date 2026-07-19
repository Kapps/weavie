# Weavie.Hosting.Tests flake: concurrent first-load of Microsoft.AspNetCore.App

Status: fixed, 2026-07-19

## Symptom

`main`'s `ci` run at 2026-07-19T16:05Z ([job 88211947278](https://github.com/Kapps/weavie/actions/runs/29694172917/job/88211947278), commit `9e6ed00d`, PR #430's merge) failed 141 of 318 `Weavie.Hosting.Tests`, every one with the identical stack:

```
System.IO.FileNotFoundException: Could not load file or assembly 'Microsoft.AspNetCore.Http, Version=10.0.0.0, ...'
   at Microsoft.AspNetCore.Hosting.GenericWebHostBuilder.<.ctor>b__4_2(...)
   at Microsoft.AspNetCore.Builder.WebApplicationBuilder.InitializeHosting(...)
   at Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
   at Weavie.Hosting.Web.WorkspaceHttpServer.StartAsync() ...
   at Weavie.Hosting.Tests.TestHost.StartAsync(...)
```

The very next CI run on a superset commit (same test code, three more merges on top) passed 318/318. Locally the suite ran clean four times in a row. Not a regression from #430 — #430 touches WebSocket subprotocol negotiation, nowhere near ASP.NET Core hosting startup.

## Root cause

xunit runs test collections in parallel (one collection per class by default). Every test that calls `TestHost.StartAsync()` reaches `WebApplication.CreateBuilder()` for the first time in that process. When many threads race to trigger the *first* load of the `Microsoft.AspNetCore.App` shared framework concurrently, the CLR can hit a transient assembly-load race and throw `FileNotFoundException`. Once that load fails once for the process, later `CreateBuilder()` calls on other threads fail identically — explaining why the failure hit 141 tests at once rather than one.

## Fix

`tests/Weavie.Hosting.Tests/AspNetCoreWarmup.cs` forces the load once, single-threaded, via a `[ModuleInitializer]` — module initializers run exactly once before any test executes, ahead of xunit's parallel collections. Same pattern as `TestRoot.cs`'s `WEAVIE_ROOT` redirect.
