using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NetSwitcher
{
    internal sealed class IpProfileDialog : Form
    {
        private static readonly Color Bg = ColorTranslator.FromHtml("#141821");
        private static readonly Color Panel = ColorTranslator.FromHtml("#1E2430");
        private static readonly Color Input = ColorTranslator.FromHtml("#263041");
        private static readonly Color Border = ColorTranslator.FromHtml("#354154");
        private static readonly Color TextColor = ColorTranslator.FromHtml("#E6EAF2");
        private static readonly Color Muted = ColorTranslator.FromHtml("#9BA7BA");
        private static readonly Color Accent = ColorTranslator.FromHtml("#4A9EFF");

        private readonly TextBox _name = CreateTextBox();
        private readonly ComboBox _adapter = CreateComboBox();
        private readonly TextBox _address = CreateTextBox();
        private readonly TextBox _mask = CreateTextBox();
        private readonly TextBox _gateway = CreateTextBox();
        private readonly TextBox _dns1 = CreateTextBox();
        private readonly TextBox _dns2 = CreateTextBox();
        private readonly CheckBox _showInTray = new CheckBox();
        private readonly DataGridView _routes = new DataGridView();
        private readonly IpProfile _workingCopy;

        public IpProfile ResultProfile { get; private set; }

        public IpProfileDialog(IEnumerable<AdapterInfo> adapters, IpProfile profile = null)
        {
            _workingCopy = profile?.Clone() ?? new IpProfile();
            Text = profile == null ? "新增 IP 配置" : "编辑 IP 配置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(650, 650);
            BackColor = Bg;
            ForeColor = TextColor;
            Font = CreateFont(9f);

            BuildUi();
            PopulateAdapters(adapters ?? Enumerable.Empty<AdapterInfo>());
            LoadProfile();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 18, 20, 16),
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Bg
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label
            {
                AutoSize = true,
                Text = Text,
                ForeColor = TextColor,
                Font = CreateFont(13f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 4)
            };
            var note = new Label
            {
                AutoSize = true,
                Text = "固定 IP 只修改所选网卡的地址参数，不会改变出口优先级。",
                ForeColor = Muted,
                Margin = new Padding(0, 0, 0, 14)
            };

            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 7,
                Margin = new Padding(0),
                BackColor = Bg
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            AddField(fields, "配置名称", _name, 0, 0, 2);
            AddField(fields, "目标网卡", _adapter, 1, 0, 2);
            AddField(fields, "IPv4 地址", _address, 2, 0);
            AddField(fields, "子网掩码", _mask, 2, 1);
            AddField(fields, "默认网关（可空）", _gateway, 3, 0);
            AddField(fields, "首选 DNS（可空）", _dns1, 3, 1);
            AddField(fields, "备用 DNS（可空）", _dns2, 4, 0);

            _showInTray.Text = "显示在托盘快捷菜单";
            _showInTray.AutoSize = true;
            _showInTray.ForeColor = TextColor;
            _showInTray.Margin = new Padding(3, 9, 3, 10);
            fields.Controls.Add(_showInTray, 1, 4);

            var routeHeader = new Panel { Dock = DockStyle.Fill, Height = 35, Margin = new Padding(0, 10, 0, 4) };
            var routeTitle = new Label
            {
                Text = "静态路由（可选）",
                AutoSize = true,
                ForeColor = TextColor,
                Font = CreateFont(9.5f, FontStyle.Bold),
                Location = new Point(0, 8)
            };
            var routeHint = new Label
            {
                Text = "没有路由时保持空白即可",
                AutoSize = true,
                ForeColor = Muted,
                Location = new Point(130, 9)
            };
            routeHeader.Controls.Add(routeTitle);
            routeHeader.Controls.Add(routeHint);

            ConfigureRoutesGrid();

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 14, 0, 0)
            };
            var save = MakeButton("保存配置", true);
            var cancel = MakeButton("取消", false);
            save.Click += SaveClicked;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;

            var header = new Panel { Dock = DockStyle.Top, Height = 56, Margin = new Padding(0) };
            header.Controls.Add(title);
            header.Controls.Add(note);
            note.Location = new Point(0, 29);

            var routeArea = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            routeArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            routeArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            routeArea.Controls.Add(routeHeader, 0, 0);
            routeArea.Controls.Add(_routes, 0, 1);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(fields, 0, 1);
            root.Controls.Add(routeArea, 0, 3);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);
        }

        private void ConfigureRoutesGrid()
        {
            _routes.Dock = DockStyle.Fill;
            _routes.BackgroundColor = Panel;
            _routes.BorderStyle = BorderStyle.FixedSingle;
            _routes.GridColor = Border;
            _routes.EnableHeadersVisualStyles = false;
            _routes.ColumnHeadersDefaultCellStyle.BackColor = Input;
            _routes.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
            _routes.ColumnHeadersDefaultCellStyle.SelectionBackColor = Input;
            _routes.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextColor;
            _routes.DefaultCellStyle.BackColor = Panel;
            _routes.DefaultCellStyle.ForeColor = TextColor;
            _routes.DefaultCellStyle.SelectionBackColor = ColorTranslator.FromHtml("#355274");
            _routes.DefaultCellStyle.SelectionForeColor = TextColor;
            _routes.DefaultCellStyle.NullValue = "";
            _routes.RowHeadersVisible = false;
            _routes.AllowUserToAddRows = true;
            _routes.AllowUserToDeleteRows = true;
            _routes.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _routes.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _routes.MultiSelect = false;
            _routes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Destination", HeaderText = "目标网段，例如 10.20.0.0/16", FillWeight = 48 });
            _routes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Gateway", HeaderText = "网关", FillWeight = 32 });
            _routes.Columns.Add(new DataGridViewTextBoxColumn { Name = "Metric", HeaderText = "跃点数", FillWeight = 20 });
        }

        private static void AddField(TableLayoutPanel layout, string labelText, Control control, int row, int column, int span = 1)
        {
            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                RowCount = 2,
                ColumnCount = 1,
                Margin = new Padding(column == 0 ? 0 : 6, 0, column == 0 && span == 1 ? 6 : 0, 8)
            };
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            host.Controls.Add(new Label
            {
                Text = labelText,
                AutoSize = true,
                ForeColor = Muted,
                Margin = new Padding(0, 0, 0, 4)
            }, 0, 0);
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0);
            host.Controls.Add(control, 0, 1);
            layout.Controls.Add(host, column, row);
            if (span > 1) layout.SetColumnSpan(host, span);
        }

        private void PopulateAdapters(IEnumerable<AdapterInfo> adapters)
        {
            var list = adapters
                .Where(adapter => adapter != null)
                .OrderBy(NetworkService.LooksVirtual)
                .ThenBy(adapter => adapter.Name)
                .ToList();
            foreach (var adapter in list) _adapter.Items.Add(adapter);

            var selected = NetworkService.FindByIdentity(list, _workingCopy.AdapterId, _workingCopy.AdapterName);
            if (selected != null) _adapter.SelectedItem = selected;
            else if (_adapter.Items.Count > 0) _adapter.SelectedIndex = 0;
        }

        private void LoadProfile()
        {
            _name.Text = _workingCopy.Name;
            _address.Text = _workingCopy.Address;
            _mask.Text = string.IsNullOrWhiteSpace(_workingCopy.SubnetMask) ? "255.255.255.0" : _workingCopy.SubnetMask;
            _gateway.Text = _workingCopy.Gateway;
            _dns1.Text = _workingCopy.DnsServers?.ElementAtOrDefault(0) ?? "";
            _dns2.Text = _workingCopy.DnsServers?.ElementAtOrDefault(1) ?? "";
            _showInTray.Checked = _workingCopy.ShowInTray;
            foreach (var route in _workingCopy.Routes ?? new List<IpRouteProfile>())
                _routes.Rows.Add(route.DestinationPrefix, route.Gateway, route.Metric);
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            if (!TryBuildProfile(out var profile, out var error))
            {
                MessageBox.Show(this, error, "无法保存配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ResultProfile = profile;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var work = Screen.FromControl(this).WorkingArea;
            var width = Math.Min(Width, Math.Max(320, work.Width - 24));
            var height = Math.Min(Height, Math.Max(420, work.Height - 24));
            Size = new Size(width, height);
            Location = new Point(
                work.Left + Math.Max(0, (work.Width - Width) / 2),
                work.Top + Math.Max(0, (work.Height - Height) / 2));
        }

        private bool TryBuildProfile(out IpProfile profile, out string error)
        {
            profile = _workingCopy.Clone();
            error = "";
            if (!(_adapter.SelectedItem is AdapterInfo adapter))
            {
                error = "请选择目标网卡。";
                return false;
            }

            profile.Name = _name.Text;
            profile.AdapterId = adapter.Id;
            profile.AdapterName = adapter.Name;
            profile.Address = _address.Text;
            profile.SubnetMask = _mask.Text;
            profile.Gateway = _gateway.Text;
            profile.DnsServers = new[] { _dns1.Text, _dns2.Text }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            profile.ShowInTray = _showInTray.Checked;
            profile.Routes = new List<IpRouteProfile>();

            foreach (DataGridViewRow row in _routes.Rows)
            {
                if (row.IsNewRow) continue;
                var destination = Convert.ToString(row.Cells["Destination"].Value)?.Trim() ?? "";
                var gateway = Convert.ToString(row.Cells["Gateway"].Value)?.Trim() ?? "";
                var metricText = Convert.ToString(row.Cells["Metric"].Value)?.Trim() ?? "";
                if (destination.Length == 0 && gateway.Length == 0 && metricText.Length == 0) continue;
                if (!int.TryParse(metricText.Length == 0 ? "10" : metricText, out var metric))
                {
                    error = "静态路由的跃点数必须是数字。";
                    return false;
                }
                profile.Routes.Add(new IpRouteProfile
                {
                    DestinationPrefix = destination,
                    Gateway = gateway,
                    Metric = metric
                });
            }
            return profile.TryValidate(out error);
        }

        private static TextBox CreateTextBox() => new TextBox
        {
            BackColor = Input,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Height = 29
        };

        private static ComboBox CreateComboBox() => new ComboBox
        {
            BackColor = Input,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Height = 29
        };

        private static Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = false,
                Size = new Size(104, 36),
                Margin = new Padding(8, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Panel,
                ForeColor = primary ? Color.Black : TextColor,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            return button;
        }

        private static Font CreateFont(float size, FontStyle style = FontStyle.Regular)
        {
            try { return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); }
            catch { return new Font(SystemFonts.DefaultFont.FontFamily, size, style, GraphicsUnit.Point); }
        }
    }
}
