using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetSwitcher
{
    public sealed class MainForm : Form
    {
        #region 配色

        private static readonly Color C_Bg = ColorTranslator.FromHtml("#141821");
        private static readonly Color C_Card = ColorTranslator.FromHtml("#1E2430");
        private static readonly Color C_Border = ColorTranslator.FromHtml("#2C3444");
        private static readonly Color C_Header = ColorTranslator.FromHtml("#191E28");
        private static readonly Color C_Text = ColorTranslator.FromHtml("#E6EAF2");
        private static readonly Color C_Muted = ColorTranslator.FromHtml("#8892A6");
        private static readonly Color C_Wired = ColorTranslator.FromHtml("#4A9EFF");
        private static readonly Color C_Wifi = ColorTranslator.FromHtml("#A78BFA");
        private static readonly Color C_Ok = ColorTranslator.FromHtml("#34D399");
        private static readonly Color C_Warn = ColorTranslator.FromHtml("#FBBF24");
        private static readonly Color C_Bad = ColorTranslator.FromHtml("#F87171");

        #endregion

        // DPI 缩放系数。AutoScaleMode.None，所有像素尺寸统一乘这个系数，
        // 避免框架自动缩放与手工布局打架。
        private static readonly float DpiScale = InitScale();

        private static float InitScale()
        {
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                var dpi = g.DpiX;
                return dpi > 1 ? dpi / 96f : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        private static int S(int v) => Math.Max(1, (int)Math.Round(v * DpiScale));

        private readonly AppConfig _cfg;
        private List<AdapterInfo> _adapters = new List<AdapterInfo>();

        // 界面元素
        private Panel _header, _summary, _cardsHost, _options, _statusBar;
        private Panel _cardLeft, _cardRight;
        private Label _lblTitle, _lblVer, _sumTitle, _sumSub, _statusLabel;
        private Button _btnClose, _btnMin, _btnRefresh;
        private CheckBox _chkHard, _chkRestore;

        private readonly AdapterCard _cardWired = new AdapterCard();
        private readonly AdapterCard _cardWifi = new AdapterCard();

        private NotifyIcon _tray;
        private Timer _timer;
        private bool _loading = true;
        private bool _syncing;
        private bool _busy;
        private bool _metricsStale;
        private bool _hideTipShown;

        private readonly Dictionary<int, Tuple<long, long>> _lastBytes = new Dictionary<int, Tuple<long, long>>();
        private DateTime _lastSample = DateTime.MinValue;
        private AdapterInfo _currentExit;

        public MainForm()
        {
            _cfg = AppConfig.Load();
            BuildUi();
            InitTray();

            _adapters = NetworkService.Enumerate();
            NetworkService.RefreshMetrics(_adapters);
            PopulateCombos();
            UpdateUi();

            _loading = false;

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        #region 界面搭建

        private void BuildUi()
        {
            Text = "网络一键切换器";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(S(760), S(600));
            BackColor = C_Bg;
            ForeColor = C_Text;
            Font = F(9f);
            Icon = IconFactory.Create(IconFactory.WiredColor);
            MaximizeBox = false;

            _header = BuildHeader();
            _summary = BuildSummaryArea();
            _cardsHost = new Panel { BackColor = C_Bg };
            _cardLeft = BuildCard("有线以太网", _cardWired, C_Wired, "切换到有线");
            _cardRight = BuildCard("无线 Wi-Fi", _cardWifi, C_Wifi, "切换到 Wi-Fi");
            _cardsHost.Controls.Add(_cardLeft);
            _cardsHost.Controls.Add(_cardRight);
            _options = BuildOptionsArea();
            _statusBar = BuildStatusArea();

            Controls.Add(_cardsHost);
            Controls.Add(_options);
            Controls.Add(_statusBar);
            Controls.Add(_summary);
            Controls.Add(_header);

            _cardWired.Button.Click += (s, e) => FireAndTrack(SwitchTo(true, false));
            _cardWifi.Button.Click += (s, e) => FireAndTrack(SwitchTo(false, false));
            _cardWired.Combo.SelectedIndexChanged += (s, e) => OnComboChanged();
            _cardWifi.Combo.SelectedIndexChanged += (s, e) => OnComboChanged();

            LayoutChrome();
        }

        private Panel BuildHeader()
        {
            var panel = new Panel { BackColor = C_Header };

            _lblTitle = new Label
            {
                Text = "网络一键切换器",
                ForeColor = C_Text,
                Font = F(11.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(S(20), S(15))
            };

            _lblVer = new Label
            {
                Text = "v1.0",
                ForeColor = C_Muted,
                Font = F(8f),
                AutoSize = true
            };

            _btnClose = MakeTitleButton("✕");
            _btnClose.Click += (s, e) => HideToTray();

            _btnMin = MakeTitleButton("─");
            _btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;

            _btnRefresh = MakeTitleButton("⟳");
            _btnRefresh.Click += OnRefreshClick;

            panel.Controls.Add(_lblTitle);
            panel.Controls.Add(_lblVer);
            panel.Controls.Add(_btnClose);
            panel.Controls.Add(_btnMin);
            panel.Controls.Add(_btnRefresh);

            panel.MouseDown += (s, e) => BeginDragMove(e);
            foreach (Control c in panel.Controls)
            {
                if (c is Label) c.MouseDown += (s, e) => BeginDragMove(e);
            }
            return panel;
        }

        private Button MakeTitleButton(string text)
        {
            var b = new Button
            {
                Size = new Size(S(32), S(30)),
                Text = text,
                FlatStyle = FlatStyle.Flat,
                ForeColor = C_Muted,
                BackColor = C_Header,
                Font = F(10f),
                TabStop = false,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#2A3242");
            return b;
        }

        private Panel BuildSummaryArea()
        {
            var panel = new Panel { BackColor = C_Bg };

            var tip = new Label
            {
                Text = "当前上网出口",
                ForeColor = C_Muted,
                Font = F(9f),
                AutoSize = true,
                Location = new Point(S(20), S(10))
            };

            _sumTitle = new Label
            {
                Text = "读取中…",
                ForeColor = C_Text,
                Font = F(15f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(S(20), S(30))
            };

            _sumSub = new Label
            {
                Text = "",
                ForeColor = C_Muted,
                Font = F(9f),
                AutoSize = true,
                Location = new Point(S(20), S(66))
            };

            panel.Controls.Add(tip);
            panel.Controls.Add(_sumTitle);
            panel.Controls.Add(_sumSub);
            return panel;
        }

        private Panel BuildOptionsArea()
        {
            var panel = new Panel { BackColor = C_Bg };

            _chkHard = CheckRow("切换时禁用另一张网卡（彻底隔离，切换时另一张会断开）", C_Text);
            _chkHard.Location = new Point(S(20), S(10));
            _chkHard.CheckedChanged += OnHardIsolateChanged;

            _chkRestore = CheckRow("启动时自动恢复上次选择的出口", C_Muted);
            _chkRestore.Location = new Point(S(20), S(46));
            _chkRestore.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _cfg.RestoreOnStartup = _chkRestore.Checked;
                _cfg.Save();
            };

            panel.Controls.Add(_chkHard);
            panel.Controls.Add(_chkRestore);
            return panel;
        }

        private CheckBox CheckRow(string text, Color color)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                ForeColor = color,
                Font = F(9f)
            };
        }

        private Panel BuildStatusArea()
        {
            var panel = new Panel { BackColor = C_Header };
            _statusLabel = new Label
            {
                Bounds = new Rectangle(S(20), 0, 100, S(34)),
                ForeColor = C_Muted,
                Font = F(9f),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "就绪"
            };
            panel.Controls.Add(_statusLabel);
            return panel;
        }

        private Panel BuildCard(string title, AdapterCard card, Color accent, string buttonText)
        {
            var root = new Panel
            {
                BackColor = C_Card
            };
            root.Paint += (s, e) =>
            {
                using var pen = new Pen(C_Border, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, root.Width - 1, root.Height - 1);
            };

            var headTitle = new Label
            {
                Text = title,
                ForeColor = accent,
                Font = F(10.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(S(16), S(12))
            };

            card.Badge = new Label
            {
                Text = "",
                ForeColor = C_Ok,
                Font = F(8.5f, FontStyle.Bold),
                AutoSize = true
            };

            card.Combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = ColorTranslator.FromHtml("#263041"),
                ForeColor = C_Text,
                FlatStyle = FlatStyle.Flat,
                Font = F(9.5f)
            };

            card.Status = RowLabel(C_Text, 10f, FontStyle.Bold);
            card.Ip = RowLabel(C_Text, 9f);
            card.Gateway = RowLabel(C_Muted, 9f);
            card.Extra = RowLabel(C_Muted, 8.5f);
            card.Rate = RowLabel(accent, 9.5f, FontStyle.Bold);
            card.Metric = RowLabel(C_Muted, 9f);

            card.Button = new Button
            {
                Text = buttonText,
                Font = F(10f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Black,
                BackColor = accent,
                Cursor = Cursors.Hand
            };
            card.Button.FlatAppearance.BorderSize = 0;
            card.Button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(accent, 0.08f);
            card.Button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(accent, 0.12f);

            card.Root = root;
            card.HeadTitle = headTitle;

            root.Controls.Add(headTitle);
            root.Controls.Add(card.Badge);
            root.Controls.Add(card.Combo);
            root.Controls.Add(card.Status);
            root.Controls.Add(card.Ip);
            root.Controls.Add(card.Gateway);
            root.Controls.Add(card.Extra);
            root.Controls.Add(card.Rate);
            root.Controls.Add(card.Metric);
            root.Controls.Add(card.Button);
            return root;
        }

        private static Label RowLabel(Color color, float size, FontStyle style = FontStyle.Regular) =>
            new Label
            {
                ForeColor = color,
                Font = F(size, style),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = "—"
            };

        /// <summary>按当前窗口尺寸摆放所有元素。全部手工定位，行为完全可控。</summary>
        private void LayoutChrome()
        {
            if (_header == null || _cardLeft == null) return;

            int w = ClientSize.Width;
            int h = ClientSize.Height;

            int headerH = S(54);
            int summaryH = S(104);
            int statusH = S(34);
            int optionsH = S(80);

            _header.Bounds = new Rectangle(0, 0, w, headerH);
            _summary.Bounds = new Rectangle(0, headerH, w, summaryH);

            int hostTop = headerH + summaryH;
            _options.Bounds = new Rectangle(0, h - statusH - optionsH, w, optionsH);
            _statusBar.Bounds = new Rectangle(0, h - statusH, w, statusH);

            _cardsHost.Bounds = new Rectangle(S(16), hostTop + S(4), w - 2 * S(16), h - hostTop - S(4) - optionsH - statusH - S(6));

            int gap = S(14);
            int cw = (_cardsHost.Width - gap) / 2;
            _cardLeft.Bounds = new Rectangle(0, 0, cw, _cardsHost.Height);
            _cardRight.Bounds = new Rectangle(cw + gap, 0, _cardsHost.Width - cw - gap, _cardsHost.Height);

            // 标题栏内部
            _lblVer.Location = new Point(_lblTitle.Right + S(8), S(22));
            _btnClose.Location = new Point(w - S(50), S(12));
            _btnMin.Location = new Point(w - S(84), S(12));
            _btnRefresh.Location = new Point(w - S(118), S(12));

            _statusLabel.Bounds = new Rectangle(S(20), 0, w - S(40), statusH);

            LayoutCard(_cardLeft, _cardWired);
            LayoutCard(_cardRight, _cardWifi);
        }

        private static void LayoutCard(Panel root, AdapterCard card)
        {
            int w = root.Width;
            int h = root.Height;
            int pad = S(16);
            int inner = w - 2 * pad;

            card.HeadTitle.Location = new Point(pad, S(12));
            card.Combo.Bounds = new Rectangle(pad, S(42), inner, S(30));
            card.Status.Bounds = new Rectangle(pad, S(82), inner, S(26));
            card.Ip.Bounds = new Rectangle(pad, S(112), inner, S(22));
            card.Gateway.Bounds = new Rectangle(pad, S(138), inner, S(22));
            card.Extra.Bounds = new Rectangle(pad, S(164), inner, S(22));
            card.Rate.Bounds = new Rectangle(pad, S(192), inner, S(26));
            card.Metric.Bounds = new Rectangle(pad, S(222), inner, S(22));
            card.Button.Bounds = new Rectangle(pad, h - S(16) - S(46), inner, S(46));

            card.Badge.Location = new Point(w - pad - card.Badge.Width, S(16));
        }

        private void InitTray()
        {
            _tray = new NotifyIcon
            {
                Icon = IconFactory.Create(IconFactory.WiredColor),
                Text = "网络一键切换器",
                Visible = true
            };

            var menu = new ContextMenuStrip
            {
                BackColor = ColorTranslator.FromHtml("#1E2430"),
                ForeColor = C_Text,
                ShowImageMargin = false,
                ShowCheckMargin = false
            };
            menu.Items.Add("切换到有线以太网", null, (s, e) => { ShowWindow(); FireAndTrack(SwitchTo(true, false)); });
            menu.Items.Add("切换到 Wi-Fi", null, (s, e) => { ShowWindow(); FireAndTrack(SwitchTo(false, false)); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示主界面", null, (s, e) => ShowWindow());
            menu.Items.Add("退出", null, (s, e) =>
            {
                _tray.Visible = false;
                Application.Exit();
            });

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowWindow();
            _tray.BalloonTipClicked += (s, e) => ShowWindow();
        }

        #endregion

        #region 数据刷新

        private void PopulateCombos()
        {
            SyncCombo(_cardWired.Combo, NetworkService.BuildCandidateList(_adapters, false, _cfg.EthernetAdapter), _cfg.EthernetAdapter);
            SyncCombo(_cardWifi.Combo, NetworkService.BuildCandidateList(_adapters, true, _cfg.WifiAdapter), _cfg.WifiAdapter);
            _chkHard.Checked = _cfg.HardIsolate;
            _chkRestore.Checked = _cfg.RestoreOnStartup;
            SaveSelection();
        }

        private void SyncCombo(ComboBox combo, List<AdapterInfo> items, string remembered)
        {
            var want = combo.SelectedItem is AdapterInfo cur ? cur.Name : remembered;
            if (string.IsNullOrEmpty(want)) want = remembered;

            var newKey = string.Join("|", items.Select(i => i.Name));
            var oldKey = string.Join("|", combo.Items.OfType<AdapterInfo>().Select(i => i.Name));

            _syncing = true;
            if (newKey != oldKey)
            {
                combo.BeginUpdate();
                combo.Items.Clear();
                foreach (var it in items) combo.Items.Add(it);
                combo.EndUpdate();
            }
            if (combo.Items.Count > 0)
            {
                var pick = items.FirstOrDefault(i => i.Name == want) ?? items[0];
                if (!ReferenceEquals(combo.SelectedItem, pick)) combo.SelectedItem = pick;
            }
            _syncing = false;
        }

        private void OnComboChanged()
        {
            if (_loading || _syncing) return;
            SaveSelection();
            FireAndTrack(RefreshSlow());
        }

        private void SaveSelection()
        {
            if (_cardWired.Combo?.SelectedItem is AdapterInfo a1) _cfg.EthernetAdapter = a1.Name;
            if (_cardWifi.Combo?.SelectedItem is AdapterInfo a2) _cfg.WifiAdapter = a2.Name;
            _cfg.Save();
        }

        private static string SelectedName(ComboBox combo) =>
            combo.SelectedItem is AdapterInfo a ? a.Name : null;

        private void RefreshAdapters(bool withMetrics)
        {
            var prev = _adapters;
            _adapters = NetworkService.Enumerate();
            NetworkService.CarryMetrics(prev, _adapters);
            if (withMetrics || _metricsStale)
            {
                NetworkService.RefreshMetrics(_adapters);
                _metricsStale = false;
            }
            UpdateUi();
        }

        private async Task RefreshSlow()
        {
            await Task.Run(() => { });
            RefreshAdapters(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_busy) return;
            RefreshAdapters(false);
        }

        private void UpdateUi()
        {
            var now = DateTime.Now;
            var dt = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).TotalSeconds;

            _currentExit = _adapters
                .Where(a => a.CanRoute && a.Metric >= 0)
                .OrderBy(a => a.EffectiveMetric)
                .FirstOrDefault();

            UpdateCard(_cardWired, SelectedName(_cardWired.Combo), dt, C_Wired);
            UpdateCard(_cardWifi, SelectedName(_cardWifi.Combo), dt, C_Wifi);
            UpdateSummary();

            _lastBytes.Clear();
            foreach (var a in _adapters)
            {
                if (a.IfIndex >= 0) _lastBytes[a.IfIndex] = Tuple.Create(a.BytesSent, a.BytesReceived);
            }
            _lastSample = now;
        }

        private void UpdateCard(AdapterCard card, string name, double dt, Color accent)
        {
            var a = string.IsNullOrEmpty(name) ? null : _adapters.FirstOrDefault(x => x.Name == name);

            if (a == null)
            {
                card.Status.Text = "未检测到网卡";
                card.Status.ForeColor = C_Bad;
                card.Badge.Text = "";
                card.Ip.Text = "IP：—";
                card.Gateway.Text = "网关：—";
                card.Extra.Text = "—";
                card.Rate.Text = "↑ —    ↓ —";
                card.Metric.Text = "接口跃点数：—";
                card.Button.Enabled = false;
            }
            else
            {
                card.Button.Enabled = !_busy;

                if (!a.IsUp)
                {
                    card.Status.Text = "未连接（网卡已禁用或不可用）";
                    card.Status.ForeColor = C_Bad;
                }
                else if (!a.HasIp)
                {
                    card.Status.Text = a.IsWireless ? "已启用但未连接 Wi-Fi" : "网线未插好";
                    card.Status.ForeColor = C_Warn;
                }
                else
                {
                    card.Status.Text = a.IsWireless ? "已连接 Wi-Fi" : "网线已插好";
                    card.Status.ForeColor = C_Ok;
                }

                bool isExit = _currentExit != null && _currentExit.IfIndex == a.IfIndex && a.IsUp;
                card.Badge.Text = isExit ? "● 当前出口" : "";
                card.Badge.ForeColor = C_Ok;

                card.Ip.Text = "IP：" + (a.HasIp ? a.Ip : "—");
                card.Gateway.Text = "网关：" + (string.IsNullOrEmpty(a.Gateway) ? "—" : a.Gateway);
                card.Extra.Text = "带宽 " + NetworkService.FormatLinkSpeed(a.SpeedBps) +
                                  "   MAC " + (string.IsNullOrEmpty(a.Mac) ? "—" : a.Mac);

                double up = 0, down = 0;
                if (dt > 0.3 && a.IfIndex >= 0 && _lastBytes.TryGetValue(a.IfIndex, out var last))
                {
                    up = Math.Max(0, a.BytesSent - last.Item1) / dt;
                    down = Math.Max(0, a.BytesReceived - last.Item2) / dt;
                }
                card.Rate.Text = "↑ " + NetworkService.FormatSpeed(up) + "     ↓ " + NetworkService.FormatSpeed(down);
                card.Rate.ForeColor = accent;

                card.Metric.Text = a.Metric >= 0
                    ? "接口跃点数 " + a.Metric + "（数字越小越优先）"
                    : "接口跃点数 未知";
            }

            if (card.Root != null && card.Badge != null)
            {
                card.Badge.Location = new Point(card.Root.Width - S(16) - card.Badge.Width, S(16));
            }
        }

        private void UpdateSummary()
        {
            if (_currentExit == null)
            {
                _sumTitle.Text = "未检测到可用网络";
                _sumTitle.ForeColor = C_Bad;
                _sumSub.Text = "请检查网线是否插好，或 Wi-Fi 是否已经连上。";
                return;
            }

            var backup = _adapters
                .Where(a => a.CanRoute && a.Metric >= 0 && a.IfIndex != _currentExit.IfIndex)
                .OrderBy(a => a.EffectiveMetric)
                .FirstOrDefault();

            _sumTitle.Text = _currentExit.Name + "  ·  " + _currentExit.KindText;
            _sumTitle.ForeColor = _currentExit.IsWireless ? C_Wifi : C_Wired;
            _sumSub.Text = "IP " + _currentExit.Ip + "　跃点数 " + _currentExit.Metric +
                           "　" + (backup != null ? "备用出口 " + backup.Name : "无备用出口");
        }

        #endregion

        #region 切换

        private async Task SwitchTo(bool toWired, bool silent)
        {
            if (_busy) return;

            var targetName = toWired ? SelectedName(_cardWired.Combo) : SelectedName(_cardWifi.Combo);
            var otherName = toWired ? SelectedName(_cardWifi.Combo) : SelectedName(_cardWired.Combo);

            if (string.IsNullOrEmpty(targetName))
            {
                SetStatus("请先在下拉框里选择要使用的那张网卡。", true);
                return;
            }

            var target = _adapters.FirstOrDefault(x => x.Name == targetName);
            if (target == null)
            {
                SetStatus("找不到网卡「" + targetName + "」，请点右上角 ⟳ 刷新后重试。", true);
                return;
            }

            AdapterInfo other = string.IsNullOrEmpty(otherName) || otherName == targetName
                ? null
                : _adapters.FirstOrDefault(x => x.Name == otherName);

            if ((!target.IsUp || !target.HasIp) && !silent)
            {
                var reason = !target.IsUp ? "网卡未启用或被禁用" : "没有拿到 IP 地址";
                var dr = MessageBox.Show(this,
                    "网卡「" + target.Name + "」现在还不能上网（" + reason + "）。\n继续把它设为出口的话，可能会导致断网。\n\n仍要继续吗？",
                    "目标网卡未连接",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }

            _busy = true;
            SetBusyUi(true);
            SetStatus("正在切换到「" + target.Name + "」…");

            bool hard = _chkHard.Checked;
            string message = "";
            bool ok = false;
            try
            {
                await Task.Run(() => ok = NetworkService.SetExit(target, other, hard, out message));
            }
            catch (Exception ex)
            {
                message = "切换失败：" + ex.Message;
            }

            _busy = false;
            SetBusyUi(false);

            _cfg.LastExit = target.Name;
            _cfg.Save();

            _metricsStale = true;
            RefreshAdapters(true);

            SetStatus(message, !ok);
            if (!silent)
            {
                _tray.BalloonTipTitle = ok ? "切换完成" : "切换未完成";
                _tray.BalloonTipText = message;
                _tray.ShowBalloonTip(3000);
            }
        }

        private async void OnRefreshClick(object s, EventArgs e)
        {
            SetStatus("正在刷新网卡信息…");
            await Task.Run(() => NetworkService.RefreshMetrics(_adapters));
            RefreshAdapters(true);
            SetStatus("已刷新网卡信息");
        }

        private void OnHardIsolateChanged(object sender, EventArgs e)
        {
            if (_loading) return;

            _cfg.HardIsolate = _chkHard.Checked;
            _cfg.Save();

            if (_chkHard.Checked)
            {
                SetStatus("已开启隔离模式：下次切换时会禁用另一张网卡。");
                return;
            }

            SetStatus("正在恢复双网卡同时连接…");
            FireAndTrack(ReEnableOther());
        }

        private async Task ReEnableOther()
        {
            var names = new[] { SelectedName(_cardWired.Combo), SelectedName(_cardWifi.Combo) };
            await Task.Run(() =>
            {
                foreach (var name in names)
                {
                    var ad = string.IsNullOrEmpty(name) ? null : _adapters.FirstOrDefault(x => x.Name == name);
                    if (ad != null) NetworkService.SetAdapterEnabled(ad, true);
                }
            });
            RefreshAdapters(true);
            SetStatus("已恢复两张网卡同时连接的状态。");
        }

        private void SetBusyUi(bool busy)
        {
            _cardWired.Button.Enabled = !busy;
            _cardWifi.Button.Enabled = !busy;
            _cardWired.Combo.Enabled = !busy;
            _cardWifi.Combo.Enabled = !busy;
            _chkHard.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void FireAndTrack(Task task) => task.ContinueWith(t =>
        {
            if (t.Exception != null) SetStatus("操作出错：" + t.Exception.InnerException?.Message, true);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        #endregion

        #region 窗口行为

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyRoundedRegion();
            LayoutChrome();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated)
            {
                ApplyRoundedRegion();
                LayoutChrome();
            }
        }

        private void ApplyRoundedRegion()
        {
            using var path = IconFactory.RoundedRect(new Rectangle(0, 0, Width, Height), 12);
            Region = new Region(path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(C_Border, 1);
            using var path = IconFactory.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 不让下拉框初始获得焦点（避免出现整条蓝色选中高亮）
            ActiveControl = _lblTitle;
            FireAndTrack(RestoreLastExit());
        }

        private async Task RestoreLastExit()
        {
            if (string.IsNullOrEmpty(_cfg.LastExit) || !_cfg.RestoreOnStartup) return;

            await Task.Delay(1500);
            RefreshAdapters(true);

            var last = _adapters.FirstOrDefault(a => a.Name == _cfg.LastExit);
            if (last == null || !last.CanRoute) return;

            if (_currentExit != null && _currentExit.IfIndex == last.IfIndex)
            {
                SetStatus("上次选择的出口「" + last.Name + "」仍在使用中。");
                return;
            }

            bool toWired = !last.IsWireless;
            if (SelectedName(toWired ? _cardWired.Combo : _cardWifi.Combo) != last.Name) return;

            await SwitchTo(toWired, true);
            SetStatus("已自动恢复上次的上网出口：" + last.Name);
        }

        private void HideToTray()
        {
            Hide();
            if (!_hideTipShown)
            {
                _hideTipShown = true;
                _tray.BalloonTipTitle = "已最小化到系统托盘";
                _tray.BalloonTipText = "双击图标重新打开，右键图标可以直接一键切换。";
                _tray.ShowBalloonTip(3000);
            }
        }

        private void ShowWindow()
        {
            if (!Visible) Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _timer?.Stop();
            _tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Stop();
                _timer?.Dispose();
                _tray?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void SetStatus(string text, bool isError = false)
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = text;
            _statusLabel.ForeColor = isError ? C_Bad : C_Muted;
        }

        private void BeginDragMove(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            var msg = Message.Create(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
            WndProc(ref msg);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        #endregion

        private static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            try
            {
                return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font(SystemFonts.DefaultFont.FontFamily, size, style, GraphicsUnit.Point);
            }
        }

        private sealed class AdapterCard
        {
            public Panel Root;
            public Label HeadTitle;
            public ComboBox Combo;
            public Label Status;
            public Label Ip;
            public Label Gateway;
            public Label Extra;
            public Label Rate;
            public Label Metric;
            public Label Badge;
            public Button Button;
        }
    }
}
