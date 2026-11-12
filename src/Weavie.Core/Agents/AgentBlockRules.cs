using System.Globalization;
using System.Text.RegularExpressions;

namespace Weavie.Core.Agents;

/// <summary>The bounds a block request must fit; a request over one is refused to the agent with the number.</summary>
public static class AgentBlockLimits {
	/// <summary>The largest tool-argument payload, in UTF-8 bytes.</summary>
	public const int ArgumentBytes = 1024 * 1024;
	/// <summary>Series per chart.</summary>
	public const int Series = 12;
	/// <summary>Points per series.</summary>
	public const int Points = 5000;
	/// <summary>Threshold lines or bands per chart.</summary>
	public const int Marks = 20;
	/// <summary>Rows per table.</summary>
	public const int Rows = 2000;
	/// <summary>Columns per table.</summary>
	public const int Columns = 24;
	/// <summary>Characters per text value, title, or name.</summary>
	public const int Text = 2000;
}

/// <summary>A block request the tool refuses; the message names the field, the value, and the limit.</summary>
public sealed class AgentBlockException(string message) : Exception(message);

/// <summary>Validates block tool requests and applies them to a conversation's current block.</summary>
public static partial class AgentBlockRules {
	/// <summary>Validates and normalizes a request against the conversation's current block with that id.</summary>
	public static AgentBlockOperation Validate(AgentBlockRequest request, AgentBlock? current, bool show) {
		ArgumentNullException.ThrowIfNull(request);
		if (!BlockId().IsMatch(request.Id)) Refuse("id must be 1-64 letters, digits, '.', '_' or '-'.");
		int parts = (request.Chart is null ? 0 : 1) + (request.Table is null ? 0 : 1) + (request.Append is null ? 0 : 1);
		if (show) {
			if (current is not null) Refuse($"Block '{request.Id}' is already shown; change it with updateBlock.");
			if (request.Append is not null || request.Keep is not null || parts != 1) Refuse("showBlock takes exactly one of 'chart' or 'table'.");
			string title = Text(request.Title, "title") is { Length: > 0 } given ? given : Fail<string>("showBlock requires a 'title'.");
			return new AgentBlockOperation.Show(new AgentBlock {
				Id = request.Id,
				Title = title,
				Chart = request.Chart is { } shownChart ? Chart(shownChart) : null,
				Table = request.Table is { } shownTable ? Table(shownTable) : null,
			});
		}
		if (current is null) {
			Refuse($"Block '{request.Id}' does not exist. If you showed it earlier, this agent did not report the tool result to Weavie; present the data as text.");
		}
		if (parts != 1) Refuse("updateBlock takes exactly one of 'chart', 'table', or 'append'.");
		if ((request.Chart is not null && current!.Chart is null) || (request.Table is not null && current!.Table is null)) {
			Refuse($"Block '{request.Id}' is a {current!.Kind}; an update keeps its kind.");
		}
		if ((request.Append is null) != (request.Keep is null)) Refuse("'keep' is required with 'append', and only with it.");
		return new AgentBlockOperation.Update(request with {
			Title = Text(request.Title, "title"),
			Chart = request.Chart is { } chart ? Chart(chart) : null,
			Table = request.Table is { } table ? Table(table) : null,
			Append = request.Append is { } append ? Append(append, current!, request.Keep!.Value) : null,
		});
	}

	/// <summary>The block after <paramref name="operation"/>, or null when it no longer applies to <paramref name="current"/>.</summary>
	public static AgentBlock? Apply(AgentBlock? current, AgentBlockOperation operation, long nowMs) {
		ArgumentNullException.ThrowIfNull(operation);
		if (operation is AgentBlockOperation.Show show) return current is null ? show.Block : null;
		var request = ((AgentBlockOperation.Update)operation).Request;
		if (current is null) return null;
		var next = current with { Title = request.Title ?? current.Title, UpdatedAtMs = nowMs };
		if (request.Chart is { } chart) return next with { Chart = chart };
		if (request.Table is { } table) return next with { Table = table };
		var append = request.Append!;
		int keep = request.Keep!.Value;
		if (current.Table is { } rows) return next with { Table = rows with { Rows = Last([.. rows.Rows, .. append.Rows ?? []], keep) } };
		var series = current.Chart!.Series.ToList();
		foreach (var added in append.Series ?? []) {
			int index = series.FindIndex(existing => existing.Name == added.Name);
			if (index < 0) return null;
			series[index] = series[index] with { Points = Last([.. series[index].Points, .. added.Points], keep) };
		}
		return next with { Chart = current.Chart with { Series = series } };
	}

