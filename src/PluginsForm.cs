using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BatteryViewer
{
    /// <summary>插件管理: 列表(勾选=启用), 右侧是添加、新建、编辑、移除、错误详情、打开目录、重新加载。</summary>
    internal class PluginsForm : Form
    {
        private readonly AppController _app;
        private readonly ListView _list;
        private readonly Button _edit, _remove, _error;
        private readonly Label _status;
        private readonly Dictionary<string, ListViewItem> _items = new Dictionary<string, ListViewItem>();
        private bool _binding;

        public PluginsForm(AppController app)
        {
            _app = app;
            Text = "插件管理";
            Font = Ui.Font;
            AutoScaleMode = AutoScaleMode.None;
            Icon = Ui.AppIcon(32);
            ClientSize = Ui.Sz(720, 360);
            MinimumSize = Ui.Sz(560, 300);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ShowInTaskbar = true;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = Ui.Pd(12, 10, 12, 10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var hint = new Label
            {
                AutoSize = true, Margin = Ui.Pd(0, 0, 0, 8),
                Text = "每种设备由一个插件负责读电量。勾选表示启用。添加、修改插件后立即生效, 不用重启程序。",
            };
            root.Controls.Add(hint, 0, 0);
            root.SetColumnSpan(hint, 2);

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, Margin = Ui.Pd(0),
            };
            _list.Columns.Add("插件", Ui.S(130));
            _list.Columns.Add("来源", Ui.S(56));
            _list.Columns.Add("状态", Ui.S(170));
            _list.Columns.Add("说明", Ui.S(200));
            _list.ItemChecked += (s, e) =>
            {
                if (!_binding) _app.SetPluginEnabled((string)e.Item.Tag, e.Item.Checked);
            };
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => { if (_error.Enabled) ShowError(); };
            _list.Resize += (s, e) => FitColumns();
            root.Controls.Add(_list, 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Ui.Pd(10, 0, 0, 0) };
            Func<string, EventHandler, Button> add = (text, onClick) =>
            {
                var b = new Button { Text = text, FlatStyle = FlatStyle.System, Width = Ui.S(130), Height = Ui.S(30), Margin = Ui.Pd(0, 0, 0, 6) };
                b.Click += onClick;
                buttons.Controls.Add(b);
                return b;
            };
            add("添加插件文件…", (s, e) => AddFile());
            add("从模板新建…", (s, e) => NewFromTemplate());
            _edit = add("编辑", (s, e) => OpenInEditor(Selected().Path));
            _remove = add("移除", (s, e) => Remove());
            _error = add("错误详情", (s, e) => ShowError());
            buttons.Controls.Add(new Label { Height = Ui.S(10), Margin = Ui.Pd(0) });
            add("打开插件目录", (s, e) => { Directory.CreateDirectory(Settings.PluginDir); Process.Start("explorer.exe", "\"" + Settings.PluginDir + "\""); });
            add("重新加载", (s, e) => _app.ReloadPlugins());
            root.Controls.Add(buttons, 1, 1);

            _status = new Label { AutoSize = true, ForeColor = Ui.Sub, Margin = Ui.Pd(0, 8, 0, 0) };
            root.Controls.Add(_status, 0, 2);
            root.SetColumnSpan(_status, 2);
            UpdateButtons();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_app.Exiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private void FitColumns()
        {
            int used = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;
            _list.Columns[3].Width = Math.Max(Ui.S(120), _list.ClientSize.Width - used - Ui.S(4));
        }

        private PluginStatus Selected()
        {
            return _list.SelectedItems.Count > 0 ? _status_of(_list.SelectedItems[0]) : null;
        }

        private static PluginStatus _status_of(ListViewItem item) { return (PluginStatus)item.SubItems[0].Tag; }

        private void UpdateButtons()
        {
            var p = Selected();
            bool custom = p != null && !p.Builtin && !string.IsNullOrEmpty(p.Path);
            _edit.Enabled = custom && p.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            _remove.Enabled = custom;
            _error.Enabled = p != null && p.Error.Length > 0;
        }

        public void UpdateView(List<PluginStatus> plugins, bool loading, bool scanning)
        {
            _binding = true;
            try
            {
                _status.Text = loading ? "正在加载插件…" : scanning ? "正在读取电量…"
                    : "插件目录: " + Settings.PluginDir + "    (.cs 源文件或 .dll, 文件名以 _ 开头的不加载)";

                // 插件集合变了才重建列表, 否则只改文字, 不打断正在进行的点击和选择
                var ids = plugins.Select(p => p.Id).ToList();
                if (!ids.SequenceEqual(_items.Keys))
                {
                    string sel = Selected() != null ? Selected().Id : null;
                    _list.BeginUpdate();
                    _list.Items.Clear();
                    _items.Clear();
                    foreach (var p in plugins)
                    {
                        var item = new ListViewItem(new[] { "", "", "", "" }) { Tag = p.Id, UseItemStyleForSubItems = false };
                        _list.Items.Add(item);
                        _items[p.Id] = item;
                        if (p.Id == sel) item.Selected = true;
                    }
                    _list.EndUpdate();
                }
                foreach (var p in plugins)
                {
                    var item = _items[p.Id];
                    item.SubItems[0].Tag = p;
                    if (item.Checked == p.Disabled) item.Checked = !p.Disabled;
                    SetText(item.SubItems[0], p.Name);
                    SetText(item.SubItems[1], p.Builtin ? "内置" : "自定义");
                    string state; Color color;
                    if (p.Disabled) { state = "已停用"; color = Ui.Sub; }
                    else if (p.Error.Length > 0) { state = "出错: " + FirstLine(p.Error); color = Ui.Red; }
                    else if (!p.Scanned) { state = "等待扫描…"; color = Ui.Sub; }
                    else { state = string.Format("{0} 台设备 · {1} ms", p.DeviceCount, p.LastScanMs); color = Ui.Green; }
                    SetText(item.SubItems[2], state);
                    item.SubItems[2].ForeColor = color;
                    SetText(item.SubItems[3], p.Description.Length > 0 ? p.Description : Path.GetFileName(p.Path ?? ""));
                }
                UpdateButtons();
            }
            finally { _binding = false; }
        }

        private static void SetText(ListViewItem.ListViewSubItem s, string text) { if (s.Text != text) s.Text = text; }

        private static string FirstLine(string error)
        {
            var lines = error.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            // 编译失败时第一行是"编译失败:", 显示第二行的具体错误更有用
            return lines.Length > 1 && lines[0].EndsWith(":") ? lines[1] : lines.Length > 0 ? lines[0] : error;
        }

        // ---------------------------------------------------------------- 操作
        private void AddFile()
        {
            using (var dlg = new OpenFileDialog { Title = "选择插件文件", Filter = "设备电量插件 (*.cs;*.dll)|*.cs;*.dll" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dest = PluginLoader.TargetPath(dlg.FileName);
                if (File.Exists(dest) && !string.Equals(Path.GetFullPath(dest), Path.GetFullPath(dlg.FileName), StringComparison.OrdinalIgnoreCase)
                    && MessageBox.Show(this, Path.GetFileName(dest) + " 已经存在, 要替换吗?", "添加插件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                try
                {
                    Cursor = Cursors.WaitCursor;
                    var loaded = PluginLoader.Install(dlg.FileName);
                    _app.ReloadPlugins();
                    MessageBox.Show(this, "已添加: " + string.Join("、", loaded.Select(lp => lp.Plugin.Name)), "添加插件",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (PluginFileException e)
                {
                    ShowText("不能添加这个插件", e.Message);
                }
                finally { Cursor = Cursors.Default; }
            }
        }

        private void NewFromTemplate()
        {
            string name = InputDialog.Ask(this, "从模板新建插件", "插件文件名(英文字母、数字、下划线, 如 my_mouse):");
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                string path = PluginLoader.NewFromTemplate(name.Trim());
                _app.ReloadPlugins();
                OpenInEditor(path);
                MessageBox.Show(this, "已建好 " + Path.GetFileName(path) + " 并用编辑器打开。\r\n\r\n按注释改成你的设备, 保存后回到这里点「重新加载」。",
                    "从模板新建", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (PluginFileException e)
            {
                MessageBox.Show(this, e.Message, "不能新建", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Remove()
        {
            var p = Selected();
            if (p == null) return;
            if (MessageBox.Show(this, "移除「" + p.Name + "」?\r\n\r\n文件会挪到插件目录下的 _removed 文件夹, 需要时可以挪回来。",
                    "移除插件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            string trash = Path.Combine(Settings.PluginDir, "_removed");
            Directory.CreateDirectory(trash);
            string dest = Path.Combine(trash, Path.GetFileName(p.Path));
            if (File.Exists(dest)) dest = Path.Combine(trash, Path.GetFileNameWithoutExtension(p.Path) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(p.Path));
            try { File.Move(p.Path, dest); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "移除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _app.ReloadPlugins();
        }

        private void ShowError()
        {
            var p = Selected();
            if (p != null && p.Error.Length > 0) ShowText(p.Name + " - 错误详情", (p.Path ?? "") + "\r\n\r\n" + p.Error);
        }

        private void ShowText(string title, string text)
        {
            using (var f = new Form { Text = title, Font = Font, AutoScaleMode = AutoScaleMode.None, ClientSize = Ui.Sz(640, 320),
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Icon = Icon })
            {
                var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Font = new Font("Consolas", 9f), Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"), BackColor = SystemColors.Window };
                f.Controls.Add(box);
                box.Select(0, 0);
                f.ShowDialog(this);
            }
        }

        /// <summary>用 VS Code 打开, 没有就用记事本。</summary>
        private static void OpenInEditor(string path)
        {
            string code = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
                .Select(d => Path.Combine(d.Trim(), "code.cmd")).FirstOrDefault(File.Exists);
            var psi = code != null
                ? new ProcessStartInfo(code, "\"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo("notepad.exe", "\"" + path + "\"");
            Process.Start(psi);
        }
    }

    /// <summary>一行文字输入的小对话框。</summary>
    internal static class InputDialog
    {
        public static string Ask(IWin32Window owner, string title, string prompt)
        {
            using (var f = new Form { Text = title, Font = Ui.Font, AutoScaleMode = AutoScaleMode.None, FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, ClientSize = Ui.Sz(360, 120) })
            {
                var label = new Label { Text = prompt, AutoSize = true, Location = new Point(Ui.S(12), Ui.S(14)) };
                var box = new TextBox { Location = new Point(Ui.S(12), Ui.S(40)), Width = Ui.S(336) };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.System, Size = Ui.Sz(80, 28), Location = new Point(Ui.S(180), Ui.S(80)) };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.System, Size = Ui.Sz(80, 28), Location = new Point(Ui.S(268), Ui.S(80)) };
                f.Controls.AddRange(new Control[] { label, box, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
