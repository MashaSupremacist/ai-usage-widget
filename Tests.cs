using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    internal static class Tests
    {
        static int passed, failed;
        static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794050000);
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message + (e.InnerException == null ? "" : " (" + e.InnerException.GetType().Name + ")")); }
        }
        static void Rejects(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Malformed data was accepted");
        }
        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        static void WithCache(Action<string> test)
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usage-cache-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "usage.json");
            try { test(path); }
            finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); Directory.Delete(directory); }
        }
        sealed class MalformedUsageHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("not json") });
            }
        }
        static string CodexWindow(double used, int minutes)
        {
            return "{\"usedPercent\":" + used.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"windowDurationMins\":" + minutes + ",\"resetsAt\":1794401183}";
        }
        [STAThread]
        public static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--live") >= 0)
            {
                foreach (bool isCodex in new[] { false, true })
                    try {
                        var credentials = UsageClient.ReadCredentials(isCodex);
                        var result = System.Threading.Tasks.Task.Run(() => UsageClient.FetchAsync(isCodex, credentials, System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
                        Console.WriteLine("LIVE " + (isCodex ? "Codex" : "Claude") + " five-hour=" + (result.FiveHour == null ? "not reported" : result.FiveHour.Used.ToString()) + " weekly=" + (result.Weekly == null ? "not reported" : result.Weekly.Used.ToString()) + " account-name=" + (String.IsNullOrEmpty(result.AccountName) ? "unavailable" : "present"));
                    } catch (Exception e) {
                        Console.WriteLine("LIVE " + (isCodex ? "Codex" : "Claude") + " failed: " + e.GetType().Name + ": " + e.Message);
                        for (var inner = e.InnerException; inner != null; inner = inner.InnerException) Console.WriteLine("CAUSE " + inner.GetType().Name + ": " + inner.Message);
                        failed++;
                    }
                return failed == 0 ? 0 : 1;
            }
            Run("weekly primary never becomes five-hour usage", () => {
                var value = UsageParser.ParseCodex("{\"rateLimits\":{\"primary\":" + CodexWindow(50, 10080) + ",\"secondary\":null}}");
                Check(value.FiveHour == null && value.Weekly.Used == 50, "Wrong window mapping");
            });
            Run("both Codex windows map by duration even when swapped", () => {
                var value = UsageParser.ParseCodex("{\"rateLimits\":{\"primary\":" + CodexWindow(60, 10080) + ",\"secondary\":" + CodexWindow(25, 300) + "}}");
                Check(value.FiveHour.Used == 25 && value.Weekly.Used == 60, "Swapped windows mislabeled");
            });
            Run("Codex main bucket takes precedence over legacy view", () => {
                var value = UsageParser.ParseCodex("{\"rateLimits\":{\"primary\":" + CodexWindow(99,300) + "},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":" + CodexWindow(12,300) + "}}}");
                Check(value.FiveHour.Used == 12, "Wrong bucket selected");
            });
            Run("unknown Codex duration is not relabeled", () => {
                var value = UsageParser.ParseCodex("{\"rateLimits\":{\"primary\":" + CodexWindow(30,60) + "}}");
                Check(value.FiveHour == null && value.Weekly == null, "Invented five-hour window");
            });
            Run("Claude percentages preserve explicit zero and fraction", () => {
                var value = UsageParser.ParseClaude("{\"five_hour\":{\"utilization\":0,\"resets_at\":null},\"seven_day\":{\"utilization\":41.2,\"resets_at\":\"2026-11-11T12:00:00Z\"}}");
                Check(value.FiveHour.Used == 0 && value.Weekly.Used == 41.2 && value.Weekly.ResetAt.Value.Year == 2026, "Valid values changed");
            });
            Run("missing Claude window remains absent", () => {
                var value = UsageParser.ParseClaude("{\"five_hour\":null,\"seven_day\":{\"utilization\":80,\"resets_at\":null}}");
                Check(value.FiveHour == null && value.Weekly.Used == 80, "Missing window became zero");
            });
            Run("invalid usage percentage types and ranges are rejected", () => {
                foreach (var invalid in new[] { "true", "\"30\"", "-1", "101", "null" })
                    Rejects(() => UsageParser.ParseClaude("{\"five_hour\":{\"utilization\":" + invalid + ",\"resets_at\":null}}"));
            });
            Run("malformed reset timestamps are rejected", () => {
                Rejects(() => UsageParser.ParseClaude("{\"five_hour\":{\"utilization\":20,\"resets_at\":\"tomorrow\"}}"));
                Rejects(() => UsageParser.ParseCodex("{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300,\"resetsAt\":true}}}"));
            });
            Run("malformed payload is not an empty successful response", () => {
                Rejects(() => UsageParser.ParseClaude("not json"));
                Rejects(() => UsageParser.ParseCodex("{}"));
            });
            Run("passed reset time does not invent zero usage", () => {
                var value = UsageParser.ParseClaude("{\"five_hour\":{\"utilization\":75,\"resets_at\":\"2020-01-01T00:00:00Z\"}}");
                Check(value.FiveHour.IsExpired(Now) && value.FiveHour.Used == 75, "Reset fabricated a measurement");
            });
            Run("network failure retains the original measurement age", () => {
                var state = new ProviderState(); state.UseIdentity("account-a");
                state.Accept(new UsageSnapshot { FiveHour = new UsageWindow { Used = 35 } }, Now);
                state.Fail("Offline");
                Check(state.Snapshot.FiveHour.Used == 35 && state.UpdatedAt == Now && state.Error == "Offline", "Failure hid freshness");
            });
            Run("identity change clears the previous account values", () => {
                var state = new ProviderState(); state.UseIdentity("account-a");
                state.Accept(new UsageSnapshot { Weekly = new UsageWindow { Used = 50 } }, Now);
                state.UseIdentity("account-b");
                Check(state.Snapshot == null && state.UpdatedAt == null, "Another account's values leaked");
            });
            Run("same identity preserves a retained reading", () => {
                var state = new ProviderState(); state.UseIdentity("account-a"); state.Accept(new UsageSnapshot(), Now);
                state.UseIdentity("account-a");
                Check(state.UpdatedAt == Now, "Unchanged identity erased the reading");
            });
            Run("corrupt settings safely fall back to defaults", () => {
                var value = WidgetSettings.Parse("not json");
                Check(value.Transparency == 50 && !value.Pinned, "Corruption prevents a safe start");
            });
            Run("settings clamp image transparency without losing pin preference", () => {
                var value = WidgetSettings.Parse("{\"Transparency\":999,\"Pinned\":true}");
                Check(value.Transparency == 100 && value.Pinned, "Unbounded transparency");
            });
            Run("background transparency never dims the foreground window", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    form.ApplyTransparency(50);
                    Check(form.Opacity == 1.0, "Usage indicators became transparent");
                }
            });
            Run("corrupt background does not prevent opening", () => {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "invalid-background.tmp");
                File.WriteAllText(path, "not an image");
                try { using (var form = new WidgetForm(new WidgetSettings { BackgroundPath = path })) Check(form.Opacity == 1, "Window failed to open"); }
                finally { File.Delete(path); }
            });
            Run("an unreported row collapses content within the fixed window", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    form.ShowUsage(true, new UsageSnapshot { FiveHour = new UsageWindow { Used = 20 }, Weekly = new UsageWindow { Used = 50 } }, Now);
                    var list = Descendants(form).OfType<AccountsPanel>().Single();
                    int both = list.ContentHeight; Size window = form.ClientSize;
                    form.ShowUsage(true, new UsageSnapshot { Weekly = new UsageWindow { Used = 50 } }, Now);
                    Check(list.ContentHeight < both && form.ClientSize == window, "Unreported row or fixed window has the wrong size");
                    var weekly = Descendants(form).OfType<MeterBar>().Single(m => m.AccessibleName == "CODEX weekly remaining");
                    Check(weekly.Width == list.ClientSize.Width - 192 * form.DeviceDpi / 96, "Bars did not relayout when a scrollbar disappeared");
                }
            });
            Run("meters display the remaining allowance with a left label", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    foreach (var used in new[] { 0.0, 100.0, 41.2 }) {
                        foreach (bool isCodex in new[] { false, true })
                            form.ShowUsage(isCodex, new UsageSnapshot { FiveHour = new UsageWindow { Used = used }, Weekly = new UsageWindow { Used = used } }, Now);
                        double left = 100 - used;
                        string number = left.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                        var meters = Descendants(form).OfType<MeterBar>().Where(m => m.AccessibleName.StartsWith("CLAUDE ") || m.AccessibleName.StartsWith("CODEX ")).ToArray();
                        Check(meters.Length == 4 && meters.All(m => m.Value == left), "Bars still show usage consumed");
                        Check(meters.All(m => m.AccessibilityObject.Value == number + " percent left" && m.AccessibleDescription.Contains("% left")), "Accessible meter still describes consumed usage");
                        var labels = Descendants(form).OfType<Label>().Where(l => l.Text == number + "% left").ToArray();
                        Check(labels.Length == 4, "Remaining percentages are not labeled left");
                        Check(labels.All(l => System.Windows.Forms.TextRenderer.MeasureText(l.Text, l.Font).Width <= l.Width), "Remaining percentage is clipped");
                    }
                }
            });
            Run("routine update text and its extra space are removed", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    foreach (bool isCodex in new[] { false, true })
                        form.ShowUsage(isCodex, new UsageSnapshot { Weekly = new UsageWindow { Used = 50 } }, Now);
                    Check(!Descendants(form).OfType<Label>().Any(l => l.Text.StartsWith("Updated ") || l.Text.StartsWith("Updates every ") || l.Text.StartsWith("Connecting") || l.Text.StartsWith("Updating")), "Routine update text is still present");
                }
            });
            Run("additional accounts scroll without resizing the widget", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    foreach (bool codex in new[] { false, true })
                        form.ShowUsage(codex, new UsageSnapshot { Weekly = new UsageWindow { Used = 30 } }, Now);
                    Size window = form.ClientSize;
                    Check(window == new Size(340 * form.DeviceDpi / 96, 232 * form.DeviceDpi / 96), "Current compact window size changed");
                    typeof(WidgetForm).GetMethod("AddProvider", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(form, new object[] { true, "EXTRA", Color.Blue, 0 });
                    form.ShowUsage(true, new UsageSnapshot { Weekly = new UsageWindow { Used = 30 } }, Now);
                    var list = Descendants(form).OfType<AccountsPanel>().Single();
                    Check(form.ClientSize == window && list.ContentHeight > list.ClientSize.Height, "Additional account is clipped or resizes the window");
                    Check(list.CanScroll && !list.VerticalScroll.Visible && !list.HorizontalScroll.Visible, "Account list lacks its thin scroller or still shows a native scrollbar");
                    var meter = Descendants(form).OfType<MeterBar>().Single(m => m.AccessibleName == "EXTRA weekly remaining");
                    int before = meter.Top; list.ScrollTo(list.MaxScroll, false);
                    Check(list.ScrollY == list.MaxScroll && meter.Top == before - list.MaxScroll, "Scrolling does not move the rows");
                    form.ShowUsage(true, new UsageSnapshot { Weekly = new UsageWindow { Used = 35 } }, Now);
                    Check(list.ScrollY == list.MaxScroll && meter.Top == before - list.MaxScroll, "A routine refresh jumps the scroll position");
                    list.ScrollTo(-500, false);
                    Check(list.ScrollY == 0 && meter.Top == before, "Scrolling past the top is not clamped");
                }
            });
            Run("header icon, title, plan badge and account name share one center line", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    form.ShowUsage(false, new UsageSnapshot { AccountName = "Preview", PlanName = "Pro", Weekly = new UsageWindow { Used = 20 } }, Now);
                    var all = Descendants(form).ToArray();
                    var row = new Control[] {
                        all.OfType<PictureBox>().First(p => p.AccessibleName == "CLAUDE icon"),
                        all.OfType<Label>().First(l => l.Text == "CLAUDE"),
                        all.OfType<Label>().Single(l => l.AccessibleName == "CLAUDE plan: Pro"),
                        all.OfType<Label>().Single(l => l.AccessibleName == "Claude account: Preview") };
                    var centers = row.Select(c => c.Top * 2 + c.Height).ToArray();
                    Check(centers.Max() - centers.Min() <= 2, "Header items are vertically misaligned");
                }
            });
            Run("crop stays within portrait and landscape images at the window aspect ratio", () => {
                foreach (var size in new[] { new Size(1200, 600), new Size(600, 1200), new Size(40, 40) }) {
                    var crop = new CropSelection(size, new Size(340, 232));
                    foreach (double zoom in new[] { 1.0, 2.0, 4.0 }) {
                        crop.Zoom = zoom;
                        foreach (var center in new[] { new PointF(-1000, -1000), new PointF(5000, 5000), new PointF(size.Width / 2f, size.Height / 2f) }) {
                            crop.MoveTo(center); var bounds = crop.Bounds;
                            Check(bounds.Left >= -0.001 && bounds.Top >= -0.001 && bounds.Right <= size.Width + 0.001 && bounds.Bottom <= size.Height + 0.001, "Crop escaped the original image");
                            Check(Math.Abs(bounds.Width / bounds.Height - 340f / 232) < 0.001, "White frame no longer matches the window");
                        }
                    }
                    crop.Zoom = 1; float width = crop.Bounds.Width; crop.Zoom = 2;
                    Check(Math.Abs(crop.Bounds.Width * 2 - width) < 0.001, "Zoom does not select a smaller source area");
                }
            });
            Run("accepted crop uses the selected pixels and preserves the original", () => {
                using (var image = new Bitmap(80, 40)) {
                    using (var g = Graphics.FromImage(image)) { g.Clear(Color.Red); g.FillRectangle(Brushes.Blue, 40, 0, 40, 40); }
                    var crop = new CropSelection(image.Size, new Size(40, 40));
                    crop.MoveTo(new PointF(20, 20));
                    using (var selected = crop.Render(image)) Check(selected.Size == new Size(40, 40) && selected.GetPixel(20, 20).R > 250, "Left selection was not rendered");
                    crop.MoveTo(new PointF(60, 20)); crop.Zoom = 2;
                    using (var selected = crop.Render(image)) Check(selected.GetPixel(20, 20).B > 250, "Moved/zoomed selection was not rendered");
                    Check(image.GetPixel(10, 20).R == 255 && image.GetPixel(70, 20).B == 255, "Cropping altered the original image");
                }
            });
            Run("crop preview supports dragging and arrow keys without changing the source", () => {
                using (var image = new Bitmap(200, 100)) {
                    var crop = new CropSelection(image.Size, new Size(340, 232)); crop.Zoom = 2;
                    using (var preview = new CropPreview(image, crop) { Size = new Size(400, 300) }) {
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        typeof(CropPreview).GetMethod("OnMouseDown", flags).Invoke(preview, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 200, 150, 0) });
                        typeof(CropPreview).GetMethod("OnMouseMove", flags).Invoke(preview, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 250, 150, 0) });
                        typeof(CropPreview).GetMethod("OnMouseUp", flags).Invoke(preview, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 250, 150, 0) });
                        Check(crop.Center.X > 100 && crop.Zoom == 2, "Dragging did not move the selected area");
                        float moved = crop.Center.X;
                        typeof(CropPreview).GetMethod("OnKeyDown", flags).Invoke(preview, new object[] { new KeyEventArgs(Keys.Left) });
                        Check(crop.Center.X < moved, "Arrow key did not move the selected area");
                    }
                    using (var dialog = new BackgroundCropDialog(image, new Size(340, 232))) {
                        dialog.DialogResult = DialogResult.Cancel;
                        Check(dialog.SelectedImage == null && image.Size == new Size(200, 100), "Cancel committed a crop or disposed the original");
                    }
                }
            });
            Run("settings conversion failures do not prevent startup", () => {
                foreach (var text in new[] { "{\"Transparency\":2147483648}", "{\"Left\":-2147483649}", "{\"Pinned\":\"maybe\"}" })
                    Check(WidgetSettings.Parse(text).Transparency == 50, "Invalid settings did not fall back");
            });
            Run("switching accounts clears the previous account throttle", () => {
                var state = new ProviderState(); state.UseIdentity("account-a"); state.Accept(new UsageSnapshot(), Now);
                state.RetryNotBefore = Now.AddHours(2);
                Check(!state.CanFetch(Now, true), "Manual refresh ignored server throttling");
                state.UseIdentity("account-b");
                Check(state.Snapshot == null && state.CanFetch(Now, true), "Old account blocked or contaminated new account");
            });
            Run("shutdown awaits outstanding refresh cleanup", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    var pending = new System.Threading.Tasks.TaskCompletionSource<int>();
                    var view = typeof(WidgetForm).GetField("claude", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(form);
                    view.GetType().GetField("ActiveTask").SetValue(view, pending.Task);
                    var stopping = form.StopRefreshingAsync();
                    Check(!stopping.IsCompleted, "Shutdown completed before cleanup");
                    pending.SetResult(0); stopping.GetAwaiter().GetResult();
                    Check(stopping.IsCompleted, "Shutdown did not finish after cleanup");
                }
            });
            Run("Codex account label comes from signed-in identity with email fallback", () => {
                string payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"email\":\"person@example.test\"}")).TrimEnd('=').Replace('+','-').Replace('/','_');
                Check(UsageParser.NameFromJwt("x." + payload + ".x") == "person@example.test", "Email fallback missing");
                Check(UsageParser.NameFromJwt("not a token") == null, "Invalid identity produced a name");
            });
            Run("Claude account label prefers display name and falls back to email", () => {
                Check(UsageParser.NameFromClaudeProfile("{\"account\":{\"display_name\":\"Pat\",\"full_name\":\"Pat Example\",\"email\":\"pat@example.test\"}}") == "Pat", "Display name ignored");
                Check(UsageParser.NameFromClaudeProfile("{\"account\":{\"display_name\":\"\",\"email\":\"pat@example.test\"}}") == "pat@example.test", "Email fallback missing");
            });
            Run("Codex live profile prefers current name and falls back to email", () => {
                Check(UsageParser.NameFromCodexProfile("{\"name\":\" Current\\u0001 Name \",\"email\":\"person@example.test\"}") == "Current Name", "Live profile name ignored or unsanitized");
                Check(UsageParser.NameFromCodexProfile("{\"name\":\" \",\"email\":\"person@example.test\"}") == "person@example.test", "Email fallback missing");
                Check(UsageParser.NameFromCodexProfile("{\"name\":\"" + new string('a', 200) + "\"}").Length == 160, "Profile name was not bounded");
            });
            Run("successful account names are rechecked on the next usage refresh", () => {
                var profile = new ProfileState(); Check(profile.Begin("account-a", Now), "Initial profile blocked"); profile.Accept("Initial");
                Check(profile.Begin("account-a", Now.AddSeconds(1)), "Independent profile deadline skipped the next usage refresh");
                profile.Accept("Updated");
                Check(profile.Name == "Updated", "Successful rename was not stored");
            });
            Run("failed profiles keep the same account name and retry after sixty seconds", () => {
                var profile = new ProfileState(); profile.Begin("account-a", Now); profile.Accept("Initial");
                Check(profile.Begin("account-a", Now.AddSeconds(1)), "Successful profile blocked the next refresh");
                Check(!profile.Begin("account-a", Now.AddSeconds(60)), "Failed profile retried too early");
                Check(profile.Begin("account-a", Now.AddSeconds(61)), "Failed profile never became due again");
                Check(profile.Name == "Initial", "Failure discarded the same account's fallback");
            });
            Run("switching profiles forgets previous names and retry deadlines", () => {
                var profile = new ProfileState(); profile.Begin("account-a", Now); profile.Name = "Initial"; profile.NextAttempt = Now.AddHours(1);
                Check(profile.Begin("account-b", Now.AddSeconds(1)), "Old profile retry blocked the new account");
                Check(profile.Name == null, "Old account name leaked into the new account");
            });
            Run("Codex sign-ins sharing a workspace have isolated identities", () => {
                string prior = Environment.GetEnvironmentVariable("CODEX_HOME");
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credential-fixture-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory); string path = Path.Combine(directory, "auth.json");
                try {
                    Environment.SetEnvironmentVariable("CODEX_HOME", directory);
                    File.WriteAllText(path, "{\"tokens\":{\"account_id\":\"shared-workspace\",\"access_token\":\"fixture-token-a\"}}");
                    var first = UsageClient.ReadCredentials(true);
                    File.WriteAllText(path, "{\"tokens\":{\"account_id\":\"shared-workspace\",\"access_token\":\"fixture-token-b\"}}");
                    var second = UsageClient.ReadCredentials(true);
                    Check(first.Identity != second.Identity, "Different sign-ins retained the same profile identity");
                    Check(second.AccessToken == "fixture-token-b", "Codex profile credential was discarded");
                } finally {
                    Environment.SetEnvironmentVariable("CODEX_HOME", prior); File.Delete(path); Directory.Delete(directory);
                }
            });
            Run("saved usage restores original values and age as a stale reading", () => WithCache(path => {
                var state = new ProviderState(); state.UseIdentity("account-a");
                state.Accept(new UsageSnapshot { AccountName = "Preview", FiveHour = new UsageWindow { Used = 20, ResetAt = Now.AddHours(1) }, Weekly = new UsageWindow { Used = 30 } }, Now);
                UsageCache.Save(path, "account-a", state);
                var restored = new ProviderState(); restored.UseIdentity("account-a");
                Check(UsageCache.Restore(path, "account-a", restored, Now.AddMinutes(1)), "Restart lost the last successful reading");
                Check(restored.Snapshot.FiveHour.Used == 20 && restored.Snapshot.Weekly.Used == 30 && restored.Snapshot.AccountName == "Preview", "Saved reading changed");
                Check(restored.Snapshot.FiveHour.ResetAt == Now.AddHours(1) && restored.UpdatedAt == Now && restored.Error != null, "Restored reading pretends to be fresh");
            }));
            Run("saved usage and throttle never cross account identities", () => WithCache(path => {
                var state = new ProviderState(); state.UseIdentity("account-a"); state.Accept(new UsageSnapshot { Weekly = new UsageWindow { Used = 40 } }, Now);
                state.RetryNotBefore = Now.AddHours(1); UsageCache.Save(path, "account-a", state);
                var restored = new ProviderState(); restored.UseIdentity("account-b");
                Check(!UsageCache.Restore(path, "account-b", restored, Now) && restored.Snapshot == null && restored.CanFetch(Now, true), "Another account's cached usage or throttle leaked");
            }));
            Run("rate-limit retry survives restart even without a successful reading", () => WithCache(path => {
                var state = new ProviderState(); state.UseIdentity("account-a"); state.Fail("Claude is rate limited"); state.RetryNotBefore = Now.AddMinutes(2);
                UsageCache.Save(path, "account-a", state);
                var restored = new ProviderState(); restored.UseIdentity("account-a");
                Check(UsageCache.Restore(path, "account-a", restored, Now), "Retry deadline was discarded");
                Check(restored.Snapshot == null && !restored.CanFetch(Now.AddSeconds(119), true) && restored.CanFetch(Now.AddMinutes(2), true), "Restart bypassed or extended the rate-limit deadline");
            }));
            Run("corrupt or invalid cached percentages do not prevent a fresh request", () => WithCache(path => {
                foreach (string content in new[] { "not json", "{\"Identity\":\"account-a\",\"HasReading\":true,\"UpdatedAt\":1794050000,\"WeeklyUsed\":101}", "{\"Identity\":\"account-a\",\"HasReading\":true,\"UpdatedAt\":1794050000,\"WeeklyUsed\":true}", "{\"Identity\":\"account-a\",\"HasReading\":true,\"UpdatedAt\":1794050000,\"WeeklyUsed\":\"30\"}", "{\"Identity\":\"account-a\",\"RetryAt\":999999999999999}" }) {
                    File.WriteAllText(path, content); var state = new ProviderState(); state.UseIdentity("account-a");
                    Check(!UsageCache.Restore(path, "account-a", state, Now) && state.Snapshot == null && state.CanFetch(Now, true), "Invalid cached data was displayed or blocked requests");
                }
            }));
            Run("fresh usage replaces stale cache without inventing absent windows", () => WithCache(path => {
                var state = new ProviderState(); state.UseIdentity("account-a"); state.Accept(new UsageSnapshot { Weekly = new UsageWindow { Used = 50 } }, Now);
                state.RetryNotBefore = Now.AddHours(1); UsageCache.Save(path, "account-a", state);
                state.Accept(new UsageSnapshot { Weekly = new UsageWindow { Used = 55 } }, Now.AddMinutes(1)); state.RetryNotBefore = DateTimeOffset.MinValue;
                UsageCache.Save(path, "account-a", state);
                var restored = new ProviderState(); restored.UseIdentity("account-a");
                Check(UsageCache.Restore(path, "account-a", restored, Now.AddMinutes(2)) && restored.Snapshot.FiveHour == null && restored.Snapshot.Weekly.Used == 55 && restored.CanFetch(Now.AddMinutes(2), true), "Fresh reading retained obsolete usage or retry state");
            }));
            Run("a malformed Claude response keeps the widget alive", () => WithCache(path => {
                string prior = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                string credentials = Path.Combine(Path.GetDirectoryName(path), ".credentials.json");
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var httpField = typeof(UsageClient).GetField("Http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var oldHttp = (HttpClient)httpField.GetValue(null);
                using (var http = new HttpClient(new MalformedUsageHandler())) {
                    try {
                        File.WriteAllText(credentials, "{\"claudeAiOauth\":{\"accessToken\":\"fixture-token\"}}");
                        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.GetDirectoryName(path)); httpField.SetValue(null, http);
                        using (var form = new WidgetForm(new WidgetSettings())) {
                            var view = typeof(WidgetForm).GetField("claude", flags).GetValue(form);
                            view.GetType().GetField("CachePath").SetValue(view, path);
                            var refresh = (Task)typeof(WidgetForm).GetMethod("RefreshAsync", flags).Invoke(form, new object[] { view, true });
                            var deadline = DateTime.UtcNow.AddSeconds(5);
                            while (!refresh.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
                            Check(refresh.IsCompleted, "Refresh did not finish"); refresh.GetAwaiter().GetResult();
                            var state = (ProviderState)view.GetType().GetField("State").GetValue(view);
                            Check(state.Error == "Usage response could not be read" && state.Snapshot == null, "Malformed response was treated as real usage");
                        }
                    } finally {
                        httpField.SetValue(null, oldHttp); Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", prior); File.Delete(credentials);
                    }
                }
            }));
            Run("three-dot menu offers Add account and Background settings", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    var button = Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "Widget settings");
                    Check(button.ContextMenuStrip != null, "Three dots still opens the background panel directly");
                    var labels = button.ContextMenuStrip.Items.Cast<ToolStripItem>().Select(i => i.Text).ToArray();
                    Check(labels.SequenceEqual(new[] { "Add account", "Background settings" }), "Required menu choices are missing");
                }
            });
            Run("active Pin uses readable bold white text on dark blue", () => {
                foreach (bool pinned in new[] { false, true }) {
                    using (var form = new WidgetForm(new WidgetSettings { Pinned = pinned })) {
                        var pin = Descendants(form).OfType<CheckBox>().Single(c => c.AccessibleName == "Always on top");
                        Check(pin.Font.Name == "Segoe UI" && pin.Font.Bold == pinned, "Pin font does not match its state");
                        Check(pin.ForeColor.ToArgb() == Color.White.ToArgb(), "Pin text is not white");
                        if (pinned) Check(pin.FlatAppearance.CheckedBackColor.GetBrightness() < 0.5, "Active Pin background has poor contrast");
                    }
                }
            });
            Run("Claude keeps the same orange when a reading becomes stale", () => {
                using (var meter = new MeterBar { Size = new Size(100, 16), Value = 50, FillColor = Color.FromArgb(217, 119, 87) })
                using (var fresh = new Bitmap(100, 16))
                using (var stale = new Bitmap(100, 16)) {
                    meter.DrawToBitmap(fresh, new Rectangle(0, 0, 100, 16)); meter.Stale = true;
                    meter.DrawToBitmap(stale, new Rectangle(0, 0, 100, 16));
                    Check(fresh.GetPixel(20, 8).ToArgb() == stale.GetPixel(20, 8).ToArgb(), "Claude's orange darkens for stale readings");
                    Check(meter.AccessibilityObject.Value.Contains("stale"), "Constant color hid stale reading information");
                }
            });
            Run("an extra Codex account updates its own row", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    form.ShowUsage(true, new UsageSnapshot { AccountName = "Default", Weekly = new UsageWindow { Used = 25 } }, Now);
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var attach = typeof(WidgetForm).GetMethod("AttachAccount", flags);
                    Check(attach != null, "No additional account can be attached");
                    var profile = AccountProfile.Create(true); var view = attach.Invoke(form, new object[] { profile });
                    var apply = typeof(WidgetForm).GetMethod("ApplyUsage", flags);
                    Check(apply != null, "Readings still route by provider instead of account");
                    apply.Invoke(form, new object[] { view, new UsageSnapshot { AccountName = "Extra", Weekly = new UsageWindow { Used = 70 } }, Now });
                    var defaults = typeof(WidgetForm).GetField("codex", flags).GetValue(form);
                    var first = (ProviderState)defaults.GetType().GetField("State").GetValue(defaults);
                    var second = (ProviderState)view.GetType().GetField("State").GetValue(view);
                    Check(first.Snapshot.AccountName == "Default" && second.Snapshot.AccountName == "Extra", "Extra account replaced the default account");
                    Check(!Object.ReferenceEquals(defaults.GetType().GetField("ProfileCache").GetValue(defaults), view.GetType().GetField("ProfileCache").GetValue(view)), "Accounts share a profile-name cache");
                    Check((string)view.GetType().GetField("CachePath").GetValue(view) == profile.CachePath, "Extra account shares the default usage cache");
                    var again = attach.Invoke(form, new object[] { profile });
                    Check(Object.ReferenceEquals(view, again), "Saved account was attached twice");
                }
            });
            Run("reported plan appears beside the matching account name", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    var snapshot = new UsageSnapshot { AccountName = "Preview", Weekly = new UsageWindow { Used = 20 } };
                    var plan = typeof(UsageSnapshot).GetField("PlanName");
                    Check(plan != null, "Subscription plan is not read"); plan.SetValue(snapshot, "Pro");
                    form.ShowUsage(false, snapshot, Now);
                    var badge = Descendants(form).OfType<Label>().SingleOrDefault(l => l.AccessibleName == "CLAUDE plan: Pro");
                    Check(badge != null && badge.Text == "Pro", "Account plan badge is missing");
                    var name = Descendants(form).OfType<Label>().Single(l => l.AccessibleName == "Claude account: Preview");
                    Check(badge.Right < name.Left && name.Right <= 317 * form.DeviceDpi / 96, "Plan badge overlaps the account name");
                }
            });
            Run("separate Antigravity quota lanes survive restart without an aggregate", () => WithCache(path => {
                var state = new ProviderState(); state.UseIdentity("agy-account");
                state.Accept(new UsageSnapshot { PlanName = "Google AI Pro", CustomLanes = new List<UsageLane> {
                    new UsageLane { Label = "Gemini 5h", Window = new UsageWindow { Used = 20, ResetAt = Now.AddHours(5) } },
                    new UsageLane { Label = "Claude wk", Window = new UsageWindow { Used = 40 } }
                } }, Now);
                UsageCache.Save(path, "agy-account", state);
                var restored = new ProviderState(); restored.UseIdentity("agy-account");
                Check(UsageCache.Restore(path, "agy-account", restored, Now.AddMinutes(1)), "Antigravity cache could not be restored");
                Check(restored.Snapshot.FiveHour == null && restored.Snapshot.Weekly == null && restored.Snapshot.CustomLanes != null && restored.Snapshot.CustomLanes.Count == 2, "Separate pools were discarded or combined");
                Check(restored.Snapshot.CustomLanes[0].Label == "Gemini 5h" && restored.Snapshot.CustomLanes[0].Window.Used == 20 && restored.Snapshot.CustomLanes[0].Window.ResetAt == Now.AddHours(5), "Saved lane changed");
                Check(restored.Snapshot.PlanName == "Google AI Pro", "Antigravity plan was discarded");
            }));
            Run("invalid cached custom lanes cannot become real readings", () => WithCache(path => {
                File.WriteAllText(path, "{\"Identity\":\"agy-account\",\"HasReading\":true,\"UpdatedAt\":1794050000,\"CustomLanes\":[{\"Label\":\"Gemini 5h\",\"Used\":true}]}");
                var state = new ProviderState(); state.UseIdentity("agy-account");
                Check(!UsageCache.Restore(path, "agy-account", state, Now) && state.Snapshot == null, "Malformed cached lane became usage");
            }));
            Run("cache preserves the full accepted custom-quota bounds", () => WithCache(path => {
                var state = new ProviderState(); state.UseIdentity("agy-account");
                state.Accept(new UsageSnapshot { CustomLanes = Enumerable.Range(0, 64).Select(i => new UsageLane {
                    Label = i.ToString("00") + new string('a', 111), Window = new UsageWindow { Used = 25 }
                }).ToList() }, Now);
                UsageCache.Save(path, "agy-account", state);
                var restored = new ProviderState(); restored.UseIdentity("agy-account");
                Check(UsageCache.Restore(path, "agy-account", restored, Now) && restored.Snapshot.CustomLanes.Count == 64 && restored.Snapshot.CustomLanes.All(lane => lane.Label.Length == 113), "Valid provider quotas were discarded on restart");
            }));
            Run("Antigravity is present automatically with independent quota lanes", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var field = typeof(WidgetForm).GetField("antigravity", flags);
                    Check(field != null, "Antigravity is not an automatic provider");
                    var view = field.GetValue(form); var apply = typeof(WidgetForm).GetMethod("ApplyUsage", flags);
                    apply.Invoke(form, new object[] { view, new UsageSnapshot { AccountName = "Preview AGY", CustomLanes = new List<UsageLane> {
                        new UsageLane { Label = "Gemini 5h", Window = new UsageWindow { Used = 20 } },
                        new UsageLane { Label = "Gemini wk", Window = new UsageWindow { Used = 40 } },
                        new UsageLane { Label = "Claude 5h", Window = new UsageWindow { Used = 60 } },
                        new UsageLane { Label = "Claude wk", Window = new UsageWindow { Used = 80 } }
                    } }, Now });
                    var meters = Descendants(form).OfType<MeterBar>().Where(m => m.AccessibleName.StartsWith("AGY ") && !m.AccessibleName.Contains("five-hour") && !m.AccessibleName.Contains("weekly")).ToArray();
                    Check(meters.Length == 4 && meters.Select(m => m.Value.Value).SequenceEqual(new[] { 80.0, 60.0, 40.0, 20.0 }), "AGY pools do not have independent remaining bars");
                    var list = Descendants(form).OfType<AccountsPanel>().Single();
                    Check(list.ContentHeight > list.Height && form.ClientSize == new Size(340 * form.DeviceDpi / 96, 232 * form.DeviceDpi / 96), "Three providers do not scroll in the fixed window");
                }
            });
            Run("an added Cursor account gets its own monthly row", () => {
                using (var form = new WidgetForm(new WidgetSettings())) {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var view = typeof(WidgetForm).GetMethod("AttachAccount", flags).Invoke(form, new object[] { AccountProfile.Create(false, true) });
                    typeof(WidgetForm).GetMethod("ApplyUsage", flags).Invoke(form, new object[] { view, new UsageSnapshot { AccountName = "Preview Cursor", PlanName = "Pro", CustomLanes = new List<UsageLane> { new UsageLane { Label = "Monthly", Window = new UsageWindow { Used = 25 } } } }, Now });
                    var all = Descendants(form).ToArray();
                    Check(all.OfType<Label>().Any(l => l.Text == "CURSOR") && all.OfType<PictureBox>().Any(p => p.AccessibleName == "CURSOR icon"), "Cursor row is missing");
                    var meters = all.OfType<MeterBar>().Where(m => m.AccessibleName.StartsWith("CURSOR ")).ToArray();
                    Check(meters.Length == 3 && meters.Single(m => m.AccessibleName == "CURSOR Monthly remaining").Value == 75, "Cursor monthly bar is wrong");
                    Check(!all.OfType<Label>().Single(l => l.AccessibleName == "Cursor account: Preview Cursor").Text.StartsWith("Account"), "Cursor account name missing");
                }
            });
            Run("closed Antigravity keeps its last reading as stale instead of blanking", () => WithCache(path => {
                var saved = new ProviderState(); saved.UseIdentity("agy-account");
                saved.Accept(new UsageSnapshot { AccountName = "Preview AGY", PlanName = "Pro", CustomLanes = new List<UsageLane> {
                    new UsageLane { Label = "Gemini 5h", Window = new UsageWindow { Used = 30 } }, new UsageLane { Label = "Claude wk", Window = new UsageWindow { Used = 60 } } } }, Now);
                UsageCache.Save(path, "agy-account", saved);
                using (var form = new WidgetForm(new WidgetSettings())) {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var view = typeof(WidgetForm).GetField("antigravity", flags).GetValue(form);
                    view.GetType().GetField("CachePath").SetValue(view, path);
                    var keep = typeof(WidgetForm).GetMethod("KeepOfflineReading", flags);
                    Check(keep != null, "No offline fallback for Antigravity");
                    keep.Invoke(form, new object[] { view, "Open Antigravity to connect" });
                    var state = (ProviderState)view.GetType().GetField("State").GetValue(view);
                    Check(state.Snapshot != null && state.Snapshot.CustomLanes.Count == 2 && state.Error == "Open Antigravity to connect", "Closed Antigravity blanked its last reading");
                    var meter = Descendants(form).OfType<MeterBar>().Single(m => m.AccessibleName == "AGY Gemini 5h remaining");
                    Check(meter.Value == 70 && meter.Stale, "Offline AGY reading is not shown as stale");
                    var codex = typeof(WidgetForm).GetField("codex", flags).GetValue(form);
                    keep.Invoke(form, new object[] { codex, "Sign in to Codex to connect" });
                    Check(((ProviderState)codex.GetType().GetField("State").GetValue(codex)).Snapshot == null, "Signed-out Claude/Codex kept a reading");
                }
            }));
            AccountChecks.Run(Run, Check);
            AntigravityChecks.Run(Run, Check);
            CursorChecks.Run(Run, Check);
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
