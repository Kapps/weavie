namespace Weavie.Core.Git;

/// <summary>
/// Remembers the paths an ignore query reported as ignored for <see cref="Lifetime"/>, so repeated agent edits to an
/// ignored file don't each spawn git. Only ignored answers are kept: a non-ignored path gets tracked and is never re-asked.
/// </summary>
public sealed class IgnoredPathCache {
	/// <summary>How long an ignored answer is trusted before the query is asked again.</summary>
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

	private readonly Func<IReadOnlyList<string>, IReadOnlySet<string>> _query;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, DateTimeOffset> _expiry = new(StringComparer.Ordinal);

	/// <summary>Caches <paramref name="query"/>'s ignored answers, timed by <paramref name="time"/>.</summary>
	public IgnoredPathCache(Func<IReadOnlyList<string>, IReadOnlySet<string>> query, TimeProvider time) {
		ArgumentNullException.ThrowIfNull(query);
		ArgumentNullException.ThrowIfNull(time);
		_query = query;
		_time = time;
	}

	/// <summary>Which of <paramref name="paths"/> are ignored, asking the query only about paths not known to be.</summary>
	public IReadOnlySet<string> IgnoredAmong(IReadOnlyList<string> paths) {
		ArgumentNullException.ThrowIfNull(paths);
		var now = _time.GetUtcNow();
		var ignored = new HashSet<string>(StringComparer.Ordinal);
		var unknown = new List<string>();
		lock (_gate) {
			foreach (var (path, expires) in _expiry.ToList()) {
				if (expires <= now) _expiry.Remove(path);
			}

			foreach (string path in paths) {
				if (_expiry.ContainsKey(path)) ignored.Add(path);
				else unknown.Add(path);
			}
		}

		if (unknown.Count == 0) return ignored;
		var found = _query(unknown);
		lock (_gate) {
			foreach (string path in found) _expiry[path] = now + Lifetime;
		}

		ignored.UnionWith(found);
		return ignored;
	}
}
