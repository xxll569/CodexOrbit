using CodexQuota.Models;
using CodexQuota.Services;
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexQuota
{
    public partial class MiniStatusWindow : Window
    {
        private enum MiniDockSide
        {
            None,
            Left,
            Right
        }

        private const double ExpandedWithShortWidth = 188d;
        private const double GaugeOnlyWidth = 68d;
        private const double HandleWidth = 14d;
        private const double DockThreshold = 56d;

        private readonly Action _showMenuAction;
        private readonly DispatcherTimer _collapseTimer;
        private Rect _workingArea;
        private Rect _screenBounds;
        private bool _hasPosition;
        private bool _savedPositionAvailable;
        private double _savedLeft;
        private double _savedTop;
        private double _expandedWidth = ExpandedWithShortWidth;
        private double _gaugePercent;
        private bool _isCollapsed;
        private bool _suppressRevealUntilMouseLeave;
        private bool _dockCollapseTransition;
        private MiniDockSide _dockSide;

        public MiniStatusWindow(Action showMenuAction)
        {
            InitializeComponent();
            _showMenuAction = showMenuAction;

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _collapseTimer.Tick += CollapseTimer_Tick;

            string savedDockSide;
            bool ignoredAutoHide;
            if (WindowSettings.TryLoadMiniState(out _savedLeft, out _savedTop,
                out savedDockSide, out ignoredAutoHide))
            {
                _savedPositionAvailable = true;
                _dockSide = ParseDockSide(savedDockSide);
            }
        }

        public void Reveal()
        {
            _collapseTimer.Stop();
            _suppressRevealUntilMouseLeave = false;
            _dockCollapseTransition = false;
            if (_dockSide != MiniDockSide.None) SetCollapsed(false, true);
        }

        public void UpdateStatus(UsageWindowSnapshot shortWindow, UsageWindowSnapshot weekWindow,
            bool shortValid, bool weekValid, DateTimeOffset now, string statusMessage)
        {
            bool showShortPill = shortValid && weekValid;
            bool gaugeValid = weekValid || shortValid;
            UsageWindowSnapshot gaugeWindow = weekValid ? weekWindow : shortWindow;

            PillShell.Visibility = showShortPill ? Visibility.Visible : Visibility.Collapsed;
            ShortValueText.Text = shortValid
                ? Math.Round(shortWindow.RemainingPercent).ToString("0") + "%"
                : "--";
            ShortResetText.Text = shortValid
                ? (shortWindow.IsUnusedInCurrentWindow
                    ? "新周期 · 使用后刷新"
                    : FormatCompactCountdown(shortWindow.ResetsAt - now))
                : "等待同步";

            _gaugePercent = gaugeValid ? Math.Max(0d, Math.Min(100d, gaugeWindow.RemainingPercent)) : 0d;
            GaugeValueText.Text = gaugeValid ? Math.Round(_gaugePercent).ToString("0") + "%" : "--";
            GaugeLabelText.Text = gaugeValid && gaugeWindow.IsUnusedInCurrentWindow
                ? (weekValid ? "7d·新" : "5h·新")
                : (weekValid ? "7d" : (shortValid ? "5h" : "同步"));
            GaugeLabelText.Foreground = new SolidColorBrush(weekValid
                ? Color.FromRgb(151, 122, 196)
                : Color.FromRgb(104, 163, 184));
            UpdateGaugeArc(_gaugePercent, gaugeValid);
            HandleProgress.Height = gaugeValid ? Math.Max(3d, 38d * _gaugePercent / 100d) : 3d;

            _expandedWidth = showShortPill ? ExpandedWithShortWidth : GaugeOnlyWidth;
            ExpandedContent.Width = _expandedWidth;
            if (!_isCollapsed)
            {
                Width = _expandedWidth;
                if (_dockSide != MiniDockSide.None) PositionDocked(false);
            }

              bool isUnusedResetWindow = gaugeValid && gaugeWindow.IsUnusedInCurrentWindow;
              bool isShowingStaleData = string.Equals(
                  statusMessage,
                  CodexUsageReader.StaleDataStatusMessage,
                  StringComparison.Ordinal);
              DetailStatusText.Text = shortValid || weekValid
                  ? (isShowingStaleData
                      ? CodexUsageReader.StaleDataStatusMessage
                      : (isUnusedResetWindow ? "额度已重置 · 使用后刷新" : "同步正常"))
                  : (string.IsNullOrWhiteSpace(statusMessage) ? "等待新快照" : statusMessage);
              DetailStatusText.Foreground = new SolidColorBrush(shortValid || weekValid
                  ? (isUnusedResetWindow || isShowingStaleData
                      ? Color.FromRgb(245, 182, 92)
                      : Color.FromRgb(110, 220, 185))
                  : Color.FromRgb(245, 182, 92));
            DetailValueText.Text = BuildDetail(shortWindow, weekWindow, shortValid, weekValid, now);
        }

        public void ShowNearTaskbar(Rect workingArea, Rect screenBounds)
        {
            _workingArea = workingArea;
            _screenBounds = screenBounds;
            if (!_hasPosition)
            {
                _hasPosition = true;
                if (_savedPositionAvailable && IsVisiblePosition(_savedLeft, _savedTop))
                {
                    Left = _savedLeft;
                    Top = _savedTop;
                }
                else
                {
                    _dockSide = MiniDockSide.None;
                    PositionNearTaskbar();
                }
            }
            else if (!IsVisiblePosition(Left, Top))
            {
                _dockSide = MiniDockSide.None;
                PositionNearTaskbar();
            }

            if (!IsVisible) Show();
            UpdateLayout();
            RefreshCurrentScreenRects(false);
            Top = Clamp(Top, _workingArea.Top, _workingArea.Bottom - Height);

            if (_dockSide != MiniDockSide.None)
                SetCollapsed(true, false);
            else
                SetCollapsed(false, false);

            StartEntranceAnimation();
        }

        public void HideStatus()
        {
            _collapseTimer.Stop();
            Hide();
        }

        public void RenderPreview(string path)
        {
            _collapseTimer.Stop();
            _dockSide = MiniDockSide.None;
            SetCollapsed(false, false);
            Root.BeginAnimation(OpacityProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            Root.Opacity = 1d;
            RootTranslate.Y = 0d;
            UpdateLayout();

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

        protected override void OnClosed(EventArgs e)
        {
            _collapseTimer.Stop();
            base.OnClosed(e);
        }

        private void PositionNearTaskbar()
        {
            const double gap = 8d;
            Width = _expandedWidth;
            bool taskbarAtBottom = _workingArea.Bottom < _screenBounds.Bottom - 1d;
            bool taskbarAtTop = _workingArea.Top > _screenBounds.Top + 1d;
            bool taskbarAtRight = _workingArea.Right < _screenBounds.Right - 1d;
            bool taskbarAtLeft = _workingArea.Left > _screenBounds.Left + 1d;

            if (taskbarAtTop)
            {
                Left = _workingArea.Right - Width - 10d;
                Top = _workingArea.Top + gap;
            }
            else if (taskbarAtRight)
            {
                Left = _workingArea.Right - Width - gap;
                Top = _workingArea.Bottom - Height - 10d;
            }
            else if (taskbarAtLeft)
            {
                Left = _workingArea.Left + gap;
                Top = _workingArea.Bottom - Height - 10d;
            }
            else
            {
                Left = _workingArea.Right - Width - 10d;
                Top = taskbarAtBottom
                    ? _workingArea.Bottom - Height - gap
                    : _workingArea.Bottom - Height - 10d;
            }
        }

        private void RefreshCurrentScreenRects(bool useCursor)
        {
            if (!IsVisible) return;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null) return;

            Drawing.Point physicalPoint;
            if (useCursor)
                physicalPoint = Forms.Cursor.Position;
            else
            {
                Point screenPoint = PointToScreen(new Point(Math.Max(1d, ActualWidth / 2d), ActualHeight / 2d));
                physicalPoint = new Drawing.Point((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
            }
            Forms.Screen screen = Forms.Screen.FromPoint(physicalPoint);
            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point workTopLeft = fromDevice.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
            Point workBottomRight = fromDevice.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
            Point screenTopLeft = fromDevice.Transform(new Point(screen.Bounds.Left, screen.Bounds.Top));
            Point screenBottomRight = fromDevice.Transform(new Point(screen.Bounds.Right, screen.Bounds.Bottom));
            _workingArea = new Rect(workTopLeft, workBottomRight);
            _screenBounds = new Rect(screenTopLeft, screenBottomRight);
        }

        private bool IsVisiblePosition(double left, double top)
        {
            if (double.IsNaN(left) || double.IsInfinity(left) ||
                double.IsNaN(top) || double.IsInfinity(top))
                return false;

            const double minimumVisible = 24d;
            double virtualLeft = SystemParameters.VirtualScreenLeft;
            double virtualTop = SystemParameters.VirtualScreenTop;
            double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            return left + _expandedWidth - minimumVisible >= virtualLeft &&
                   left + minimumVisible <= virtualRight &&
                   top + Height - minimumVisible >= virtualTop &&
                   top + minimumVisible <= virtualBottom;
        }

        private void EvaluateDockAfterDrag()
        {
            RefreshCurrentScreenRects(true);
            double windowRight = Left + ActualWidth;

            if (Left <= _workingArea.Left + DockThreshold)
                _dockSide = MiniDockSide.Left;
            else if (windowRight >= _workingArea.Right - DockThreshold)
                _dockSide = MiniDockSide.Right;
            else
                _dockSide = MiniDockSide.None;

            Top = Clamp(Top, _workingArea.Top, _workingArea.Bottom - Height);
            if (_dockSide == MiniDockSide.None)
            {
                _suppressRevealUntilMouseLeave = false;
                _dockCollapseTransition = false;
                Left = Clamp(Left, _workingArea.Left, _workingArea.Right - _expandedWidth);
                SetCollapsed(false, false);
            }
            else
            {
                _suppressRevealUntilMouseLeave = true;
                _dockCollapseTransition = true;
                SetCollapsed(true, true);
            }
            SaveMiniState();
        }

        private void PositionDocked(bool collapsed)
        {
            double width = collapsed ? HandleWidth : _expandedWidth;
            Width = width;
            Left = _dockSide == MiniDockSide.Right
                ? _workingArea.Right - width
                : _workingArea.Left;
            Top = Clamp(Top, _workingArea.Top, _workingArea.Bottom - Height);
        }

        private void SetCollapsed(bool collapsed, bool animate)
        {
            if (_dockSide == MiniDockSide.None) collapsed = false;
            _collapseTimer.Stop();
            _isCollapsed = collapsed;

            double targetWidth = collapsed ? HandleWidth : _expandedWidth;
            double targetLeft = Left;
            if (_dockSide == MiniDockSide.Right)
                targetLeft = _workingArea.Right - targetWidth;
            else if (_dockSide == MiniDockSide.Left)
                targetLeft = _workingArea.Left;

            ExpandedContent.Visibility = Visibility.Visible;
            CollapsedHandle.Visibility = Visibility.Visible;
            ExpandedContent.IsHitTestVisible = !collapsed;

            if (!animate)
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(LeftProperty, null);
                ExpandedContent.BeginAnimation(OpacityProperty, null);
                CollapsedHandle.BeginAnimation(OpacityProperty, null);
                Width = targetWidth;
                Left = targetLeft;
                ExpandedContent.Opacity = collapsed ? 0d : 1d;
                CollapsedHandle.Opacity = collapsed ? 1d : 0d;
                ExpandedContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                CollapsedHandle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            double startWidth = ActualWidth > 0d ? ActualWidth : Width;
            double startLeft = Left;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(190);
            var widthAnimation = new DoubleAnimation(startWidth, targetWidth, duration) { EasingFunction = easing };
            var leftAnimation = new DoubleAnimation(startLeft, targetLeft, duration) { EasingFunction = easing };
            var expandedAnimation = new DoubleAnimation(ExpandedContent.Opacity, collapsed ? 0d : 1d, duration)
            { EasingFunction = easing };
            var handleAnimation = new DoubleAnimation(CollapsedHandle.Opacity, collapsed ? 1d : 0d, duration)
            { EasingFunction = easing };

            widthAnimation.Completed += delegate
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(LeftProperty, null);
                ExpandedContent.BeginAnimation(OpacityProperty, null);
                CollapsedHandle.BeginAnimation(OpacityProperty, null);
                Width = targetWidth;
                Left = targetLeft;
                ExpandedContent.Opacity = collapsed ? 0d : 1d;
                CollapsedHandle.Opacity = collapsed ? 1d : 0d;
                ExpandedContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                CollapsedHandle.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
                if (_dockCollapseTransition)
                {
                    _dockCollapseTransition = false;
                    if (!IsMouseOver) _suppressRevealUntilMouseLeave = false;
                }
            };

            Width = targetWidth;
            Left = targetLeft;
            BeginAnimation(WidthProperty, widthAnimation);
            BeginAnimation(LeftProperty, leftAnimation);
            ExpandedContent.BeginAnimation(OpacityProperty, expandedAnimation);
            CollapsedHandle.BeginAnimation(OpacityProperty, handleAnimation);
        }

        private void ScheduleCollapse()
        {
            if (_dockSide == MiniDockSide.None || _isCollapsed) return;
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void CollapseTimer_Tick(object sender, EventArgs e)
        {
            _collapseTimer.Stop();
            if (_dockSide != MiniDockSide.None && !IsMouseOver)
                SetCollapsed(true, true);
        }

        private void StartEntranceAnimation()
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            Root.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
            RootTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(4d, 0d, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
        }

        private void Root_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapseTimer.Stop();
            if (_suppressRevealUntilMouseLeave || _dockCollapseTransition) return;
            if (_dockSide != MiniDockSide.None && _isCollapsed)
                SetCollapsed(false, true);

            PillBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(242, 10, 16, 27), Color.FromArgb(248, 13, 21, 35),
                    TimeSpan.FromMilliseconds(120)));
            GaugeBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(246, 9, 15, 26), Color.FromArgb(250, 12, 20, 34),
                    TimeSpan.FromMilliseconds(120)));
        }

        private void Root_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_suppressRevealUntilMouseLeave && !_dockCollapseTransition)
                _suppressRevealUntilMouseLeave = false;
            PillBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(248, 13, 21, 35), Color.FromArgb(242, 10, 16, 27),
                    TimeSpan.FromMilliseconds(120)));
            GaugeBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(250, 12, 20, 34), Color.FromArgb(246, 9, 15, 26),
                    TimeSpan.FromMilliseconds(120)));
            ScheduleCollapse();
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _collapseTimer.Stop();
            _suppressRevealUntilMouseLeave = false;
            _dockCollapseTransition = false;
            if (_isCollapsed) SetCollapsed(false, false);

            double startLeft = Left;
            double startTop = Top;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标按键状态在系统接管拖动前变化时，保留普通单击行为。
            }

            double movedX = Math.Abs(Left - startLeft);
            double movedY = Math.Abs(Top - startTop);
            bool wasDragged = movedX >= SystemParameters.MinimumHorizontalDragDistance ||
                              movedY >= SystemParameters.MinimumVerticalDragDistance;
            if (wasDragged)
            {
                EvaluateDockAfterDrag();
                return;
            }
        }

        private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_showMenuAction != null) _showMenuAction();
        }

        private void UpdateGaugeArc(double percent, bool valid)
        {
            if (!valid || percent <= 0d)
            {
                GaugeProgressPath.Data = null;
                return;
            }

            double drawPercent = Math.Min(99.999d, percent);
            const double center = 31d;
            const double radius = 27d;
            Point start = PointOnCircle(center, center, radius, -90d);
            Point end = PointOnCircle(center, center, radius, -90d + drawPercent * 3.6d);
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = drawPercent > 50d
            });
            GaugeProgressPath.Data = new PathGeometry(new[] { figure });
        }

        private void SaveMiniState()
        {
            double left = double.IsNaN(Left) ? _savedLeft : Left;
            double top = double.IsNaN(Top) ? _savedTop : Top;
            WindowSettings.SaveMiniState(left, top, _dockSide.ToString(), true);
            _savedLeft = left;
            _savedTop = top;
            _savedPositionAvailable = true;
        }

        private static MiniDockSide ParseDockSide(string value)
        {
            if (string.Equals(value, "Left", StringComparison.OrdinalIgnoreCase)) return MiniDockSide.Left;
            if (string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase)) return MiniDockSide.Right;
            return MiniDockSide.None;
        }

        private static Point PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180d;
            return new Point(centerX + radius * Math.Cos(radians), centerY + radius * Math.Sin(radians));
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (maximum < minimum) return minimum;
            return Math.Max(minimum, Math.Min(value, maximum));
        }

        private static string FormatCompactCountdown(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero) return "等待同步";
            if (remaining.TotalDays >= 1d)
                return string.Format("{0}d{1}h 后重置", (int)remaining.TotalDays, remaining.Hours);
            if (remaining.TotalHours >= 1d)
                return string.Format("{0}h{1}m 后重置", (int)remaining.TotalHours, remaining.Minutes);
            return string.Format("{0}m 后重置", Math.Max(0, remaining.Minutes));
        }

        private static string BuildDetail(UsageWindowSnapshot shortWindow, UsageWindowSnapshot weekWindow,
            bool shortValid, bool weekValid, DateTimeOffset now)
        {
            string shortText = shortValid
                ? (shortWindow.IsUnusedInCurrentWindow
                    ? "5h 100% · 新周期，使用后刷新"
                    : "5h " + Math.Round(shortWindow.RemainingPercent).ToString("0") + "% · " + FormatCountdown(shortWindow.ResetsAt - now))
                : "5h 等待同步";
            string weekText = weekValid
                ? (weekWindow.IsUnusedInCurrentWindow
                    ? "7d 100% · 新周期，使用后刷新"
                    : "7d " + Math.Round(weekWindow.RemainingPercent).ToString("0") + "% · " + FormatCountdown(weekWindow.ResetsAt - now))
                : "7d 等待同步";
            return shortText + "\n" + weekText;
        }

        private static string FormatCountdown(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero) return "等待同步";
            if (remaining.TotalDays >= 1)
                return string.Format("{0}天{1}时后重置", (int)remaining.TotalDays, remaining.Hours);
            if (remaining.TotalHours >= 1)
                return string.Format("{0}时{1}分后重置", (int)remaining.TotalHours, remaining.Minutes);
            return string.Format("{0}分后重置", Math.Max(0, remaining.Minutes));
        }
    }
}
