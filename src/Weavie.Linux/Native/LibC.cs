using System.Runtime.InteropServices;

namespace Weavie.Linux.Native;

/// <summary>Linux process primitives needed before the GTK host starts.</summary>
internal static partial class LibC {
	[LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int execv(string path, IntPtr arguments);

	[LibraryImport("libc", EntryPoint = "setenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int setenv(string name, string value, int overwrite);

	[LibraryImport("libc", EntryPoint = "unsetenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int unsetenv(string name);
}
