using System.Runtime.InteropServices;

namespace Weavie.Core.Lsp;

internal sealed partial class LinuxWorkspaceDirectoryWatchSet {
	private const uint WatchMask = InModify
		| InAttrib
		| InCloseWrite
		| InMovedFrom
		| InMovedTo
		| InCreate
		| InDelete
		| InDeleteSelf
		| InMoveSelf
		| InOnlyDir;
	private const uint InModify = 0x00000002;
	private const uint InAttrib = 0x00000004;
	private const uint InCloseWrite = 0x00000008;
	private const uint InMovedFrom = 0x00000040;
	private const uint InMovedTo = 0x00000080;
	private const uint InCreate = 0x00000100;
	private const uint InDelete = 0x00000200;
	private const uint InDeleteSelf = 0x00000400;
	private const uint InMoveSelf = 0x00000800;
	private const uint InQueueOverflow = 0x00004000;
	private const uint InIgnored = 0x00008000;
	private const uint InOnlyDir = 0x01000000;
	private const int CloseOnExec = 0x00080000;
	private const int NonBlocking = 0x00000800;
	private const short PollIn = 0x0001;
	private const short PollFailure = 0x0038;
	private const int Interrupted = 4;
	private const int NoSuchFileOrDirectory = 2;
	private const int WouldBlock = 11;
	private const int EventHeaderSize = 16;

	[StructLayout(LayoutKind.Sequential)]
	private struct PollDescriptor {
		public int FileDescriptor;
		public short Events;
		public short ReturnedEvents;
	}

	[LibraryImport("libc", SetLastError = true)]
	private static partial int inotify_init1(int flags);

	[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int inotify_add_watch(int fd, string pathname, uint mask);

	[LibraryImport("libc", SetLastError = true)]
	private static partial int inotify_rm_watch(int fd, int wd);

	[LibraryImport("libc", SetLastError = true)]
	private static partial int eventfd(uint initval, int flags);

	[LibraryImport("libc", SetLastError = true)]
	private static unsafe partial int poll(PollDescriptor* fds, nuint nfds, int timeout);

	[LibraryImport("libc", SetLastError = true)]
	private static partial nint read(int fd, byte[] buffer, nuint count);

	[LibraryImport("libc", SetLastError = true)]
	private static partial nint write(int fd, byte[] buffer, nuint count);

	[LibraryImport("libc", SetLastError = true)]
	private static partial int close(int fd);
}
