namespace Weavie.Win.Hosting;

/// <summary>
/// Builds the Windows menu bar for a workspace window — the native UI for the Core open-folder /
/// recents functionality (<see cref="AppController"/> + <c>WorkspaceManager</c>). Dark-styled to match
/// the web chrome (styles.css <c>--bar</c>/<c>--fg</c>/<c>--border</c>), since the default WinForms
/// MenuStrip is light gray. Mac sibling: the NSMenu built in AppDelegate.
/// </summary>
internal static class AppMenu {
	private static readonly Color BarColor = Color.FromArgb(0x25, 0x25, 0x26);
	private static readonly Color FgColor = Color.FromArgb(0xd4, 0xd4, 0xd4);
	private static readonly Color BorderColor = Color.FromArgb(0x33, 0x33, 0x33);
	private static readonly Color SelectionColor = Color.FromArgb(0x09, 0x47, 0x71);
	private static bool _darkChromeInstalled;

	/// <summary>
	/// Installs the dark menu renderer process-wide. Setting the global <see cref="ToolStripManager.Renderer"/>
	/// (rather than a per-strip renderer) is the reliable way to override WinForms' light default — a
	/// per-instance renderer is silently ignored unless render mode is flipped. Idempotent; UI thread only.
	/// </summary>
	public static void UseDarkChrome() {
		if (_darkChromeInstalled) {
			return;
		}

		ToolStripManager.Renderer = new DarkRenderer();
		_darkChromeInstalled = true;
	}

	/// <summary>Builds the File menu bar for <paramref name="owner"/>, wired to <paramref name="app"/>'s actions.</summary>
	public static MenuStrip Build(Form owner, AppController app) {
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(app);

		var menu = new MenuStrip {
			Dock = DockStyle.Top,
			BackColor = BarColor,
			ForeColor = FgColor,
		};

		var openFolder = new ToolStripMenuItem("Open Folder…", null, (_, _) => app.OpenFolderInteractive(owner)) {
			ShortcutKeys = Keys.Control | Keys.O,
			ForeColor = FgColor,
		};
		var openRecent = new ToolStripMenuItem("Open &Recent") { ForeColor = FgColor };
		// Rebuilt each time it opens so it always reflects the current recents (and across windows).
		openRecent.DropDownOpening += (_, _) => PopulateRecent(openRecent, app);
		// Seed one item so the submenu arrow shows before it's first opened.
		openRecent.DropDownItems.Add(new ToolStripMenuItem("(loading…)") { Enabled = false });

		var closeWindow = new ToolStripMenuItem("Close Window", null, (_, _) => owner.Close()) {
			ShortcutKeys = Keys.Control | Keys.W,
			ForeColor = FgColor,
		};
		var exit = new ToolStripMenuItem("Exit", null, (_, _) => app.Quit()) { ForeColor = FgColor };

		var file = new ToolStripMenuItem("&File") { ForeColor = FgColor };
		file.DropDownItems.AddRange([
			openFolder,
			openRecent,
			new ToolStripSeparator(),
			closeWindow,
			exit,
		]);

		menu.Items.Add(file);
		return menu;
	}

	private static void PopulateRecent(ToolStripMenuItem openRecent, AppController app) {
		openRecent.DropDownItems.Clear();
		var items = app.Recents.Items;
		if (items.Count == 0) {
			openRecent.DropDownItems.Add(new ToolStripMenuItem("(no recent folders)") { Enabled = false, ForeColor = FgColor });
			return;
		}

		foreach (string path in items) {
			string captured = path;
			openRecent.DropDownItems.Add(new ToolStripMenuItem(DisplayName(captured), null, (_, _) => app.OpenOrFocus(captured)) {
				ToolTipText = captured,
				ForeColor = FgColor,
			});
		}
	}

	private static string DisplayName(string path) {
		string leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return string.IsNullOrEmpty(leaf) ? path : $"{leaf}    {path}";
	}

	/// <summary>
	/// A dark menu renderer that paints backgrounds and text explicitly (the stock professional renderer is
	/// light gray). Borders/margins still come from the dark color table.
	/// </summary>
	private sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColorTable()) {
		protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
			e.Graphics.Clear(BarColor);

		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
			e.TextColor = e.Item.Enabled ? FgColor : Color.FromArgb(0x80, 0x80, 0x80);
			base.OnRenderItemText(e);
		}

		protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
			bool active = e.Item.Selected || e.Item.Pressed;
			using var brush = new SolidBrush(active ? SelectionColor : BarColor);
			e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
		}
	}

	private sealed class DarkColorTable : ProfessionalColorTable {
		public override Color ToolStripDropDownBackground => BarColor;
		public override Color ImageMarginGradientBegin => BarColor;
		public override Color ImageMarginGradientMiddle => BarColor;
		public override Color ImageMarginGradientEnd => BarColor;
		public override Color MenuItemSelected => SelectionColor;
		public override Color MenuItemBorder => SelectionColor;
		public override Color MenuBorder => BorderColor;
		public override Color SeparatorDark => BorderColor;
		public override Color SeparatorLight => BorderColor;
	}
}
