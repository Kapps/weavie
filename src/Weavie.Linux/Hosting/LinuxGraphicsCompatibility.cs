using System.Globalization;
using System.Runtime.InteropServices;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>Configures native graphics before GTK while isolating the workaround from managed children.</summary>
internal static class LinuxGraphicsCompatibility {
	private const string WaylandDisplay = "WAYLAND_DISPLAY";
	private const string DisableNvidiaExplicitSync = "__NV_DISABLE_EXPLICIT_SYNC";
	private const string ReexecMarker = "WEAVIE_NVIDIA_IMPLICIT_SYNC_REEXEC";
	private const string SelfCommandLine = "/proc/self/cmdline";
	private const string SelfExecutable = "/proc/self/exe";

	/// <summary>Applies startup compatibility before any GTK or WebKit native call.</summary>
	internal static void Apply() {
		string? marker = Environment.GetEnvironmentVariable(ReexecMarker);
		if (marker is not null) {
			string processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
			if (marker != processId || Environment.GetEnvironmentVariable(DisableNvidiaExplicitSync) != "1") {
				throw new InvalidOperationException("The NVIDIA implicit-sync process handoff was invalid.");
			}

			UnsetNativeEnvironmentVariable(ReexecMarker);
			// .NET 10 caches managed child environments separately; native WebKit helpers still inherit libc environ.
			UnsetManagedEnvironmentVariable(ReexecMarker);
			UnsetManagedEnvironmentVariable(DisableNvidiaExplicitSync);
			return;
		}

		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(WaylandDisplay))
			|| Environment.GetEnvironmentVariable(DisableNvidiaExplicitSync) is not null) {
			return;
		}

		// WebKit 280210: WebKitGTK commits without an acquire fence on NVIDIA explicit-sync surfaces.
		SetNativeEnvironmentVariable(DisableNvidiaExplicitSync, "1");
		SetNativeEnvironmentVariable(ReexecMarker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
		ReplaceCurrentProcess();
	}

	private static void SetNativeEnvironmentVariable(string name, string value) {
		if (LibC.setenv(name, value, overwrite: 1) != 0) {
			throw new System.ComponentModel.Win32Exception(
				Marshal.GetLastPInvokeError(), $"Could not set native environment variable {name}.");
		}
	}

	private static void UnsetNativeEnvironmentVariable(string name) {
		if (LibC.unsetenv(name) != 0) {
			throw new System.ComponentModel.Win32Exception(
				Marshal.GetLastPInvokeError(), $"Could not clear native environment variable {name}.");
		}
	}

	private static void UnsetManagedEnvironmentVariable(string name) =>
		Environment.SetEnvironmentVariable(name, null);

	private static void ReplaceCurrentProcess() {
		byte[] commandLine = File.ReadAllBytes(SelfCommandLine);
		if (commandLine.Length == 0 || commandLine[^1] != 0) {
			throw new InvalidOperationException($"{SelfCommandLine} was empty or not NUL-terminated.");
		}

		nint[] nativeArguments = new IntPtr[commandLine.Count(value => value == 0) + 1];
		GCHandle pinnedArguments = default;
		try {
			int argumentIndex = 0;
			int argumentStart = 0;
			for (int i = 0; i < commandLine.Length; i++) {
				if (commandLine[i] != 0) {
					continue;
				}

				int length = i - argumentStart;
				IntPtr argument = Marshal.AllocHGlobal(length + 1);
				if (length > 0) {
					Marshal.Copy(commandLine, argumentStart, argument, length);
				}

				Marshal.WriteByte(argument, length, 0);
				nativeArguments[argumentIndex++] = argument;
				argumentStart = i + 1;
			}

			pinnedArguments = GCHandle.Alloc(nativeArguments, GCHandleType.Pinned);
			_ = LibC.execv(SelfExecutable, pinnedArguments.AddrOfPinnedObject());
			throw new System.ComponentModel.Win32Exception(
				Marshal.GetLastPInvokeError(), $"Could not replace the process through {SelfExecutable}.");
		} finally {
			if (pinnedArguments.IsAllocated) {
				pinnedArguments.Free();
			}

			foreach (IntPtr argument in nativeArguments) {
				Marshal.FreeHGlobal(argument);
			}
		}
	}
}
