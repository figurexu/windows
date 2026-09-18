using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsMonitor
{
    /// <summary>
    /// 主窗口：Win11 风格深色仪表盘，实时显示 CPU / 显卡 温度、功耗、频率及历史曲线。
    /// 支持最小化/关闭到系统托盘、托盘实时数值提示、窗口置顶。
    /// </summary>
    public class MainForm : Form
    {
        private readonly Timer _timer;
        private MonitorService _monitor;
        private bool _initFailed;
        private string _initError = "";
        private DateTime _lastUpdate;
        private bool _exiting;
        private bool _balloonShown;
        private Icon _appIcon;
        private NotifyIcon _notify;
        private RoundedButton _pinButton;
        private RoundedButton _overlayButton;
        private TaskbarOverlay _overlay;
        private DetailPopup _detailPopup;
        private HardwareSnapshot _lastSnap;
        private DateTime _lastDetailToggle = DateTime.MinValue;

        private readonly PanelData _cpu = new PanelData("CPU", Color.FromArgb(0x4F, 0xC3, 0xF7));
        private readonly PanelData _gpu = new PanelData("GPU", Color.FromArgb(0x34, 0xD3, 0x99));

        // 字体（优先 Win11 的 Segoe UI Variable，Win10 自动回退 Segoe UI）
        private readonly Font _fontTitle = PickFont("Segoe UI Variable Display", "Segoe UI", 21f, FontStyle.Bold);
        private readonly Font _fontSubtitle = PickFont("Segoe UI Variable Text", "Segoe UI", 9.5f, FontStyle.Regular);
        private readonly Font _fontBadge = PickFont("Segoe UI Variable Text", "Segoe UI", 13.5f, FontStyle.Bold);
        private readonly Font _fontName = PickFont("Segoe UI Variable Text", "Segoe UI", 9.5f, FontStyle.Regular);
        private readonly Font _fontSource = PickFont("Segoe UI Variable Text", "Segoe UI", 8.5f, FontStyle.Bold);
        private readonly Font _fontMetric = PickFont("Segoe UI Variable Text", "Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _fontValue = PickFont("Segoe UI Variable Display", "Segoe UI", 26f, FontStyle.Bold);
        private readonly Font _fontUnit = PickFont("Segoe UI Variable Text", "Segoe UI", 10.5f, FontStyle.Regular);
        private readonly Font _fontStatus = PickFont("Segoe UI Variable Text", "Segoe UI", 9f, FontStyle.Regular);
        private readonly Font _fontFooter = PickFont("Segoe UI Variable Text", "Segoe UI", 8.5f, FontStyle.Regular);
        private readonly Font _fontButton = PickFont("Segoe UI Variable Text", "Segoe UI", 9.5f, FontStyle.Bold);

        // 配色（深色、低饱和、蓝紫调）
        private static readonly Color ColorBgTop = Color.FromArgb(0x21, 0x23, 0x2B);
        private static readonly Color ColorBgBottom = Color.FromArgb(0x16, 0x18, 0x1E);
        private static readonly Color ColorPanel = Color.FromArgb(0x27, 0x2B, 0x35);
        private static readonly Color ColorPanelBorder = Color.FromArgb(0x37, 0x3D, 0x4A);
        private static readonly Color ColorTile = Color.FromArgb(0x1F, 0x23, 0x2C);
        private static readonly Color ColorTileBorder = Color.FromArgb(0x2E, 0x34, 0x3F);
        private static readonly Color ColorText = Color.FromArgb(0xED, 0xF1, 0xF7);
        private static readonly Color ColorDim = Color.FromArgb(0x97, 0xA1, 0xB3);
        private static readonly Color ColorFaint = Color.FromArgb(0x5F, 0x6A, 0x7D);
        private static readonly Color ColorOk = Color.FromArgb(0x34, 0xD3, 0x99);
        private static readonly Color ColorWarn = Color.FromArgb(0xFB, 0xBF, 0x24);
        private static readonly Color ColorErr = Color.FromArgb(0xF8, 0x71, 0x71);
        private static readonly Color AccentTemp = Color.FromArgb(0xF8, 0x71, 0x71);
        private static readonly Color AccentPower = Color.FromArgb(0xFB, 0xBF, 0x24);
        private static readonly Color AccentClock = Color.FromArgb(0x60, 0xA5, 0xFA);

        private readonly StringFormat _center =
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        public MainForm()
        {
            Text = "硬件监控";
            BackColor = ColorBgBottom;
            ClientSize = new Size(920, 520);
            MinimumSize = new Size(860, 480);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9f);

            _appIcon = CreateAppIcon();
            Icon = _appIcon;

            _cpu.Metrics[0].Init("温度", AccentTemp, "°C");
            _cpu.Metrics[1].Init("功耗", AccentPower, "W");
            _cpu.Metrics[2].Init("频率", AccentClock, "GHz");
            _gpu.Metrics[0].Init("温度", AccentTemp, "°C");
            _gpu.Metrics[1].Init("功耗", AccentPower, "W");
            _gpu.Metrics[2].Init("频率", AccentClock, "GHz");

            _pinButton = new RoundedButton("置顶");
            _pinButton.Accent = Color.FromArgb(0x4F, 0xC3, 0xF7);
            _pinButton.Click += delegate
            {
                TopMost = !TopMost;
                _pinButton.Active = TopMost;
            };
            Controls.Add(_pinButton);

            _overlayButton = new RoundedButton("任务栏");
            _overlayButton.Accent = Color.FromArgb(0x8B, 0x5C, 0xF6);
            _overlayButton.Click += delegate { ToggleOverlay(); };
            Controls.Add(_overlayButton);

            _overlay = new TaskbarOverlay();
            _overlay.VisibleChanged += delegate
            {
                if (_overlayButton != null) _overlayButton.Active = _overlay.Visible;
            };
            _overlay.Clicked += delegate { ToggleDetail(); };
            _overlay.AttachToTaskbar();

            SetupTray();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTimerTick;
            _timer.Start();

            FormClosed += OnFormClosed;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4); // 深色标题栏
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, 4); // 圆角窗口 (Win11)
            }
            catch { }
        }

        // ---------- 系统托盘 ----------

        private void SetupTray()
        {
            _notify = new NotifyIcon();
            _notify.Icon = _appIcon;
            _notify.Text = "硬件监控";
            _notify.Visible = true;
            _notify.DoubleClick += delegate { ToggleWindow(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("显示 / 隐藏窗口");
            show.Click += delegate { ToggleWindow(); };
            ToolStripMenuItem mini = new ToolStripMenuItem("任务栏显示");
            mini.Click += delegate { ToggleOverlay(); };
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(show);
            menu.Items.Add(mini);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            _notify.ContextMenuStrip = menu;
        }

        private void ToggleOverlay()
        {
            if (_overlay.OverlayOn)
            {
                _overlay.OverlayOn = false;
                _overlay.Hide();
                if (_detailPopup != null) _detailPopup.HidePopup();
            }
            else
            {
                _overlay.OverlayOn = true;
                _overlay.RefreshPosition();
            }
            if (_overlayButton != null) _overlayButton.Active = _overlay.Visible;
        }

        private void ToggleDetail()
        {
            // 防抖：180ms 内的连续点击忽略（配合淡入动画，连点不会闪）
            DateTime now = DateTime.Now;
            if ((now - _lastDetailToggle).TotalMilliseconds < 180) return;
            _lastDetailToggle = now;

            if (!_overlay.Visible) return;
            if (_detailPopup == null) _detailPopup = new DetailPopup();
            if (_detailPopup.Visible)
            {
                _detailPopup.HidePopup();
                return;
            }
            Rectangle anchor = _overlay.ScreenBounds;
            _detailPopup.ShowAbove(anchor);
            if (_lastSnap != null) _detailPopup.UpdateData(_lastSnap, _cpu, _gpu);
        }

        private void ToggleWindow()
        {
            if (Visible)
            {
                HideToTray();
            }
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                ShowInTaskbar = true;
                Activate();
            }
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (_detailPopup != null) _detailPopup.HidePopup();
            if (!_balloonShown)
            {
                _balloonShown = true;
                try { _notify.ShowBalloonTip(2500, "硬件监控", "已最小化到系统托盘，双击图标可恢复窗口。", ToolTipIcon.Info); }
                catch { }
            }
        }

        private void ExitApp()
        {
            _exiting = true;
            if (_notify != null) { _notify.Visible = false; _notify.Dispose(); _notify = null; }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) HideToTray();
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            if (_monitor != null)
            {
                try { _monitor.Dispose(); }
                catch { }
            }
            if (_overlay != null)
            {
                try { _overlay.Dispose(); }
                catch { }
            }
            if (_detailPopup != null)
            {
                try { _detailPopup.Dispose(); }
                catch { }
            }
            if (_appIcon != null)
            {
                try { _appIcon.Dispose(); }
                catch { }
            }
        }

        // ---------- 数据刷新 ----------

        private void EnsureMonitor()
        {
            if (_monitor != null || _initFailed) return;
            try { _monitor = new MonitorService(); }
            catch (Exception ex) { _initFailed = true; _initError = ex.Message; }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            EnsureMonitor();
            if (_monitor == null)
            {
                _status = "初始化失败: " + Truncate(_initError, 40);
                Invalidate();
                return;
            }

            try
            {
                HardwareSnapshot snap = _monitor.ReadSnapshot();
                _lastSnap = snap;
                _lastUpdate = DateTime.Now;

                UpdatePanel(_cpu, snap.CpuName, snap.CpuBlocked, snap.CpuFromAida64,
                    snap.CpuTemperature, snap.CpuPower, snap.CpuClock,
                    "未检测到 CPU 传感器（请以管理员身份运行本程序）",
                    "CPU 数据不可用：请确认 AIDA64 已运行并启用共享内存");
                UpdatePanel(_gpu, snap.GpuName, false, false,
                    snap.GpuTemperature, snap.GpuPower, snap.GpuClock,
                    "未检测到显卡传感器",
                    null);

                if (_overlay != null && _overlay.Visible) _overlay.UpdateValues(snap);
                if (_detailPopup != null && _detailPopup.Visible) _detailPopup.UpdateData(snap, _cpu, _gpu);

                UpdateTrayText(snap);
            }
            catch (Exception ex)
            {
                _status = "读取失败: " + Truncate(ex.Message, 40);
            }
            LayoutPinButton();
            Invalidate();
        }

        private void UpdateTrayText(HardwareSnapshot snap)
        {
            if (_notify == null) return;
            string cpu = FormatCpuTray(snap);
            string gpu = FormatGpuTray(snap);
            string text = "硬件监控 — " + cpu + " | " + gpu;
            if (text.Length > 63) text = text.Substring(0, 63);
            _notify.Text = text;
        }

        private static string FormatCpuTray(HardwareSnapshot snap)
        {
            string t = snap.CpuTemperature.HasValue ? snap.CpuTemperature.Value.ToString("F0") + "°C" : "--";
            string p = snap.CpuPower.HasValue ? snap.CpuPower.Value.ToString("F0") + "W" : "--";
            string c = snap.CpuClock.HasValue ? (snap.CpuClock.Value / 1000f).ToString("F1") + "G" : "--";
            return "CPU " + t + " " + p + " " + c;
        }

        private static string FormatGpuTray(HardwareSnapshot snap)
        {
            string t = snap.GpuTemperature.HasValue ? snap.GpuTemperature.Value.ToString("F0") + "°C" : "--";
            string p = snap.GpuPower.HasValue ? snap.GpuPower.Value.ToString("F0") + "W" : "--";
            string c = snap.GpuClock.HasValue ? (snap.GpuClock.Value / 1000f).ToString("F1") + "G" : "--";
            return "GPU " + t + " " + p + " " + c;
        }

        private string _status = "正在读取传感器…";

        private static void UpdatePanel(PanelData p, string name, bool blocked, bool fromAida64,
            float? temp, float? power, float? clock, string missing, string blockedMessage)
        {
            if (string.IsNullOrEmpty(name))
            {
                p.Available = false;
                p.Name = missing;
                p.NameWarn = true;
                p.Source = "不可用";
                for (int i = 0; i < 3; i++) p.Metrics[i].SetValue(null, "--", null);
                return;
            }
            p.Available = true;
            if (blocked)
            {
                p.Name = blockedMessage;
                p.NameWarn = true;
                p.Source = "驱动受限";
                for (int i = 0; i < 3; i++) p.Metrics[i].SetValue(null, "--", null);
                return;
            }
            p.Name = name;
            p.NameWarn = false;
            p.Source = fromAida64 ? "AIDA64" : "原生";
            p.Metrics[0].SetValue(temp, FormatValue(temp, "F0"), TempColor(temp));
            p.Metrics[1].SetValue(power, FormatValue(power, "F1"), null);
            p.Metrics[2].SetValue(clock, FormatClock(clock), null);
        }

        private static string FormatValue(float? v, string format)
        {
            return v.HasValue ? v.Value.ToString(format) : "--";
        }

        private static string FormatClock(float? v)
        {
            return v.HasValue ? (v.Value / 1000f).ToString("F2") : "--";
        }

        private static Color? TempColor(float? v)
        {
            if (!v.HasValue) return null;
            if (v.Value < 60f) return ColorOk;
            if (v.Value < 80f) return ColorWarn;
            return ColorErr;
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static Font PickFont(string primary, string fallback, float size, FontStyle style)
        {
            return PickFontSafe(primary, fallback, size, style);
        }

        public static Font PickFontSafe(string primary, string fallback, float size, FontStyle style)
        {
            bool has = false;
            foreach (FontFamily f in FontFamily.Families)
            {
                if (f.Name == primary) { has = true; break; }
            }
            return new Font(has ? primary : fallback, size, style);
        }

        private void LayoutPinButton()
        {
            SizeF pill = TextRenderer.MeasureText(_status, _fontStatus);
            int pillW = (int)pill.Width + 34;
            int pillX = ClientSize.Width - 18 - pillW;
            _overlayButton.SetBounds(pillX - 70 - 70, 16, 62, 28);
            _pinButton.SetBounds(pillX - 70, 16, 62, 28);
        }

        // ---------- 绘制 ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int W = ClientSize.Width;
            int H = ClientSize.Height;

            // 背景渐变
            using (LinearGradientBrush bg = new LinearGradientBrush(
                new Rectangle(0, 0, W, H), ColorBgTop, ColorBgBottom, LinearGradientMode.Vertical))
                g.FillRectangle(bg, 0, 0, W, H);

            DrawHeader(g, W);
            DrawFooter(g, W, H);

            const int M = 18, gap = 16;
            float cardTop = 86f;
            float cardBottom = H - 34f;
            float cardW = (W - M * 2f - gap) / 2f;
            float cardH = cardBottom - cardTop;

            DrawCard(g, new RectangleF(M, cardTop, cardW, cardH), _cpu, Color.FromArgb(0x4F, 0xC3, 0xF7));
            DrawCard(g, new RectangleF(M + cardW + gap, cardTop, cardW, cardH), _gpu, Color.FromArgb(0x34, 0xD3, 0x99));
        }

        private void DrawHeader(Graphics g, int W)
        {
            const int M = 18;

            using (SolidBrush dot = new SolidBrush(ColorOk))
                g.FillEllipse(dot, M + 2, 22, 9, 9);
            using (SolidBrush title = new SolidBrush(ColorText))
                g.DrawString("硬件监控", _fontTitle, title, M + 18, 10f);
            using (SolidBrush sub = new SolidBrush(ColorDim))
                g.DrawString("CPU · 显卡  温度 / 功耗 / 频率", _fontSubtitle, sub, M + 20, 46f);

            // 状态胶囊（右侧）
            Color pillBg, pillText, pillDot;
            string label = _status;
            if (_status.StartsWith("初始化失败") || _status.StartsWith("读取失败"))
            {
                pillBg = Color.FromArgb(0x3A, 0x22, 0x24); pillText = Color.FromArgb(0xFC, 0xA5, 0xA5); pillDot = ColorErr;
            }
            else if (_cpu.Available && _gpu.Available)
            {
                pillBg = Color.FromArgb(0x17, 0x35, 0x2A); pillText = Color.FromArgb(0x6E, 0xE7, 0xB7); pillDot = ColorOk; label = "实时";
            }
            else if (_cpu.Available || _gpu.Available)
            {
                pillBg = Color.FromArgb(0x35, 0x2D, 0x16); pillText = Color.FromArgb(0xFC, 0xD3, 0x4D); pillDot = ColorWarn; label = "部分受限";
            }
            else
            {
                pillBg = Color.FromArgb(0x3A, 0x22, 0x24); pillText = Color.FromArgb(0xFC, 0xA5, 0xA5); pillDot = ColorErr; label = "无数据";
            }

            SizeF ls = g.MeasureString(label, _fontStatus);
            float pillW = ls.Width + 36f;
            float pillH = 26f;
            float pillX = W - M - pillW;
            float pillY = 17f;
            using (GraphicsPath pp = RoundedRect(new RectangleF(pillX, pillY, pillW, pillH), 13f))
            using (SolidBrush pb = new SolidBrush(pillBg))
            {
                g.FillPath(pb, pp);
            }
            using (SolidBrush pd = new SolidBrush(pillDot))
                g.FillEllipse(pd, pillX + 10f, pillY + pillH / 2f - 3f, 6f, 6f);
            using (SolidBrush pt = new SolidBrush(pillText))
                g.DrawString(label, _fontStatus, pt, pillX + 22f, pillY + 4.5f);

            // 更新时间（胶囊下方右对齐）
            string time = _lastUpdate == DateTime.MinValue ? "" : "更新 " + _lastUpdate.ToString("HH:mm:ss");
            if (time.Length > 0)
            {
                SizeF ts = g.MeasureString(time, _fontFooter);
                using (SolidBrush tb = new SolidBrush(ColorFaint))
                    g.DrawString(time, _fontFooter, tb, pillX + pillW - ts.Width, pillY + pillH + 5f);
            }
        }

        private void DrawFooter(Graphics g, int W, int H)
        {
            string footer = "数据来源: AIDA64 共享内存 + LibreHardwareMonitor · 每秒刷新 · 关闭窗口即最小化到托盘";
            SizeF fs = g.MeasureString(footer, _fontFooter);
            using (SolidBrush fb = new SolidBrush(ColorFaint))
                g.DrawString(footer, _fontFooter, fb, (W - fs.Width) / 2f, H - 24f);
        }

        private void DrawCard(Graphics g, RectangleF rect, PanelData p, Color accent)
        {
            using (GraphicsPath path = RoundedRect(rect, 14f))
            using (SolidBrush bg = new SolidBrush(ColorPanel))
            using (Pen border = new Pen(ColorPanelBorder, 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            const float pad = 16f;

            // 顶部：彩色圆点 + 面板名 + 数据源标签
            float headerY = rect.Y + pad;
            using (SolidBrush dot = new SolidBrush(accent))
                g.FillEllipse(dot, rect.X + pad, headerY + 6f, 10f, 10f);
            using (SolidBrush badge = new SolidBrush(ColorText))
                g.DrawString(p.Badge, _fontBadge, badge, rect.X + pad + 16f, headerY - 2f);

            SizeF srcSize = g.MeasureString(p.Source, _fontSource);
            float srcW = srcSize.Width + 20f;
            float srcH = 20f;
            float srcX = rect.Right - pad - srcW;
            Color srcText = p.Available ? Color.FromArgb(0x9B, 0xC7, 0xFF) : ColorFaint;
            using (GraphicsPath sp = RoundedRect(new RectangleF(srcX, headerY + 1f, srcW, srcH), 10f))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(0x1B, 0x2A, 0x3D)))
            {
                g.FillPath(sb, sp);
            }
            using (SolidBrush st = new SolidBrush(srcText))
                g.DrawString(p.Source, _fontSource, st, srcX + 10f, headerY + 3f);

            // 设备名 / 状态提示
            string display = p.Name;
            float maxNameW = rect.Width - pad * 2f;
            using (SolidBrush nameBrush = new SolidBrush(p.NameWarn ? ColorWarn : ColorDim))
            {
                SizeF ns = g.MeasureString(display, _fontName);
                if (ns.Width > maxNameW && display.Length > 1)
                {
                    while (display.Length > 1 && g.MeasureString(display + "…", _fontName).Width > maxNameW)
                        display = display.Substring(0, display.Length - 1);
                    display += "…";
                }
                g.DrawString(display, _fontName, nameBrush, rect.X + pad, headerY + 32f);
            }

            // 三个指标块
            float tilesTop = headerY + 58f;
            float tilesBottom = rect.Bottom - pad;
            float tileH = tilesBottom - tilesTop;
            const float tileGap = 8f;
            float tileW = (rect.Width - pad * 2f - tileGap * 2f) / 3f;

            for (int i = 0; i < 3; i++)
            {
                RectangleF tile = new RectangleF(rect.X + pad + i * (tileW + tileGap), tilesTop, tileW, tileH);
                DrawTile(g, tile, p.Metrics[i]);
            }
        }

        private void DrawTile(Graphics g, RectangleF rect, MetricData m)
        {
            using (GraphicsPath path = RoundedRect(rect, 11f))
            using (SolidBrush bg = new SolidBrush(ColorTile))
            using (Pen border = new Pen(ColorTileBorder, 1f))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            using (SolidBrush titleBrush = new SolidBrush(ColorDim))
                g.DrawString(m.Title, _fontMetric, titleBrush, rect.X + 11f, rect.Y + 9f);

            Color valueColor = m.ValueBrush.HasValue ? m.ValueBrush.Value : m.Accent;
            using (SolidBrush valueBrush = new SolidBrush(valueColor))
                g.DrawString(m.ValueText, _fontValue, valueBrush, rect.X + 9f, rect.Y + 27f);

            if (m.Unit.Length > 0 && m.ValueText != "--")
            {
                SizeF vs = g.MeasureString(m.ValueText, _fontValue);
                using (SolidBrush unitBrush = new SolidBrush(ColorDim))
                    g.DrawString(m.Unit, _fontUnit, unitBrush, rect.X + 11f + vs.Width + 4f, rect.Y + 40f);
            }

            RectangleF chart = new RectangleF(rect.X + 7f, rect.Bottom - 30f, rect.Width - 14f, 22f);
            DrawSparkline(g, chart, m.History, m.Accent);
        }

        private static void DrawSparkline(Graphics g, RectangleF rect, List<float> history, Color accent)
        {
            if (history.Count < 2 || rect.Width < 20f || rect.Height < 8f) return;

            float min = history[0];
            float max = history[0];
            foreach (float v in history)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            float range = max - min;
            if (range < 1e-6f) range = 1f;
            float pad = range * 0.12f;
            min -= pad;
            max += pad;
            range = max - min;

            PointF[] pts = new PointF[history.Count];
            for (int i = 0; i < history.Count; i++)
            {
                float x = rect.Left + (float)i / (history.Count - 1) * rect.Width;
                float y = rect.Bottom - (history[i] - min) / range * rect.Height;
                pts[i] = new PointF(x, y);
            }

            using (GraphicsPath fill = new GraphicsPath())
            {
                fill.StartFigure();
                fill.AddLine(new PointF(pts[0].X, rect.Bottom), pts[0]);
                for (int i = 1; i < pts.Length; i++) fill.AddLine(pts[i - 1], pts[i]);
                fill.AddLine(pts[pts.Length - 1], new PointF(pts[pts.Length - 1].X, rect.Bottom));
                fill.CloseFigure();
                using (LinearGradientBrush br = new LinearGradientBrush(rect,
                    Color.FromArgb(60, accent), Color.FromArgb(0, accent), LinearGradientMode.Vertical))
                    g.FillPath(br, fill);
            }

            using (Pen pen = new Pen(accent, 1.6f))
                g.DrawLines(pen, pts);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            return RoundedRectSafe(r, radius);
        }

        public static GraphicsPath RoundedRectSafe(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private Icon CreateAppIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = RoundedRect(new RectangleF(2f, 2f, 28f, 28f), 8f))
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new Rectangle(2, 2, 28, 28),
                    Color.FromArgb(0x4F, 0xC3, 0xF7), Color.FromArgb(0x8B, 0x5C, 0xF6), 45f))
                {
                    g.FillPath(br, path);
                }
                using (SolidBrush w = new SolidBrush(Color.White))
                {
                    g.FillRectangle(w, 6, 18, 3, 7);
                    g.FillRectangle(w, 11, 13, 3, 12);
                    g.FillRectangle(w, 16, 16, 3, 9);
                    g.FillRectangle(w, 21, 10, 3, 15);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            bmp.Dispose();
            return Icon.FromHandle(hIcon);
        }

        // ---------- 自定义控件 ----------

        private class RoundedButton : Control
        {
            public Color Accent = Color.FromArgb(0x4F, 0xC3, 0xF7);
            public bool Active;
            private bool _hover;

            public RoundedButton(string text)
            {
                Text = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                Size = new Size(62, 28);
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                Color fill = Active ? Accent : (_hover ? Color.FromArgb(0x30, 0x36, 0x44) : Color.FromArgb(0x26, 0x2B, 0x35));
                Color text = Active ? Color.White : (_hover ? ColorText : ColorDim);

                using (GraphicsPath path = RoundedRect(new RectangleF(0f, 0f, Width - 1f, Height - 1f), 14f))
                using (SolidBrush bg = new SolidBrush(fill))
                using (Pen border = new Pen(Color.FromArgb(0x3A, 0x41, 0x50), 1f))
                {
                    g.FillPath(bg, path);
                    if (!Active) g.DrawPath(border, path);
                }

                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ---------- 数据模型 ----------

        public class MetricData
        {
            public string Title = "";
            public Color Accent;
            public string Unit = "";
            public string ValueText = "--";
            public Color? ValueBrush;
            public List<float> History = new List<float>();
            public float? MaxValue;
            public float? MinValue;
            private const int MaxPoints = 240;

            public void Init(string title, Color accent, string unit)
            {
                Title = title;
                Accent = accent;
                Unit = unit;
            }

            public void SetValue(float? value, string text, Color? brush)
            {
                ValueText = text ?? "--";
                ValueBrush = brush;
                if (value.HasValue)
                {
                    History.Add(value.Value);
                    if (History.Count > MaxPoints) History.RemoveAt(0);
                    if (!MaxValue.HasValue || value.Value > MaxValue.Value) MaxValue = value.Value;
                    if (!MinValue.HasValue || value.Value < MinValue.Value) MinValue = value.Value;
                }
                else
                {
                    History.Clear();
                }
            }
        }

        public class PanelData
        {
            public string Badge;
            public Color BadgeColor;
            public string Name = "读取中…";
            public bool NameWarn;
            public bool Available;
            public string Source = "";
            public MetricData[] Metrics = new MetricData[3];

            public PanelData(string badge, Color color)
            {
                Badge = badge;
                BadgeColor = color;
                for (int i = 0; i < 3; i++) Metrics[i] = new MetricData();
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    }
}
