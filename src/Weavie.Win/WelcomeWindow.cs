using Weavie.Win.Hosting;

namespace Weavie.Win;

/// <summary>
/// The app's empty state: shown on launch when there's no workspace to restore, and when the user picks
/// File ▸ Close Window on the last workspace window. Deliberately has <em>no</em> session — no WebView,
/// terminals, Claude, MCP, or LSP load until a folder is opened. Just an Open Folder action and a recent-
/// workspaces list. Closing it with nothing else open quits the app. Mac sibling: the equivalent empty
/// window in AppDelegate.
///
/// Sizing/layout are deliberately resolution-relative, not fixed pixels, so it looks right on any display:
/// the window opens at a fraction of the screen's working area (<see cref="SizeToScreen"/>), the content is
/// a centered column re-laid on every resize (<see cref="LayoutColumn"/>), and every row metric is scaled
/// through <c>LogicalToDeviceUnits</c> so DPI-scaled fonts never overflow their rows.
/// </summary>
internal sealed class WelcomeWindow : Form {
	// Fraction of the screen's working area the window opens at — "about half the screen".
	private const double WidthFraction = 0.5;
	private const double HeightFraction = 0.62;

	// Logical (96-DPI) layout metrics; scaled to device pixels via LogicalToDeviceUnits at use.
	private const int MaxColumnWidth = 820;
	private const int ColumnMargin = 52;
	private const int RowPadX = 16;

	private static readonly Color Bg = Color.FromArgb(0x1e, 0x1e, 0x1e);
	private static readonly Color Fg = Color.FromArgb(0xd4, 0xd4, 0xd4);
	private static readonly Color Dim = Color.FromArgb(0x8a, 0x8a, 0x8a);
	private static readonly Color Accent = Color.FromArgb(0x4e, 0xc9, 0xb0);
	private static readonly Color AccentHover = Color.FromArgb(0x63, 0xd8, 0xc2);
	private static readonly Color Border = Color.FromArgb(0x33, 0x33, 0x33);
	private static readonly Color RowHover = Color.FromArgb(0x2a, 0x2d, 0x2e);

	private readonly AppController _app;
	private readonly TableLayoutPanel _column;
	private readonly ListBox _recentList;
	private readonly Label _emptyHint;
	private readonly Button _openButton;
	private readonly Font _nameFont = new("Segoe UI", 11f, FontStyle.Bold);
	private readonly Font _pathFont = new("Segoe UI", 9f);

	public WelcomeWindow(AppController app) {
		ArgumentNullException.ThrowIfNull(app);
		_app = app;

		Text = "weavie";
		BackColor = Bg;
		ForeColor = Fg;
		Font = new Font("Segoe UI", 10.5f);
		ClientSize = new Size(900, 620); // fallback; SizeToScreen overrides on Load
		MinimumSize = new Size(620, 460);
		StartPosition = FormStartPosition.Manual;

		var title = new Label {
			Text = "weavie",
			Font = new Font("Segoe UI", 32f, FontStyle.Bold),
			ForeColor = Accent,
			AutoSize = true,
			Margin = new Padding(0, 0, 0, 2),
		};

		var subtitle = new Label {
			Text = "Open a folder to start a workspace.",
			ForeColor = Dim,
			AutoSize = true,
			Margin = new Padding(3, 0, 0, 0),
		};

		_openButton = new Button {
			Text = "Open Folder…",
			FlatStyle = FlatStyle.Flat,
			BackColor = Accent,
			ForeColor = Bg,
			Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
			AutoSize = false,
			Margin = new Padding(3, 24, 0, 0),
			Cursor = Cursors.Hand,
		};
		_openButton.FlatAppearance.BorderSize = 0;
		_openButton.FlatAppearance.MouseOverBackColor = AccentHover;
		_openButton.FlatAppearance.MouseDownBackColor = AccentHover;
		_openButton.Click += (_, _) => _app.OpenFolderInteractive(this);

		var recentLabel = new Label {
			Text = "Recent",
			ForeColor = Dim,
			AutoSize = true,
			Margin = new Padding(3, 36, 0, 8),
		};

		// 1px hairline frame: a Border-colored host with 1px padding around a Bg-filled list.
		var recentHost = new Panel {
			BackColor = Border,
			Padding = new Padding(1),
			Dock = DockStyle.Fill,
			Margin = new Padding(0),
		};

		_recentList = new ListBox {
			BackColor = Bg,
			ForeColor = Fg,
			BorderStyle = BorderStyle.None,
			DrawMode = DrawMode.OwnerDrawFixed,
			IntegralHeight = false,
			Dock = DockStyle.Fill,
		};
		_recentList.DrawItem += OnDrawRecentItem;
		_recentList.DoubleClick += OnRecentActivated;
		_recentList.KeyDown += (_, e) => {
			if (e.KeyCode == Keys.Enter) {
				OnRecentActivated(_recentList, EventArgs.Empty);
			}
		};

		_emptyHint = new Label {
			Text = "No recent folders yet — open one to get started.",
			ForeColor = Dim,
			TextAlign = ContentAlignment.MiddleCenter,
			Dock = DockStyle.Fill,
			Visible = false,
		};

		// Label first, list on top: when there are no recents we hide the list and the hint shows through.
		recentHost.Controls.Add(_emptyHint);
		recentHost.Controls.Add(_recentList);

		_column = new TableLayoutPanel {
			ColumnCount = 1,
			RowCount = 5,
			BackColor = Bg,
		};
		_column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		_column.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
		_column.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // subtitle
		_column.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // open button
		_column.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // recent label
		_column.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // recent list (fills)
		_column.Controls.Add(title, 0, 0);
		_column.Controls.Add(subtitle, 0, 1);
		_column.Controls.Add(_openButton, 0, 2);
		_column.Controls.Add(recentLabel, 0, 3);
		_column.Controls.Add(recentHost, 0, 4);

		Controls.Add(_column);

		Load += (_, _) => {
			SizeToScreen();
			PopulateRecent();
		};
		Resize += (_, _) => LayoutColumn();
	}

