using System.Runtime.InteropServices;

namespace Weavie.AcpDistribution;

internal static class AcpPlatformTarget {
	public static string Current() {
		string os = OperatingSystem.IsWindows() ? "windows"
			: OperatingSystem.IsMacOS() ? "darwin"
			: OperatingSystem.IsLinux() ? "linux"
			: throw new PlatformNotSupportedException("The ACP Registry does not support this operating system.");
		string architecture = RuntimeInformation.ProcessArchitecture switch {
			Architecture.X64 => "x86_64",
			Architecture.Arm64 => "aarch64",
			_ => throw new PlatformNotSupportedException(
				$"The ACP Registry does not support {RuntimeInformation.ProcessArchitecture}."),
		};
		return $"{os}-{architecture}";
	}
}
