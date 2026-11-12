using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Weavie.Core.Mcp;

namespace Weavie.Core.Agents;

/// <summary>
/// One conversation's block tool calls awaiting their transcript rows. A call stages its operation and returns a
/// token; the conversation that sees the token come back on its own tool row takes the operation and commits it.
/// </summary>
public sealed partial class AgentBlockClaims {
	private readonly Lock _gate = new();
	private readonly Dictionary<string, AgentBlockOperation> _staged = new(StringComparer.Ordinal);
	private Func<string, AgentBlock?>? _find;

	/// <summary>Whether a conversation that can show blocks owns this grant.</summary>
	public bool Bound {
		get { lock (_gate) return _find is not null; }
	}

	/// <summary>Binds the owning conversation's block lookup; a grant has one owner.</summary>
	public void Bind(Func<string, AgentBlock?> find) {
		ArgumentNullException.ThrowIfNull(find);
		lock (_gate) {
			if (_find is not null) throw new InvalidOperationException("A conversation already owns these block claims.");
			_find = find;
		}
	}

	/// <summary>The owning conversation's current block <paramref name="id"/>.</summary>
	public AgentBlock? Find(string id) {
		Func<string, AgentBlock?> find;
		lock (_gate) find = _find ?? throw new ToolUnavailableException("Blocks are shown only in Weavie's agent pane.");
		return find(id);
	}

	/// <summary>Holds <paramref name="operation"/> until its row is claimed and returns the token that claims it.</summary>
	public string Stage(AgentBlockOperation operation) {
		ArgumentNullException.ThrowIfNull(operation);
		string token = "weavie-block:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		lock (_gate) _staged.Add(token, operation);
		return token;
	}

	/// <summary>Takes the operation whose token appears in a tool row's output, once.</summary>
	public AgentBlockOperation? Take(string? text, IReadOnlyList<AgentPaneContent>? content) {
		lock (_gate) {
			if (_staged.Count == 0) return null;
			foreach (string? output in (IEnumerable<string?>)[text, .. content?.Select(block => block.Text) ?? []]) {
				if (output is null) continue;
				foreach (var match in Token().EnumerateMatches(output)) {
					if (_staged.Remove(output.Substring(match.Index, match.Length), out var operation)) return operation;
				}
			}
			return null;
		}
	}

	/// <summary>Removes and returns every operation no row claimed.</summary>
	public IReadOnlyList<AgentBlockOperation> Drain() {
		lock (_gate) {
			AgentBlockOperation[] unclaimed = [.. _staged.Values];
			_staged.Clear();
			return unclaimed;
		}
	}

	[GeneratedRegex("weavie-block:[0-9a-f]{32}")]
	private static partial Regex Token();
}
