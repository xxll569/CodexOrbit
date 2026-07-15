using System;

namespace CodexQuota.Models
{
    public sealed class UsageWindowSnapshot
    {
        public int WindowMinutes { get; set; }
        public double UsedPercent { get; set; }
        public DateTimeOffset ResetsAt { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public string SourceFile { get; set; }

        public double RemainingPercent
        {
            get { return Math.Max(0d, Math.Min(100d, 100d - UsedPercent)); }
        }

        public bool IsUnusedInCurrentWindow
        {
            get { return UsedPercent <= 0d; }
        }

        public bool IsExpired(DateTimeOffset now)
        {
            return ResetsAt <= now;
        }
    }

    public sealed class UsageSnapshot
    {
        public UsageWindowSnapshot ShortWindow { get; set; }
        public UsageWindowSnapshot WeekWindow { get; set; }
        public string StatusMessage { get; set; }

        public bool HasAnyData
        {
            get { return ShortWindow != null || WeekWindow != null; }
        }
    }
}
