using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
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

        // 布局使用 96 DPI 下的逻辑尺寸，并按窗口当前所在显示器的 DPI 动态换算。
        // 保持 AutoScaleMode.None，避免框架自动缩放与手工布局重复生效。
        private float _dpiScale = InitScale();

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

        private int S(int v) => Math.Max(1, (int)Math.Round(v * _dpiScale));

        private readonly AppConfig _cfg;
        private List<AdapterInfo> _adapters = new List<AdapterInfo>();

        // 界面元素
        private Panel _header, _summary, _cardsHost, _options, _statusBar;
        private Panel _cardLeft, _cardRight;
        private Label _lblTitle, _lblVer, _sumTip, _sumTitle, _sumSub, _statusLabel;
        private Button _btnClose, _btnMin, _btnRefresh, _btnDetails, _btnDiagnostics, _btnRestoreDefaults;
        private CheckBox _chkHard, _chkRestore;

        private readonly AdapterCard _cardWired = new AdapterCard();
        private readonly AdapterCard _cardWifi = new AdapterCard();

        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _networkDebounceTimer;
        private ToolStripLabel _trayStatus;
        private ToolStripMenuItem _trayWired, _trayWifi;
        private bool _loading = true;
        private bool _syncing;
        private bool _busy;
        private bool _refreshing;
        private bool _metricsStale;
        private bool _hideTipShown;

        private readonly Dictionary<int, Tuple<long, long>> _lastBytes = new Dictionary<int, Tuple<long, long>>();
        private DateTime _lastSample = DateTime.MinValue;
        private AdapterInfo _currentExit;
        private SwitchResult _lastOperation;

        public MainForm()
        {
            _cfg = AppConfig.Load();
            BuildUi();
            InitTray();

            _adapters = NetworkService.Enumerate();
            _metricsStale = true;
            PopulateCombos();
            UpdateUi();

            _loading = false;

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += OnTick;
            _timer.Start();

            _networkDebounceTimer = new Timer { Interval = 600 };
            _networkDebounceTimer.Tick += (s, e) =>
            {
                _networkDebounceTimer.Stop();
                if (!_busy) FireAndTrack(RefreshAdaptersAsync(true));
            };
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        #region 界面搭建

        private void BuildUi()
        {
            Text = "网络一键切换器";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(S(760), S(560));
            MinimumSize = new Size(S(640), S(480));
            DoubleBuffered = true;
            ResizeRedraw = true;
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

            _sumTip = new Label
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

            panel.Controls.Add(_sumTip);
            panel.Controls.Add(_sumTitle);
            panel.Controls.Add(_sumSub);
            return panel;
        }

        private Panel BuildOptionsArea()
        {
            var panel = new Panel { BackColor = C_Bg };

            _chkHard = CheckRow("切换时禁用另一张网卡（彻底隔离，切换时另一张会断开）", C_Text);
            _chkHard.Location = new Point(S(20), S(8));
            _chkHard.CheckedChanged += OnHardIsolateChanged;

            _chkRestore = CheckRow("启动时自动恢复上次选择的出口", C_Muted);
            _chkRestore.Location = new Point(S(20), S(38));
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
            _btnDetails = MakeFooterButton("操作详情");
            _btnDetails.Enabled = false;
            _btnDetails.Click += (s, e) => ShowOperationDetails();

            _btnDiagnostics = MakeFooterButton("复制诊断");
            _btnDiagnostics.Click += (s, e) => CopyDiagnostics();

            _btnRestoreDefaults = MakeFooterButton("恢复系统默认");
            _btnRestoreDefaults.Click += (s, e) => FireAndTrack(RestoreWindowsDefaults());

            panel.Controls.Add(_statusLabel);
            panel.Controls.Add(_btnDetails);
            panel.Controls.Add(_btnDiagnostics);
            panel.Controls.Add(_btnRestoreDefaults);
            return panel;
        }

        private Button MakeFooterButton(string text)
        {
            var button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                ForeColor = C_Muted,
                BackColor = C_Header,
                Font = F(8f),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#2A3242");
            return button;
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

            int headerH = S(50);
            int summaryH = S(86);
            int statusH = S(32);
            int optionsH = S(68);

            _header.Bounds = new Rectangle(0, 0, w, headerH);
            _summary.Bounds = new Rectangle(0, headerH, w, summaryH);

            int hostTop = headerH + summaryH;
            _options.Bounds = new Rectangle(0, h - statusH - optionsH, w, optionsH);
            _statusBar.Bounds = new Rectangle(0, h - statusH, w, statusH);

            _cardsHost.Bounds = new Rectangle(
                S(16),
                hostTop + S(4),
                Math.Max(S(200), w - 2 * S(16)),
                Math.Max(S(220), h - hostTop - S(4) - optionsH - statusH - S(6)));

            int gap = S(12);
            int cw = (_cardsHost.Width - gap) / 2;
            _cardLeft.Bounds = new Rectangle(0, 0, cw, _cardsHost.Height);
            _cardRight.Bounds = new Rectangle(cw + gap, 0, _cardsHost.Width - cw - gap, _cardsHost.Height);

            // 标题栏内部
            _lblTitle.Location = new Point(S(20), S(13));
            _lblTitle.MaximumSize = new Size(Math.Max(S(120), w - S(190)), headerH);
            _lblVer.Location = new Point(_lblTitle.Right + S(8), S(19));
            _btnClose.Location = new Point(w - S(48), S(10));
            _btnMin.Location = new Point(w - S(82), S(10));
            _btnRefresh.Location = new Point(w - S(116), S(10));

            _sumTip.Location = new Point(S(20), S(6));
            _sumTitle.Location = new Point(S(20), S(24));
            _sumTitle.MaximumSize = new Size(Math.Max(S(120), w - S(40)), S(30));
            _sumSub.Location = new Point(S(20), S(57));
            _sumSub.MaximumSize = new Size(Math.Max(S(120), w - S(40)), S(24));

            _chkHard.Location = new Point(S(20), S(7));
            _chkRestore.Location = new Point(S(20), S(36));

            _btnRestoreDefaults.Bounds = new Rectangle(w - S(112), 0, S(104), statusH);
            _btnDiagnostics.Bounds = new Rectangle(_btnRestoreDefaults.Left - S(78), 0, S(76), statusH);
            _btnDetails.Bounds = new Rectangle(_btnDiagnostics.Left - S(76), 0, S(74), statusH);
            _statusLabel.Bounds = new Rectangle(S(20), 0, Math.Max(S(100), _btnDetails.Left - S(28)), statusH);

            LayoutCard(_cardLeft, _cardWired);
            LayoutCard(_cardRight, _cardWifi);
        }

        private void LayoutCard(Panel root, AdapterCard card)
        {
            int w = root.Width;
            int h = root.Height;
            int pad = S(16);
            int inner = w - 2 * pad;
            bool compact = h < S(270);

            if (compact)
            {
                card.HeadTitle.Location = new Point(pad, S(7));
                card.Combo.Bounds = new Rectangle(pad, S(30), inner, S(26));
                card.Status.Bounds = new Rectangle(pad, S(59), inner, S(21));
                card.Ip.Bounds = new Rectangle(pad, S(82), inner, S(18));
                card.Gateway.Bounds = new Rectangle(pad, S(102), inner, S(18));
                card.Extra.Visible = false;
                card.Rate.Bounds = new Rectangle(pad, S(125), inner, S(21));
                card.Metric.Bounds = new Rectangle(pad, S(148), inner, S(18));
                card.Button.Bounds = new Rectangle(pad, h - S(10) - S(38), inner, S(38));
            }
            else
            {
                card.HeadTitle.Location = new Point(pad, S(10));
                card.Combo.Bounds = new Rectangle(pad, S(36), inner, S(28));
                card.Status.Bounds = new Rectangle(pad, S(69), inner, S(23));
                card.Ip.Bounds = new Rectangle(pad, S(94), inner, S(20));
                card.Gateway.Bounds = new Rectangle(pad, S(116), inner, S(20));
                card.Extra.Visible = true;
                card.Extra.Bounds = new Rectangle(pad, S(138), inner, S(20));
                card.Rate.Bounds = new Rectangle(pad, S(162), inner, S(23));
                card.Metric.Bounds = new Rectangle(pad, S(187), inner, S(20));
                card.Button.Bounds = new Rectangle(pad, h - S(12) - S(40), inner, S(40));
            }

            card.Badge.Location = new Point(w - pad - card.Badge.Width, S(13));
        }

        private void InitTray()
        {
            _tray = new NotifyIcon
            {
                Icon = IconFactory.Create(IconFactory.WiredColor),
                Text = "网络一键切换器",
                Visible = true
            };

            _trayMenu = new ContextMenuStrip
            {
                BackColor = ColorTranslator.FromHtml("#202838"),
                ForeColor = C_Text,
                Font = F(10f),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                DropShadowEnabled = true,
                MinimumSize = new Size(S(330), 0),
                Padding = new Padding(S(5)),
                Renderer = new TrayMenuRenderer()
            };

            var title = MakeTrayLabel("网络一键切换器", true, C_Text);
            _trayStatus = MakeTrayLabel("正在读取网络状态…", false, C_Warn);
            var switchGroup = MakeTrayLabel("快速切换", false, ColorTranslator.FromHtml("#AEB8CA"));
            var toolsGroup = MakeTrayLabel("工具与设置", false, ColorTranslator.FromHtml("#AEB8CA"));

            _trayWired = new ToolStripMenuItem("切换到有线以太网", null,
                (s, e) => { ShowWindow(false); FireAndTrack(SwitchTo(true, false)); });
            _trayWifi = new ToolStripMenuItem("切换到 Wi-Fi", null,
                (s, e) => { ShowWindow(false); FireAndTrack(SwitchTo(false, false)); });
            StyleTrayAction(_trayWired);
            StyleTrayAction(_trayWifi);

            _trayMenu.Items.Add(title);
            _trayMenu.Items.Add(_trayStatus);
            _trayMenu.Items.Add(MakeTraySeparator());
            _trayMenu.Items.Add(switchGroup);
            _trayMenu.Items.Add(_trayWired);
            _trayMenu.Items.Add(_trayWifi);
            _trayMenu.Items.Add(MakeTrayAction("恢复 Windows 自动跃点数", (s, e) =>
            {
                ShowWindow(false);
                FireAndTrack(RestoreWindowsDefaults());
            }));
            _trayMenu.Items.Add(MakeTraySeparator());
            _trayMenu.Items.Add(toolsGroup);
            _trayMenu.Items.Add(MakeTrayAction("查看最近操作详情", (s, e) => ShowOperationDetails()));
            _trayMenu.Items.Add(MakeTrayAction("复制诊断信息", (s, e) => CopyDiagnostics()));
            _trayMenu.Items.Add(MakeTrayAction("打开日志所在文件夹", (s, e) => OpenLogFolder()));
            _trayMenu.Items.Add(MakeTrayAction("清空日志", (s, e) => ClearLogs()));
            _trayMenu.Items.Add(MakeTraySeparator());
            _trayMenu.Items.Add(MakeTrayAction("打开主界面", (s, e) => ShowWindow()));
            _trayMenu.Items.Add(MakeTrayAction("退出程序", (s, e) =>
            {
                _tray.Visible = false;
                Application.Exit();
            }));

            _tray.ContextMenuStrip = _trayMenu;
            _tray.DoubleClick += (s, e) => ShowWindow();
            _tray.BalloonTipClicked += (s, e) => ShowWindow();
        }

        private ToolStripLabel MakeTrayLabel(string text, bool title, Color color)
        {
            return new ToolStripLabel(text)
            {
                ForeColor = color,
                Font = F(title ? 10.5f : 8.5f, title ? FontStyle.Bold : FontStyle.Regular),
                Padding = title
                    ? new Padding(S(11), S(7), S(11), S(2))
                    : new Padding(S(11), S(2), S(11), S(5)),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private ToolStripMenuItem MakeTrayAction(string text, EventHandler click)
        {
            var item = new ToolStripMenuItem(text, null, click);
            StyleTrayAction(item);
            return item;
        }

        private void StyleTrayAction(ToolStripMenuItem item)
        {
            item.ForeColor = C_Text;
            item.Padding = new Padding(S(10), S(6), S(12), S(6));
            item.Margin = new Padding(S(2), S(1), S(2), S(1));
        }

        private ToolStripSeparator MakeTraySeparator()
        {
            return new ToolStripSeparator
            {
                Margin = new Padding(S(10), S(5), S(10), S(5))
            };
        }

        #endregion

        #region 数据刷新

        private void PopulateCombos()
        {
            SyncCombo(_cardWired.Combo,
                NetworkService.BuildCandidateList(_adapters, false, _cfg.EthernetAdapterId, _cfg.EthernetAdapter),
                _cfg.EthernetAdapterId, _cfg.EthernetAdapter);
            SyncCombo(_cardWifi.Combo,
                NetworkService.BuildCandidateList(_adapters, true, _cfg.WifiAdapterId, _cfg.WifiAdapter),
                _cfg.WifiAdapterId, _cfg.WifiAdapter);
            _chkHard.Checked = _cfg.HardIsolate;
            _chkRestore.Checked = _cfg.RestoreOnStartup;
            SaveSelection();
        }

        private void SyncCombo(ComboBox combo, List<AdapterInfo> items, string rememberedId, string rememberedName)
        {
            var current = combo.SelectedItem as AdapterInfo;
            var wantId = current?.Id ?? rememberedId;
            var wantName = current?.Name ?? rememberedName;

            var newKey = string.Join("|", items.Select(i => i.Id + ":" + i.Name));
            var oldKey = string.Join("|", combo.Items.OfType<AdapterInfo>().Select(i => i.Id + ":" + i.Name));

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
                var pick = NetworkService.FindByIdentity(items, wantId, wantName) ?? items[0];
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
            if (_cardWired.Combo?.SelectedItem is AdapterInfo a1)
            {
                _cfg.EthernetAdapter = a1.Name;
                _cfg.EthernetAdapterId = a1.Id;
            }
            if (_cardWifi.Combo?.SelectedItem is AdapterInfo a2)
            {
                _cfg.WifiAdapter = a2.Name;
                _cfg.WifiAdapterId = a2.Id;
            }
            _cfg.Save();
        }

        private static string SelectedName(ComboBox combo) =>
            combo.SelectedItem is AdapterInfo a ? a.Name : null;

        private AdapterInfo SelectedAdapter(ComboBox combo)
        {
            var selected = combo.SelectedItem as AdapterInfo;
            return selected == null ? null : NetworkService.FindByIdentity(_adapters, selected.Id, selected.Name);
        }

        private async Task<bool> RefreshAdaptersAsync(bool withMetrics)
        {
            if (_refreshing) return false;

            _refreshing = true;
            try
            {
                var previous = _adapters;
                var readMetrics = withMetrics || _metricsStale;
                var snapshot = await Task.Run(() =>
                {
                    var current = NetworkService.Enumerate();
                    NetworkService.CarryMetrics(previous, current);
                    var metricsLoaded = !readMetrics || NetworkService.RefreshMetrics(current);
                    return Tuple.Create(current, metricsLoaded);
                });

                if (IsDisposed) return false;

                _adapters = snapshot.Item1;
                if (readMetrics) _metricsStale = !snapshot.Item2;
                SyncCombo(_cardWired.Combo,
                    NetworkService.BuildCandidateList(_adapters, false, _cfg.EthernetAdapterId, _cfg.EthernetAdapter),
                    (_cardWired.Combo.SelectedItem as AdapterInfo)?.Id ?? _cfg.EthernetAdapterId,
                    SelectedName(_cardWired.Combo) ?? _cfg.EthernetAdapter);
                SyncCombo(_cardWifi.Combo,
                    NetworkService.BuildCandidateList(_adapters, true, _cfg.WifiAdapterId, _cfg.WifiAdapter),
                    (_cardWifi.Combo.SelectedItem as AdapterInfo)?.Id ?? _cfg.WifiAdapterId,
                    SelectedName(_cardWifi.Combo) ?? _cfg.WifiAdapter);
                UpdateUi();
                return snapshot.Item2;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async Task RefreshSlow()
        {
            await RefreshAdaptersAsync(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!Visible || _busy || _refreshing) return;
            FireAndTrack(RefreshAdaptersAsync(false));
        }

        private void UpdateUi()
        {
            var now = DateTime.Now;
            var dt = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).TotalSeconds;

            _currentExit = _adapters
                .Where(a => a.CanRoute && a.Metric >= 0)
                .OrderBy(a => a.EffectiveMetric)
                .FirstOrDefault();

            UpdateCard(_cardWired, SelectedAdapter(_cardWired.Combo), dt, C_Wired);
            UpdateCard(_cardWifi, SelectedAdapter(_cardWifi.Combo), dt, C_Wifi);
            UpdateSummary();
            UpdateTrayMenu();

            _lastBytes.Clear();
            foreach (var a in _adapters)
            {
                if (a.IfIndex >= 0) _lastBytes[a.IfIndex] = Tuple.Create(a.BytesSent, a.BytesReceived);
            }
            _lastSample = now;
        }

        private void UpdateCard(AdapterCard card, AdapterInfo a, double dt, Color accent)
        {
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
                card.Badge.Location = new Point(card.Root.Width - S(16) - card.Badge.Width, S(13));
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

        private void UpdateTrayMenu()
        {
            if (_trayStatus == null) return;
            var currentText = _currentExit == null ? "当前出口：未识别" : "当前出口：" + _currentExit.Name;
            _trayStatus.Text = currentText;
            _trayStatus.ForeColor = _currentExit == null ? C_Warn : C_Ok;
            _trayWired.Checked = _currentExit != null && _currentExit.IsEthernet;
            _trayWifi.Checked = _currentExit != null && _currentExit.IsWireless;

            var wired = SelectedAdapter(_cardWired.Combo);
            var wifi = SelectedAdapter(_cardWifi.Combo);
            _trayWired.Text = "切换到有线" + (wired == null ? "（未检测到）" : " · " + wired.Name);
            _trayWifi.Text = "切换到 Wi-Fi" + (wifi == null ? "（未检测到）" : " · " + wifi.Name);
            _trayWired.Enabled = wired != null && !_busy;
            _trayWifi.Enabled = wifi != null && !_busy;
            _tray.Text = currentText.Length <= 63 ? currentText : currentText.Substring(0, 63);
        }

        #endregion

        #region 切换

        private async Task<bool> SwitchTo(bool toWired, bool silent)
        {
            if (_busy || _refreshing) return false;

            var target = SelectedAdapter(toWired ? _cardWired.Combo : _cardWifi.Combo);
            var other = SelectedAdapter(toWired ? _cardWifi.Combo : _cardWired.Combo);

            if (target == null)
            {
                SetStatus("请先在下拉框里选择要使用的那张网卡。", true);
                return false;
            }

            if (other != null && string.Equals(other.Id, target.Id, StringComparison.OrdinalIgnoreCase)) other = null;

            if ((!target.IsUp || !target.HasIp) && !silent)
            {
                var reason = !target.IsUp ? "网卡未启用或被禁用" : "没有拿到 IP 地址";
                var dr = MessageBox.Show(this,
                    "网卡「" + target.Name + "」现在还不能上网（" + reason + "）。\n继续把它设为出口的话，可能会导致断网。\n\n仍要继续吗？",
                    "目标网卡未连接",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return false;
            }

            _busy = true;
            SetBusyUi(true);
            SetStatus("正在切换并验证「" + target.Name + "」…");

            bool hard = _chkHard.Checked;
            try
            {
                try
                {
                    _lastOperation = await NetworkService.SwitchExitWithRollbackAsync(target, other, hard);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("切换网络出口时发生异常。", ex);
                    _lastOperation = new SwitchResult
                    {
                        Message = "切换失败：" + ex.Message,
                        Success = false
                    };
                }

                _btnDetails.Enabled = true;
                if (_lastOperation.Success)
                {
                    _cfg.LastExit = target.Name;
                    _cfg.LastExitId = target.Id;
                    _cfg.Save();
                }

                _metricsStale = true;
                await RefreshAdaptersAsync(true);

                SetStatus(_lastOperation.Message, !_lastOperation.Success);
                if (!silent)
                {
                    _tray.BalloonTipTitle = _lastOperation.Success ? "切换完成" : "已执行安全保护";
                    _tray.BalloonTipText = _lastOperation.Message;
                    _tray.ShowBalloonTip(3000);
                    if (!_lastOperation.Success) ShowOperationDetails();
                }
                return _lastOperation.Success;
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
            }
        }

        private async void OnRefreshClick(object s, EventArgs e)
        {
            if (_busy || _refreshing) return;

            _busy = true;
            SetBusyUi(true);
            SetStatus("正在刷新网卡信息…");
            try
            {
                var ok = await RefreshAdaptersAsync(true);
                SetStatus(ok ? "已刷新网卡信息" : "网卡已刷新，但无法读取跃点数。", !ok);
            }
            catch (Exception ex)
            {
                AppLogger.Error("刷新网卡信息时发生异常。", ex);
                SetStatus("刷新失败：" + ex.Message, true);
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
            }
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
            var adapters = new[] { SelectedAdapter(_cardWired.Combo), SelectedAdapter(_cardWifi.Combo) }
                .Where(a => a != null)
                .ToArray();

            _busy = true;
            SetBusyUi(true);
            try
            {
                var failures = await Task.Run(() =>
                {
                    var errors = new List<string>();
                    foreach (var adapter in adapters)
                    {
                        if (!NetworkService.SetAdapterEnabled(adapter, true, out var error)) errors.Add(error);
                    }
                    return errors;
                });

                _metricsStale = true;
                await RefreshAdaptersAsync(true);
                SetStatus(failures.Count == 0
                    ? "已恢复两张网卡同时连接的状态。"
                    : string.Join("；", failures), failures.Count > 0);
                if (failures.Count > 0)
                    AppLogger.Warning("恢复网卡启用状态未完全成功：" + string.Join("；", failures));
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
            }
        }

        private async Task RestoreWindowsDefaults()
        {
            if (_busy || _refreshing) return;
            var answer = MessageBox.Show(this,
                "这会启用当前选择的有线和 Wi-Fi 网卡，并把 IPv4/IPv6 跃点数恢复为 Windows 自动管理。\n\n继续吗？",
                "恢复系统默认网络设置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            var selected = new[] { SelectedAdapter(_cardWired.Combo), SelectedAdapter(_cardWifi.Combo) }
                .Where(a => a != null)
                .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            _busy = true;
            SetBusyUi(true);
            SetStatus("正在恢复 Windows 自动网络设置…");
            try
            {
                var operation = await Task.Run(() =>
                    NetworkService.RestoreAutomaticMetrics(selected, out var message)
                        ? (success: true, message)
                        : (success: false, message));

                _lastOperation = new SwitchResult
                {
                    Success = operation.success,
                    Message = operation.message
                };
                _lastOperation.Steps.Add(new OperationStep
                {
                    Name = "恢复 Windows 自动跃点数",
                    Success = operation.success,
                    Detail = operation.message
                });
                _btnDetails.Enabled = true;

                if (operation.success)
                {
                    _loading = true;
                    _chkHard.Checked = false;
                    _loading = false;
                    _cfg.HardIsolate = false;
                    _cfg.LastExit = "";
                    _cfg.LastExitId = "";
                    _cfg.Save();
                }

                _metricsStale = true;
                await RefreshAdaptersAsync(true);
                SetStatus(operation.message, !operation.success);
                if (!operation.success) ShowOperationDetails();
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
            }
        }

        private void ShowOperationDetails()
        {
            if (_lastOperation == null)
            {
                MessageBox.Show(this, "当前还没有可显示的操作记录。", "操作详情",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show(this, _lastOperation.ToDetailText(), "操作详情",
                MessageBoxButtons.OK,
                _lastOperation.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void CopyDiagnostics()
        {
            try
            {
                Clipboard.SetText(DiagnosticsService.BuildReport(_adapters, _currentExit, _lastOperation));
                SetStatus("诊断信息已复制到剪贴板。包含 IP 和网卡标识，分享前请确认接收方可信。");
            }
            catch (Exception ex)
            {
                AppLogger.Error("复制诊断信息失败。", ex);
                SetStatus("复制诊断信息失败：" + ex.Message, true);
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                var argument = File.Exists(AppLogger.LogPath)
                    ? "/select,\"" + AppLogger.LogPath + "\""
                    : "\"" + AppContext.BaseDirectory + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Error("打开日志目录失败。", ex);
                SetStatus("打开日志目录失败：" + ex.Message, true);
            }
        }

        private void ClearLogs()
        {
            if (MessageBox.Show(this, "确定清空当前及历史日志吗？", "清空日志",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            if (AppLogger.Clear(out var error))
            {
                SetStatus("日志已清空。");
                AppLogger.Info("用户清空了日志。");
            }
            else
            {
                SetStatus("清空日志失败：" + error, true);
            }
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e) => QueueNetworkRefresh();

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e) => QueueNetworkRefresh();

        private void QueueNetworkRefresh()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    _networkDebounceTimer.Stop();
                    _networkDebounceTimer.Start();
                }));
            }
            catch (InvalidOperationException)
            {
                // 窗口销毁过程中忽略网络事件。
            }
        }

        private void SetBusyUi(bool busy)
        {
            _cardWired.Button.Enabled = !busy && SelectedAdapter(_cardWired.Combo) != null;
            _cardWifi.Button.Enabled = !busy && SelectedAdapter(_cardWifi.Combo) != null;
            _cardWired.Combo.Enabled = !busy;
            _cardWifi.Combo.Enabled = !busy;
            _chkHard.Enabled = !busy;
            _btnRefresh.Enabled = !busy;
            _btnRestoreDefaults.Enabled = !busy;
            UpdateTrayMenu();
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void FireAndTrack(Task task) => task.ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                var error = t.Exception.GetBaseException();
                AppLogger.Error("后台操作发生异常。", error);
                SetStatus("操作出错：" + error.Message, true);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());

        #endregion

        #region 窗口行为

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var actualScale = Math.Max(1f, DeviceDpi / 96f);
            if (Math.Abs(actualScale - _dpiScale) > 0.01f)
            {
                _dpiScale = actualScale;
                ClientSize = new Size(S(760), S(560));
                MinimumSize = new Size(S(640), S(480));
                RefreshDpiFonts();
            }
            Bounds = FitToWorkingArea(Bounds);
            ApplyRoundedRegion();
            LayoutChrome();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            SuspendLayout();
            try
            {
                _dpiScale = Math.Max(1f, e.DeviceDpiNew / 96f);
                base.OnDpiChanged(e);
                MinimumSize = new Size(S(640), S(480));
                Bounds = FitToWorkingArea(e.SuggestedRectangle);
                RefreshDpiFonts();
                LayoutChrome();
                ApplyRoundedRegion();
                Invalidate(true);
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private Rectangle FitToWorkingArea(Rectangle requested)
        {
            var work = Screen.FromRectangle(requested).WorkingArea;
            var margin = S(12);
            var width = Math.Min(requested.Width, Math.Max(MinimumSize.Width, work.Width - margin * 2));
            var height = Math.Min(requested.Height, Math.Max(MinimumSize.Height, work.Height - margin * 2));
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);

            var x = Math.Max(work.Left, Math.Min(requested.X, work.Right - width));
            var y = Math.Max(work.Top, Math.Min(requested.Y, work.Bottom - height));
            return new Rectangle(x, y, width, height);
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
            if (Width <= S(24) || Height <= S(24)) return;
            using var path = IconFactory.RoundedRect(new Rectangle(0, 0, Width, Height), S(12));
            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= S(2) || Height <= S(2)) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(C_Border, 1);
            using var path = IconFactory.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(12));
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 不让下拉框初始获得焦点（避免出现整条蓝色选中高亮）
            ActiveControl = _lblTitle;
            FireAndTrack(InitializeAsync());
        }

        private async Task InitializeAsync()
        {
            await RefreshAdaptersAsync(true);
            await RestoreLastExit();
        }

        private async Task RestoreLastExit()
        {
            if ((string.IsNullOrEmpty(_cfg.LastExitId) && string.IsNullOrEmpty(_cfg.LastExit)) ||
                !_cfg.RestoreOnStartup) return;

            await Task.Delay(1500);
            await RefreshAdaptersAsync(true);

            var last = NetworkService.FindByIdentity(_adapters, _cfg.LastExitId, _cfg.LastExit);
            if (last == null || !last.CanRoute) return;

            if (_currentExit != null && _currentExit.IfIndex == last.IfIndex)
            {
                SetStatus("上次选择的出口「" + last.Name + "」仍在使用中。");
                return;
            }

            bool toWired = !last.IsWireless;
            var selected = SelectedAdapter(toWired ? _cardWired.Combo : _cardWifi.Combo);
            if (selected == null || !string.Equals(selected.Id, last.Id, StringComparison.OrdinalIgnoreCase)) return;

            if (await SwitchTo(toWired, true))
                SetStatus("已自动恢复上次的上网出口：" + last.Name);
        }

        private void HideToTray()
        {
            _timer?.Stop();
            Hide();
            if (!_hideTipShown)
            {
                _hideTipShown = true;
                _tray.BalloonTipTitle = "已最小化到系统托盘";
                _tray.BalloonTipText = "双击图标重新打开，右键图标可以直接一键切换。";
                _tray.ShowBalloonTip(3000);
            }
        }

        private void ShowWindow(bool refreshAdapters = true)
        {
            if (!Visible) Show();
            WindowState = FormWindowState.Normal;
            _timer?.Start();
            if (refreshAdapters && !_busy) FireAndTrack(RefreshAdaptersAsync(true));
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
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                _timer?.Stop();
                _timer?.Dispose();
                _networkDebounceTimer?.Stop();
                _networkDebounceTimer?.Dispose();
                _tray?.Dispose();
                _trayMenu?.Dispose();
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

        private void RefreshDpiFonts()
        {
            ReplaceFont(this, 9f);
            ReplaceFont(_lblTitle, 11.5f, FontStyle.Bold);
            ReplaceFont(_lblVer, 8f);
            ReplaceFont(_btnClose, 10f);
            ReplaceFont(_btnMin, 10f);
            ReplaceFont(_btnRefresh, 10f);
            ReplaceFont(_sumTip, 9f);
            ReplaceFont(_sumTitle, 15f, FontStyle.Bold);
            ReplaceFont(_sumSub, 9f);
            ReplaceFont(_chkHard, 9f);
            ReplaceFont(_chkRestore, 9f);
            ReplaceFont(_statusLabel, 9f);
            ReplaceFont(_btnDetails, 8f);
            ReplaceFont(_btnDiagnostics, 8f);
            ReplaceFont(_btnRestoreDefaults, 8f);
            RefreshCardFonts(_cardWired);
            RefreshCardFonts(_cardWifi);
        }

        private static void RefreshCardFonts(AdapterCard card)
        {
            ReplaceFont(card.HeadTitle, 10.5f, FontStyle.Bold);
            ReplaceFont(card.Combo, 9.5f);
            ReplaceFont(card.Status, 10f, FontStyle.Bold);
            ReplaceFont(card.Ip, 9f);
            ReplaceFont(card.Gateway, 9f);
            ReplaceFont(card.Extra, 8.5f);
            ReplaceFont(card.Rate, 9.5f, FontStyle.Bold);
            ReplaceFont(card.Metric, 9f);
            ReplaceFont(card.Badge, 8.5f, FontStyle.Bold);
            ReplaceFont(card.Button, 10f, FontStyle.Bold);
        }

        private static void ReplaceFont(Control control, float size, FontStyle style = FontStyle.Regular)
        {
            if (control == null) return;
            var previous = control.Font;
            control.Font = F(size, style);
            previous?.Dispose();
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

        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Color NormalText = ColorTranslator.FromHtml("#F3F6FA");
            private static readonly Color DisabledText = ColorTranslator.FromHtml("#8490A5");

            public TrayMenuRenderer() : base(new TrayMenuColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item is ToolStripLabel)
                    e.TextColor = e.Item.ForeColor;
                else
                    e.TextColor = e.Item.Enabled ? NormalText : DisabledText;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? NormalText : DisabledText;
                base.OnRenderArrow(e);
            }
        }

        private sealed class TrayMenuColorTable : ProfessionalColorTable
        {
            private static readonly Color Background = ColorTranslator.FromHtml("#202838");
            private static readonly Color Selection = ColorTranslator.FromHtml("#304765");
            private static readonly Color Accent = ColorTranslator.FromHtml("#5DAEFF");
            private static readonly Color Border = ColorTranslator.FromHtml("#46546A");
            private static readonly Color Separator = ColorTranslator.FromHtml("#354154");

            public TrayMenuColorTable()
            {
                UseSystemColors = false;
            }

            public override Color ToolStripDropDownBackground => Background;
            public override Color ImageMarginGradientBegin => Background;
            public override Color ImageMarginGradientMiddle => Background;
            public override Color ImageMarginGradientEnd => Background;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Accent;
            public override Color MenuItemSelected => Selection;
            public override Color MenuItemSelectedGradientBegin => Selection;
            public override Color MenuItemSelectedGradientEnd => Selection;
            public override Color MenuItemPressedGradientBegin => Selection;
            public override Color MenuItemPressedGradientMiddle => Selection;
            public override Color MenuItemPressedGradientEnd => Selection;
            public override Color CheckBackground => Selection;
            public override Color CheckSelectedBackground => Selection;
            public override Color CheckPressedBackground => Accent;
            public override Color SeparatorDark => Separator;
            public override Color SeparatorLight => Separator;
        }
    }
}
