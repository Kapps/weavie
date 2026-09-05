using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Weavie.Core.Processes;

namespace Weavie.Mac.Tests;

internal static class ProcessTreeProbe {
	internal static int Run(string mode, string resultPath) {
		int host = Environment.ProcessId;
		int hostGroup = Group(host);
		WriteState(resultPath, "started", ("host", host), ("hostGroup", hostGroup));
		if (hostGroup != host) {
			throw new InvalidOperationException($"LaunchServices host {host} is not group leader ({hostGroup}).");
		}

		var start = new ProcessStartInfo("/bin/sh") {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add("sleep 300 & printf '%s\\n' $!; wait");
		bool owned = mode == "--owned-process-probe";
		if (owned) {
			using var child = OwnedProcess.Start(start);
			return Terminate(resultPath, host, hostGroup, true, child.Id, child.StandardOutput,
				() => child.Kill(entireProcessTree: true), child.WaitForExit);
		}
		using var control = Process.Start(start)
			?? throw new InvalidOperationException("Could not launch process-tree fixture.");
		return Terminate(resultPath, host, hostGroup, false, control.Id, control.StandardOutput,
			() => control.Kill(entireProcessTree: true), control.WaitForExit);
	}

	private static int Terminate(string resultPath, int host, int hostGroup, bool owned,
		int childId, TextReader output, Action kill, Action wait) {
		int descendant = int.Parse(output.ReadLine()
			?? throw new InvalidOperationException("Fixture exited before readiness."), CultureInfo.InvariantCulture);
		int childGroup = Group(childId);
		int descendantGroup = Group(descendant);
		WriteState(resultPath, "killing", ("host", host), ("hostGroup", hostGroup),
			("child", childId), ("childGroup", childGroup), ("descendant", descendant), ("descendantGroup", descendantGroup));
		if (owned && (childGroup == hostGroup || descendantGroup != childGroup)) {
			throw new InvalidOperationException("Owned process tree shares the GUI group or lost its descendant.");
		}
		if (!owned && childGroup != hostGroup) {
			throw new InvalidOperationException("Unisolated control did not inherit the GUI process group.");
		}

		kill();
		wait();
		if (!SpinWait.SpinUntil(() => Dead(descendant), TimeSpan.FromSeconds(5))) {
			throw new InvalidOperationException($"Descendant {descendant} survived tree termination.");
		}
		WriteState(resultPath, "survived", ("host", host), ("hostGroup", hostGroup),
			("child", childId), ("childGroup", childGroup), ("descendant", descendant), ("descendantGroup", descendantGroup));
		return 0;
	}

	private static void WriteState(string path, string phase, params (string Name, int Value)[] fields) {
		using var stream = File.Create(path);
		using var json = new Utf8JsonWriter(stream);
		json.WriteStartObject();
		json.WriteString("phase", phase);
		foreach (var (name, value) in fields) json.WriteNumber(name, value);
		json.WriteEndObject();
	}

	private static int Group(int pid) => int.Parse(Ps(pid, "pgid="), CultureInfo.InvariantCulture);

	private static bool Dead(int pid) {
		string state = Ps(pid, "stat=").Trim();
		return state.Length == 0 || state.StartsWith('Z');
	}

	private static string Ps(int pid, string field) {
		var start = new ProcessStartInfo("/bin/ps") {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		start.ArgumentList.Add("-o");
		start.ArgumentList.Add(field);
		start.ArgumentList.Add("-p");
		start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
		using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch ps.");
		string output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return output;
	}
}
