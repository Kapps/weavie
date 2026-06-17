using Weavie.Win.Hosting;

namespace Weavie.Win;

/// <summary>
/// The app's empty state: shown on launch when there's no workspace to restore, and whenever the last
/// workspace window closes. Deliberately has <em>no</em> session — no WebView, terminals, Claude, MCP, or
/// LSP load until a folder is opened. Just an Open Folder action and a recent-workspaces list. Closing it
/// with nothing else open quits the app. Mac sibling: the equivalent empty window in AppDelegate.
/// </summary>
internal sealed class WelcomeWindow : Form {
	private static readonly Color Bg = Color.FromArgb(0x1e, 0x1e, 0x1e);
	private static readonly Color Fg = Color.FromArgb(0xd4, 0xd4, 0xd4);
	private static readonly Color Dim = Color.FromArgb(0x80, 0x80, 0x80);
	private static readonly Color Accent = Color.FromArgb(0x4e, 0xc9, 0xb0);
	private static readonly Color Surface = Color.FromArgb(0x25, 0x25, 0x26);

	private readonly AppController _app;
	private readonly ListBox _recentList;

	public WelcomeWindow(AppController app) {
		ArgumentNullException.ThrowIfNull(app);
		_app = app;

		Text = "weavie";
		BackColor = Bg;
		ForeColor = Fg;
		ClientSize = new Size(580, 440);
		StartPosition = FormStartPosition.CenterScreen;
		MinimumSize = new Size(420, 320);
		Font = new Font("Segoe UI", 9f);

		var title = new Label {
			Text = "weavie",
			Font = new Font("Segoe UI", 26f, FontStyle.Bold),
			ForeColor = Accent,
			AutoSize = true,
			Location = new Point(28, 28),
		};

		var subtitle = new Label {
			Text = "Open a folder to start a workspace.",
			ForeColor = Dim,
			AutoSize = true,
			Location = new Point(31, 78),
		};

		var openButton = new Button {
			Text = "Open Folder…",
			FlatStyle = FlatStyle.Flat,
			BackColor = Surface,
			ForeColor = Fg,
			Location = new Point(31, 108),
			Size = new Size(140, 34),
			Anchor = AnchorStyles.Top | AnchorStyles.Left,
		};
		openButton.FlatAppearance.BorderColor = Color.FromArgb(0x33, 0x33, 0x33);
		openButton.Click += (_, _) => _app.OpenFolderInteractive(this);

		var recentLabel = new Label {
			Text = "Recent",
			ForeColor = Dim,
			AutoSize = true,
			Location = new Point(31, 162),
		};

		_recentList = new ListBox {
			BackColor = Bg,
			ForeColor = Fg,
			BorderStyle = BorderStyle.FixedSingle,
			IntegralHeight = false,
			Location = new Point(31, 184),
			Size = new Size(518, 224),
			Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
		};
		_recentList.DoubleClick += OnRecentActivated;
		_recentList.KeyDown += (_, e) => {
			if (e.KeyCode == Keys.Enter) {
				OnRecentActivated(_recentList, EventArgs.Empty);
			}
		};

		Controls.AddRange([title, subtitle, openButton, recentLabel, _recentList]);

		Load += (_, _) => PopulateRecent();
	}

	/// <inheritdoc/>
	protected override void OnHandleCreated(EventArgs e) {
		base.OnHandleCreated(e);
		NativeChrome.UseDarkTitleBar(Handle);
	}

	private void PopulateRecent() {
		_recentList.BeginUpdate();
		_recentList.Items.Clear();
		foreach (string path in _app.Recents.Items) {
			_recentList.Items.Add(path);
		}

		_recentList.EndUpdate();
	}

	private void OnRecentActivated(object? sender, EventArgs e) {
		if (_recentList.SelectedItem is string path) {
			_app.OpenOrFocus(path);
		}
	}
}
