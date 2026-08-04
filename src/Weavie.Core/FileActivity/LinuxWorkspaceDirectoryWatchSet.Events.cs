using System.Runtime.InteropServices;
using System.Text;

namespace Weavie.Core.FileActivity;

internal sealed partial class LinuxWorkspaceDirectoryWatchSet {
	private void ReadAvailableEvents(byte[] buffer) {
		nint length = read(_inotifyFd, buffer, (nuint)buffer.Length);
		if (length < 0) {
			if (Marshal.GetLastPInvokeError() == WouldBlock) {
				return;
			}

			throw NativeFailure("read(inotify)");
		}

		int offset = 0;
		while (offset + EventHeaderSize <= length) {
			int watch = BitConverter.ToInt32(buffer, offset);
			uint mask = BitConverter.ToUInt32(buffer, offset + 4);
			uint cookie = BitConverter.ToUInt32(buffer, offset + 8);
			uint nameBufferLength = BitConverter.ToUInt32(buffer, offset + 12);
			int next = checked(offset + EventHeaderSize + (int)nameBufferLength);
			if (next > length) {
				throw new IOException("inotify returned a truncated event.");
			}

			HandleEvent(
				watch,
				mask,
				cookie,
				buffer.AsSpan(offset + EventHeaderSize, (int)nameBufferLength));
			offset = next;
		}

	}

	private void HandleEvent(
		int watch,
		uint mask,
		uint cookie,
		ReadOnlySpan<byte> nameBuffer) {
		if ((mask & InQueueOverflow) != 0) {
			_error(new IOException("The Linux workspace-change queue overflowed."));
			return;
		}

		string? directory;
		lock (_gate) {
			_watchPaths.TryGetValue(watch, out directory);
			if ((mask & InIgnored) != 0 && directory is not null) {
				_watchPaths.Remove(watch);
				_pathWatches.Remove(directory);
			}
		}

		if (directory is null || (mask & InIgnored) != 0) {
			return;
		}

		int nameLength = nameBuffer.IndexOf((byte)0);
		if (nameLength < 0) {
			nameLength = nameBuffer.Length;
		}

		string path = nameLength == 0
			? directory
			: Path.Combine(directory, Encoding.UTF8.GetString(nameBuffer[..nameLength]));
		if ((mask & InMovedFrom) != 0) {
			_pendingMoves[cookie] = (path, Environment.TickCount64 + MovePairTimeoutMilliseconds);
			return;
		}

		if ((mask & InMovedTo) != 0) {
			if (_pendingMoves.Remove(cookie, out var move)) {
				RekeyMovedWatch(move.Path, path);
				_renamed(move.Path, path);
			} else {
				_created(Change(path, WatcherChangeTypes.Created));
			}

			return;
		}

		if ((mask & (InDelete | InDeleteSelf)) != 0) {
			_deleted(Change(path, WatcherChangeTypes.Deleted));
		}

		if ((mask & InCreate) != 0) {
			_created(Change(path, WatcherChangeTypes.Created));
		}

		if ((mask & (InModify | InAttrib | InCloseWrite)) != 0) {
			_changed(Change(path, WatcherChangeTypes.Changed));
		}
	}

	private int PendingMoveTimeout() {
		if (_pendingMoves.Count == 0) {
			return -1;
		}

		long deadline = _pendingMoves.Values.Min(move => move.Deadline);
		return Math.Max(0, (int)(deadline - Environment.TickCount64));
	}

	private void FlushExpiredMoves() {
		long now = Environment.TickCount64;
		foreach (var (cookie, move) in _pendingMoves.Where(entry => entry.Value.Deadline <= now).ToArray()) {
			_pendingMoves.Remove(cookie);
			_deleted(Change(move.Path, WatcherChangeTypes.Deleted));
		}
	}

	private void RekeyMovedWatch(string oldPath, string newPath) {
		lock (_gate) {
			string prefix = oldPath + Path.DirectorySeparatorChar;
			foreach (var (path, watch) in _pathWatches
				.Where(entry => entry.Key == oldPath || entry.Key.StartsWith(prefix, StringComparison.Ordinal))
				.ToArray()) {
				string rebased = newPath + path[oldPath.Length..];
				_pathWatches.Remove(path);
				_pathWatches[rebased] = watch;
				_watchPaths[watch] = rebased;
			}
		}
	}

	private static FileSystemEventArgs Change(string path, WatcherChangeTypes kind) =>
		new(kind, Path.GetDirectoryName(path)!, Path.GetFileName(path));
}
