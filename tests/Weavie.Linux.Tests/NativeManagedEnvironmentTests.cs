using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class NativeManagedEnvironmentTests {
	[Fact]
	public async Task ManagedChildEnvironment_OmitsNativeOnlyVariable() {
		const string name = "WEAVIE_TEST_NATIVE_MANAGED_ENVIRONMENT_SPLIT";
		Assert.Equal(0, SetNativeEnvironmentVariable(name, "1", overwrite: 1));
		try {
			Assert.Equal("1", Marshal.PtrToStringUTF8(GetNativeEnvironmentVariable(name)));

			Environment.SetEnvironmentVariable(name, null);

			Assert.Null(Environment.GetEnvironmentVariable(name));
			Assert.Equal("1", Marshal.PtrToStringUTF8(GetNativeEnvironmentVariable(name)));
			var info = new ProcessStartInfo("/usr/bin/env") {
				RedirectStandardOutput = true,
				UseShellExecute = false,
			};
			using var process = Process.Start(info)!;
			string output = await process.StandardOutput.ReadToEndAsync();
			await process.WaitForExitAsync();
			Assert.Equal(0, process.ExitCode);
			Assert.DoesNotContain(output.Split('\n'), line => line.StartsWith($"{name}=", StringComparison.Ordinal));
		} finally {
			Assert.Equal(0, UnsetNativeEnvironmentVariable(name));
		}
	}

	[DllImport("libc", EntryPoint = "getenv")]
	private static extern IntPtr GetNativeEnvironmentVariable(string name);

	[DllImport("libc", EntryPoint = "setenv")]
	private static extern int SetNativeEnvironmentVariable(string name, string value, int overwrite);

	[DllImport("libc", EntryPoint = "unsetenv")]
	private static extern int UnsetNativeEnvironmentVariable(string name);
}
