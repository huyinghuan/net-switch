using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NetSwitcher
{
    internal sealed class IpProfileEventArgs : EventArgs
    {
        public IpProfileEventArgs(IpProfile profile) => Profile = profile;
        public IpProfile Profile { get; }
    }

    internal sealed class IpProfilesView : UserControl
    {
        private static readonly Color Bg = ColorTranslator.FromHtml("#141821");
        private static readonly Color Card = ColorTranslator.FromHtml("#1E2430");
        private static readonly Color Border = ColorTranslator.FromHtml("#2C3444");
        private static readonly Color TextColor = ColorTranslator.FromHtml("#E6EAF2");
        private static readonly Color Muted = ColorTranslator.FromHtml("#8892A6");
        private static readonly Color Accent = ColorTranslator.FromHtml("#4A9EFF");
        private static readonly Color Selection = ColorTranslator.FromHtml("#2F4D70");
        private static readonly Color Ok = ColorTranslator.FromHtml("#34D399");
        private static readonly Color Danger = ColorTranslator.FromHtml("#F87171");

        private readonly AppConfig _config;
        private readonly Func<IReadOnlyList<AdapterInfo>> _adapterProvider;
        private float _scale = 1f;
        private bool _busy;

        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Button _newButton;
        private readonly ListBox _profileList;
        private readonly Panel _listCard;
        private readonly Panel _dhcpCard;
        private readonly Label _dhcpNote;
        private readonly Button _dhcpButton;
        private readonly Panel _detailCard;
        private readonly Label _detailName;
        private readonly Label _detailStatus;
        private readonly Label[] _fieldLabels;
        private readonly Label[] _fieldValues;
        private readonly Button _editButton;
        private readonly Button _deleteButton;
        private readonly Button _applyButton;

        public event EventHandler<IpProfileEventArgs> ApplyRequested;
        public event EventHandler<IpProfileEventArgs> RestoreDhcpRequested;
        public event EventHandler ProfilesChanged;

        public IpProfilesView(AppConfig config, Func<IReadOnlyList<AdapterInfo>> adapterProvider)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _adapterProvider = adapterProvider ?? throw new ArgumentNullException(nameof(adapterProvider));
            BackColor = Bg;
            ForeColor = TextColor;
            DoubleBuffered = true;

            _title = MakeLabel("常用 IP 配置", 12f, FontStyle.Bold, TextColor);
            _subtitle = MakeLabel("只修改所选网卡的地址参数，不改变当前出口优先级", 8.5f, FontStyle.Regular, Muted);
            _newButton = MakeButton("＋  新增配置", true);
            _newButton.Click += (sender, args) => AddProfile();

            _listCard = MakeCard();
            _profileList = new ListBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Card,
                ForeColor = TextColor,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                ItemHeight = S(54)
            };
            _profileList.DrawItem += DrawProfileItem;
            _profileList.SelectedIndexChanged += (sender, args) => UpdateDetails();
            _listCard.Controls.Add(_profileList);

            _dhcpCard = MakeCard();
            _dhcpNote = MakeLabel("选择上方配置后，可将它绑定的网卡恢复为自动获取 IP 和 DNS。", 8.5f, FontStyle.Regular, Muted);
            _dhcpNote.AutoEllipsis = true;
            _dhcpButton = MakeButton("恢复自动获取（DHCP）", false);
            _dhcpButton.Click += (sender, args) =>
            {
                if (SelectedProfile != null)
                    RestoreDhcpRequested?.Invoke(this, new IpProfileEventArgs(SelectedProfile));
            };
            _dhcpCard.Controls.Add(_dhcpNote);
            _dhcpCard.Controls.Add(_dhcpButton);

            _detailCard = MakeCard();
            _detailName = MakeLabel("请选择一个配置", 11f, FontStyle.Bold, TextColor);
            _detailStatus = MakeLabel("", 8.5f, FontStyle.Bold, Ok);
            var names = new[] { "应用到网卡", "IPv4 地址", "子网掩码", "默认网关（可空）", "DNS（可空）", "静态路由（可选）" };
            _fieldLabels = names.Select(name => MakeLabel(name, 8.5f, FontStyle.Regular, Muted)).ToArray();
            _fieldValues = names.Select(_ => MakeLabel("—", 9f, FontStyle.Regular, TextColor)).ToArray();
            foreach (var label in _fieldValues) label.AutoEllipsis = true;

            _editButton = MakeButton("编辑", false);
            _deleteButton = MakeButton("删除", false);
            _deleteButton.ForeColor = Danger;
            _applyButton = MakeButton("应用此配置", true);
            _editButton.Click += (sender, args) => EditSelectedProfile();
            _deleteButton.Click += (sender, args) => DeleteSelectedProfile();
            _applyButton.Click += (sender, args) =>
            {
                if (SelectedProfile != null)
                    ApplyRequested?.Invoke(this, new IpProfileEventArgs(SelectedProfile));
            };

            _detailCard.Controls.Add(_detailName);
            _detailCard.Controls.Add(_detailStatus);
            foreach (var label in _fieldLabels) _detailCard.Controls.Add(label);
            foreach (var label in _fieldValues) _detailCard.Controls.Add(label);
            _detailCard.Controls.Add(_editButton);
            _detailCard.Controls.Add(_deleteButton);
            _detailCard.Controls.Add(_applyButton);

            Controls.Add(_title);
            Controls.Add(_subtitle);
            Controls.Add(_newButton);
            Controls.Add(_listCard);
            Controls.Add(_dhcpCard);
            Controls.Add(_detailCard);

            ApplyScale(1f);
            ReloadProfiles();
        }

        public IpProfile SelectedProfile => _profileList.SelectedItem as IpProfile;

        public void ApplyScale(float scale)
        {
            _scale = Math.Max(1f, scale);
            _profileList.ItemHeight = S(54);
            ReplaceFont(_title, 12f, FontStyle.Bold);
            ReplaceFont(_subtitle, 8.5f);
            ReplaceFont(_newButton, 9f, FontStyle.Bold);
            ReplaceFont(_profileList, 9f);
            ReplaceFont(_dhcpNote, 8.5f);
            ReplaceFont(_dhcpButton, 9f);
            ReplaceFont(_detailName, 11f, FontStyle.Bold);
            ReplaceFont(_detailStatus, 8.5f, FontStyle.Bold);
            foreach (var label in _fieldLabels) ReplaceFont(label, 8.5f);
            foreach (var label in _fieldValues) ReplaceFont(label, 9f);
            ReplaceFont(_editButton, 9f);
            ReplaceFont(_deleteButton, 9f);
            ReplaceFont(_applyButton, 9f, FontStyle.Bold);
            LayoutView();
            Invalidate(true);
        }

        public void ReloadProfiles(string preferredId = null)
        {
            var selectedId = preferredId ?? SelectedProfile?.Id;
            _profileList.BeginUpdate();
            _profileList.Items.Clear();
            foreach (var profile in _config.IpProfiles ?? new List<IpProfile>())
                _profileList.Items.Add(profile);
            _profileList.EndUpdate();

            if (_profileList.Items.Count > 0)
            {
                var index = 0;
                if (!string.IsNullOrWhiteSpace(selectedId))
                {
                    for (var i = 0; i < _profileList.Items.Count; i++)
                    {
                        if (_profileList.Items[i] is IpProfile item && item.Id == selectedId)
                        {
                            index = i;
                            break;
                        }
                    }
                }
                _profileList.SelectedIndex = index;
            }
            UpdateDetails();
        }

        public void RefreshCurrentState()
        {
            UpdateDetails();
            _profileList.Invalidate();
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            _newButton.Enabled = !busy;
            UpdateActionState();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutView();
        }

        private void LayoutView()
        {
            if (_title == null) return;
            var pad = S(18);
            var top = S(16);
            var toolbarHeight = S(58);
            var gap = S(12);
            var leftWidth = Math.Min(S(265), Math.Max(S(215), (Width - pad * 2 - gap) / 3));

            _title.Location = new Point(pad, top);
            _subtitle.Location = new Point(pad, top + S(25));
            _subtitle.MaximumSize = new Size(Math.Max(S(200), Width - S(210)), S(24));
            _newButton.Bounds = new Rectangle(Math.Max(pad, Width - pad - S(126)), top, S(126), S(36));

            var bodyTop = top + toolbarHeight;
            var bodyHeight = Math.Max(S(220), Height - bodyTop - S(14));
            var dhcpHeight = S(116);
            _listCard.Bounds = new Rectangle(pad, bodyTop, leftWidth, Math.Max(S(110), bodyHeight - dhcpHeight - gap));
            _profileList.Bounds = new Rectangle(S(7), S(7), Math.Max(S(80), _listCard.Width - S(14)), Math.Max(S(60), _listCard.Height - S(14)));
            _dhcpCard.Bounds = new Rectangle(pad, _listCard.Bottom + gap, leftWidth, dhcpHeight);
            _dhcpNote.Bounds = new Rectangle(S(12), S(10), Math.Max(S(80), leftWidth - S(24)), S(42));
            _dhcpButton.Bounds = new Rectangle(S(12), dhcpHeight - S(46), Math.Max(S(80), leftWidth - S(24)), S(34));

            var detailLeft = pad + leftWidth + gap;
            _detailCard.Bounds = new Rectangle(detailLeft, bodyTop, Math.Max(S(220), Width - detailLeft - pad), bodyHeight);
            LayoutDetails();
        }

        private void LayoutDetails()
        {
            var pad = S(18);
            var width = _detailCard.Width;
            _detailName.Bounds = new Rectangle(pad, S(16), Math.Max(S(100), width - S(180)), S(26));
            _detailStatus.Bounds = new Rectangle(Math.Max(pad, width - S(145)), S(18), S(125), S(23));
            _detailStatus.TextAlign = ContentAlignment.MiddleRight;

            var y = S(60);
            var labelWidth = S(125);
            var buttonY = Math.Max(S(278), _detailCard.Height - S(54));
            var availableRowsHeight = Math.Max(S(204), buttonY - y - S(8));
            var rowHeight = Math.Max(S(34), Math.Min(S(46), availableRowsHeight / _fieldLabels.Length));
            for (var i = 0; i < _fieldLabels.Length; i++)
            {
                _fieldLabels[i].Bounds = new Rectangle(pad, y + S(3), labelWidth, S(22));
                _fieldValues[i].Bounds = new Rectangle(pad + labelWidth, y, Math.Max(S(80), width - pad * 2 - labelWidth), S(26));
                y += rowHeight;
            }

            _applyButton.Bounds = new Rectangle(width - pad - S(126), buttonY, S(126), S(36));
            _deleteButton.Bounds = new Rectangle(_applyButton.Left - S(82), buttonY, S(74), S(36));
            _editButton.Bounds = new Rectangle(_deleteButton.Left - S(82), buttonY, S(74), S(36));
        }

        private void AddProfile()
        {
            using var dialog = new IpProfileDialog(_adapterProvider());
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.ResultProfile == null) return;
            if (HasDuplicateName(dialog.ResultProfile.Name, null))
            {
                MessageBox.Show(FindForm(), "已经存在同名配置，请换一个名称。", "无法保存配置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _config.IpProfiles.Add(dialog.ResultProfile);
            _config.Save();
            ReloadProfiles(dialog.ResultProfile.Id);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void EditSelectedProfile()
        {
            var selected = SelectedProfile;
            if (selected == null) return;
            using var dialog = new IpProfileDialog(_adapterProvider(), selected);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.ResultProfile == null) return;
            if (HasDuplicateName(dialog.ResultProfile.Name, selected.Id))
            {
                MessageBox.Show(FindForm(), "已经存在同名配置，请换一个名称。", "无法保存配置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var index = _config.IpProfiles.FindIndex(profile => profile.Id == selected.Id);
            if (index >= 0) _config.IpProfiles[index] = dialog.ResultProfile;
            _config.Save();
            ReloadProfiles(dialog.ResultProfile.Id);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteSelectedProfile()
        {
            var selected = SelectedProfile;
            if (selected == null) return;
            if (MessageBox.Show(FindForm(), "确定删除 IP 配置「" + selected.Name + "」吗？\n这不会立即修改网卡。",
                    "删除 IP 配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            _config.IpProfiles.RemoveAll(profile => profile.Id == selected.Id);
            if (_config.LastAppliedIpProfileId == selected.Id) _config.LastAppliedIpProfileId = "";
            _config.Save();
            ReloadProfiles();
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool HasDuplicateName(string name, string exceptId) =>
            _config.IpProfiles.Any(profile =>
                profile.Id != exceptId && string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase));

        private void UpdateDetails()
        {
            var profile = SelectedProfile;
            if (profile == null)
            {
                _detailName.Text = "还没有保存的 IP 配置";
                _detailStatus.Text = "";
                foreach (var value in _fieldValues) value.Text = "—";
                _dhcpNote.Text = "新增配置后，可以在这里将对应网卡恢复为自动获取 IP 和 DNS。";
                UpdateActionState();
                return;
            }

            var adapter = NetworkService.FindByIdentity(_adapterProvider(), profile.AdapterId, profile.AdapterName);
            var current = _config.LastAppliedIpProfileId == profile.Id && NetworkService.IpProfileMatchesAdapter(profile, adapter);
            _detailName.Text = profile.Name;
            _detailStatus.Text = current ? "● 当前配置" : "";
            _fieldValues[0].Text = profile.AdapterName + (adapter == null ? "（当前未检测到）" : "");
            _fieldValues[1].Text = profile.Address;
            _fieldValues[2].Text = profile.SubnetMask;
            _fieldValues[3].Text = string.IsNullOrWhiteSpace(profile.Gateway) ? "未设置" : profile.Gateway;
            _fieldValues[4].Text = profile.DnsServers == null || profile.DnsServers.Count == 0
                ? "未设置"
                : string.Join("，", profile.DnsServers);
            _fieldValues[5].Text = profile.Routes == null || profile.Routes.Count == 0
                ? "未添加"
                : string.Join("；", profile.Routes.Select(route => route.DestinationPrefix + " → " + route.Gateway));
            _dhcpNote.Text = "将「" + profile.AdapterName + "」恢复为自动获取 IP 和 DNS。";
            UpdateActionState();
        }

        private void UpdateActionState()
        {
            var hasProfile = SelectedProfile != null;
            _editButton.Enabled = hasProfile && !_busy;
            _deleteButton.Enabled = hasProfile && !_busy;
            _applyButton.Enabled = hasProfile && !_busy;
            _dhcpButton.Enabled = hasProfile && !_busy;
            _profileList.Enabled = !_busy;
        }

        private void DrawProfileItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _profileList.Items.Count) return;
            var profile = (IpProfile)_profileList.Items[e.Index];
            var selected = (e.State & DrawItemState.Selected) != 0;
            var bounds = e.Bounds;
            using (var brush = new SolidBrush(selected ? Selection : Card))
                e.Graphics.FillRectangle(brush, bounds);

            var currentAdapter = NetworkService.FindByIdentity(_adapterProvider(), profile.AdapterId, profile.AdapterName);
            var current = _config.LastAppliedIpProfileId == profile.Id &&
                          NetworkService.IpProfileMatchesAdapter(profile, currentAdapter);
            var left = bounds.Left + S(12);
            var rightPad = current ? S(26) : S(8);
            TextRenderer.DrawText(e.Graphics, profile.Name, Font,
                new Rectangle(left, bounds.Top + S(7), Math.Max(S(30), bounds.Width - S(20) - rightPad), S(21)),
                TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, profile.Address + " / " + profile.SubnetMask, Font,
                new Rectangle(left, bounds.Top + S(29), Math.Max(S(30), bounds.Width - S(20)), S(18)),
                Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (current)
            {
                using var brush = new SolidBrush(Ok);
                e.Graphics.FillEllipse(brush, bounds.Right - S(18), bounds.Top + S(13), S(7), S(7));
            }
            e.DrawFocusRectangle();
        }

        private Panel MakeCard()
        {
            var panel = new Panel { BackColor = Card };
            panel.Paint += (sender, args) =>
            {
                using var pen = new Pen(Border, 1f);
                args.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            };
            return panel;
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Card,
                ForeColor = primary ? Color.Black : TextColor,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent, .08f) : ColorTranslator.FromHtml("#293242");
            return button;
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color) => new Label
        {
            Text = text,
            AutoSize = false,
            ForeColor = color,
            Font = CreateFont(size, style),
            TextAlign = ContentAlignment.MiddleLeft
        };

        private int S(int value) => Math.Max(1, (int)Math.Round(value * _scale));

        private static Font CreateFont(float size, FontStyle style = FontStyle.Regular)
        {
            try { return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); }
            catch { return new Font(SystemFonts.DefaultFont.FontFamily, size, style, GraphicsUnit.Point); }
        }

        private static void ReplaceFont(Control control, float size, FontStyle style = FontStyle.Regular)
        {
            var old = control.Font;
            control.Font = CreateFont(size, style);
            old?.Dispose();
        }
    }
}
