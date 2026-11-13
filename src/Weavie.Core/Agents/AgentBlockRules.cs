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
	/// <summary>Chart kinds.</summary>
	public static readonly IReadOnlyList<string> ChartKinds = ["line", "area", "bar", "scatter"];
	/// <summary>X-axis scales.</summary>
	public static readonly IReadOnlyList<string> XScales = ["time", "category", "number"];
	/// <summary>Series styles.</summary>
	public static readonly IReadOnlyList<string> SeriesStyles = ["solid", "comparison"];
	/// <summary>Vertical axes.</summary>
	public static readonly IReadOnlyList<string> YAxes = ["y", "y2"];
	/// <summary>Axes a band can lie along.</summary>
	public static readonly IReadOnlyList<string> BandAxes = ["x", "y", "y2"];
	/// <summary>Table column types.</summary>
	public static readonly IReadOnlyList<string> ColumnTypes = ["time", "number", "text", "level"];
	/// <summary>The id rule, as the tool schema states it.</summary>
	public const string IdRule = "1-64 letters, digits, '.', '_' or '-'";
	/// <summary>The MCP tool that changes a block.</summary>
	public const string UpdateTool = "updateBlock";

	/// <summary>
	/// Whether a tool row's title is <see cref="UpdateTool"/> under whatever prefix its agent gives an MCP tool; a
	/// command line that mentions the name is not. It decides only whether the in-flight row is drawn; claiming a
	/// block never reads a title.
	/// </summary>
	public static bool NamesUpdate(string? title) => title is not null && UpdateTitle().IsMatch(title);

	/// <summary>Validates and normalizes a request against the conversation's current block with that id.</summary>
	public static AgentBlockOperation Validate(AgentBlockRequest request, AgentBlock? current, bool show) {
		ArgumentNullException.ThrowIfNull(request);
		if (!BlockId().IsMatch(request.Id)) Refuse($"id must be {IdRule}.");
		int parts = (request.Chart is null ? 0 : 1) + (request.Table is null ? 0 : 1) + (request.Append is null ? 0 : 1);
		if (show) {
			if (current is not null) Refuse($"Block '{request.Id}' is already shown; change it with updateBlock.");
			if (request.Append is not null || parts != 1) Refuse("showBlock takes exactly one of 'chart' or 'table'.");
			string title = Text(request.Title, "title") is { Length: > 0 } given ? given : Fail<string>("showBlock requires a 'title'.");
			var (chart, table) = Content(request);
			return new AgentBlockOperation.Show(new AgentBlock { Id = request.Id, Title = title, Chart = chart, Table = table }, Dashboard(request.Dashboard));
		}
		if (request.Dashboard is not null) Refuse("updateBlock cannot move a block into a dashboard; show it again with showBlock under a new id.");
		if (current is null) {
			Refuse($"Block '{request.Id}' does not exist. If you showed it earlier, this agent did not report the tool result to Weavie; present the data as text.");
		}
		if (parts != 1) Refuse("updateBlock takes exactly one of 'chart', 'table', or 'append'.");
		if ((request.Chart is not null && current!.Chart is null) || (request.Table is not null && current!.Table is null)) {
			Refuse($"Block '{request.Id}' is a {current!.Kind}; an update keeps its kind.");
		}
		var (replacedChart, replacedTable) = Content(request);
		return new AgentBlockOperation.Update(request with {
			Title = Text(request.Title, "title"),
			Chart = replacedChart,
			Table = replacedTable,
			Append = request.Append is { } append ? Append(append, current!) : null,
		});
	}

	/// <summary>The block after <paramref name="operation"/>, or null when it no longer applies to <paramref name="current"/>.</summary>
	public static AgentBlock? Apply(AgentBlock? current, AgentBlockOperation operation, long nowMs) {
		ArgumentNullException.ThrowIfNull(operation);
		if (operation is AgentBlockOperation.Show show) return current is null ? show.Block : null;
		var request = ((AgentBlockOperation.Update)operation).Request;
		if (current is null) return null;
		var next = current with { Title = request.Title ?? current.Title, UpdatedAtMs = nowMs };
		if (request.Chart is { } chart) return current.Chart is null ? null : next with { Chart = chart };
		if (request.Table is { } table) return current.Table is null ? null : next with { Table = table };
		var append = request.Append!;
		int keep = append.Keep;
		// Validation saw the block as it was then; an update applied since may have changed its shape.
		if (current.Table is { } rows) {
			return append.Rows!.All(row => row.Count == rows.Columns.Count)
				? next with { Table = rows with { Rows = Last([.. rows.Rows, .. append.Rows!], keep) } }
				: null;
		}
		var series = current.Chart!.Series.ToList();
		foreach (var added in append.Series!) {
			int index = series.FindIndex(existing => existing.Name == added.Name);
			if (index < 0) series.Add(new AgentChartSeries { Name = added.Name, Points = Last(added.Points, keep) });
			else series[index] = series[index] with { Points = Last([.. series[index].Points, .. added.Points], keep) };
		}
		return series.Count > AgentBlockLimits.Series ? null : next with { Chart = current.Chart with { Series = series } };
	}

	private static (AgentChart? Chart, AgentTable? Table) Content(AgentBlockRequest request) =>
		(request.Chart is { } chart ? Chart(chart) : null, request.Table is { } table ? Table(table) : null);

	private static AgentChart Chart(AgentChart chart) {
		OneOf(chart.Kind, ChartKinds, "chart.kind");
		string x = OneOf(chart.X.Type, XScales, "chart.x.type");
		if (chart.Y?.Type is not null || chart.Y2?.Type is not null) Refuse("Only chart.x takes a 'type'.");
		var series = Items(chart.Series, AgentBlockLimits.Series, "chart.series");
		if (series.Count == 0) Refuse("chart.series needs at least one series.");
		Unique(series.Select(line => line.Name), "chart.series");
		return chart with {
			X = Axis(chart.X, "chart.x"),
			Y = chart.Y is { } y ? Axis(y, "chart.y") : null,
			Y2 = chart.Y2 is { } y2 ? Axis(y2, "chart.y2") : null,
			Series = [.. series.Select((line, index) => {
				string path = $"chart.series[{index}]";
				if (line.Style is not null) OneOf(line.Style, SeriesStyles, path + ".style");
				YAxis(line.Axis, chart, path);
				return line with { Name = Name(line.Name, path + ".name"), Points = Points(line.Points, x, path + ".points") };
			})],
			Thresholds = chart.Thresholds is null ? null : [.. Items(chart.Thresholds, AgentBlockLimits.Marks, "chart.thresholds").Select((threshold, index) => {
				string path = $"chart.thresholds[{index}]";
				YAxis(threshold.Axis, chart, path);
				Finite(threshold.Value, path + ".value");
				return threshold with { Label = Text(threshold.Label, path + ".label") };
			})],
			Bands = chart.Bands is null ? null : [.. Items(chart.Bands, AgentBlockLimits.Marks, "chart.bands").Select((band, index) => {
				string path = $"chart.bands[{index}]";
				string axis = band.Axis is null ? "x" : OneOf(band.Axis, BandAxes, path + ".axis");
				if (axis != "x") YAxis(axis, chart, path);
				string scale = axis == "x" ? x : "number";
				return band with { Label = Text(band.Label, path + ".label"), From = Scalar(band.From, scale, path + ".from"), To = Scalar(band.To, scale, path + ".to") };
			})],
		};
	}

	private static AgentTable Table(AgentTable table) {
		var columns = Items(table.Columns, AgentBlockLimits.Columns, "table.columns");
		if (columns.Count == 0) Refuse("table.columns needs at least one column.");
		Unique(columns.Select(column => column.Name), "table.columns");
		var normalized = columns.Select((column, index) => {
			OneOf(column.Type, ColumnTypes, $"table.columns[{index}].type");
			return column with { Name = Name(column.Name, $"table.columns[{index}].name") };
		}).ToList();
		return new AgentTable { Columns = normalized, Rows = Rows(table.Rows, normalized, "table.rows") };
	}

	private static AgentBlockAppend Append(AgentBlockAppend append, AgentBlock current) {
		int keep = append.Keep;
		if (current.Chart is { } chart) {
			if (append.Rows is not null || append.Series is not { Count: > 0 }) Refuse("append to a chart takes 'series'.");
			Keep(keep, AgentBlockLimits.Points, "points per series");
			var added = Items(append.Series!, AgentBlockLimits.Series, "append.series");
			Unique(added.Select(line => line.Name), "append.series");
			int total = chart.Series.Select(line => line.Name).Union(added.Select(line => line.Name), StringComparer.Ordinal).Count();
			if (total > AgentBlockLimits.Series) Refuse($"append.series would give the chart {total:N0} series; the limit is {AgentBlockLimits.Series:N0}.");
			return new AgentBlockAppend {
				Keep = keep,
				Series = [.. added.Select((line, index) => line with {
					Name = Name(line.Name, $"append.series[{index}].name"),
					Points = Points(line.Points, chart.X.Type!, $"append.series[{index}].points"),
				})],
			};
		}
		if (append.Series is not null || append.Rows is null) Refuse("append to a table takes 'rows'.");
		Keep(keep, AgentBlockLimits.Rows, "rows");
		return new AgentBlockAppend { Keep = keep, Rows = Rows(append.Rows!, current.Table!.Columns, "append.rows") };
	}

	private static IReadOnlyList<IReadOnlyList<AgentBlockValue>> Points(IReadOnlyList<IReadOnlyList<AgentBlockValue>> points, string x, string path) {
		if (points.Count > AgentBlockLimits.Points) {
			Refuse($"{path} has {points.Count:N0} points; the limit is {AgentBlockLimits.Points:N0} per series. Aggregate and resend.");
		}
		return [.. Items(points, AgentBlockLimits.Points, path).Select((point, index) => point.Count == 2
			? (IReadOnlyList<AgentBlockValue>)[Scalar(point[0], x, $"{path}[{index}][0]"), Scalar(point[1], "number", $"{path}[{index}][1]")]
			: Fail<IReadOnlyList<AgentBlockValue>>($"{path}[{index}] must be an [x, y] pair."))];
	}

	private static IReadOnlyList<IReadOnlyList<AgentBlockValue>> Rows(IReadOnlyList<IReadOnlyList<AgentBlockValue>> rows, IReadOnlyList<AgentTableColumn> columns, string path) {
		if (rows.Count > AgentBlockLimits.Rows) {
			Refuse($"{path} has {rows.Count:N0} rows; the limit is {AgentBlockLimits.Rows:N0}. Aggregate or filter and resend.");
		}
		return [.. Items(rows, AgentBlockLimits.Rows, path).Select((row, index) => row.Count == columns.Count
			? (IReadOnlyList<AgentBlockValue>)[.. row.Select((cell, column) => Scalar(cell, columns[column].Type, $"{path}[{index}][{column}]"))]
			: Fail<IReadOnlyList<AgentBlockValue>>($"{path}[{index}] has {row.Count} values; the table has {columns.Count} columns."))];
	}

	// Normalizes one value to its scale: times become Unix milliseconds, numbers numbers, everything else text.
	private static AgentBlockValue Scalar(AgentBlockValue value, string scale, string path) {
		if (value.IsNull) return value;
		switch (scale) {
			case "time":
				if (value.Number is { } ms) return new(Finite(ms, path), null);
				return DateTimeOffset.TryParse(value.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
					? new(time.ToUnixTimeMilliseconds(), null)
					: Fail<AgentBlockValue>($"{path} is '{value.Text}'; a time is Unix milliseconds or an ISO 8601 string.");
			case "number":
				if (value.Number is { } number) return new(Finite(number, path), null);
				return double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
					? new(parsed, null)
					: Fail<AgentBlockValue>($"{path} is '{value.Text}'; it must be a number.");
			default:
				return new(null, Text(value.ToString(), path));
		}
	}

	private static double Finite(double value, string path) => double.IsFinite(value) ? value : Fail<double>($"{path} must be a finite number.");

	// A list from the agent may hold JSON nulls, which the contract types can't express.
	private static IReadOnlyList<T> Items<T>(IReadOnlyList<T?> items, int limit, string path) where T : class {
		if (items.Count > limit) Refuse($"{path} has {items.Count:N0} items; the limit is {limit:N0}.");
		int empty = items.ToList().FindIndex(item => item is null);
		return empty < 0 ? [.. items.Select(item => item!)] : Fail<IReadOnlyList<T>>($"{path}[{empty}] must not be null.");
	}

	private static string OneOf(string? value, IReadOnlyList<string> allowed, string path) =>
		value is not null && allowed.Contains(value) ? value : Fail<string>($"{path} must be {string.Join(", ", allowed)}.");

	private static void Unique(IEnumerable<string> names, string path) {
		var list = names.ToList();
		if (list.Distinct(StringComparer.Ordinal).Count() != list.Count) Refuse($"{path} names must be unique.");
	}

	private static void Keep(int keep, int limit, string noun) {
		if (keep < 1 || keep > limit) Refuse($"keep is {keep:N0}; it must be 1 to {limit:N0} {noun}.");
	}

	private static void YAxis(string? axis, AgentChart chart, string path) {
		if (axis is not null) OneOf(axis, YAxes, path + ".axis");
		if (axis == "y2" && chart.Y2 is null) Refuse($"{path} uses axis y2, but the chart has no y2.");
	}

	private static AgentChartAxis Axis(AgentChartAxis axis, string path) =>
		axis with { Label = Text(axis.Label, path + ".label"), Unit = Text(axis.Unit, path + ".unit") };

	private static AgentDashboardRequest? Dashboard(AgentDashboardRequest? dashboard) {
		if (dashboard is null) return null;
		if (!BlockId().IsMatch(dashboard.Id)) Refuse($"dashboard.id must be {IdRule}.");
		return dashboard with { Title = Name(dashboard.Title, "dashboard.title") };
	}

	private static string Name(string name, string path) =>
		Text(name, path) is { Length: > 0 } valid ? valid : Fail<string>($"{path} must not be empty.");

	private static string? Text(string? text, string path) => text is { Length: > AgentBlockLimits.Text }
		? Fail<string>($"{path} has {text.Length:N0} characters; the limit is {AgentBlockLimits.Text:N0}.")
		: text?.Trim();

	private static IReadOnlyList<T> Last<T>(IReadOnlyList<T> values, int keep) => values.Count <= keep ? values : [.. values.Skip(values.Count - keep)];

	private static void Refuse(string message) => throw new AgentBlockException(message);

	private static T Fail<T>(string message) => throw new AgentBlockException(message);

	[GeneratedRegex(@"^(\S*[^A-Za-z0-9\s])?" + UpdateTool + "$")]
	private static partial Regex UpdateTitle();

	[GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
	private static partial Regex BlockId();
}