	private static AgentChart Chart(AgentChart chart) {
		if (chart.Kind is not ("line" or "area" or "bar" or "scatter")) Refuse("chart.kind must be line, area, bar, or scatter.");
		string x = chart.X.Type is "time" or "category" or "number" ? chart.X.Type : Fail<string>("chart.x.type must be time, category, or number.");
		if (chart.Y?.Type is not null || chart.Y2?.Type is not null) Refuse("Only chart.x takes a 'type'.");
		Count(chart.Series.Count, AgentBlockLimits.Series, "chart.series", "series");
		if (chart.Series.Count == 0) Refuse("chart.series needs at least one series.");
		if (chart.Series.Select(series => series.Name).Distinct(StringComparer.Ordinal).Count() != chart.Series.Count) Refuse("chart.series names must be unique.");
		Count(chart.Thresholds?.Count ?? 0, AgentBlockLimits.Marks, "chart.thresholds", "thresholds");
		Count(chart.Bands?.Count ?? 0, AgentBlockLimits.Marks, "chart.bands", "bands");
		return chart with {
			X = Axis(chart.X, "chart.x"),
			Y = chart.Y is { } y ? Axis(y, "chart.y") : null,
			Y2 = chart.Y2 is { } y2 ? Axis(y2, "chart.y2") : null,
			Series = [.. chart.Series.Select((series, index) => {
				string path = $"chart.series[{index}]";
				if (series.Style is not (null or "solid" or "comparison")) Refuse($"{path}.style must be solid or comparison.");
				YAxis(series.Axis, chart, path);
				return series with { Name = Name(series.Name, path + ".name"), Points = Points(series.Points, x, path + ".points") };
			})],
			Thresholds = chart.Thresholds?.Select((threshold, index) => {
				YAxis(threshold.Axis, chart, $"chart.thresholds[{index}]");
				return threshold with { Label = Text(threshold.Label, $"chart.thresholds[{index}].label") };
			}).ToList(),
			Bands = chart.Bands?.Select((band, index) => {
				string path = $"chart.bands[{index}]";
				string scale = band.Axis is null or "x" ? x : band.Axis is "y" or "y2" ? "number" : Fail<string>($"{path}.axis must be x, y, or y2.");
				if (band.Axis == "y2" && chart.Y2 is null) Refuse($"{path} uses axis y2, but the chart has no y2.");
				return band with { Label = Text(band.Label, path + ".label"), From = Scalar(band.From, scale, path + ".from"), To = Scalar(band.To, scale, path + ".to") };
			}).ToList(),
		};
	}

	private static AgentTable Table(AgentTable table) {
		Count(table.Columns.Count, AgentBlockLimits.Columns, "table.columns", "columns");
		if (table.Columns.Count == 0) Refuse("table.columns needs at least one column.");
		if (table.Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != table.Columns.Count) Refuse("table.columns names must be unique.");
		var columns = table.Columns.Select((column, index) => column.Type is "time" or "number" or "text" or "level"
			? column with { Name = Name(column.Name, $"table.columns[{index}].name") }
			: Fail<AgentTableColumn>($"table.columns[{index}].type must be time, number, text, or level.")).ToList();
		return new AgentTable { Columns = columns, Rows = Rows(table.Rows, columns, "table.rows") };
	}

	private static AgentBlockAppend Append(AgentBlockAppend append, AgentBlock current, int keep) {
		if (current.Chart is { } chart) {
			if (append.Rows is not null || append.Series is not { Count: > 0 } series) return Fail<AgentBlockAppend>("append to a chart takes 'series'.");
			if (keep is < 1 or > AgentBlockLimits.Points) Refuse($"keep is {keep:N0}; it must be 1 to {AgentBlockLimits.Points:N0} points per series.");
			return new AgentBlockAppend {
				Series = [.. series.Select((added, index) => chart.Series.Any(existing => existing.Name == added.Name)
					? added with { Points = Points(added.Points, chart.X.Type!, $"append.series[{index}].points") }
					: Fail<AgentChartSeriesPoints>($"append.series[{index}] names '{added.Name}', which the chart does not have. Replace the chart to add a series."))],
			};
		}
		if (append.Series is not null || append.Rows is null) Refuse("append to a table takes 'rows'.");
		if (keep is < 1 or > AgentBlockLimits.Rows) Refuse($"keep is {keep:N0}; it must be 1 to {AgentBlockLimits.Rows:N0} rows.");
		return new AgentBlockAppend { Rows = Rows(append.Rows!, current.Table!.Columns, "append.rows") };
	}

