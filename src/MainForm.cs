using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatteryViewer
{
    /// <summary>设备面板: 每台设备一行(名称、电量、原生进度条、状态、托盘图标开关)。数值原地更新, 不重建控件。</summary>
    internal class MainForm : Form
    {
        private readonly AppController _app;
        private readonly FlowLayoutPanel _list;
        private readonly Label _empty, _summary;
        private readonly LinkLabel _pluginsLink;
        private readonly Button _refresh;
        private readonly ComboBox _interval;
        private readonly CheckBox _autoStart;
        private readonly Dictionary<string, DeviceRow> _rows = new Dictionary<string, DeviceRow>();
        private readonly Timer _ageTimer = new Timer { Interval = 30000 };
        private bool _binding, _placed;

        public MainForm(AppController app)
        {
            _app = app;
            Text = "设备电量";
            Font = Ui.Font;
            AutoScaleMode = AutoScaleMode.None;
            Icon = Ui.AppIcon(32);
            BackColor = Ui.Back;
            MaximizeBox = false;
            ClientSize = Ui.Sz(460, 300);
            MinimumSize = Ui.Sz(380, 260);
            StartPosition = FormStartPosition.Manual;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Ui.Pd(12, 10, 12, 10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ---- 顶栏
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = Ui.Pd(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _summary = new Label { AutoSize = true, ForeColor = Ui.Sub, Anchor = AnchorStyles.Left, Margin = Ui.Pd(2, 0, 0, 0) };
            top.Controls.Add(_summary, 0, 0);
            var plugins = new Button { Text = "插件…", FlatStyle = FlatStyle.System, AutoSize = true, MinimumSize = Ui.Sz(80, 28) };
            plugins.Click += (s, e) => _app.ShowPlugins();
            top.Controls.Add(plugins, 1, 0);
            _refresh = new Button { Text = "刷新", FlatStyle = FlatStyle.System, AutoSize = true, MinimumSize = Ui.Sz(80, 28) };
            _refresh.Click += (s, e) => _app.RefreshNow();
            top.Controls.Add(_refresh, 2, 0);
            root.Controls.Add(top, 0, 0);

            // ---- 设备列表
            var listHost = new Panel { Dock = DockStyle.Fill, Margin = Ui.Pd(0) };
            _list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = Ui.Pd(0) };
            _list.ClientSizeChanged += (s, e) => FitRows();
            _empty = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.Sub, Visible = false,
                Text = "还没发现设备\r\n\r\n罗技接收器插好、鼠标动一下; 蓝牙设备先在系统里连上\r\n其他设备可以在「插件…」里添加对应的插件",
            };
            listHost.Controls.Add(_list);
            listHost.Controls.Add(_empty);
            root.Controls.Add(listHost, 0, 1);

            // ---- 底栏
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 2, Margin = Ui.Pd(0, 6, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _pluginsLink = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Left, LinkBehavior = LinkBehavior.HoverUnderline, Margin = Ui.Pd(2, 0, 0, 6) };
            _pluginsLink.LinkClicked += (s, e) => _app.ShowPlugins();
            bottom.Controls.Add(_pluginsLink, 0, 0);
            bottom.SetColumnSpan(_pluginsLink, 4);
            bottom.Controls.Add(new Label { Text = "刷新间隔", AutoSize = true, Anchor = AnchorStyles.Left, Margin = Ui.Pd(2, 0, 4, 0) }, 0, 1);
            _interval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(80), Anchor = AnchorStyles.Left };
            foreach (var sec in Settings.Intervals) _interval.Items.Add(IntervalText(sec));
            _interval.SelectedIndexChanged += (s, e) => { if (!_binding) _app.SetInterval(Settings.Intervals[_interval.SelectedIndex]); };
            bottom.Controls.Add(_interval, 1, 1);
            _autoStart = new CheckBox { Text = "开机自启", AutoSize = true, FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Right, Margin = Ui.Pd(10, 3, 0, 3) };
            _autoStart.CheckedChanged += (s, e) => { if (!_binding) _app.SetAutoStart(_autoStart.Checked); };
            bottom.Controls.Add(_autoStart, 3, 1);
            root.Controls.Add(bottom, 0, 2);

            _ageTimer.Tick += (s, e) => { foreach (var r in _rows.Values) r.RefreshAge(); };
            VisibleChanged += (s, e) => { _ageTimer.Enabled = Visible; };
        }

        private static string IntervalText(int sec) { return sec < 60 ? sec + " 秒" : (sec / 60) + " 分钟"; }

        /// <summary>第一次显示时放在屏幕右下角(托盘附近)。</summary>
        public void ShowNearTray()
        {
            if (!_placed)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - Ui.S(12), wa.Bottom - Height - Ui.S(12));
                _placed = true;
            }
            _binding = true;
            _autoStart.Checked = Settings.AutoStart;
            _binding = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关窗口只是隐藏, 程序留在托盘; 从托盘菜单退出时才真正关闭
            if (e.CloseReason == CloseReason.UserClosing && !_app.Exiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage) { _app.ShowMain(); return; }
            if (m.Msg == Program.QuitMessage) { _app.Exit(); return; }
            base.WndProc(ref m);
        }

        public void UpdateView(List<DeviceState> devices, List<PluginStatus> plugins, bool busy, Settings settings)
        {
            _binding = true;
            try
            {
                _refresh.Enabled = !busy;
                _refresh.Text = busy ? "刷新中…" : "刷新";
                int online = devices.Count(d => d.Online);
                _summary.Text = devices.Count == 0 ? "" : string.Format("{0} 台设备, {1} 台在线", devices.Count, online);
                int idx = Array.IndexOf(Settings.Intervals, settings.Interval);
                if (idx >= 0 && _interval.SelectedIndex != idx) _interval.SelectedIndex = idx;

                var enabled = plugins.Where(p => !p.Disabled).ToList();
                var failed = enabled.Where(p => p.Error.Length > 0).ToList();
                string text = string.Format("已启用 {0} 个插件", enabled.Count);
                if (enabled.Count < plugins.Count) text += string.Format(", 停用 {0} 个", plugins.Count - enabled.Count);
                if (failed.Count > 0) text += string.Format(", {0} 个出错", failed.Count);
                // 末尾补个空格: LinkLabel 量中文宽度偏小, 不补最后一个字会被截掉
                _pluginsLink.Text = text + "  ·  管理插件 ";
                _pluginsLink.LinkArea = new LinkArea(text.Length + 5, 4);
                _pluginsLink.ForeColor = failed.Count > 0 ? Ui.Red : Ui.Sub;

                // 设备行: 增删有变化的, 其余原地更新
                _list.SuspendLayout();
                var ids = new HashSet<string>(devices.Select(d => d.Id));
                foreach (var id in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
                {
                    _list.Controls.Remove(_rows[id]);
                    _rows[id].Dispose();
                    _rows.Remove(id);
                }
                var hidden = new HashSet<string>(settings.HiddenDevices);
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    DeviceRow row;
                    if (!_rows.TryGetValue(d.Id, out row))
                    {
                        row = new DeviceRow(_app) { Width = RowWidth() };
                        _rows[d.Id] = row;
                        _list.Controls.Add(row);
                    }
                    if (_list.Controls.GetChildIndex(row) != i) _list.Controls.SetChildIndex(row, i);
                    row.Bind(d, !hidden.Contains(d.Id));
                }
                FitHeight(devices.Count);
                FitRows();
                _list.ResumeLayout();
                _empty.Visible = devices.Count == 0;
                _list.Visible = devices.Count > 0;
            }
            finally { _binding = false; }
        }

        private int _fittedCount = -1;

        /// <summary>窗口高度按设备数量调整(最多 5 行, 再多滚动), 用户拖过大小之后就不再自动调。</summary>
        private void FitHeight(int count)
        {
            if (count == _fittedCount || _userSized) return;
            _fittedCount = count;
            int rows = Math.Max(1, Math.Min(5, count));
            int listH = count == 0 ? Ui.S(150) : rows * (Ui.S(84) + Ui.S(6));
            int chrome = ClientSize.Height - _list.Parent.Height;
            _resizing = true;
            int bottomEdge = Bottom;
            ClientSize = new Size(ClientSize.Width, chrome + listH);
            if (_placed && Visible) Top = bottomEdge - Height;   // 贴着屏幕底部往上长
            _resizing = false;
        }

        private bool _userSized, _resizing;

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (!_resizing) _userSized = true;
        }

        private int RowWidth()
        {
            // 行多到要滚动时给竖滚动条留位置, 否则占满宽度
            int content = _rows.Count * (Ui.S(84) + Ui.S(6));
            int w = _list.Width - (content > _list.Height ? SystemInformation.VerticalScrollBarWidth : 0) - Ui.S(2);
            return Math.Max(Ui.S(200), w);
        }

        private void FitRows()
        {
            int w = RowWidth();
            foreach (var r in _rows.Values) r.Width = w;
        }
    }

    /// <summary>一台设备的一行。</summary>
    internal class DeviceRow : Panel
    {
        private readonly AppController _app;
        private readonly Label _kind, _name, _level, _detail;
        private readonly LevelBar _bar;
        private readonly CheckBox _tray;
        private DeviceState _d;
        private bool _binding;

        public DeviceRow(AppController app)
        {
            _app = app;
            BackColor = Ui.Card;
            Margin = Ui.Pd(0, 0, 0, 6);
            Padding = Ui.Pd(12, 8, 12, 8);
            Height = Ui.S(84);
            DoubleBuffered = true;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = Ui.Pd(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(30)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(12)));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(t);

            _kind = new Label { AutoSize = true, BackColor = Color.FromArgb(235, 235, 235), ForeColor = Ui.Sub, Padding = Ui.Pd(4, 1, 4, 1),
                Font = new Font(Ui.Font.FontFamily, 8f), Anchor = AnchorStyles.Left, Margin = Ui.Pd(0, 0, 6, 0) };
            _name = new Label { AutoSize = true, Font = new Font(Ui.Font.FontFamily, 10.5f, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = Ui.Pd(0) };
            _level = new Label { AutoSize = true, Font = new Font("Segoe UI", 15f, FontStyle.Bold), Anchor = AnchorStyles.Right, Margin = Ui.Pd(0) };
            t.Controls.Add(_kind, 0, 0);
            t.Controls.Add(_name, 1, 0);
            t.Controls.Add(_level, 2, 0);

            _bar = new LevelBar { Dock = DockStyle.Fill, Margin = Ui.Pd(0, 3, 0, 3) };
            t.Controls.Add(_bar, 0, 1);
            t.SetColumnSpan(_bar, 3);

            _detail = new Label { AutoSize = true, ForeColor = Ui.Sub, Anchor = AnchorStyles.Left, Margin = Ui.Pd(0), Font = new Font(Ui.Font.FontFamily, 8.5f) };
            t.Controls.Add(_detail, 0, 2);
            t.SetColumnSpan(_detail, 2);
            _tray = new CheckBox { Text = "托盘图标", AutoSize = true, FlatStyle = FlatStyle.System, Anchor = AnchorStyles.Right, Margin = Ui.Pd(0),
                Font = new Font(Ui.Font.FontFamily, 8.5f) };
            _tray.CheckedChanged += (s, e) => { if (!_binding && _d != null) _app.SetDeviceHidden(_d.Id, !_tray.Checked); };
            t.Controls.Add(_tray, 2, 2);
        }

        public void Bind(DeviceState d, bool shownInTray)
        {
            _d = d;
            _binding = true;
            try
            {
                Set(_kind, Ui.KindName(d.Kind));
                Set(_name, d.Name);
                Set(_level, (d.Charging ? "⚡" : "") + (d.Level.HasValue ? d.Level + "%" : "--"));
                _level.ForeColor = Ui.TextColor(d.Level, d.Charging, d.Online);
                _bar.SetLevel(d.Level, Ui.TextColor(d.Level, d.Charging, d.Online));
                if (_tray.Checked != shownInTray) _tray.Checked = shownInTray;
                RefreshAge();
            }
            finally { _binding = false; }
        }

        public void RefreshAge()
        {
            if (_d == null) return;
            var bits = new List<string> { _d.PluginName };
            if (_d.Charging && _d.Detail != "已充满") bits.Add("充电中");
            if (!_d.Online) bits.Add("离线");
            if (_d.Detail.Length > 0) bits.Add(_d.Detail);
            bits.Add(_d.Updated > 0 ? Ago(_d.Updated) + "更新" : "从未读到电量");
            Set(_detail, string.Join(" · ", bits));
        }

        private static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

        private static string Ago(double unix)
        {
            double s = Poller.UnixNow() - unix;
            if (s < 60) return "刚刚";
            if (s < 3600) return (int)(s / 60) + " 分钟前";
            return new DateTime(1970, 1, 1).AddSeconds(unix).ToLocalTime().ToString("MM-dd HH:mm") + " ";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Ui.Line)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>
    /// 电量条: 扁平的圆角条, 颜色跟电量文字一致(充电蓝 / 绿 / 黄 / 红 / 离线灰)。
    /// 不用系统进度条, 因为它绿色状态下会一直闪一道流光, 看起来像在充电。
    /// </summary>
    internal class LevelBar : Control
    {
        private int? _level;
        private Color _color;

        public LevelBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
        }

        public void SetLevel(int? level, Color color)
        {
            if (_level == level && _color == color) return;
            _level = level;
            _color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float h = Math.Min(Height, Ui.S(6)), y = (Height - h) / 2;
            using (var track = new SolidBrush(Color.FromArgb(229, 229, 229)))
                FillPill(g, track, new RectangleF(0, y, Width - 1, h));
            if (_level.HasValue && _level > 0)
                using (var fill = new SolidBrush(_color))
                    FillPill(g, fill, new RectangleF(0, y, Math.Max(h, (Width - 1) * _level.Value / 100f), h));
        }

        private static void FillPill(Graphics g, Brush b, RectangleF r)
        {
            using (var p = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float d = r.Height;
                p.AddArc(r.X, r.Y, d, d, 90, 180);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }
    }
}
