using System.Text;

namespace Weavie.Core.Terminal;

/// <summary>
/// Latches the terminal state a PTY child has established by scanning its raw output stream — DECSET/DECRST
/// private modes, SM/RM, and the OSC 0/2 window title — so a client xterm that attaches to an already-live
/// child (a reattach, reload, or background-loaded session) can be brought to the same state via
/// <see cref="BuildRestore"/>. One instance per child launch; a restart gets a fresh tracker, which is the reset.
/// <para>Transient state is excluded at record time: <c>?2026</c> (synchronized output — replaying a latched
/// "begin sync" would freeze the client's rendering) and <c>?1048</c> (cursor save/restore, per-frame drawing
/// state). Scroll regions, cursor style, charsets, and other drawing state are consumed but never recorded —
/// the TUI re-establishes those on its next redraw.</para>
/// </summary>
public sealed class TerminalModeTracker {
	// Mirrors xterm.js's own parser bounds so host-side latching can't diverge from what the client accepted.
	private const int MaxParams = 32;
	private const int MaxOscPayload = 10_000_000;
	private static readonly int[] ExcludedPrivateModes = [1048, 2026];

	private enum State { Ground, Escape, EscapeOneMore, Csi, OscSelector, OscPayload, StringSkip }

	private readonly Lock _gate = new();
	private readonly Dictionary<int, bool> _privateModes = [];
	private readonly Dictionary<int, bool> _modes = [];
	private byte[]? _title;

	// Parser state, kept across Feed calls because sequences arrive split over arbitrary chunk boundaries.
	private State _state = State.Ground;
	private bool _csiPrivate;
	private bool _csiIgnored; // non-'?' prefix, an intermediate byte, or a param overflow: consume but never record
	private readonly List<int> _csiParams = [];
	private int _csiCurrent;
	private bool _csiHasDigits;
	private int _oscSelector;
	private bool _oscRetain;
	private bool _stringSawEsc;
	private readonly List<byte> _oscPayload = [];

	/// <summary>
	/// Scans a chunk of raw PTY output, latching any mode/title changes it contains. Sequences may be split
	/// across chunks. Safe to call from the child's read thread while <see cref="BuildRestore"/> runs elsewhere.
	/// </summary>
	public void Feed(ReadOnlySpan<byte> data) {
		lock (_gate) {
			int i = 0;
			while (i < data.Length) {
				if (_state == State.Ground) {
					int esc = data[i..].IndexOf((byte)0x1b);
					if (esc < 0) {
						return;
					}

					i += esc + 1;
					_state = State.Escape;
					continue;
				}

				byte b = data[i];
				i++;
				switch (_state) {
					case State.Escape:
						OnEscapeByte(b);
						break;
					case State.EscapeOneMore:
						_state = State.Ground; // the charset/alignment byte itself carries no latched state
						break;
					case State.Csi:
						OnCsiByte(b);
						break;
					case State.OscSelector:
					case State.OscPayload:
					case State.StringSkip:
						OnStringByte(b);
						break;
					default:
						_state = State.Ground;
						break;
				}
			}
		}
	}

	/// <summary>
	/// The synthesized restore preamble bringing a fresh xterm to the latched state: the title, then any
	/// buffer-switch mode (47/1047/1049) so later state lands in the right buffer, then the remaining private
	/// modes and SM/RM modes in ascending order. Empty when nothing was latched.
	/// </summary>
	public byte[] BuildRestore() {
		lock (_gate) {
			if (_title is null && _privateModes.Count == 0 && _modes.Count == 0) {
				return [];
			}

			using var restore = new MemoryStream();
			if (_title is not null) {
				restore.Write("\x1b]0;"u8);
				restore.Write(_title);
				restore.WriteByte(0x07);
			}

			foreach (var (mode, set) in _privateModes.OrderBy(m => m.Key is not (47 or 1047 or 1049)).ThenBy(m => m.Key)) {
				restore.Write(Encoding.ASCII.GetBytes($"\x1b[?{mode}{(set ? 'h' : 'l')}"));
			}

			foreach (var (mode, set) in _modes.OrderBy(m => m.Key)) {
				restore.Write(Encoding.ASCII.GetBytes($"\x1b[{mode}{(set ? 'h' : 'l')}"));
			}

			return restore.ToArray();
		}
	}

