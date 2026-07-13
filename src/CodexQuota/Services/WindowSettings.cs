using System;
using System.Globalization;
using System.IO;

namespace CodexQuota.Services
{
    public static class WindowSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuota",
            "window-ring-v2.txt");

        private static readonly string MiniPositionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuota",
            "window-mini-v1.txt");

        public static bool TryLoad(out double left, out double top, out double width, out double height, out bool topmost)
        {
            left = 0;
            top = 0;
            width = 120;
            height = 120;
            topmost = true;
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                string[] values = File.ReadAllLines(SettingsPath);
                if (values.Length < 3 ||
                    !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) ||
                    !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top))
                    return false;

                if (values.Length >= 5)
                {
                    return double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
                        double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height) &&
                        bool.TryParse(values[4], out topmost);
                }

                return bool.TryParse(values[2], out topmost);
            }
            catch { return false; }
        }

        public static void Save(double left, double top, double width, double height, bool topmost)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[]
                {
                    left.ToString(CultureInfo.InvariantCulture),
                    top.ToString(CultureInfo.InvariantCulture),
                    width.ToString(CultureInfo.InvariantCulture),
                    height.ToString(CultureInfo.InvariantCulture),
                    topmost.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }

        public static bool TryLoadMiniPosition(out double left, out double top)
        {
            string dockSide;
            bool autoHide;
            return TryLoadMiniState(out left, out top, out dockSide, out autoHide);
        }

        public static bool TryLoadMiniState(out double left, out double top,
            out string dockSide, out bool autoHide)
        {
            left = 0;
            top = 0;
            dockSide = "None";
            autoHide = true;
            try
            {
                if (!File.Exists(MiniPositionPath)) return false;
                string[] values = File.ReadAllLines(MiniPositionPath);
                if (values.Length < 2 ||
                    !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) ||
                    !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top))
                    return false;

                if (values.Length >= 3 && !string.IsNullOrWhiteSpace(values[2]))
                    dockSide = values[2];
                if (values.Length >= 4)
                {
                    bool parsedAutoHide;
                    if (bool.TryParse(values[3], out parsedAutoHide))
                        autoHide = parsedAutoHide;
                }
                return true;
            }
            catch { return false; }
        }

        public static void SaveMiniPosition(double left, double top)
        {
            SaveMiniState(left, top, "None", true);
        }

        public static void SaveMiniState(double left, double top, string dockSide, bool autoHide)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MiniPositionPath));
                File.WriteAllLines(MiniPositionPath, new[]
                {
                    left.ToString(CultureInfo.InvariantCulture),
                    top.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(dockSide) ? "None" : dockSide,
                    autoHide.ToString(CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }
    }
}
