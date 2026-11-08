using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    public sealed class WidgetSettings
    {
        public int Transparency = 50;
        public bool Pinned;
        public string BackgroundPath;
        public int Left = Int32.MinValue, Top = Int32.MinValue;
        // Row keys ("claude", "codex", "antigravity", "cursor", "account:<id>") in display order, and built-in rows the user removed.
        public List<string> Order = new List<string>(), Hidden = new List<string>();
        internal string FilePath = null;
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageWidget"); } }
        public static WidgetSettings Parse(string text)
        {
            try
            {
                var result = new JavaScriptSerializer().Deserialize<WidgetSettings>(text) ?? new WidgetSettings();
                result.Transparency = Math.Max(0, Math.Min(100, result.Transparency));
                result.Order = (result.Order ?? new List<string>()).Where(k => !String.IsNullOrEmpty(k)).Distinct().Take(256).ToList();
                result.Hidden = (result.Hidden ?? new List<string>()).Where(k => !String.IsNullOrEmpty(k)).Distinct().Take(256).ToList();
                return result;
            }
            catch (ArgumentException) { return new WidgetSettings(); }
            catch (InvalidOperationException) { return new WidgetSettings(); }
            catch (OverflowException) { return new WidgetSettings(); }
            catch (FormatException) { return new WidgetSettings(); }
            catch (Exception e)
            {
                if (e.InnerException is OverflowException || e.InnerException is FormatException || e.InnerException is ArgumentException) return new WidgetSettings();
                throw;
            }
        }
        public static WidgetSettings Load()
        {
            try
            {
                string path = Path.Combine(Folder, "settings.json");
                return File.Exists(path) && new FileInfo(path).Length <= 65536 ? Parse(File.ReadAllText(path)) : new WidgetSettings();
            }
            catch (IOException) { return new WidgetSettings(); }
            catch (UnauthorizedAccessException) { return new WidgetSettings(); }
        }
        public static void ReplaceFile(string temporary, string destination)
        {
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        public void Save()
        {
            string destination = FilePath ?? Path.Combine(Folder, "settings.json"), temporary = destination + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this));
            ReplaceFile(temporary, destination);
        }
    }
    internal static class Shapes
    {
        public static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
    }
    internal sealed class MeterBar : Control
    {
        double? target;
        float shown;
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 15 };
        public double? Value
        {
            get { return target; }
            set
            {
                if (value == target) return;
                target = value;
                // Animate only while visible on screen; hidden or off-screen meters snap so renders and checks see the real value.
                if (IsHandleCreated && Visible && FindForm() != null && FindForm().Visible) animation.Start();
                else { shown = Goal; animation.Stop(); Invalidate(); }
            }
        }
        float Goal { get { return target.HasValue ? (float)Math.Max(0, Math.Min(100, target.Value)) : 0; } }
        public bool Stale;
        public Color FillColor;
        public MeterBar()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent; AccessibleRole = AccessibleRole.ProgressBar; TabStop = false;
            animation.Tick += delegate {
                shown += (Goal - shown) * 0.16f;
                if (Math.Abs(Goal - shown) < 0.15f) { shown = Goal; animation.Stop(); }
                Invalidate();
            };
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new MeterAccessibility(this); }
        sealed class MeterAccessibility : ControlAccessibleObject
        {
            readonly MeterBar meter;
            public MeterAccessibility(MeterBar owner) : base(owner) { meter = owner; }
            public override string Value { get { return meter.Value.HasValue ? meter.Value.Value.ToString("0.#", CultureInfo.InvariantCulture) + " percent left" + (meter.Stale ? ", stale" : "") : "Not reported"; } }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var track = Shapes.Rounded(new RectangleF(0, Height / 2f - 3, Width, 6), 3))
            using (var brush = new SolidBrush(Color.FromArgb(42, 52, 68))) e.Graphics.FillPath(brush, track);
            if (shown <= 0.05f) return;
            float width = Math.Max(2, Width * shown / 100);
            using (var fill = Shapes.Rounded(new RectangleF(0, Height / 2f - 3, width, 6), 3))
            using (var brush = new SolidBrush(FillColor)) e.Graphics.FillPath(brush, fill);
        }
    }
    // Fixed viewport with eased scrolling and a thin overlay thumb instead of the native scrollbar.
    internal sealed class AccountsPanel : Panel
    {
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 15 };
        int contentHeight, scrollY, dragOffset = -1;
        float smoothY, targetY;
        bool hover;
        public AccountsPanel()
        {
            DoubleBuffered = true; BackColor = Color.Transparent;
            SetStyle(ControlStyles.ResizeRedraw, true);
            animation.Tick += delegate {
                smoothY += (targetY - smoothY) * 0.22f;
                if (Math.Abs(targetY - smoothY) < 0.5f) { smoothY = targetY; animation.Stop(); }
                ApplyScroll((int)Math.Round(smoothY));
            };
        }
        public int ScrollY { get { return scrollY; } }
        public int MaxScroll { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
        public bool CanScroll { get { return MaxScroll > 0; } }
        public int ContentHeight
        {
            get { return contentHeight; }
            set { contentHeight = value; targetY = Math.Min(targetY, MaxScroll); smoothY = Math.Min(smoothY, MaxScroll); ApplyScroll(Math.Min(scrollY, MaxScroll)); }
        }
        public void ScrollTo(float y, bool smooth = true)
        {
            targetY = Math.Max(0, Math.Min(MaxScroll, y));
            if (smooth && IsHandleCreated && Visible) animation.Start();
            else { animation.Stop(); smoothY = targetY; ApplyScroll((int)Math.Round(targetY)); }
        }
        void ApplyScroll(int y)
        {
            if (y == scrollY) return;
            int delta = scrollY - y; scrollY = y;
            SuspendLayout();
            foreach (Control child in Controls) child.Top += delta;
            ResumeLayout(false);
            Invalidate(true);
        }
        RectangleF Thumb
        {
            get
            {
                float scale = DeviceDpi / 96f, h = ClientSize.Height, track = h - 8 * scale;
                float size = Math.Max(24 * scale, track * h / Math.Max(1, contentHeight));
                float top = 4 * scale + (track - size) * (MaxScroll == 0 ? 0 : scrollY / (float)MaxScroll);
                float width = (hover || dragOffset >= 0 ? 5 : 3) * scale;
                return new RectangleF(ClientSize.Width - 4 * scale - width, top, width, size);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!CanScroll) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var thumb = Thumb;
            using (var path = Shapes.Rounded(thumb, thumb.Width / 2))
            using (var brush = new SolidBrush(Color.FromArgb(hover || dragOffset >= 0 ? 150 : 80, 220, 228, 240))) e.Graphics.FillPath(brush, path);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
            ScrollTo(targetY - e.Delta / 120f * 48 * DeviceDpi / 96f);
        }
        bool OverGutter(Point p) { return CanScroll && p.X >= ClientSize.Width - 14 * DeviceDpi / 96f; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !OverGutter(e.Location)) return;
            var thumb = Thumb;
            if (e.Y < thumb.Top || e.Y > thumb.Bottom) ScrollTo(targetY + (e.Y < thumb.Top ? -1 : 1) * ClientSize.Height * 0.8f);
            else { dragOffset = (int)(e.Y - thumb.Top); Capture = true; Invalidate(); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragOffset >= 0) {
                var thumb = Thumb; float range = ClientSize.Height - 8 * DeviceDpi / 96f - thumb.Height;
                if (range > 0) ScrollTo((e.Y - dragOffset - 4 * DeviceDpi / 96f) / range * MaxScroll, false);
                return;
            }
            bool over = OverGutter(e.Location);
            if (over != hover) { hover = over; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragOffset >= 0) { dragOffset = -1; Capture = false; hover = OverGutter(e.Location); Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover && dragOffset < 0) { hover = false; Invalidate(); }
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
    internal sealed class WidgetForm : Form
    {
        readonly WidgetSettings settings;
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly ToolTip tips = new ToolTip();
        readonly Label heading;
        readonly AccountsPanel accounts;
        readonly List<ProviderView> providers = new List<ProviderView>();
        readonly CheckBox pin;
        readonly Font pinnedFont;
        readonly ContextMenuStrip mainMenu = new ContextMenuStrip();
        readonly ProviderView claude, codex, antigravity;
        Bitmap background, backdrop;
        bool closing, closeReady, layingOut;
        // Paint the window and all children into one buffer so transparent labels never flicker while scrolling or updating.
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x02000000; return p; } }
        static readonly Color Orange = Color.FromArgb(217, 119, 87), Blue = Color.FromArgb(91, 155, 255), Teal = Color.FromArgb(92, 211, 183), Silver = Color.FromArgb(214, 220, 230);
        sealed class LaneView { public Label Name, Text; public MeterBar Meter; }
        sealed class ProviderView
        {
            public bool Codex, Busy, Antigravity, Cursor;
            public bool Custom { get { return Antigravity || Cursor; } }
            public Task ActiveTask = Task.FromResult(0);
            public ProviderState State = new ProviderState();
            public MeterBar FiveHour, Weekly;
            public Label FiveText, WeekText, FiveLabel, WeekLabel, Title, Account, Plan;
            public PictureBox Logo;
            public int TopLogical, HeightLogical;
            public DateTimeOffset NextIdentityCheck;
            public string CacheIdentity, CachePath;
            public AccountProfile Source;
            public ProfileState ProfileCache = new ProfileState();
            public ContextMenuStrip AccountMenu;
            public List<LaneView> Lanes = new List<LaneView>();
        }
        public WidgetForm(WidgetSettings value)
        {
            settings = value;
            Text = "AI Usage"; Name = "AIUsageWidget";
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f); BackColor = Color.FromArgb(14, 19, 29); ForeColor = Color.FromArgb(235, 240, 248);
            pinnedFont = new Font(Font, FontStyle.Bold);
            ClientSize = new Size(340, 256); FormBorderStyle = FormBorderStyle.FixedSingle;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }
            MaximizeBox = false; StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
            Opacity = 1.0;
            heading = AddLabel("SUBSCRIPTION USAGE", 18, 7, 200, 26, 8f, Color.FromArgb(164, 177, 199), false);
            heading.TextAlign = ContentAlignment.MiddleLeft;
            pin = new CheckBox { Text = "Pin", Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(236, 7, 46, 26), Checked = settings.Pinned, AccessibleName = "Always on top", TabIndex = 0 };
            pin.FlatAppearance.BorderSize = 0; Controls.Add(pin);
            pin.CheckedChanged += delegate { TopMost = settings.Pinned = pin.Checked; StylePin(); SaveSettings(); };
            TopMost = pin.Checked; StylePin();
            var configure = new Button { Text = "···", FlatStyle = FlatStyle.Flat, Bounds = new Rectangle(289, 7, 34, 26), AccessibleName = "Widget settings", TabIndex = 1 };
            mainMenu.Items.Add("Add account", null, async delegate { await ShowAccountDialogAsync(null); });
            mainMenu.Items.Add("Background settings", null, delegate { ShowSettings(); });
            var restore = mainMenu.Items.Add("Show removed subscriptions", null, delegate { ShowRemovedRows(); });
            mainMenu.Opening += delegate { restore.Visible = settings.Hidden.Count > 0; };
            configure.ContextMenuStrip = mainMenu;
            configure.FlatAppearance.BorderSize = 0; configure.Click += delegate { mainMenu.Show(configure, new Point(0, configure.Height)); }; Controls.Add(configure);
            tips.SetToolTip(pin, "Keep this window above other windows"); tips.SetToolTip(configure, "Accounts and background settings");
            accounts = new AccountsPanel { Bounds = new Rectangle(0, 40, 340, 216), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, AccessibleName = "Accounts", TabIndex = 2 };
            accounts.Paint += PaintAccounts; accounts.ClientSizeChanged += delegate { LayoutMeters(); };
            // Right-clicking the empty part of a card opens the same row menu as its labels and bars.
            accounts.MouseUp += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Right) return;
                float y = (e.Y + accounts.ScrollY) / (DeviceDpi / 96f);
                var row = providers.FirstOrDefault(v => v.HeightLogical > 0 && y >= v.TopLogical - 4 && y < v.TopLogical - 4 + v.HeightLogical);
                if (row != null) row.AccountMenu.Show(accounts, e.Location);
            };
            Controls.Add(accounts);
            claude = AddProvider(false, "CLAUDE", Orange, 4);
            codex = AddProvider(true, "CODEX", Blue, 114);
            antigravity = AddProvider(false, "AGY", Teal, 0); antigravity.Antigravity = true;
            antigravity.CachePath = Path.Combine(WidgetSettings.Folder, "antigravity-usage.json");
            if (CursorClient.DesktopInstalled) {
                var cursor = AddProvider(false, "CURSOR", Silver, 0); cursor.Cursor = true;
                cursor.CachePath = Path.Combine(WidgetSettings.Folder, "cursor-usage.json");
            }
            ApplySavedOrder();
            if (!String.IsNullOrEmpty(settings.BackgroundPath))
            {
                try { background = ReadImage(settings.BackgroundPath); }
                catch (Exception e)
                {
                    if (!(e is IOException || e is ArgumentException || e is OutOfMemoryException || e is UnauthorizedAccessException)) throw;
                    settings.BackgroundPath = null; heading.AccessibleDescription = "Background unavailable · choose another image";
                    tips.SetToolTip(heading, heading.AccessibleDescription);
                }
            }
            Shown += async delegate {
                try { foreach (var profile in AccountStore.Load(AccountStorePath)) AttachAccount(profile); }
                catch (Exception e) {
                    if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)) throw;
                    heading.AccessibleDescription = "Saved accounts could not be loaded"; tips.SetToolTip(heading, heading.AccessibleDescription);
                }
                LayoutMeters(); PlaceWindow(); timer.Start(); await Task.WhenAll(ShownRows().Select(v => RefreshAsync(v, true)));
            };
            DpiChanged += delegate { LayoutMeters(); };
            ClientSizeChanged += delegate { ResetBackdrop(); };
            timer.Tick += async delegate { var rows = ShownRows(); foreach (var view in rows) Render(view); await Task.WhenAll(rows.Select(v => RefreshAsync(v, false))); };
            FormClosing += async delegate(object sender, FormClosingEventArgs e)
            {
                if (closeReady) return;
                e.Cancel = true; if (closing) return;
                // A minimized window reports Windows' -32000 placeholder; save where it will be restored instead.
                var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                settings.Left = bounds.Left; settings.Top = bounds.Top; SaveSettings();
                try { await StopRefreshingAsync(); }
                catch (Exception) { /* All tracked tasks finish before a fault is returned. */ }
                closeReady = true; Close();
            };
        }
        void StylePin()
        {
            pin.Font = pin.Checked ? pinnedFont : Font; pin.ForeColor = Color.White;
            pin.BackColor = Color.FromArgb(28, 36, 49); pin.FlatAppearance.CheckedBackColor = Color.FromArgb(38, 75, 128);
        }
        ProviderView AttachAccount(AccountProfile source)
        {
            var existing = providers.FirstOrDefault(v => v.Source != null && v.Source.Id == source.Id);
            if (existing != null) return existing;
            var view = AddProvider(source.Codex, source.Cursor ? "CURSOR" : source.Codex ? "CODEX" : "CLAUDE", source.Cursor ? Silver : source.Codex ? Blue : Orange, 0);
            view.Cursor = source.Cursor;
            view.Source = source; view.CachePath = source.CachePath;
            view.AccountMenu.Items.Insert(0, new ToolStripSeparator());
            view.AccountMenu.Items.Insert(0, new ToolStripMenuItem("Reconnect account", null, async delegate { await ShowAccountDialogAsync(source); }));
            ApplySavedOrder(); LayoutMeters(); return view;
        }
        internal string AccountStorePath = null;
        static string RowKey(ProviderView view) { return view.Source != null ? "account:" + view.Source.Id : view.Antigravity ? "antigravity" : view.Cursor ? "cursor" : view.Codex ? "codex" : "claude"; }
        bool IsHidden(ProviderView view) { return settings.Hidden.Contains(RowKey(view)); }
        List<ProviderView> ShownRows() { return providers.Where(v => !IsHidden(v)).ToList(); }
        static IEnumerable<Control> RowControls(ProviderView view)
        {
            var fixedControls = new Control[] { view.Logo, view.Title, view.Plan, view.Account, view.FiveHour, view.Weekly, view.FiveText, view.WeekText, view.FiveLabel, view.WeekLabel };
            return fixedControls.Concat(view.Lanes.SelectMany(l => new Control[] { l.Name, l.Text, l.Meter }));
        }
        void ApplySavedOrder()
        {
            // Rows not in the saved order (e.g. a newly added account) keep their place after the ordered ones.
            var sorted = providers.Select((v, i) => new { v, i, k = settings.Order.IndexOf(RowKey(v)) })
                .OrderBy(x => x.k < 0 ? Int32.MaxValue : x.k).ThenBy(x => x.i).Select(x => x.v).ToList();
            providers.Clear(); providers.AddRange(sorted);
        }
        void RowsChanged()
        {
            settings.Order = providers.Select(RowKey).ToList(); SaveSettings();
            LayoutMeters(); accounts.Invalidate(true);
        }
        // delta: -1 up, +1 down, Int32.MinValue to the top, Int32.MaxValue to the bottom. Hidden rows keep no position.
        void MoveRow(ProviderView view, int delta)
        {
            var shown = ShownRows(); int from = shown.IndexOf(view); if (from < 0) return;
            int to = delta == Int32.MinValue ? 0 : delta == Int32.MaxValue ? shown.Count - 1 : Math.Max(0, Math.Min(shown.Count - 1, from + delta));
            if (to == from) return;
            shown.RemoveAt(from); shown.Insert(to, view);
            var hidden = providers.Where(IsHidden).ToList();
            providers.Clear(); providers.AddRange(shown); providers.AddRange(hidden);
            RowsChanged();
            accounts.ScrollTo((view.TopLogical - 4) * DeviceDpi / 96f);
        }
        async Task RemoveRowAsync(ProviderView view, bool confirm)
        {
            if (closing || !providers.Contains(view)) return;
            string name = view.Title.Text + (String.IsNullOrEmpty(view.Account.Text) ? "" : " · " + view.Account.Text);
            if (confirm)
            {
                string text = view.Source != null
                    ? "Remove " + name + " from the widget?\n\nThe widget's saved sign-in for this account is deleted. This cannot be undone, but you can add the account again later."
                    : "Remove " + name + " from the widget?\n\nYou can bring it back from ··· → Show removed subscriptions.";
                if (MessageBox.Show(this, text, "Remove subscription", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            }
            if (view.Source == null)
            {
                settings.Hidden.Add(RowKey(view)); RowsChanged(); return;
            }
            providers.Remove(view); RowsChanged();
            try { await view.ActiveTask; }
            catch (Exception) { /* The row is gone; its last refresh result no longer matters. */ }
            foreach (var control in RowControls(view).ToList()) control.Dispose();
            view.AccountMenu.Dispose();
            try
            {
                AccountStore.Save(AccountStore.Load(AccountStorePath).Where(p => p.Id != view.Source.Id), AccountStorePath);
                if (Directory.Exists(view.Source.ConfigDirectory)) Directory.Delete(view.Source.ConfigDirectory, true);
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)) throw;
                heading.AccessibleDescription = "The removed account's files could not all be deleted"; tips.SetToolTip(heading, heading.AccessibleDescription);
            }
        }
        void ShowRemovedRows()
        {
            var restored = providers.Where(IsHidden).ToList();
            settings.Hidden.Clear(); RowsChanged();
            foreach (var view in restored) { var task = RefreshAsync(view, true); }
        }
        async Task ShowAccountDialogAsync(AccountProfile source)
        {
            if (closing) return;
            using (var dialog = new AccountDialog(source)) {
                if (dialog.ShowDialog(this) != DialogResult.OK || closing) return;
                var view = dialog.Antigravity ? antigravity : AttachAccount(dialog.Profile);
                view.CacheIdentity = null; view.State.UseIdentity(null); view.State.RetryNotBefore = DateTimeOffset.MinValue;
                await RefreshAsync(view, true);
                accounts.ScrollTo((view.TopLogical - 4) * DeviceDpi / 96f);
            }
        }
        Label AddLabel(string text, int x, int y, int width, int height, float size, Color color, bool bold, Control parent = null)
        {
            var label = new Label { Text = text, Bounds = new Rectangle(x, y, width, height), Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true };
            (parent ?? this).Controls.Add(label); return label;
        }
        ProviderView AddProvider(bool isCodex, string title, Color color, int y)
        {
            var logo = new PictureBox { Bounds = new Rectangle(24, y + 6, 21, 21), BackColor = Color.Transparent, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = title + " icon" };
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", isCodex ? "codex.png" : title == "AGY" ? "antigravity.png" : title == "CURSOR" ? "cursor.png" : "claude.ico");
            try
            {
                if (isCodex || title == "AGY" || title == "CURSOR") { using (var image = Image.FromFile(path)) logo.Image = new Bitmap(image); }
                else { using (var icon = new Icon(path)) logo.Image = icon.ToBitmap(); }
            }
            catch (Exception e) { if (!(e is IOException || e is ArgumentException)) throw; }
            if (logo.Image == null)
            {
                logo.Paint += delegate(object sender, PaintEventArgs e) {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(color, 2))
                        for (int i = 0; i < (isCodex ? 6 : 12); i++) { double a = i * Math.PI * 2 / (isCodex ? 6 : 12); e.Graphics.DrawLine(pen, 10 + (float)Math.Cos(a) * 4, 10 + (float)Math.Sin(a) * 4, 10 + (float)Math.Cos(a) * 9, 10 + (float)Math.Sin(a) * 9); }
                };
            }
            accounts.Controls.Add(logo);
            var view = new ProviderView { Codex = isCodex, Logo = logo, CachePath = Path.Combine(WidgetSettings.Folder, isCodex ? "codex-usage.json" : "claude-usage.json") };
            // Header row: icon, title, plan badge and account name share one vertical center (y + 16.5).
            view.Title = AddLabel(title, 54, y + 5, 78, 23, 9f, color, true, accounts);
            view.Title.TextAlign = ContentAlignment.MiddleLeft;
            view.Account = AddLabel("", 136, y + 5, 181, 23, 8f, Color.FromArgb(192, 204, 222), false, accounts);
            view.Account.TextAlign = ContentAlignment.MiddleRight; view.Account.AccessibleName = title + " signed-in account";
            view.Plan = AddLabel("", 136, y + 7, 50, 19, 7.5f, color, true, accounts);
            view.Plan.TextAlign = ContentAlignment.MiddleCenter; view.Plan.BackColor = Color.FromArgb(31, 41, 57); view.Plan.Visible = false;
            view.FiveLabel = AddLabel("5-hour", 24, y + 38, 57, 20, 9f, Color.FromArgb(207, 217, 232), false, accounts);
            view.WeekLabel = AddLabel("Weekly", 24, y + 66, 57, 20, 9f, Color.FromArgb(207, 217, 232), false, accounts);
            view.FiveHour = new MeterBar { Bounds = new Rectangle(85, y + 40, 148, 14), FillColor = color, AccessibleName = title + " five-hour remaining" };
            view.Weekly = new MeterBar { Bounds = new Rectangle(85, y + 68, 148, 14), FillColor = color, AccessibleName = title + " weekly remaining" };
            accounts.Controls.Add(view.FiveHour); accounts.Controls.Add(view.Weekly);
            view.FiveText = AddLabel("—", 240, y + 38, 77, 20, 9f, ForeColor, true, accounts);
            view.WeekText = AddLabel("—", 240, y + 66, 77, 20, 9f, ForeColor, true, accounts);
            view.FiveText.TextAlign = view.WeekText.TextAlign = ContentAlignment.TopRight;
            view.AccountMenu = new ContextMenuStrip();
            var toTop = view.AccountMenu.Items.Add("Move to top", null, delegate { MoveRow(view, Int32.MinValue); });
            var up = view.AccountMenu.Items.Add("Move up", null, delegate { MoveRow(view, -1); });
            var down = view.AccountMenu.Items.Add("Move down", null, delegate { MoveRow(view, 1); });
            var toBottom = view.AccountMenu.Items.Add("Move to bottom", null, delegate { MoveRow(view, Int32.MaxValue); });
            view.AccountMenu.Items.Add(new ToolStripSeparator());
            view.AccountMenu.Items.Add("Remove subscription", null, async delegate { await RemoveRowAsync(view, true); });
            view.AccountMenu.Opening += delegate {
                var shown = ShownRows(); int index = shown.IndexOf(view);
                toTop.Enabled = up.Enabled = index > 0; down.Enabled = toBottom.Enabled = index >= 0 && index < shown.Count - 1;
            };
            foreach (var control in RowControls(view)) control.ContextMenuStrip = view.AccountMenu;
            providers.Add(view); return view;
        }
        void PlaceWindow()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            int x = settings.Left == Int32.MinValue ? area.Right - Width - 24 : settings.Left;
            int y = settings.Top == Int32.MinValue ? area.Top + 32 : settings.Top;
            area = Screen.FromRectangle(new Rectangle(x, y, Width, Height)).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), Math.Max(area.Top, Math.Min(y, area.Bottom - Height)));
        }
        public void ApplyTransparency(int value)
        {
            settings.Transparency = Math.Max(0, Math.Min(100, value)); ResetBackdrop();
        }
        // Re-assigning an unchanged tooltip every second makes a visible tooltip blink; only touch it on change.
        void SetTip(Control control, string text) { if (tips.GetToolTip(control) != (text ?? "")) tips.SetToolTip(control, text); }
        void ResetBackdrop()
        {
            if (backdrop != null) backdrop.Dispose(); backdrop = null; Invalidate(true);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Wheel over the header (or with focus on Pin/···) still scrolls the account list.
            var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
            accounts.ScrollTo(accounts.ScrollY - e.Delta / 120f * 48 * DeviceDpi / 96f);
        }
        internal void ShowUsage(bool isCodex, UsageSnapshot snapshot, DateTimeOffset updated)
        {
            ApplyUsage(isCodex ? codex : claude, snapshot, updated);
        }
        void ApplyUsage(ProviderView view, UsageSnapshot snapshot, DateTimeOffset updated)
        {
            view.State.Accept(snapshot, updated); LayoutMeters(); Render(view);
        }
        internal async Task StopRefreshingAsync()
        {
            closing = true; timer.Stop(); cancellation.Cancel();
            await Task.WhenAll(providers.Select(v => v.ActiveTask)).ConfigureAwait(false);
        }
        List<UsageLane> CustomLanes(ProviderView view)
        {
            if (view.State.Snapshot != null) return view.State.Snapshot.CustomLanes ?? new List<UsageLane>();
            var labels = view.Antigravity ? new[] { "Gemini 5h", "Gemini wk", "Claude 5h", "Claude wk" } : view.Cursor ? new[] { "Monthly" } : new string[0];
            return labels.Select(label => new UsageLane { Label = label }).ToList();
        }
        void SyncLaneViews(ProviderView view, List<UsageLane> lanes)
        {
            while (view.Lanes.Count > lanes.Count) {
                var last = view.Lanes[view.Lanes.Count - 1];
                last.Name.Dispose(); last.Text.Dispose(); last.Meter.Dispose(); view.Lanes.RemoveAt(view.Lanes.Count - 1);
            }
            while (view.Lanes.Count < lanes.Count) {
                var lane = new LaneView {
                    Name = AddLabel("", 24, 0, 57, 20, 7.5f, Color.FromArgb(207, 217, 232), false, accounts),
                    Text = AddLabel("—", 240, 0, 77, 20, 9f, ForeColor, true, accounts),
                    Meter = new MeterBar { FillColor = view.Antigravity ? Teal : view.Cursor ? Silver : view.Codex ? Blue : Orange }
                };
                lane.Text.TextAlign = ContentAlignment.TopRight; accounts.Controls.Add(lane.Meter); view.Lanes.Add(lane);
                lane.Name.ContextMenuStrip = lane.Text.ContextMenuStrip = lane.Meter.ContextMenuStrip = view.AccountMenu;
            }
            for (int i = 0; i < lanes.Count; i++) {
                view.Lanes[i].Name.Text = lanes[i].Label; view.Lanes[i].Meter.AccessibleName = view.Title.Text + " " + lanes[i].Label + " remaining";
                SetTip(view.Lanes[i].Name, lanes[i].Label.StartsWith("Claude") ? lanes[i].Label.Replace("Claude", "Claude/GPT") : lanes[i].Label);
            }
        }
        void LayoutMeters()
        {
            if (claude == null || codex == null || layingOut) return;
            layingOut = true;
            accounts.SuspendLayout();
            try {
            float scale = DeviceDpi / 96f;
            Func<int, int> px = n => (int)Math.Round(n * scale);
            int top = 4, oldHeight = accounts.ContentHeight;
            foreach (var view in providers)
            {
                if (IsHidden(view))
                {
                    view.HeightLogical = 0;
                    foreach (var control in RowControls(view)) control.Visible = false;
                    continue;
                }
                var snapshot = view.State.Snapshot;
                bool five = !view.Custom && (snapshot == null || snapshot.FiveHour != null), week = !view.Custom && (snapshot == null || snapshot.Weekly != null);
                var lanes = CustomLanes(view); SyncLaneViews(view, lanes);
                view.TopLogical = top;
                view.HeightLogical = 44 + (five ? 28 : 0) + (week ? 28 : 0) + lanes.Count * 28;
                top += view.HeightLogical + 10;
            }
            accounts.ContentHeight = px(top - 4);
            // Children sit at their content position minus the current scroll offset; the viewport never jumps back to the top.
            Func<int, int> py = n => px(n) - accounts.ScrollY;
            int valueX = accounts.ClientSize.Width - px(100), barWidth = Math.Max(px(20), valueX - px(92));
            foreach (var view in providers)
            {
                var snapshot = view.State.Snapshot;
                bool five = !view.Custom && (snapshot == null || snapshot.FiveHour != null), week = !view.Custom && (snapshot == null || snapshot.Weekly != null);
                if (IsHidden(view)) continue;
                top = view.TopLogical; int row = top + 38;
                view.Logo.Visible = view.Title.Visible = view.Account.Visible = true;
                foreach (var lane in view.Lanes) lane.Name.Visible = lane.Text.Visible = lane.Meter.Visible = true;
                view.Logo.SetBounds(px(24), py(top + 6), px(21), px(21));
                int titleWidth = Math.Min(px(78), TextRenderer.MeasureText(view.Title.Text, view.Title.Font).Width + px(2));
                view.Title.SetBounds(px(54), py(top + 5), titleWidth, px(23));
                string plan = snapshot == null ? null : snapshot.PlanName;
                int accountX = px(54) + titleWidth + px(8); view.Plan.Visible = !String.IsNullOrEmpty(plan);
                if (!String.IsNullOrEmpty(plan)) {
                    int badgeWidth = Math.Min(px(74), TextRenderer.MeasureText(plan, view.Plan.Font).Width + px(10));
                    view.Plan.SetBounds(accountX, py(top + 7), badgeWidth, px(19)); accountX += badgeWidth + px(8);
                }
                view.Account.SetBounds(accountX, py(top + 5), Math.Max(1, accounts.ClientSize.Width - px(23) - accountX), px(23));
                view.FiveLabel.Visible = view.FiveHour.Visible = view.FiveText.Visible = five;
                view.WeekLabel.Visible = view.Weekly.Visible = view.WeekText.Visible = week;
                if (five)
                {
                    view.FiveLabel.SetBounds(px(24), py(row), px(57), px(20));
                    view.FiveHour.SetBounds(px(85), py(row + 2), barWidth, px(14));
                    view.FiveText.SetBounds(valueX, py(row), px(77), px(20)); row += 28;
                }
                if (week)
                {
                    view.WeekLabel.SetBounds(px(24), py(row), px(57), px(20));
                    view.Weekly.SetBounds(px(85), py(row + 2), barWidth, px(14));
                    view.WeekText.SetBounds(valueX, py(row), px(77), px(20)); row += 28;
                }
                foreach (var lane in view.Lanes) {
                    lane.Name.SetBounds(px(24), py(row), px(57), px(20)); lane.Meter.SetBounds(px(85), py(row + 2), barWidth, px(14));
                    lane.Text.SetBounds(valueX, py(row), px(77), px(20)); row += 28;
                }
            }
            // Cards only change shape when the content height changes; routine refreshes skip the full repaint.
            if (accounts.ContentHeight != oldHeight) accounts.Invalidate(true);
            } finally { accounts.ResumeLayout(false); layingOut = false; }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Every transparent child repaints this background, so the scaled, dimmed image is rendered once and reused.
            if (backdrop == null && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                backdrop = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(backdrop))
                {
                    g.Clear(BackColor);
                    if (background != null)
                    {
                        float scale = Math.Max((float)ClientSize.Width / background.Width, (float)ClientSize.Height / background.Height);
                        int w = (int)Math.Ceiling(background.Width * scale), h = (int)Math.Ceiling(background.Height * scale);
                        using (var attributes = new ImageAttributes())
                        {
                            var matrix = new ColorMatrix { Matrix00 = .75f, Matrix11 = .75f, Matrix22 = .75f, Matrix33 = 1 - settings.Transparency / 100f };
                            attributes.SetColorMatrix(matrix);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(background, new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h), 0, 0, background.Width, background.Height, GraphicsUnit.Pixel, attributes);
                        }
                    }
                }
            }
            if (backdrop == null) { e.Graphics.Clear(BackColor); return; }
            var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, backdrop.Size));
            e.Graphics.DrawImage(backdrop, clip, clip, GraphicsUnit.Pixel);
        }
        void PaintAccounts(object sender, PaintEventArgs e)
        {
            var g = e.Graphics; float dpi = DeviceDpi / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var view in providers)
            {
                if (view == null || view.HeightLogical == 0) continue;
                using (var path = Shapes.Rounded(new RectangleF(12 * dpi, (view.TopLogical - 4) * dpi - accounts.ScrollY, accounts.ClientSize.Width - 24 * dpi, view.HeightLogical * dpi), 10 * dpi))
                using (var brush = new SolidBrush(Color.FromArgb(130, 8, 13, 22))) g.FillPath(brush, path);
            }
        }
        Task RefreshAsync(ProviderView view, bool manual)
        {
            if (view.Busy || closing) return view.ActiveTask;
            view.ActiveTask = RefreshCoreAsync(view, manual); return view.ActiveTask;
        }
        async Task RefreshCoreAsync(ProviderView view, bool manual)
        {
            var now = DateTimeOffset.UtcNow;
            if (closing || (!manual && now < view.NextIdentityCheck && !view.State.CanFetch(now, false))) return;
            view.Busy = true; view.NextIdentityCheck = now.AddSeconds(60); bool credentialsRead = false;
            try
            {
                Credentials credentials = null; AntigravityConnection connection = null; string identity;
                string home = view.Source == null ? null : view.Source.ConfigDirectory;
                Func<Credentials> read = () => view.Cursor ? CursorClient.ReadCredentials(home) : view.Codex ? UsageClient.ReadCredentials(true, home) : UsageClient.ReadClaudeCredentials(home);
                if (view.Antigravity) { connection = await Task.Run(() => AntigravityClient.ConnectAsync(cancellation.Token)); identity = connection.Identity; }
                else {
                    credentials = await Task.Run(read);
                    credentials.Profile = view.ProfileCache; identity = credentials.Identity;
                }
                credentialsRead = true; if (closing) return;
                view.State.UseIdentity(identity);
                if (view.CacheIdentity != identity)
                {
                    view.CacheIdentity = identity;
                    UsageCache.Restore(view.CachePath, identity, view.State, now); LayoutMeters();
                }
                Render(view);
                if (!view.State.CanFetch(now, manual)) return;
                view.State.NextAttempt = now.AddSeconds(60);
                var result = view.Antigravity ? await Task.Run(() => AntigravityClient.FetchAsync(connection, cancellation.Token)) : view.Cursor ? await Task.Run(() => CursorClient.FetchAsync(credentials, cancellation.Token)) : await Task.Run(() => UsageClient.FetchAsync(view.Codex, credentials, cancellation.Token));
                credentialsRead = false;
                string currentIdentity;
                if (view.Antigravity) currentIdentity = (await Task.Run(() => AntigravityClient.ConnectAsync(cancellation.Token))).Identity;
                else currentIdentity = (await Task.Run(read)).Identity;
                credentialsRead = true;
                if (closing) return;
                if (currentIdentity != identity)
                {
                    view.State.UseIdentity(currentIdentity); view.State.Fail("Sign-in changed · checking again"); view.State.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(1); return;
                }
                ApplyUsage(view, result, DateTimeOffset.UtcNow); view.State.RetryNotBefore = DateTimeOffset.MinValue;
                UsageCache.Save(view.CachePath, identity, view.State);
            }
            catch (OperationCanceledException) { if (!closing) view.State.Fail("Usage request timed out"); }
            catch (UsageException e)
            {
                string message = view.Source != null && e.Message == "Open Claude Code to renew sign-in" ? "Sign-in expired · right-click this account to reconnect" : e.Message;
                if (!credentialsRead) KeepOfflineReading(view, message); else view.State.Fail(message);
                if (e.RetryAt.HasValue) view.State.RetryNotBefore = e.RetryAt.Value;
                if (credentialsRead && view.CacheIdentity != null) UsageCache.Save(view.CachePath, view.CacheIdentity, view.State);
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is System.Net.Http.HttpRequestException || e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is ArgumentException || e is DllNotFoundException || e is System.Security.Cryptography.CryptographicException)) throw;
                string message = e is InvalidDataException ? "Usage response could not be read" : e is System.Net.Http.HttpRequestException ? "Offline · retrying automatically" : "Usage unavailable · retrying automatically";
                if (!credentialsRead) KeepOfflineReading(view, message); else view.State.Fail(message);
                if (credentialsRead && view.CacheIdentity != null) UsageCache.Save(view.CachePath, view.CacheIdentity, view.State);
            }
            finally { view.Busy = false; if (!closing && !IsDisposed) Render(view); }
        }
        // Antigravity only exposes limits while its app runs. When it is closed, keep its last saved reading (shown stale)
        // instead of blanking the row; signed-out Claude/Codex/Cursor accounts still clear as before.
        void KeepOfflineReading(ProviderView view, string message)
        {
            string saved = view.Antigravity ? UsageCache.SavedIdentity(view.CachePath) : null;
            view.State.UseIdentity(saved);
            if (saved != null && view.CacheIdentity != saved)
            {
                view.CacheIdentity = saved;
                UsageCache.Restore(view.CachePath, saved, view.State, DateTimeOffset.UtcNow); LayoutMeters();
            }
            view.State.NextAttempt = DateTimeOffset.UtcNow.AddSeconds(60); view.State.Fail(message); Render(view);
        }
        void Render(ProviderView view)
        {
            var snapshot = view.State.Snapshot;
            string account = snapshot == null ? "" : snapshot.AccountName ?? "Account unavailable";
            view.Account.Text = account; view.Account.AccessibleName = (view.Antigravity ? "Antigravity" : view.Cursor ? "Cursor" : view.Codex ? "Codex" : "Claude") + " account: " + account; SetTip(view.Account, account);
            string plan = snapshot == null ? null : snapshot.PlanName;
            view.Plan.Text = plan ?? ""; view.Plan.AccessibleName = view.Title.Text + " plan: " + plan; SetTip(view.Plan, plan == null ? null : plan + " plan");
            RenderMeter(view.FiveHour, view.FiveText, snapshot == null ? null : snapshot.FiveHour, view.State.Error, view.State.UpdatedAt);
            RenderMeter(view.Weekly, view.WeekText, snapshot == null ? null : snapshot.Weekly, view.State.Error, view.State.UpdatedAt);
            var lanes = CustomLanes(view);
            for (int i = 0; i < Math.Min(lanes.Count, view.Lanes.Count); i++) RenderMeter(view.Lanes[i].Meter, view.Lanes[i].Text, lanes[i].Window, view.State.Error, view.State.UpdatedAt);
            string status = view.State.Error ?? (snapshot == null ? "Connecting…" : snapshot.FiveHour == null && snapshot.Weekly == null && lanes.Count == 0 ? "Limits not reported by this account" : null);
            view.Title.AccessibleDescription = status; SetTip(view.Title, status);
        }
        void RenderMeter(MeterBar meter, Label text, UsageWindow window, string error, DateTimeOffset? updated)
        {
            bool expired = window != null && window.IsExpired(DateTimeOffset.UtcNow);
            meter.Value = window == null ? (double?)null : 100 - window.Used; meter.Stale = error != null || expired;
            text.Text = meter.Value.HasValue ? meter.Value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "% left" : "—";
            text.ForeColor = meter.Stale ? Color.FromArgb(164, 177, 199) : ForeColor;
            string detail = meter.Value.HasValue ? meter.Value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "% left" : "No reading available";
            if (expired) detail += " · reset time passed, awaiting a fresh reading";
            else if (window != null && window.ResetAt.HasValue) detail += " · resets " + window.ResetAt.Value.ToLocalTime().ToString("ddd, MMM d, h:mm tt");
            if (error != null) detail += " · " + error;
            if (meter.Stale && updated.HasValue) detail += " · last confirmed " + updated.Value.ToLocalTime().ToString("MMM d, h:mm:ss tt");
            SetTip(meter, detail); SetTip(text, detail); meter.AccessibleDescription = detail;
        }
        static Bitmap ReadImage(string path)
        {
            if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new IOException("Image is too large");
            using (var stream = File.OpenRead(path))
            using (var image = Image.FromStream(stream))
            {
                if (image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > 40000000) throw new IOException("Image dimensions are too large");
                return new Bitmap(image);
            }
        }
        void ChooseBackground()
        {
            using (var picker = new OpenFileDialog { Title = "Choose a widget background", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                Bitmap image = null;
                try
                {
                    using (var source = ReadImage(picker.FileName))
                    using (var crop = new BackgroundCropDialog(source, ClientSize))
                    {
                        if (crop.ShowDialog(this) != DialogResult.OK) return;
                        image = crop.SelectedImage;
                    }
                    Directory.CreateDirectory(WidgetSettings.Folder);
                    string path = Path.Combine(WidgetSettings.Folder, "background.png"), temporary = path + ".tmp";
                    image.Save(temporary, ImageFormat.Png); WidgetSettings.ReplaceFile(temporary, path);
                    if (background != null) background.Dispose(); background = image; image = null;
                    settings.BackgroundPath = path; heading.AccessibleDescription = null; tips.SetToolTip(heading, null); ResetBackdrop(); SaveSettings();
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is OutOfMemoryException || e is System.Runtime.InteropServices.ExternalException)) throw;
                    MessageBox.Show(this, "That image could not be opened or saved. Choose a PNG, JPEG or BMP under 25 MB.", "Background", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { if (image != null) image.Dispose(); }
            }
        }
        void ShowSettings()
        {
            using (var dialog = new Form { Text = "Background settings", ClientSize = new Size(312, 194), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor, Font = Font, AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi })
            {
                var choose = new Button { Text = "Choose background…", Bounds = new Rectangle(16, 15, 174, 28), FlatStyle = FlatStyle.Flat };
                var remove = new Button { Text = "Remove", Bounds = new Rectangle(202, 15, 94, 28), FlatStyle = FlatStyle.Flat };
                var caption = new Label { Text = "Background transparency: " + settings.Transparency + "%", Bounds = new Rectangle(16, 60, 280, 20) };
                var slider = new TrackBar { Minimum = 0, Maximum = 100, Value = settings.Transparency, TickFrequency = 25, Bounds = new Rectangle(12, 84, 288, 45), AccessibleName = "Background transparency" };
                var refresh = new Button { Text = "Refresh now", Bounds = new Rectangle(16, 147, 132, 29), FlatStyle = FlatStyle.Flat };
                var done = new Button { Text = "Done", DialogResult = DialogResult.OK, Bounds = new Rectangle(164, 147, 132, 29), FlatStyle = FlatStyle.Flat };
                dialog.Controls.AddRange(new Control[] { choose, remove, caption, slider, refresh, done });
                dialog.AcceptButton = done; dialog.CancelButton = done;
                choose.Click += delegate { ChooseBackground(); };
                remove.Click += delegate { if (background != null) background.Dispose(); background = null; settings.BackgroundPath = null; ResetBackdrop(); SaveSettings(); };
                slider.ValueChanged += delegate { ApplyTransparency(slider.Value); caption.Text = "Background transparency: " + settings.Transparency + "%"; };
                refresh.Click += async delegate { await Task.WhenAll(ShownRows().Select(v => RefreshAsync(v, true))); };
                dialog.ShowDialog(this); SaveSettings();
            }
        }
        void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception e)
            {
                if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                if (heading != null) { heading.AccessibleDescription = "Preferences could not be saved"; tips.SetToolTip(heading, heading.AccessibleDescription); }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true; cancellation.Cancel(); timer.Dispose(); tips.Dispose();
                mainMenu.Dispose(); pinnedFont.Dispose();
                foreach (var view in providers) if (view.AccountMenu != null) view.AccountMenu.Dispose();
                if (background != null) background.Dispose(); if (backdrop != null) backdrop.Dispose();
                foreach (var picture in accounts.Controls.OfType<PictureBox>()) if (picture.Image != null) picture.Image.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool first;
            using (var singleton = new Mutex(true, "Local\\AIUsageWidget", out first))
            {
                if (!first) { MessageBox.Show("AI Usage Widget is already open.", "AI Usage"); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm(WidgetSettings.Load()));
            }
        }
    }
}
