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
        private Panel _header, _tabBar, _exitPage, _summary, _cardsHost, _options, _statusBar;
        private LoadingOverlay _loadingOverlay;
        private IpProfilesView _profilesView;
        private Panel _cardLeft, _cardRight;
        private Label _lblTitle, _lblVer, _sumTip, _sumTitle, _sumSub, _statusLabel;
        private Button _btnClose, _btnMin, _btnRefresh, _btnExitTab, _btnProfilesTab;
        private Button _btnUpdate, _btnDetails, _btnDiagnostics, _btnRestoreDefaults;
        private CheckBox _chkHard, _chkRestore;

        private readonly AdapterCard _cardWired = new AdapterCard();
        private readonly AdapterCard _cardWifi = new AdapterCard();

        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private Icon _applicationIcon;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _networkDebounceTimer;
        private ToolStripLabel _trayStatus;
        private ToolStripMenuItem _trayWired, _trayWifi, _trayUpdate, _trayManageProfiles;
        private ToolStripLabel _trayProfilesGroup;
        private ToolStripSeparator _trayProfilesSeparator;
        private readonly List<ToolStripMenuItem> _trayProfileItems = new List<ToolStripMenuItem>();
        private bool _loading = true;
        private bool _syncing;
        private bool _busy;
        private bool _refreshing;
        private bool _checkingForUpdate;
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
            ClientSize = new Size(S(860), S(640));
            MinimumSize = new Size(S(720), S(540));
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = C_Bg;
            ForeColor = C_Text;
            Font = F(9f);
            _applicationIcon = IconFactory.LoadApplicationIcon();
            Icon = _applicationIcon;
            MaximizeBox = false;

            _header = BuildHeader();
            _tabBar = BuildTabBar();
            _exitPage = new Panel { BackColor = C_Bg };
            _summary = BuildSummaryArea();
            _cardsHost = new Panel { BackColor = C_Bg };
            _cardLeft = BuildCard("有线以太网", _cardWired, C_Wired, "切换到有线");
            _cardRight = BuildCard("无线 Wi-Fi", _cardWifi, C_Wifi, "切换到 Wi-Fi");
            _cardsHost.Controls.Add(_cardLeft);
            _cardsHost.Controls.Add(_cardRight);
            _options = BuildOptionsArea();
            _statusBar = BuildStatusArea();
            _profilesView = new IpProfilesView(_cfg, () => _adapters);
            _profilesView.ApplyRequested += (s, e) => FireAndTrack(ApplyIpProfile(e.Profile));
            _profilesView.RestoreDhcpRequested += (s, e) => FireAndTrack(RestoreProfileAdapterDhcp(e.Profile));
            _profilesView.ProfilesChanged += (s, e) =>
            {
                RebuildTrayProfiles();
                UpdateTrayMenu();
            };
            _loadingOverlay = new LoadingOverlay
            {
                Visible = false,
                Font = F(10f, FontStyle.Bold)
            };

            _exitPage.Controls.Add(_cardsHost);
            _exitPage.Controls.Add(_options);
            _exitPage.Controls.Add(_summary);
            Controls.Add(_exitPage);
            Controls.Add(_profilesView);
            Controls.Add(_statusBar);
            Controls.Add(_tabBar);
            Controls.Add(_header);
            Controls.Add(_loadingOverlay);

            _cardWired.Button.Click += (s, e) => FireAndTrack(SwitchTo(true, false));
            _cardWifi.Button.Click += (s, e) => FireAndTrack(SwitchTo(false, false));
            _cardWired.Combo.SelectedIndexChanged += (s, e) => OnComboChanged();
            _cardWifi.Combo.SelectedIndexChanged += (s, e) => OnComboChanged();

            ShowExitPage();
            LayoutChrome();
        }

        private Panel BuildTabBar()
        {
            var panel = new Panel { BackColor = C_Header };
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(C_Border, 1f);
                e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
            };

            _btnExitTab = MakeTabButton("出口切换");
            _btnProfilesTab = MakeTabButton("IP 配置");
            _btnExitTab.Click += (s, e) => ShowExitPage();
            _btnProfilesTab.Click += (s, e) => ShowProfilesPage();
            panel.Controls.Add(_btnExitTab);
            panel.Controls.Add(_btnProfilesTab);
            return panel;
        }

        private Button MakeTabButton(string text)
        {
            var button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = C_Header,
                ForeColor = C_Muted,
                Font = F(9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#252D3B");
            return button;
        }

        private void ShowExitPage()
        {
            if (_exitPage == null) return;
            _exitPage.Visible = true;
            _profilesView.Visible = false;
            StyleActiveTab(_btnExitTab, true);
            StyleActiveTab(_btnProfilesTab, false);
        }

        private void ShowProfilesPage()
        {
            if (_profilesView == null) return;
            _exitPage.Visible = false;
            _profilesView.Visible = true;
            _profilesView.RefreshCurrentState();
            StyleActiveTab(_btnExitTab, false);
            StyleActiveTab(_btnProfilesTab, true);
        }

        private static void StyleActiveTab(Button button, bool active)
        {
            if (button == null) return;
            button.BackColor = active ? C_Bg : C_Header;
            button.ForeColor = active ? C_Wired : C_Muted;
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
                Text = "v" + UpdateService.CurrentVersionText,
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

            _btnUpdate = MakeFooterButton("检查更新");
            _btnUpdate.Click += (s, e) => FireAndTrack(CheckForUpdates());

            _btnDiagnostics = MakeFooterButton("复制诊断");
            _btnDiagnostics.Click += (s, e) => CopyDiagnostics();

            _btnRestoreDefaults = MakeFooterButton("恢复系统默认");
            _btnRestoreDefaults.Click += (s, e) => FireAndTrack(RestoreWindowsDefaults());

            panel.Controls.Add(_statusLabel);
            panel.Controls.Add(_btnUpdate);
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
            int tabsH = S(44);
            int summaryH = S(86);
            int statusH = S(32);
            int optionsH = S(68);

            _header.Bounds = new Rectangle(0, 0, w, headerH);
            _tabBar.Bounds = new Rectangle(0, headerH, w, tabsH);
            _exitPage.Bounds = new Rectangle(0, headerH + tabsH, w, Math.Max(S(200), h - headerH - tabsH - statusH));
            _profilesView.Bounds = _exitPage.Bounds;
            _summary.Bounds = new Rectangle(0, 0, w, summaryH);

            int hostTop = summaryH;
            _options.Bounds = new Rectangle(0, _exitPage.Height - optionsH, w, optionsH);
            _statusBar.Bounds = new Rectangle(0, h - statusH, w, statusH);

            _cardsHost.Bounds = new Rectangle(
                S(16),
                hostTop + S(4),
                Math.Max(S(200), w - 2 * S(16)),
                Math.Max(S(220), _exitPage.Height - hostTop - S(4) - optionsH - S(6)));

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

            _btnExitTab.Bounds = new Rectangle(S(16), S(5), S(112), S(38));
            _btnProfilesTab.Bounds = new Rectangle(S(132), S(5), S(112), S(38));

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
            _btnUpdate.Bounds = new Rectangle(_btnDetails.Left - S(76), 0, S(74), statusH);
            _statusLabel.Bounds = new Rectangle(S(20), 0, Math.Max(S(100), _btnUpdate.Left - S(28)), statusH);
            _loadingOverlay.Bounds = ClientRectangle;
            if (_loadingOverlay.Visible) _loadingOverlay.BringToFront();
            _profilesView.ApplyScale(_dpiScale);

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
                Icon = _applicationIcon,
                Text = "网络一键切换器",
                Visible = true
            };

            _trayMenu = new ContextMenuStrip
            {
                BackColor = ColorTranslator.FromHtml("#202838"),
                ForeColor = C_Text,
                Font = F(10f),
                ShowImageMargin = false,
                // 勾选区由自定义渲染器在行内绘制，避免系统默认边距越出高亮区域。
                ShowCheckMargin = false,
                DropShadowEnabled = true,
                MinimumSize = new Size(S(330), 0),
                Padding = new Padding(S(5)),
                Renderer = new TrayMenuRenderer()
            };

            var title = MakeTrayLabel("网络一键切换器", true, C_Text);
            _trayStatus = MakeTrayLabel("正在读取网络状态…", false, C_Warn);
            var switchGroup = MakeTrayLabel("快速切换", false, ColorTranslator.FromHtml("#AEB8CA"));
            _trayProfilesGroup = MakeTrayLabel("IP 配置", false, ColorTranslator.FromHtml("#AEB8CA"));
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
            _trayMenu.Items.Add(_trayProfilesGroup);
            _trayManageProfiles = MakeTrayAction("管理 IP 配置…", (s, e) =>
            {
                ShowWindow(false);
                ShowProfilesPage();
            });
            _trayMenu.Items.Add(_trayManageProfiles);
            _trayProfilesSeparator = MakeTraySeparator();
            _trayMenu.Items.Add(_trayProfilesSeparator);
            _trayMenu.Items.Add(toolsGroup);
            _trayMenu.Items.Add(MakeTrayAction("查看最近操作详情", (s, e) => ShowOperationDetails()));
            _trayMenu.Items.Add(MakeTrayAction("复制诊断信息", (s, e) => CopyDiagnostics()));
            _trayUpdate = MakeTrayAction("检查软件更新", (s, e) =>
            {
                ShowWindow(false);
                FireAndTrack(CheckForUpdates());
            });
            _trayMenu.Items.Add(_trayUpdate);
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
            RebuildTrayProfiles();
        }

        private void RebuildTrayProfiles()
        {
            if (_trayMenu == null || _trayManageProfiles == null) return;
            foreach (var item in _trayProfileItems)
            {
                _trayMenu.Items.Remove(item);
                item.Dispose();
            }
            _trayProfileItems.Clear();

            var insertAt = _trayMenu.Items.IndexOf(_trayManageProfiles);
            foreach (var profile in (_cfg.IpProfiles ?? new List<IpProfile>())
                         .Where(item => item.ShowInTray)
                         .Take(6))
            {
                var profileId = profile.Id;
                var item = MakeTrayAction(profile.Name, (s, e) =>
                {
                    var current = _cfg.IpProfiles.FirstOrDefault(candidate => candidate.Id == profileId);
                    if (current == null) return;
                    ShowWindow(false);
                    ShowProfilesPage();
                    _profilesView.ReloadProfiles(current.Id);
                    FireAndTrack(ApplyIpProfile(current));
                });
                item.Tag = profile.Id;
                _trayProfileItems.Add(item);
                _trayMenu.Items.Insert(insertAt++, item);
            }

            _trayProfilesGroup.Visible = _trayProfileItems.Count > 0;
            _trayManageProfiles.Text = _trayProfileItems.Count > 0 ? "管理 IP 配置…" : "新增 IP 配置…";
        }

        private ToolStripLabel MakeTrayLabel(string text, bool title, Color color)
        {
            return new ToolStripLabel(text)
            {
                ForeColor = color,
                Font = F(title ? 10.5f : 8.5f, title ? FontStyle.Bold : FontStyle.Regular),
                AutoSize = false,
                Size = new Size(S(310), S(title ? 40 : 30)),
                Padding = new Padding(S(11), 0, S(11), 0),
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
            item.AutoSize = false;
            item.Size = new Size(S(310), S(40));
            item.Padding = new Padding(S(10), 0, S(12), 0);
            item.Margin = new Padding(S(2), S(1), S(2), S(1));
            item.TextAlign = ContentAlignment.MiddleLeft;
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
            _profilesView?.RefreshCurrentState();
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
            foreach (var item in _trayProfileItems)
            {
                var profileId = item.Tag as string;
                var profile = _cfg.IpProfiles.FirstOrDefault(candidate => candidate.Id == profileId);
                var adapter = profile == null
                    ? null
                    : NetworkService.FindByIdentity(_adapters, profile.AdapterId, profile.AdapterName);
                item.Checked = profile != null && _cfg.LastAppliedIpProfileId == profile.Id &&
                               NetworkService.IpProfileMatchesAdapter(profile, adapter);
                item.Enabled = profile != null && adapter != null && !_busy;
            }
            if (_trayManageProfiles != null) _trayManageProfiles.Enabled = !_busy;
            _tray.Text = currentText.Length <= 63 ? currentText : currentText.Substring(0, 63);
        }

        #endregion

        #region 切换

        private async Task ApplyIpProfile(IpProfile profile)
        {
            if (_busy || _refreshing || profile == null) return;
            var target = NetworkService.FindByIdentity(_adapters, profile.AdapterId, profile.AdapterName);
            if (target == null)
            {
                SetStatus("找不到配置绑定的网卡，请编辑配置并重新选择网卡。", true);
                MessageBox.Show(this,
                    "找不到 IP 配置「" + profile.Name + "」绑定的网卡「" + profile.AdapterName + "」。\n\n请编辑配置并重新选择网卡。",
                    "目标网卡不存在", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var previous = LastAppliedProfileFor(target);

            _busy = true;
            SetBusyUi(true);
            var initialMessage = "正在准备应用 IP 配置「" + profile.Name + "」…";
            SetStatus(initialMessage);
            ShowLoading(initialMessage);
            try
            {
                var progress = new Progress<string>(message =>
                {
                    SetStatus(message);
                    ShowLoading(message);
                });
                try
                {
                    _lastOperation = await NetworkService.ApplyIpProfileWithRollbackAsync(
                        profile, target, previous, progress);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("应用固定 IP 配置时发生异常。", ex);
                    _lastOperation = new SwitchResult
                    {
                        Success = false,
                        Message = "应用固定 IP 配置失败：" + ex.Message
                    };
                }

                _btnDetails.Enabled = true;
                if (_lastOperation.Success)
                {
                    _cfg.LastAppliedIpProfileId = profile.Id;
                    _cfg.LastAppliedIpProfile = profile.Clone();
                    _cfg.Save();
                }

                await RefreshAdaptersAsync(false);
                _profilesView.ReloadProfiles(profile.Id);
                SetStatus(_lastOperation.Message, !_lastOperation.Success);
                _tray.BalloonTipTitle = _lastOperation.Success ? "IP 配置已应用" : "IP 配置未完成";
                _tray.BalloonTipText = _lastOperation.Message;
                _tray.ShowBalloonTip(3000);
                if (!_lastOperation.Success) ShowOperationDetails();
            }
            finally
            {
                HideLoading();
                _busy = false;
                SetBusyUi(false);
            }
        }

        private async Task RestoreProfileAdapterDhcp(IpProfile selectedProfile)
        {
            if (_busy || _refreshing || selectedProfile == null) return;
            var target = NetworkService.FindByIdentity(_adapters, selectedProfile.AdapterId, selectedProfile.AdapterName);
            if (target == null)
            {
                SetStatus("找不到要恢复为自动获取的网卡。", true);
                MessageBox.Show(this, "找不到网卡「" + selectedProfile.AdapterName + "」，请刷新后重试。",
                    "目标网卡不存在", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(this,
                    "确定将网卡「" + target.Name + "」恢复为自动获取 IP 和 DNS 吗？\n\n" +
                    "这不会修改该网卡的出口跃点数。",
                    "恢复自动获取", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var previous = LastAppliedProfileFor(target) ?? selectedProfile;
            _busy = true;
            SetBusyUi(true);
            var initialMessage = "正在将「" + target.Name + "」恢复为自动获取…";
            SetStatus(initialMessage);
            ShowLoading(initialMessage);
            try
            {
                var progress = new Progress<string>(message =>
                {
                    SetStatus(message);
                    ShowLoading(message);
                });
                try
                {
                    _lastOperation = await NetworkService.RestoreDhcpWithRollbackAsync(target, previous, progress);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("恢复 DHCP 时发生异常。", ex);
                    _lastOperation = new SwitchResult
                    {
                        Success = false,
                        Message = "恢复自动获取失败：" + ex.Message
                    };
                }

                _btnDetails.Enabled = true;
                if (_lastOperation.Success && previous != null)
                {
                    _cfg.LastAppliedIpProfileId = "";
                    _cfg.LastAppliedIpProfile = null;
                    _cfg.Save();
                }
                await RefreshAdaptersAsync(false);
                _profilesView.ReloadProfiles(selectedProfile.Id);
                SetStatus(_lastOperation.Message, !_lastOperation.Success);
                _tray.BalloonTipTitle = _lastOperation.Success ? "已恢复自动获取" : "恢复自动获取未完成";
                _tray.BalloonTipText = _lastOperation.Message;
                _tray.ShowBalloonTip(3000);
                if (!_lastOperation.Success) ShowOperationDetails();
            }
            finally
            {
                HideLoading();
                _busy = false;
                SetBusyUi(false);
            }
        }

        private IpProfile LastAppliedProfileFor(AdapterInfo adapter)
        {
            if (adapter == null) return null;
            var snapshot = _cfg.LastAppliedIpProfile;
            if (snapshot != null &&
                ((!string.IsNullOrWhiteSpace(snapshot.AdapterId) &&
                  string.Equals(snapshot.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase)) ||
                 (string.IsNullOrWhiteSpace(snapshot.AdapterId) &&
                  string.Equals(snapshot.AdapterName, adapter.Name, StringComparison.OrdinalIgnoreCase))))
                return snapshot;

            return _cfg.IpProfiles.FirstOrDefault(item =>
                item.Id == _cfg.LastAppliedIpProfileId &&
                ((!string.IsNullOrWhiteSpace(item.AdapterId) &&
                  string.Equals(item.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase)) ||
                 string.Equals(item.AdapterName, adapter.Name, StringComparison.OrdinalIgnoreCase)));
        }

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
            var initialMessage = "正在准备切换到「" + target.Name + "」…";
            ShowLoading(initialMessage);
            SetStatus(initialMessage);

            bool hard = _chkHard.Checked;
            try
            {
                try
                {
                    var progress = new Progress<string>(message =>
                    {
                        ShowLoading(message);
                        SetStatus(message);
                    });
                    _lastOperation = await NetworkService.SwitchExitWithRollbackAsync(
                        target, other, hard, progress);
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
                HideLoading();
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
            ShowLoading("正在恢复双网卡连接…");
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
                HideLoading();
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
            ShowLoading("正在恢复 Windows 自动网络设置…");
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
                HideLoading();
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

        private async Task CheckForUpdates()
        {
            if (_checkingForUpdate || _busy || _refreshing) return;
            _checkingForUpdate = true;
            _btnUpdate.Enabled = false;
            if (_trayUpdate != null) _trayUpdate.Enabled = false;
            SetStatus("正在连接 GitHub 检查更新…");

            try
            {
                var update = await UpdateService.CheckForUpdatesAsync(System.Threading.CancellationToken.None);
                if (!update.Success)
                {
                    AppLogger.Warning("检查更新未完成：" + update.ErrorMessage);
                    SetStatus(update.ErrorMessage, true);
                    MessageBox.Show(this, update.ErrorMessage, "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!update.IsUpdateAvailable)
                {
                    var message = "当前版本 v" + UpdateService.CurrentVersionText + " 已是最新版。";
                    SetStatus(message);
                    MessageBox.Show(this, message + "\n\n运行方式：" + update.Installation.DisplayName,
                        "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var latest = UpdateService.FormatVersion(update.LatestVersion);
                var notes = CompactReleaseNotes(update.ReleaseNotes);
                var prompt =
                    "发现新版本 v" + latest + "。\n\n" +
                    "当前版本：v" + UpdateService.CurrentVersionText + "\n" +
                    "运行方式：" + update.Installation.DisplayName + "\n" +
                    (string.IsNullOrWhiteSpace(notes) ? "" : "\n更新说明：\n" + notes + "\n") +
                    "\n是否立即下载并更新？程序会在安装前退出，完成后自动重新启动。";
                if (MessageBox.Show(this, prompt, "发现新版本",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                {
                    SetStatus("发现新版本 v" + latest + "，已暂缓更新。");
                    return;
                }

                SetStatus("正在下载并校验 v" + latest + " 更新包…");
                var download = await UpdateService.DownloadUpdateAsync(
                    update, System.Threading.CancellationToken.None);
                if (!download.Success)
                {
                    SetStatus(download.ErrorMessage, true);
                    MessageBox.Show(this, download.ErrorMessage, "更新失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                SetStatus("更新包已校验，正在启动更新程序…");
                if (!UpdateService.StartUpdateHelper(update.Installation, download.PackagePath, out var error))
                {
                    SetStatus(error, true);
                    MessageBox.Show(this, error, "更新失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _tray.Visible = false;
                Application.Exit();
            }
            finally
            {
                _checkingForUpdate = false;
                if (!IsDisposed)
                {
                    _btnUpdate.Enabled = !_busy;
                    if (_trayUpdate != null) _trayUpdate.Enabled = !_busy;
                }
            }
        }

        private static string CompactReleaseNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes)) return "";
            var normalized = notes.Replace("\r\n", "\n").Trim();
            const int maxLength = 700;
            return normalized.Length <= maxLength
                ? normalized
                : normalized.Substring(0, maxLength).TrimEnd() + "…";
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
            _btnUpdate.Enabled = !busy && !_checkingForUpdate;
            if (_trayUpdate != null) _trayUpdate.Enabled = !busy && !_checkingForUpdate;
            _btnRestoreDefaults.Enabled = !busy;
            _profilesView?.SetBusy(busy);
            UpdateTrayMenu();
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void ShowLoading(string message)
        {
            if (_loadingOverlay == null) return;
            _loadingOverlay.Start(message);
            _loadingOverlay.Bounds = ClientRectangle;
            _loadingOverlay.BringToFront();
        }

        private void HideLoading() => _loadingOverlay?.Stop();

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
                ClientSize = new Size(S(860), S(640));
                MinimumSize = new Size(S(720), S(540));
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
                MinimumSize = new Size(S(720), S(540));
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
                _applicationIcon?.Dispose();
                _applicationIcon = null;
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
            ReplaceFont(_btnExitTab, 9.5f, FontStyle.Bold);
            ReplaceFont(_btnProfilesTab, 9.5f, FontStyle.Bold);
            ReplaceFont(_sumTip, 9f);
            ReplaceFont(_sumTitle, 15f, FontStyle.Bold);
            ReplaceFont(_sumSub, 9f);
            ReplaceFont(_chkHard, 9f);
            ReplaceFont(_chkRestore, 9f);
            ReplaceFont(_statusLabel, 9f);
            ReplaceFont(_btnUpdate, 8f);
            ReplaceFont(_btnDetails, 8f);
            ReplaceFont(_btnDiagnostics, 8f);
            ReplaceFont(_btnRestoreDefaults, 8f);
            ReplaceFont(_loadingOverlay, 10f, FontStyle.Bold);
            _profilesView?.ApplyScale(_dpiScale);
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

        private sealed class LoadingOverlay : Control
        {
            private readonly System.Windows.Forms.Timer _animationTimer;
            private int _frame;

            public LoadingOverlay()
            {
                SetStyle(ControlStyles.UserPaint |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
                BackColor = C_Bg;
                ForeColor = C_Text;
                Cursor = Cursors.WaitCursor;
                TabStop = false;
                _animationTimer = new System.Windows.Forms.Timer { Interval = 70 };
                _animationTimer.Tick += (s, e) =>
                {
                    _frame = (_frame + 1) % 12;
                    Invalidate();
                };
            }

            public void Start(string message)
            {
                Text = string.IsNullOrWhiteSpace(message) ? "正在处理…" : message;
                _frame = 0;
                Visible = true;
                _animationTimer.Start();
                Invalidate();
            }

            public void Stop()
            {
                _animationTimer.Stop();
                Visible = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(C_Bg);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var scale = Math.Max(1f, DeviceDpi / 96f);
                var center = new PointF(Width / 2f, Height / 2f - 22f * scale);
                var innerRadius = 15f * scale;
                var outerRadius = 27f * scale;
                var penWidth = 4.2f * scale;

                for (var i = 0; i < 12; i++)
                {
                    var distance = (i - _frame + 12) % 12;
                    var alpha = Math.Max(38, 245 - distance * 18);
                    var angle = (float)(i * Math.PI * 2 / 12 - Math.PI / 2);
                    var start = new PointF(
                        center.X + (float)Math.Cos(angle) * innerRadius,
                        center.Y + (float)Math.Sin(angle) * innerRadius);
                    var end = new PointF(
                        center.X + (float)Math.Cos(angle) * outerRadius,
                        center.Y + (float)Math.Sin(angle) * outerRadius);
                    using var pen = new Pen(Color.FromArgb(alpha, C_Wired), penWidth)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    e.Graphics.DrawLine(pen, start, end);
                }

                var messageRect = new Rectangle(
                    (int)(24 * scale),
                    (int)(center.Y + 43 * scale),
                    Math.Max(1, Width - (int)(48 * scale)),
                    (int)(30 * scale));
                TextRenderer.DrawText(e.Graphics, Text, Font, messageRect, C_Text,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.SingleLine);

                var hintRect = new Rectangle(
                    (int)(24 * scale),
                    messageRect.Bottom + (int)(4 * scale),
                    Math.Max(1, Width - (int)(48 * scale)),
                    (int)(24 * scale));
                TextRenderer.DrawText(e.Graphics, "正在安全处理网络配置，请稍候", Font, hintRect, C_Muted,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _animationTimer.Stop();
                    _animationTimer.Dispose();
                }
                base.Dispose(disposing);
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

        private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Color NormalText = ColorTranslator.FromHtml("#F3F6FA");
            private static readonly Color DisabledText = ColorTranslator.FromHtml("#8490A5");
            private static readonly Color Background = ColorTranslator.FromHtml("#202838");
            private static readonly Color Selection = ColorTranslator.FromHtml("#355274");
            private static readonly Color SelectionBorder = ColorTranslator.FromHtml("#6EB7FF");
            private static readonly Color Separator = ColorTranslator.FromHtml("#354154");

            public TrayMenuRenderer() : base(new TrayMenuColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var bounds = new Rectangle(Point.Empty, e.Item.Size);
                using (var background = new SolidBrush(e.Item.Selected ? Selection : Background))
                    e.Graphics.FillRectangle(background, bounds);

                if (e.Item.Selected && bounds.Width > 2 && bounds.Height > 2)
                {
                    using var border = new Pen(SelectionBorder, 1f);
                    e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
                }

                if (e.Item is ToolStripMenuItem menuItem && menuItem.Checked)
                    DrawCheckedMark(e.Graphics, menuItem);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var isLabel = e.Item is ToolStripLabel;
                var color = isLabel
                    ? e.Item.ForeColor
                    : e.Item.Enabled ? NormalText : DisabledText;
                var scale = ItemScale(e.Item);
                var left = isLabel ? e.Item.Padding.Left : (int)Math.Round(24f * scale);
                var right = isLabel ? e.Item.Padding.Right : (int)Math.Round(10f * scale);
                var textBounds = new Rectangle(
                    left,
                    0,
                    Math.Max(1, e.Item.Width - left - right),
                    e.Item.Height);

                TextRenderer.DrawText(
                    e.Graphics,
                    e.Text,
                    e.TextFont,
                    textBounds,
                    color,
                    TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // 禁止默认渲染器创建独立的蓝色勾选块；勾选标记已在菜单行背景中绘制。
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var scale = Math.Max(1f, e.Item.Height / 16f);
                var inset = (int)Math.Round(10f * scale);
                var y = e.Item.Height / 2;
                using var pen = new Pen(Separator, 1f);
                e.Graphics.DrawLine(pen, inset, y, Math.Max(inset, e.Item.Width - inset), y);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? NormalText : DisabledText;
                base.OnRenderArrow(e);
            }

            private static void DrawCheckedMark(Graphics graphics, ToolStripMenuItem item)
            {
                var scale = ItemScale(item);
                var centerY = item.Height / 2f;
                var points = new[]
                {
                    new PointF(5f * scale, centerY),
                    new PointF(9f * scale, centerY + 4f * scale),
                    new PointF(17f * scale, centerY - 5f * scale)
                };
                using var pen = new Pen(NormalText, Math.Max(1.8f, 2.1f * scale))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.DrawLines(pen, points);
            }

            private static float ItemScale(ToolStripItem item) =>
                Math.Max(1f, item.Height / 40f);
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