	private void OnEscapeByte(byte b) {
		switch (b) {
			case (byte)'[':
				_csiPrivate = false;
				_csiIgnored = false;
				_csiParams.Clear();
				_csiCurrent = 0;
				_csiHasDigits = false;
				_state = State.Csi;
				break;
			case (byte)']':
				_oscSelector = 0;
				_oscRetain = true; // until a non-0/2 selector digit proves otherwise
				_oscPayload.Clear();
				_stringSawEsc = false;
				_state = State.OscSelector;
				break;
			case (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_': // DCS/SOS/PM/APC: skip to ST
				_stringSawEsc = false;
				_state = State.StringSkip;
				break;
			case (byte)'(' or (byte)')' or (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.'
				or (byte)'/' or (byte)'#' or (byte)'%' or (byte)' ':
				_state = State.EscapeOneMore;
				break;
			case 0x1b: // ESC ESC: the first is discarded, the second still introduces a sequence
				break;
			default: // ESC 7/8/=/>/c/D/E/M… — single-byte forms, none latched here
				_state = State.Ground;
				break;
		}
	}

	private void OnCsiByte(byte b) {
		if (b is >= (byte)'0' and <= (byte)'9') {
			// Clamp instead of overflowing; an absurd param can only come from a malformed stream.
			_csiCurrent = _csiCurrent < 100_000_000 ? (_csiCurrent * 10) + (b - (byte)'0') : _csiCurrent;
			_csiHasDigits = true;
			return;
		}

		switch (b) {
			case (byte)';':
				PushCsiParam();
				return;
			case (byte)'?' when !_csiHasDigits && _csiParams.Count == 0:
				_csiPrivate = true;
				return;
			case (byte)'?' or (byte)'<' or (byte)'=' or (byte)'>':
				_csiIgnored = true; // kitty/xterm-private prefixes, or a prefix after params: consume, never latch
				return;
			case >= 0x20 and <= 0x2f: // intermediate (e.g. DECSCUSR's space): the sequence is not a mode set
				_csiIgnored = true;
				return;
			case >= 0x40 and <= 0x7e: // final byte
				PushCsiParam();
				if (!_csiIgnored && (b == (byte)'h' || b == (byte)'l')) {
					RecordModes(set: b == (byte)'h');
				}

				_state = State.Ground;
				return;
			case 0x1b: // aborted mid-sequence
				_state = State.Escape;
				return;
			case 0x18 or 0x1a: // CAN/SUB abort the sequence outright (as the client's parser does)
				_state = State.Ground;
				return;
			default: // stray C0 inside CSI: ignore, keep collecting
				return;
		}
	}

	private void PushCsiParam() {
		if (!_csiHasDigits) {
			return;
		}

		if (_csiParams.Count >= MaxParams) {
			_csiIgnored = true;
		} else {
			_csiParams.Add(_csiCurrent);
		}

		_csiCurrent = 0;
		_csiHasDigits = false;
	}

	private void RecordModes(bool set) {
		foreach (int mode in _csiParams) {
			if (mode == 0) {
				continue;
			}

			if (_csiPrivate) {
				if (!ExcludedPrivateModes.Contains(mode)) {
					_privateModes[mode] = set;
				}
			} else {
				_modes[mode] = set;
			}
		}
	}

	private void OnStringByte(byte b) {
		// ST (ESC \) and, for OSC, BEL terminate; everything else is selector/payload or skipped.
		if (_stringSawEsc) {
			_stringSawEsc = false;
			if (b == (byte)'\\') {
				EndString();
				return;
			}

			// A bare ESC aborts the string; reprocess the byte as an escape introducer.
			_state = State.Escape;
			OnEscapeByte(b);
			return;
		}

		if (b == 0x1b) {
			_stringSawEsc = true;
			return;
		}

		if (b is 0x18 or 0x1a) { // CAN/SUB abort the string without applying it
			_oscRetain = false;
			EndString();
			return;
		}

		if (b == 0x07 && _state != State.StringSkip) {
			EndString();
			return;
		}

		switch (_state) {
			case State.OscSelector when b is >= (byte)'0' and <= (byte)'9':
				_oscSelector = _oscSelector < 100_000_000 ? (_oscSelector * 10) + (b - (byte)'0') : _oscSelector;
				break;
			case State.OscSelector when b == (byte)';':
				_oscRetain = _oscSelector is 0 or 2;
				_state = State.OscPayload;
				break;
			case State.OscSelector: // malformed selector: skip the rest of the string
				_oscRetain = false;
				_state = State.OscPayload;
				break;
			case State.OscPayload when _oscRetain:
				if (_oscPayload.Count >= MaxOscPayload) {
					_oscRetain = false; // oversized: the client aborts it too, so latch nothing
					_oscPayload.Clear();
				} else {
					_oscPayload.Add(b);
				}

				break;
			default:
				break;
		}
	}

	private void EndString() {
		if (_state == State.OscPayload && _oscRetain) {
			_title = [.. _oscPayload];
		}

		_oscPayload.Clear();
		_state = State.Ground;
	}
}
