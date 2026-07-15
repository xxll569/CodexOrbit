using CodexQuota.Models;
using CodexQuota.Services;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexQuota
{
    public partial class MainWindow : Window
    {
        private readonly CodexUsageReader _reader;
        private readonly DispatcherTimer _clockTimer;
        private readonly Forms.NotifyIcon _trayIcon;
        private readonly Forms.ContextMenuStrip _contextMenu;
        private readonly Forms.ToolStripMenuItem _topmostMenu;
        private readonly MiniStatusWindow _miniStatusWindow;
        private UsageSnapshot _snapshot;
        private bool _allowClose;
        private bool _hasLoadedPosition;
        private readonly string _previewPath;
        private readonly string _miniPreviewPath;
        private HwndSource _windowSource;
        private bool _maintainingAspectRatio;
        private double _shortRingPercent;
        private double _weekRingPercent;
        private bool _showShortWindow;

        private const int WmNcHitTest = 0x0084;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int HtTransparent = -1;

        public MainWindow(string previewPath, string miniPreviewPath)
        {
            InitializeComponent();
            _previewPath = previewPath;
            _miniPreviewPath = miniPreviewPath;

            _reader = new CodexUsageReader(CodexUsageReader.GetDefaultSessionsPath());
            _reader.SnapshotChanged += Reader_SnapshotChanged;

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += ClockTimer_Tick;

            _contextMenu = new Forms.ContextMenuStrip();
            _miniStatusWindow = new MiniStatusWindow(
                delegate { Dispatcher.BeginInvoke(new Action(ShowContextMenu)); });
            _contextMenu.Items.Add("重新读取", null, delegate { _reader.RequestRefresh(); });
            _topmostMenu = new Forms.ToolStripMenuItem("始终置顶") { Checked = true, CheckOnClick = true };
            _topmostMenu.CheckedChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Topmost = _topmostMenu.Checked;
                    _miniStatusWindow.Topmost = _topmostMenu.Checked;
                    SaveWindowSettings();
                }));
            };
            _contextMenu.Items.Add(_topmostMenu);
            _contextMenu.Items.Add(new Forms.ToolStripSeparator());
            _contextMenu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(ExitApplication)); });

            _trayIcon = new Forms.NotifyIcon
            {
                Text = "Codex Orbit · 等待额度数据",
                Icon = CreateTrayIcon(),
                ContextMenuStrip = _contextMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(RevealMini)); };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreWindowSettings();
            bool normalStartup = string.IsNullOrWhiteSpace(_previewPath) &&
                                 string.IsNullOrWhiteSpace(_miniPreviewPath);
            if (normalStartup)
                ShowMiniMode();
            else
            {
                LayoutGauge();
                StartEntranceAnimation();
            }
            _clockTimer.Start();
            _reader.StartWatching();

            UsageSnapshot initial = await Task.Run(new Func<UsageSnapshot>(_reader.ReadLatest));
            ApplySnapshot(initial);

            if (!string.IsNullOrWhiteSpace(_miniPreviewPath))
            {
                DateTimeOffset previewNow = DateTimeOffset.Now;
                _miniStatusWindow.UpdateStatus(
                    new UsageWindowSnapshot { UsedPercent = 32, ResetsAt = previewNow.AddHours(2).AddMinutes(18) },
                    new UsageWindowSnapshot { UsedPercent = 37, ResetsAt = previewNow.AddDays(6).AddHours(20) },
                    true, true, previewNow, "预览数据");
                Rect workingArea;
                Rect screenBounds;
                GetCurrentScreenRects(out workingArea, out screenBounds);
                Hide();
                _miniStatusWindow.ShowNearTaskbar(workingArea, screenBounds);
                await Task.Delay(260);
                _miniStatusWindow.RenderPreview(_miniPreviewPath);
                ExitApplication();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_previewPath))
            {
                await Task.Delay(450);
                RenderPreview(_previewPath);
                ExitApplication();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (_windowSource != null) _windowSource.AddHook(WindowMessageHook);
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmNcHitTest) return IntPtr.Zero;

            long packed = lParam.ToInt64();
            int screenX = unchecked((short)(packed & 0xFFFF));
            int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
            Point point = PointFromScreen(new Point(screenX, screenY));
            double centerX = ActualWidth / 2d;
            double centerY = ActualHeight / 2d;
            double dx = point.X - centerX;
            double dy = point.Y - centerY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double radius = Math.Min(ActualWidth, ActualHeight) / 2d;

            if (distance > radius + 1d)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }

            double resizeBand = Math.Max(10d, Math.Min(18d, radius * 0.16d));
            if (distance < radius - resizeBand) return IntPtr.Zero;

            double angle = Math.Atan2(dy, dx) * 180d / Math.PI;
            int hit;
            if (angle >= -22.5 && angle < 22.5) hit = HtRight;
            else if (angle >= 22.5 && angle < 67.5) hit = HtBottomRight;
            else if (angle >= 67.5 && angle < 112.5) hit = HtBottom;
            else if (angle >= 112.5 && angle < 157.5) hit = HtBottomLeft;
            else if (angle >= 157.5 || angle < -157.5) hit = HtLeft;
            else if (angle >= -157.5 && angle < -112.5) hit = HtTopLeft;
            else if (angle >= -112.5 && angle < -67.5) hit = HtTop;
            else hit = HtTopRight;

            handled = true;
            return new IntPtr(hit);
        }

        private void Reader_SnapshotChanged(object sender, UsageSnapshot snapshot)
        {
            Dispatcher.BeginInvoke(new Action(delegate { ApplySnapshot(snapshot); }));
        }

        private void ApplySnapshot(UsageSnapshot snapshot)
        {
            _snapshot = snapshot ?? new UsageSnapshot { StatusMessage = "暂无数据" };
            UpdateGaugeValues();
        }

        private void UpdateGaugeValues()
        {
            DateTimeOffset now = DateTimeOffset.Now;
            UsageWindowSnapshot shortWindow = _snapshot == null ? null : _snapshot.ShortWindow;
            UsageWindowSnapshot weekWindow = _snapshot == null ? null : _snapshot.WeekWindow;
            bool shortValid = shortWindow != null && !shortWindow.IsExpired(now);
            bool weekValid = weekWindow != null && !weekWindow.IsExpired(now);

            _shortRingPercent = shortValid ? shortWindow.RemainingPercent : 0d;
            _weekRingPercent = weekValid ? weekWindow.RemainingPercent : 0d;
            _showShortWindow = shortValid;
            ShortPercent.Text = shortValid
                ? "5h " + Math.Round(_shortRingPercent).ToString("0") + "%"
                : "5h --";
            WeekPercent.Text = weekValid
                ? "7d " + Math.Round(_weekRingPercent).ToString("0") + "%"
                : "7d --";
            ShortPercent.Opacity = shortValid ? 1d : 0.52d;
            WeekPercent.Opacity = weekValid ? 1d : 0.52d;
            ShortPercent.Visibility = shortValid ? Visibility.Visible : Visibility.Collapsed;
            ShortRing.Visibility = shortValid ? Visibility.Visible : Visibility.Collapsed;
            ShortDetailRow.Visibility = shortValid ? Visibility.Visible : Visibility.Collapsed;

            UsageWindowSnapshot resetWindow = shortValid ? shortWindow : (weekValid ? weekWindow : null);
            bool resetIsShort = shortValid;
            if (resetWindow != null)
                ResetText.Text = FormatCountdown(resetWindow.ResetsAt - now, resetIsShort);
            else if (shortWindow != null || weekWindow != null)
                ResetText.Text = "额度待同步";
            else
                ResetText.Text = "等待数据";

            UpdateDetailToolTip(now, shortWindow, weekWindow, shortValid, weekValid);
            UpdateTrayStatus(shortWindow, weekWindow, shortValid, weekValid);
            LayoutGauge();
        }

        private void UpdateDetailToolTip(DateTimeOffset now, UsageWindowSnapshot shortWindow,
            UsageWindowSnapshot weekWindow, bool shortValid, bool weekValid)
        {
            bool hasAnyData = shortWindow != null || weekWindow != null;
            bool hasValidData = shortValid || weekValid;
            bool hasUnusedResetWindow = (shortValid && shortWindow.IsUnusedInCurrentWindow) ||
                                        (weekValid && weekWindow.IsUnusedInCurrentWindow);

            if (hasValidData)
            {
                SyncStatusText.Text = hasUnusedResetWindow ? "额度已重置 · 使用后刷新" : "同步正常";
                SyncStatusText.Foreground = new SolidColorBrush(hasUnusedResetWindow
                    ? Color.FromRgb(245, 182, 92)
                    : Color.FromRgb(105, 216, 178));
            }
            else if (hasAnyData)
            {
                SyncStatusText.Text = "等待新快照";
                SyncStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 182, 92));
            }
            else
            {
                SyncStatusText.Text = string.IsNullOrWhiteSpace(_snapshot.StatusMessage)
                    ? "暂无数据"
                    : _snapshot.StatusMessage;
                SyncStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 182, 92));
            }

            ShortDetailText.Text = FormatWindowDetail(shortWindow, shortValid, now);
            WeekDetailText.Text = FormatWindowDetail(weekWindow, weekValid, now);

            DateTimeOffset? latest = null;
            if (shortWindow != null) latest = shortWindow.ObservedAt;
            if (weekWindow != null && (!latest.HasValue || weekWindow.ObservedAt > latest.Value))
                latest = weekWindow.ObservedAt;
            LastSyncText.Text = latest.HasValue ? FormatSnapshotTime(latest.Value, now) : "--";
        }

        private static string FormatWindowDetail(UsageWindowSnapshot window, bool valid, DateTimeOffset now)
        {
            if (window == null) return "未检测到数据";
            if (!valid) return "预计已重置 · 等待同步";
            if (window.IsUnusedInCurrentWindow) return "100% 剩余 · 新周期，使用后刷新";
            return Math.Round(window.RemainingPercent).ToString("0") + "% 剩余 · " +
                   FormatCountdown(window.ResetsAt - now, window.WindowMinutes == CodexUsageReader.ShortWindowMinutes) + "重置";
        }

        private static string FormatSnapshotTime(DateTimeOffset observedAt, DateTimeOffset now)
        {
            DateTime local = observedAt.LocalDateTime;
            return local.Date == now.LocalDateTime.Date
                ? "今天 " + local.ToString("HH:mm:ss")
                : local.ToString("MM-dd HH:mm:ss");
        }

        private static string FormatCountdown(TimeSpan remaining, bool isShort)
        {
            if (remaining <= TimeSpan.Zero) return "待同步";
            if (!isShort && remaining.TotalDays >= 1)
                return string.Format("{0}天{1}时后", (int)remaining.TotalDays, remaining.Hours);
            if (remaining.TotalHours >= 1)
                return string.Format("{0}时{1}分后", (int)remaining.TotalHours, remaining.Minutes);
            return string.Format("{0}分{1}秒后", Math.Max(0, remaining.Minutes), Math.Max(0, remaining.Seconds));
        }

        private void LayoutGauge()
        {
            double size = Math.Min(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
            if (size <= 0) return;

            double scale = Math.Max(0.8d, Math.Min(3d, size / 120d));
            Point center = new Point(ActualWidth / 2d, ActualHeight / 2d);
            double outerRadius = size * 0.40d;
            double outerStroke = Math.Max(4.5d, 7d * scale);
            double innerStroke = Math.Max(4d, 6d * scale);
            double ringGap = Math.Max(1d, (outerStroke + innerStroke) / 2d - 0.75d * scale);
            double innerRadius = outerRadius - ringGap;

            ConfigureTrack(WeekTrack, center, outerRadius, outerStroke);
            ConfigureTrack(ShortTrack, center, innerRadius, innerStroke);
            double backdropRadius = _showShortWindow
                ? innerRadius - innerStroke / 2d + 0.5d
                : outerRadius - outerStroke / 2d + 0.5d;
            ConfigureDisc(CenterBackdrop, center, Math.Max(8d, backdropRadius * 0.88d));
            WeekRing.StrokeThickness = outerStroke;
            ShortRing.StrokeThickness = innerStroke;
            WeekRing.Data = CreateArcGeometry(center, outerRadius, _weekRingPercent);
            ShortRing.Data = CreateArcGeometry(center, innerRadius, _shortRingPercent);

            WeekPercent.FontSize = 16.5d * scale;
            ShortPercent.FontSize = 14.5d * scale;
            ResetText.FontSize = 9.5d * scale;
        }

        private static void ConfigureTrack(System.Windows.Shapes.Ellipse ellipse, Point center, double radius, double stroke)
        {
            ellipse.Width = radius * 2d;
            ellipse.Height = radius * 2d;
            ellipse.StrokeThickness = stroke;
            Canvas.SetLeft(ellipse, center.X - radius);
            Canvas.SetTop(ellipse, center.Y - radius);
        }

        private static void ConfigureDisc(System.Windows.Shapes.Ellipse ellipse, Point center, double radius)
        {
            ellipse.Width = radius * 2d;
            ellipse.Height = radius * 2d;
            Canvas.SetLeft(ellipse, center.X - radius);
            Canvas.SetTop(ellipse, center.Y - radius);
        }

        private static Geometry CreateArcGeometry(Point center, double radius, double percent)
        {
            percent = Math.Max(0d, Math.Min(100d, percent));
            if (percent <= 0.01d) return Geometry.Empty;
            if (percent >= 99.99d) return new EllipseGeometry(center, radius, radius);

            double startAngle = -90d;
            double endAngle = startAngle + 360d * percent / 100d;
            Point start = PointOnCircle(center, radius, startAngle);
            Point end = PointOnCircle(center, radius, endAngle);
            var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = percent > 50d
            });
            return new PathGeometry(new[] { figure });
        }

        private static Point PointOnCircle(Point center, double radius, double angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180d;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            if (_snapshot == null) return;
            UpdateGaugeValues();
        }

        private void StartEntranceAnimation()
        {
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = easing });
            RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = easing });
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }

        private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowContextMenu();
        }

        private void ShowContextMenu()
        {
            _contextMenu.Show(Forms.Cursor.Position);
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_maintainingAspectRatio || !IsLoaded)
            {
                LayoutGauge();
                return;
            }

            double widthChange = Math.Abs(e.NewSize.Width - e.PreviousSize.Width);
            double heightChange = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);
            double size = widthChange >= heightChange ? e.NewSize.Width : e.NewSize.Height;
            size = Math.Max(MinWidth, Math.Min(size, 480d));

            _maintainingAspectRatio = true;
            Width = size;
            Height = size;
            _maintainingAspectRatio = false;
            LayoutGauge();
        }

        private void ShowMiniMode()
        {
            Rect workingArea;
            Rect screenBounds;
            GetCurrentScreenRects(out workingArea, out screenBounds);
            ShowInTaskbar = false;
            Hide();
            _miniStatusWindow.Topmost = _topmostMenu.Checked;
            _miniStatusWindow.ShowNearTaskbar(workingArea, screenBounds);
        }

        private void RevealMini()
        {
            if (_miniStatusWindow.IsVisible)
                _miniStatusWindow.Reveal();
            else
                ShowMiniMode();
        }

        private void RestoreWindowSettings()
        {
            double left, top, width, height;
            bool topmost;
            if (WindowSettings.TryLoad(out left, out top, out width, out height, out topmost) && IsVisiblePosition(left, top))
            {
                double size = Math.Max(MinWidth, Math.Min(Math.Max(width, height), 480d));
                Width = size;
                Height = size;
                Left = left;
                Top = top;
                _hasLoadedPosition = true;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = SystemParameters.WorkArea.Right - Width - 24;
                Top = SystemParameters.WorkArea.Bottom - Height - 24;
            }

            Topmost = topmost;
            _miniStatusWindow.Topmost = topmost;
            _topmostMenu.Checked = topmost;
        }

        private bool IsVisiblePosition(double left, double top)
        {
            return left + 80 >= SystemParameters.VirtualScreenLeft &&
                   left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
                   top + 60 >= SystemParameters.VirtualScreenTop &&
                   top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
        }

        private void SaveWindowSettings()
        {
            if (!_hasLoadedPosition && double.IsNaN(Left)) return;
            WindowSettings.Save(Left, Top, ActualWidth, ActualHeight, Topmost);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                ShowMiniMode();
            }
        }

        private void ExitApplication()
        {
            _allowClose = true;
            SaveWindowSettings();
            _clockTimer.Stop();
            _reader.Dispose();
            if (_windowSource != null) _windowSource.RemoveHook(WindowMessageHook);
            _miniStatusWindow.Close();
            _trayIcon.Visible = false;
            if (_trayIcon.Icon != null) _trayIcon.Icon.Dispose();
            _trayIcon.Dispose();
            _contextMenu.Dispose();
            Close();
            Application.Current.Shutdown();
        }

        private void RenderPreview(string path)
        {
            Root.BeginAnimation(OpacityProperty, null);
            RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            RootScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            Root.Opacity = 1;
            RootScale.ScaleX = 1;
            RootScale.ScaleY = 1;

            int pixelWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        private void UpdateTrayStatus(UsageWindowSnapshot shortWindow, UsageWindowSnapshot weekWindow,
            bool shortValid, bool weekValid)
        {
            int shortPercent = shortValid ? (int)Math.Round(shortWindow.RemainingPercent) : -1;
            int weekPercent = weekValid ? (int)Math.Round(weekWindow.RemainingPercent) : -1;
            DateTimeOffset now = DateTimeOffset.Now;

            string shortText = shortValid ? shortPercent + "%" : "--";
            string weekText = weekValid ? weekPercent + "%" : "--";
            _trayIcon.Text = "Codex Orbit · 5h " + shortText + " · 7d " + weekText;
            _miniStatusWindow.UpdateStatus(shortWindow, weekWindow, shortValid, weekValid, now,
                _snapshot == null ? null : _snapshot.StatusMessage);
        }

        private void GetCurrentScreenRects(out Rect workingArea, out Rect screenBounds)
        {
            workingArea = SystemParameters.WorkArea;
            screenBounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

            if (!IsVisible) return;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null) return;

            Point screenPoint = PointToScreen(new Point(ActualWidth / 2d, ActualHeight / 2d));
            Forms.Screen screen = Forms.Screen.FromPoint(new Drawing.Point(
                (int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point workTopLeft = fromDevice.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            Point workBottomRight = fromDevice.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            Point screenTopLeft = fromDevice.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
            Point screenBottomRight = fromDevice.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom));
            workingArea = new Rect(workTopLeft, workBottomRight);
            screenBounds = new Rect(screenTopLeft, screenBottomRight);
        }

        private static Drawing.Icon CreateTrayIcon()
        {
            using (Stream stream = typeof(MainWindow).Assembly.GetManifestResourceStream(
                "CodexQuota.Assets.tray-icon.ico"))
            {
                if (stream != null)
                {
                    using (var trayIcon = new Drawing.Icon(stream))
                        return (Drawing.Icon)trayIcon.Clone();
                }
            }

            string executablePath = Forms.Application.ExecutablePath;
            using (Drawing.Icon executableIcon = Drawing.Icon.ExtractAssociatedIcon(executablePath))
            {
                if (executableIcon != null) return (Drawing.Icon)executableIcon.Clone();
            }

            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }
    }
}
