using CodexQuota.Models;
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace CodexQuota
{
    public partial class MiniStatusWindow : Window
    {
        private readonly Action _restoreAction;
        private readonly Action _showMenuAction;
        private Rect _workingArea;
        private Rect _screenBounds;
        private bool _hasPosition;

        public MiniStatusWindow(Action restoreAction, Action showMenuAction)
        {
            InitializeComponent();
            _restoreAction = restoreAction;
            _showMenuAction = showMenuAction;
        }

        public void UpdateStatus(UsageWindowSnapshot shortWindow, UsageWindowSnapshot weekWindow,
            bool shortValid, bool weekValid, DateTimeOffset now, string statusMessage)
        {
            int shortPercent = shortValid ? (int)Math.Round(shortWindow.RemainingPercent) : 0;
            int weekPercent = weekValid ? (int)Math.Round(weekWindow.RemainingPercent) : 0;
            bool showBoth = shortValid && weekValid;

            ShortGroup.Visibility = shortValid ? Visibility.Visible : Visibility.Collapsed;
            WeekGroup.Visibility = weekValid || !shortValid ? Visibility.Visible : Visibility.Collapsed;
            Divider.Visibility = showBoth ? Visibility.Visible : Visibility.Collapsed;
            DividerColumn.Width = showBoth ? new GridLength(21) : new GridLength(0);

            ShortValueText.Text = shortValid ? shortPercent + "%" : "--";
            WeekLabelText.Text = weekValid ? "7d" : "同步";
            WeekValueText.Text = weekValid ? weekPercent + "%" : "--";
            WeekValueText.Foreground = new SolidColorBrush(weekValid
                ? Color.FromRgb(197, 90, 245)
                : Color.FromRgb(139, 149, 169));

            Width = showBoth ? 174 : 104;
            DetailStatusText.Text = shortValid || weekValid
                ? "同步正常"
                : (string.IsNullOrWhiteSpace(statusMessage) ? "等待新快照" : statusMessage);
            DetailStatusText.Foreground = new SolidColorBrush(shortValid || weekValid
                ? Color.FromRgb(110, 220, 185)
                : Color.FromRgb(245, 182, 92));
            DetailValueText.Text = BuildDetail(shortWindow, weekWindow, shortValid, weekValid, now);

            if (IsVisible && _hasPosition) PositionNearTaskbar();
        }

        public void ShowNearTaskbar(Rect workingArea, Rect screenBounds)
        {
            _workingArea = workingArea;
            _screenBounds = screenBounds;
            _hasPosition = true;
            PositionNearTaskbar();
            if (!IsVisible) Show();
            PositionNearTaskbar();
            StartEntranceAnimation();
        }

        public void HideStatus()
        {
            Hide();
        }

        public void RenderPreview(string path)
        {
            Root.BeginAnimation(OpacityProperty, null);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            Root.Opacity = 1d;
            RootTranslate.Y = 0d;

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

        private void PositionNearTaskbar()
        {
            if (!_hasPosition) return;

            const double gap = 8d;
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
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1d, 1.025d, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1d, 1.025d, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
            ShellBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(242, 10, 16, 27), Color.FromArgb(248, 13, 21, 35),
                    TimeSpan.FromMilliseconds(120)));
        }

        private void Root_MouseLeave(object sender, MouseEventArgs e)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.025d, 1d, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.025d, 1d, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
            ShellBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(248, 13, 21, 35), Color.FromArgb(242, 10, 16, 27),
                    TimeSpan.FromMilliseconds(120)));
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_restoreAction != null) _restoreAction();
        }

        private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_showMenuAction != null) _showMenuAction();
        }

        private static string BuildDetail(UsageWindowSnapshot shortWindow, UsageWindowSnapshot weekWindow,
            bool shortValid, bool weekValid, DateTimeOffset now)
        {
            string shortText = shortValid
                ? "5h " + Math.Round(shortWindow.RemainingPercent).ToString("0") + "% · " + FormatCountdown(shortWindow.ResetsAt - now)
                : "5h 等待同步";
            string weekText = weekValid
                ? "7d " + Math.Round(weekWindow.RemainingPercent).ToString("0") + "% · " + FormatCountdown(weekWindow.ResetsAt - now)
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