	private static IReadOnlyList<IReadOnlyList<AgentBlockValue>> Points(IReadOnlyList<IReadOnlyList<AgentBlockValue>> points, string x, string path) {
		if (points.Count > AgentBlockLimits.Points) {
			Refuse($"{path} has {points.Count:N0} points; the limit is {AgentBlockLimits.Points:N0} per series. Aggregate and resend.");
		}
		return [.. points.Select((point, index) => point.Count == 2
			? (IReadOnlyList<AgentBlockValue>)[Scalar(point[0], x, $"{path}[{index}][0]"), Scalar(point[1], "number", $"{path}[{index}][1]")]
			: Fail<IReadOnlyList<AgentBlockValue>>($"{path}[{index}] must be an [x, y] pair."))];
	}

	private static IReadOnlyList<IReadOnlyList<AgentBlockValue>> Rows(IReadOnlyList<IReadOnlyList<AgentBlockValue>> rows, IReadOnlyList<AgentTableColumn> columns, string path) {
		if (rows.Count > AgentBlockLimits.Rows) {
			Refuse($"{path} has {rows.Count:N0} rows; the limit is {AgentBlockLimits.Rows:N0}. Aggregate or filter and resend.");
		}
		return [.. rows.Select((row, index) => row.Count == columns.Count
			? (IReadOnlyList<AgentBlockValue>)[.. row.Select((cell, column) => Scalar(cell, columns[column].Type, $"{path}[{index}][{column}]"))]
			: Fail<IReadOnlyList<AgentBlockValue>>($"{path}[{index}] has {row.Count} values; the table has {columns.Count} columns."))];
	}

	// Normalizes one value to its scale: times become Unix milliseconds, numbers numbers, everything else text.
	private static AgentBlockValue Scalar(AgentBlockValue value, string scale, string path) {
		if (value.IsNull) return value;
		switch (scale) {
			case "time":
				if (value.Number is not null) return value;
				return DateTimeOffset.TryParse(value.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
					? new(time.ToUnixTimeMilliseconds(), null)
					: Fail<AgentBlockValue>($"{path} is '{value.Text}'; a time is Unix milliseconds or an ISO 8601 string.");
			case "number":
				if (value.Number is { } number) return double.IsFinite(number) ? value : Fail<AgentBlockValue>($"{path} must be finite.");
				return double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
					? new(parsed, null)
					: Fail<AgentBlockValue>($"{path} is '{value.Text}'; it must be a number.");
			default:
				return new(null, Text(value.ToString(), path));
		}
	}

	private static void YAxis(string? axis, AgentChart chart, string path) {
		if (axis is not (null or "y" or "y2")) Refuse($"{path}.axis must be y or y2.");
		if (axis == "y2" && chart.Y2 is null) Refuse($"{path} uses axis y2, but the chart has no y2.");
	}

	private static AgentChartAxis Axis(AgentChartAxis axis, string path) =>
		axis with { Label = Text(axis.Label, path + ".label"), Unit = Text(axis.Unit, path + ".unit") };

	private static string Name(string name, string path) =>
		Text(name, path) is { Length: > 0 } valid ? valid : Fail<string>($"{path} must not be empty.");

	private static string? Text(string? text, string path) => text is { Length: > AgentBlockLimits.Text }
		? Fail<string>($"{path} has {text.Length:N0} characters; the limit is {AgentBlockLimits.Text:N0}.")
		: text?.Trim();

	private static void Count(int count, int limit, string path, string noun) {
		if (count > limit) Refuse($"{path} has {count:N0} {noun}; the limit is {limit:N0}.");
	}

	private static IReadOnlyList<T> Last<T>(IReadOnlyList<T> values, int keep) => values.Count <= keep ? values : [.. values.Skip(values.Count - keep)];

	private static void Refuse(string message) => throw new AgentBlockException(message);

	private static T Fail<T>(string message) => throw new AgentBlockException(message);

	[GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
	private static partial Regex BlockId();
}
