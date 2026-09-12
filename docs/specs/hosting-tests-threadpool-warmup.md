# Weavie.Hosting.Tests flake: ThreadPool starvation delays a `Task.Run` dispatch

Status: fixed, 2026-09-12

## Symptom

`main`'s `ci` runs at 2026-09-12T03:10Z ([run 34669658275](https://github.com/Kapps/weavie/actions/runs/34669658275), commit `73c4fb14`, PR #855's merge) and 2026-09-12T03:53Z ([run 34671553854](https://github.com/Kapps/weavie/actions/runs/34671553854), commit `bf76e9fa`, PR #874's merge) both failed the same single test with the identical stack:

```
Weavie.Hosting.Tests.HostCoreVanishedWorktreeTests.DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession [FAIL]
System.TimeoutException : Condition was not met within the timeout.
   at Weavie.TestSupport.Wait.UntilAsync(Func`1 condition, TimeSpan timeout) in Wait.cs:line 25
   at ...DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession() in HostCoreVanishedWorktreeTests.cs:line 57
```

Neither PR touches workspace/session lifecycle code (#855 is composer draft preservation, #874 is floating-tool
layout). The same test passed on the merges immediately before and after each failure.

## Root cause

`HostCore.WireSession` deliberately hops off the raising thread before closing a vanished session:

```csharp
session.WorkspaceRootVanished += () => _ = Task.Run(() => CloseVanishedSessionAsync(session));
```

(`HostCore.Sessions.cs:22`) — necessary because the event can fire from the UI thread, and part of the close path
(`_ui.InvokeAsync`) must not run there. That `Task.Run` queues onto the CLR's shared ThreadPool, whose minimum
thread count defaults to `Environment.ProcessorCount`. xunit runs every test collection in this assembly (676
tests) concurrently through that same pool. When enough collections are mid-startup at once, the pool is
saturated past its minimum and can only grow by roughly one thread per 500ms (the hill-climbing injection
throttle) — so a lone `Task.Run` like this one can sit queued for seconds behind unrelated tests' work, even
though `CloseVanishedSessionAsync` itself completes in under a millisecond once it runs.

Confirmed with a standalone repro (600 concurrent `Task.Run` callers, matching this assembly's test count, against
a default `minThreads` of 4 on a 4-core box): the one work item we time is delayed 3.4–3.5 seconds. Raising
`ThreadPool.SetMinThreads` to 32 before the burst drops that to ~130ms. `DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession`'s
`Wait.UntilAsync` uses the 5-second default, so on a slower or noisier CI runner the same effect tips it over.

This is not particular to this one test — any test in the assembly that waits on a `Task.Run`-dispatched
continuation is exposed to the same pool-startup race.

## Fix

`tests/Weavie.Hosting.Tests/ThreadPoolWarmup.cs` raises the process's ThreadPool minimum thread count once via a
`[ModuleInitializer]` — module initializers run exactly once before any test executes, ahead of xunit's parallel
collections. Same pattern as `AspNetCoreWarmup.cs` and `PosixFileLimitWarmup.cs`, which fix the identical class of
"xunit's parallel collections race a process-wide resource at startup" flake for the shared framework load and
the file-descriptor limit respectively.
