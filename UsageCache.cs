using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal static class UsageCache
    {
        public sealed class Record
        {
            public string Identity, AccountName, PlanName, Error;
            public bool HasReading;
            public double? FiveHourUsed, WeeklyUsed;
            public long? FiveHourReset, WeeklyReset, UpdatedAt, RetryAt;
            public List<CachedLane> CustomLanes;
        }
        public sealed class CachedLane { public string Label; public double Used; public long? ResetAt; }
        static string Clean(string text)
        {
            return String.IsNullOrWhiteSpace(text) ? null : new string(text.Where(c => !Char.IsControl(c)).Take(160).ToArray()).Trim();
        }
        static long? Unix(DateTimeOffset? value) { return value.HasValue ? value.Value.ToUnixTimeSeconds() : (long?)null; }
        static UsageWindow Window(double? used, long? reset)
        {
            if (!used.HasValue) { if (reset.HasValue) throw UsageParser.BadData(); return null; }
            if (Double.IsNaN(used.Value) || Double.IsInfinity(used.Value) || used.Value < 0 || used.Value > 100) throw UsageParser.BadData();
            return new UsageWindow { Used = used.Value, ResetAt = reset.HasValue ? DateTimeOffset.FromUnixTimeSeconds(reset.Value) : (DateTimeOffset?)null };
        }
        static bool Recoverable(Exception e)
        {
            return e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException || e is FormatException || e is OverflowException || e.InnerException is ArgumentException || e.InnerException is FormatException || e.InnerException is OverflowException;
        }
        public static void Save(string path, string identity, ProviderState state)
        {
            try {
                var snapshot = state.Snapshot;
                var five = snapshot == null ? null : snapshot.FiveHour; var week = snapshot == null ? null : snapshot.Weekly;
                var record = new Record {
                    Identity = identity, HasReading = snapshot != null, AccountName = snapshot == null ? null : snapshot.AccountName, PlanName = snapshot == null ? null : snapshot.PlanName,
                    FiveHourUsed = five == null ? (double?)null : five.Used, WeeklyUsed = week == null ? (double?)null : week.Used,
                    FiveHourReset = five == null ? null : Unix(five.ResetAt), WeeklyReset = week == null ? null : Unix(week.ResetAt),
                    UpdatedAt = Unix(state.UpdatedAt), RetryAt = state.RetryNotBefore == DateTimeOffset.MinValue ? null : Unix(state.RetryNotBefore), Error = state.Error,
                    CustomLanes = snapshot == null || snapshot.CustomLanes == null ? null : snapshot.CustomLanes.Select(lane => new CachedLane { Label = lane.Label, Used = lane.Window.Used, ResetAt = Unix(lane.Window.ResetAt) }).ToList()
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", new JavaScriptSerializer().Serialize(record));
                WidgetSettings.ReplaceFile(path + ".tmp", path);
            } catch (Exception e) { if (!Recoverable(e)) throw; }
        }
        public static string SavedIdentity(string path)
        {
            try {
                if (!File.Exists(path) || new FileInfo(path).Length > 65536) return null;
                return UsageParser.Get(UsageParser.Json(File.ReadAllText(path)), "Identity") as string;
            } catch (Exception e) { if (!Recoverable(e)) throw; return null; }
        }
        public static bool Restore(string path, string identity, ProviderState state, DateTimeOffset now)
        {
            try {
                if (!File.Exists(path) || new FileInfo(path).Length > 65536) return false;
                string text = File.ReadAllText(path); var raw = UsageParser.Json(text);
                foreach (string key in new[] { "FiveHourUsed", "WeeklyUsed", "FiveHourReset", "WeeklyReset", "UpdatedAt", "RetryAt" }) {
                    var value = UsageParser.Get(raw, key); if (value == null) continue;
                    double number = UsageParser.Number(value);
                    if (key != "FiveHourUsed" && key != "WeeklyUsed" && number != Math.Truncate(number)) throw UsageParser.BadData();
                }
                var lanesValue = UsageParser.Get(raw, "CustomLanes");
                if (lanesValue != null) {
                    var lanes = lanesValue as object[];
                    if (lanes == null || lanes.Length > 64) throw UsageParser.BadData();
                    foreach (var item in lanes) {
                        var lane = item as Dictionary<string, object>;
                        string label = UsageParser.Get(lane, "Label") as string;
                        if (String.IsNullOrWhiteSpace(label) || label.Length > 113 || label.Any(Char.IsControl)) throw UsageParser.BadData();
                        double used = UsageParser.Number(UsageParser.Get(lane, "Used"));
                        if (used < 0 || used > 100) throw UsageParser.BadData();
                        var reset = UsageParser.Get(lane, "ResetAt");
                        if (reset != null) { double number = UsageParser.Number(reset); if (number != Math.Truncate(number)) throw UsageParser.BadData(); }
                    }
                }
                var record = new JavaScriptSerializer().Deserialize<Record>(text);
                if (record == null || record.Identity != identity) return false;
                var retry = record.RetryAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds(record.RetryAt.Value) : DateTimeOffset.MinValue;
                UsageSnapshot snapshot = null; DateTimeOffset updated = DateTimeOffset.MinValue;
                if (record.HasReading) {
                    if (!record.UpdatedAt.HasValue) return false;
                    updated = DateTimeOffset.FromUnixTimeSeconds(record.UpdatedAt.Value);
                    if (updated > now.AddMinutes(5)) return false;
                    snapshot = new UsageSnapshot { AccountName = Clean(record.AccountName), PlanName = record.CustomLanes == null ? UsageParser.NormalizePlan(record.PlanName) : Clean(record.PlanName), FiveHour = Window(record.FiveHourUsed, record.FiveHourReset), Weekly = Window(record.WeeklyUsed, record.WeeklyReset), CustomLanes = record.CustomLanes == null ? null : record.CustomLanes.Select(lane => new UsageLane { Label = lane.Label, Window = Window(lane.Used, lane.ResetAt) }).ToList() };
                } else if (record.UpdatedAt.HasValue || record.FiveHourUsed.HasValue || record.WeeklyUsed.HasValue || record.FiveHourReset.HasValue || record.WeeklyReset.HasValue || record.CustomLanes != null) return false;
                if (snapshot != null) { state.Accept(snapshot, updated); state.Fail("Saved reading · checking for updates"); }
                state.RetryNotBefore = retry;
                if (retry > now) state.Fail(Clean(record.Error) ?? "Waiting for the provider to allow another request");
                return true;
            } catch (Exception e) { if (!Recoverable(e)) throw; return false; }
        }
    }
}