	/// <inheritdoc/>
	protected override void OnHandleCreated(EventArgs e) {
		base.OnHandleCreated(e);
		NativeChrome.UseDarkTitleBar(Handle);

		// Size the row and button to the live DPI: point-size fonts render larger at high DPI, so fixed
		// pixel heights would clip. Measure the actual line heights and pad in scaled units.
		using var g = CreateGraphics();
		int nameH = TextRenderer.MeasureText(g, "Ag", _nameFont).Height;
		int pathH = TextRenderer.MeasureText(g, "Ag", _pathFont).Height;
		_recentList.ItemHeight = nameH + pathH + LogicalToDeviceUnits(18);
		_openButton.Size = new Size(LogicalToDeviceUnits(176), LogicalToDeviceUnits(46));
	}

	/// <summary>Opens the window at a fraction of the primary screen's working area, centered on it.</summary>
	private void SizeToScreen() {
		var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
		int w = Math.Max((int)(area.Width * WidthFraction), MinimumSize.Width);
		int h = Math.Max((int)(area.Height * HeightFraction), MinimumSize.Height);
		Bounds = new Rectangle(
			area.X + ((area.Width - w) / 2),
			area.Y + ((area.Height - h) / 2),
			w,
			h);
	}

	/// <summary>Centers the content column horizontally (clamped to <see cref="MaxColumnWidth"/>) and fills the height.</summary>
	private void LayoutColumn() {
		int margin = LogicalToDeviceUnits(ColumnMargin);
		int maxWidth = LogicalToDeviceUnits(MaxColumnWidth);
		int minWidth = LogicalToDeviceUnits(280);
		int width = Math.Clamp(ClientSize.Width - (margin * 2), minWidth, maxWidth);
		int x = (ClientSize.Width - width) / 2;
		int height = Math.Max(ClientSize.Height - (margin * 2), LogicalToDeviceUnits(200));
		_column.SetBounds(x, margin, width, height);
	}

	private void PopulateRecent() {
		_recentList.BeginUpdate();
		_recentList.Items.Clear();
		foreach (string path in _app.Recents.Items) {
			_recentList.Items.Add(path);
		}

		_recentList.EndUpdate();

		bool any = _recentList.Items.Count > 0;
		_recentList.Visible = any;
		_emptyHint.Visible = !any;
		if (any) {
			_recentList.SelectedIndex = 0;
		}
	}

	private void OnDrawRecentItem(object? sender, DrawItemEventArgs e) {
		if (e.Index < 0 || e.Index >= _recentList.Items.Count) {
			return;
		}

		string path = (string)_recentList.Items[e.Index];
		bool selected = (e.State & DrawItemState.Selected) != 0;
		var bounds = e.Bounds;
		var g = e.Graphics;

		using (var bg = new SolidBrush(selected ? RowHover : Bg)) {
			g.FillRectangle(bg, bounds);
		}

		if (selected) {
			using var accent = new SolidBrush(Accent);
			g.FillRectangle(accent, new Rectangle(bounds.Left, bounds.Top, LogicalToDeviceUnits(3), bounds.Height));
		}

		// Vertically center the two-line block (name over dimmed path) using measured line heights, so it
		// stays clear of the row edges at any DPI.
		int nameH = TextRenderer.MeasureText(g, "Ag", _nameFont).Height;
		int pathH = TextRenderer.MeasureText(g, "Ag", _pathFont).Height;
		int gap = LogicalToDeviceUnits(2);
		int top = bounds.Top + Math.Max((bounds.Height - (nameH + gap + pathH)) / 2, LogicalToDeviceUnits(4));
		int padX = LogicalToDeviceUnits(RowPadX);
		int textWidth = bounds.Width - (padX * 2);
		const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
		TextRenderer.DrawText(g, FolderLeaf(path), _nameFont, new Rectangle(bounds.Left + padX, top, textWidth, nameH), Fg, flags);
		TextRenderer.DrawText(g, path, _pathFont, new Rectangle(bounds.Left + padX, top + nameH + gap, textWidth, pathH), Dim, flags);
	}

	private void OnRecentActivated(object? sender, EventArgs e) {
		if (_recentList.SelectedItem is string path) {
			_app.OpenOrFocus(path);
		}
	}

	/// <summary>The folder's leaf name (e.g. <c>weavie</c> for <c>C:\src\weavie</c>), falling back to the full path.</summary>
	private static string FolderLeaf(string path) {
		string leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return string.IsNullOrEmpty(leaf) ? path : leaf;
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing) {
		if (disposing) {
			_nameFont.Dispose();
			_pathFont.Dispose();
		}

		base.Dispose(disposing);
	}
}
