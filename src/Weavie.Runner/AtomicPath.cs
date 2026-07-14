using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Weavie.Runner;

internal static class AtomicPath {
	private const uint DeleteAccess = 0x00010000;
	private const uint FileFlagBackupSemantics = 0x02000000;
	private const uint FileFlagOpenReparsePoint = 0x00200000;
	private const int FileRenameInfoEx = 22;
	private const uint FileRenameReplaceIfExists = 0x1;
	private const uint FileRenamePosixSemantics = 0x2;

	internal static void Replace(string source, string destination) {
		if (!OperatingSystem.IsWindows()) {
			if (Rename(source, destination) != 0) {
				throw LastNativeError(destination);
			}
			return;
		}

		using var sourceHandle = CreateFile(
			source,
			DeleteAccess,
			FileShare.ReadWrite | FileShare.Delete,
			IntPtr.Zero,
			FileMode.Open,
			FileFlagBackupSemantics | FileFlagOpenReparsePoint,
			IntPtr.Zero);
		if (sourceHandle.IsInvalid) {
			throw LastNativeError(destination);
		}

		byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
		int rootOffset = IntPtr.Size == 8 ? 8 : 4;
		int nameOffset = rootOffset + IntPtr.Size + sizeof(uint);
		byte[] info = new byte[nameOffset + name.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(info, FileRenameReplaceIfExists | FileRenamePosixSemantics);
		BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(rootOffset + IntPtr.Size), checked((uint)name.Length));
		name.CopyTo(info, nameOffset);
		if (!SetFileInformationByHandle(sourceHandle, FileRenameInfoEx, info, checked((uint)info.Length))) {
			throw LastNativeError(destination);
		}
	}

	private static IOException LastNativeError(string destination) =>
		new(
			$"could not atomically replace {destination}",
			new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

	[DllImport("libc", EntryPoint = "rename", SetLastError = true, CharSet = CharSet.Ansi)]
	private static extern int Rename(string oldPath, string newPath);

	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFile(
		string fileName,
		uint desiredAccess,
		FileShare shareMode,
		IntPtr securityAttributes,
		FileMode creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetFileInformationByHandle(
		SafeFileHandle file,
		int fileInformationClass,
		byte[] fileInformation,
		uint bufferSize);
}
