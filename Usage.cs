using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace UsageWidget
{
    internal sealed class UsageWindow
    {
        public double Used;
        public DateTimeOffset? ResetAt;
        public bool IsExpired(DateTimeOffset now) { return ResetAt.HasValue && ResetAt.Value <= now; }
    }
    internal sealed class UsageLane { public string Label; public UsageWindow Window; }
    internal sealed class UsageSnapshot { public UsageWindow FiveHour, Weekly; public string AccountName, PlanName; public List<UsageLane> CustomLanes; }
    internal static class UsageParser
    {
        static string FirstName(Dictionary<string, object> source, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = Get(source, key) as string;
                if (!String.IsNullOrWhiteSpace(value)) return new string(value.Where(c => !Char.IsControl(c)).Take(160).ToArray()).Trim();
            }
            return null;
        }
        static Dictionary<string, object> JwtPayload(string token)
        {
            if (String.IsNullOrEmpty(token)) return null;
            var parts = token.Split('.'); if (parts.Length != 3) return null;
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                return Json(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            }
            catch (FormatException) { return null; }
            catch (InvalidDataException) { return null; }
        }
        public static string NameFromJwt(string token) { return FirstName(JwtPayload(token), "name", "email", "preferred_username"); }
        public static string PlanFromJwt(string token)
        {
            var auth = Get(JwtPayload(token), "https://api.openai.com/auth") as Dictionary<string, object>;
            return NormalizePlan(Get(auth, "chatgpt_plan_type"));
        }
        public static string NormalizePlan(object value)
        {
            string plan = value as string;
            if (plan == null || plan.Length > 64) return null;
            switch (plan.Trim().ToLowerInvariant())
            {
                case "pro": case "claude_pro": return "Pro";
                case "plus": return "Plus";
                case "max": case "claude_max": return "Max";
                case "team": case "claude_team": return "Team";
                case "business": return "Business";
                case "enterprise": case "claude_enterprise": return "Enterprise";
                case "free": case "claude_free": return "Free";
                case "go": return "Go";
                case "edu": return "Edu";
                default: return null;
            }
        }
        public static string PlanFromClaudeProfile(string json)
        {
            return NormalizePlan(Get(Object(Json(json), "organization"), "organization_type"));
        }
        public static string NameFromClaudeProfile(string json)
        {
            return FirstName(Object(Json(json), "account"), "display_name", "full_name", "email");
        }
        public static string NameFromCodexProfile(string json)
        {
            return FirstName(Json(json), "name", "email");
        }
        public static Dictionary<string, object> Json(string json)
        {
            try
            {
                var value = new JavaScriptSerializer { MaxJsonLength = 2097152 }.DeserializeObject(json) as Dictionary<string, object>;
                if (value == null) throw BadData();
                return value;
            }
            catch (ArgumentException) { throw BadData(); }
            catch (InvalidOperationException) { throw BadData(); }
        }
        public static InvalidDataException BadData() { return new InvalidDataException("Unexpected usage data"); }
        public static object Get(Dictionary<string, object> value, string key)
        {
            object result; return value != null && value.TryGetValue(key, out result) ? result : null;
        }
        public static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            var item = Get(value, key);
            if (item == null) return null;
            var result = item as Dictionary<string, object>;
            if (result == null) throw BadData();
            return result;
        }
        public static double Number(object value)
        {
            if (!(value is int || value is long || value is double || value is decimal || value is float)) throw BadData();
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(result) || double.IsInfinity(result)) throw BadData();
            return result;
        }
        static double Percent(object value)
        {
            double result = Number(value);
            if (result < 0 || result > 100) throw BadData();
            return result;
        }
        public static UsageSnapshot ParseCodex(string json)
        {
            var root = Json(json);
            var buckets = Object(root, "rateLimitsByLimitId");
            var bucket = Object(buckets, "codex") ?? Object(root, "rateLimits");
            if (bucket == null) throw BadData();
            var result = new UsageSnapshot { PlanName = NormalizePlan(Get(bucket, "planType")) };
            foreach (string key in new[] { "primary", "secondary" })
            {
                var source = Object(bucket, key);
                if (source == null) continue;
                double duration = Number(Get(source, "windowDurationMins"));
                var window = new UsageWindow { Used = Percent(Get(source, "usedPercent")) };
                var reset = Get(source, "resetsAt");
                if (reset != null)
                {
                    double seconds = Number(reset);
                    if (seconds != Math.Truncate(seconds) || seconds < -62135596800d || seconds > 253402300799d) throw BadData();
                    window.ResetAt = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                }
                if (duration == 300) result.FiveHour = window;
                if (duration == 10080) result.Weekly = window;
            }
            return result;
        }
        public static UsageSnapshot ParseClaude(string json)
        {
            var root = Json(json);
            if (!root.ContainsKey("five_hour") && !root.ContainsKey("seven_day")) throw BadData();
            return new UsageSnapshot { FiveHour = ClaudeWindow(Object(root, "five_hour")), Weekly = ClaudeWindow(Object(root, "seven_day")) };
        }
        static UsageWindow ClaudeWindow(Dictionary<string, object> source)
        {
            if (source == null) return null;
            var result = new UsageWindow { Used = Percent(Get(source, "utilization")) };
            var reset = Get(source, "resets_at");
            if (reset != null)
            {
                DateTimeOffset timestamp;
                if (!(reset is string) || !DateTimeOffset.TryParse((string)reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp)) throw BadData();
                result.ResetAt = timestamp;
            }
            return result;
        }
    }
    internal sealed class ProviderState
    {
        public DateTimeOffset RetryNotBefore, NextAttempt;
        public bool CanFetch(DateTimeOffset now, bool manual) { return now >= RetryNotBefore && (manual || now >= NextAttempt); }
        public UsageSnapshot Snapshot;
        public DateTimeOffset? UpdatedAt;
        public string Error;
        string identity;
        public bool UseIdentity(string value)
        {
            if (identity == value) return false;
            identity = value; Snapshot = null; UpdatedAt = null; Error = null;
            RetryNotBefore = NextAttempt = DateTimeOffset.MinValue; return true;
        }
        public void Accept(UsageSnapshot snapshot, DateTimeOffset now) { Snapshot = snapshot; UpdatedAt = now; Error = null; }
        public void Fail(string error) { Error = error; }
    }
    internal sealed class Credentials
    {
        public string Identity, AccessToken, DisplayName, ConfigDirectory, PlanName;
        public ProfileState Profile = null;
        public bool HasExplicitHome;
        public DateTimeOffset? ExpiresAt;
    }
    internal sealed class ProfileState
    {
        string identity;
        public string Name, PlanName;
        public DateTimeOffset NextAttempt;
        public bool Begin(string value, DateTimeOffset now)
        {
            if (identity != value) { identity = value; Name = PlanName = null; NextAttempt = DateTimeOffset.MinValue; }
            if (now < NextAttempt) return false;
            NextAttempt = now.AddSeconds(60); return true;
        }
        public void Accept(string name) { Name = name; NextAttempt = DateTimeOffset.MinValue; }
    }
    internal sealed class UsageException : Exception
    {
        public DateTimeOffset? RetryAt;
        public UsageException(string message) : base(message) { }
    }
    internal static class UsageClient
    {
        static readonly HttpClient Http = CreateHttp();
        static readonly ProfileState CodexProfile = new ProfileState(), ClaudeProfile = new ProfileState();
        static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
        }
        public static Credentials ReadCredentials(bool codex, string configDirectory = null)
        {
            string root = configDirectory ?? Environment.GetEnvironmentVariable(codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR");
            if (String.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), codex ? ".codex" : ".claude");
            root = Path.GetFullPath(root);
            string path = Path.Combine(root, codex ? "auth.json" : ".credentials.json");
            if (!File.Exists(path)) throw new UsageException(codex ? "Sign in to Codex to connect" : "Sign in to Claude Code to connect");
            Dictionary<string, object> auth;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 1048576) throw new UsageException("Sign-in file could not be read");
                using (var reader = new StreamReader(stream)) auth = UsageParser.Json(reader.ReadToEnd());
            }
            var tokens = UsageParser.Object(auth, codex ? "tokens" : "claudeAiOauth");
            string access = UsageParser.Get(tokens, codex ? "access_token" : "accessToken") as string;
            if (String.IsNullOrWhiteSpace(access)) throw new UsageException(codex ? "Sign in to Codex to connect" : "Sign in to Claude Code to connect");
            string fingerprint;
            using (var hash = SHA256.Create()) fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(access)));
            var result = new Credentials { Identity = fingerprint, AccessToken = access, ConfigDirectory = root, HasExplicitHome = configDirectory != null, DisplayName = codex ? UsageParser.NameFromJwt(UsageParser.Get(tokens, "id_token") as string) : null,
                PlanName = codex ? UsageParser.PlanFromJwt(UsageParser.Get(tokens, "id_token") as string) : UsageParser.NormalizePlan(UsageParser.Get(tokens, "subscriptionType")) };
            object expiry = UsageParser.Get(tokens, "expiresAt");
            if (!codex && expiry != null)
            {
                double milliseconds = UsageParser.Number(expiry);
                if (milliseconds < 0 || milliseconds > 253402300799000d) throw UsageParser.BadData();
                result.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
            }
            return result;
        }
        static readonly Dictionary<string, DateTimeOffset> RenewNotBefore = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        internal static Action<ProcessStartInfo> RunRenewal = RunHidden;
        // Claude's access token lasts about 8 hours. Claude Code renews it (and rewrites its own sign-in file) for
        // `claude auth status`, which makes no model request. The widget never writes that file itself.
        public static Credentials ReadClaudeCredentials(string configDirectory)
        {
            var credentials = ReadCredentials(false, configDirectory);
            var now = DateTimeOffset.UtcNow;
            if (!credentials.ExpiresAt.HasValue || credentials.ExpiresAt > now.AddMinutes(5)) return credentials;
            lock (RenewNotBefore)
            {
                DateTimeOffset notBefore;
                if (RenewNotBefore.TryGetValue(credentials.ConfigDirectory, out notBefore) && now < notBefore) return credentials;
                RenewNotBefore[credentials.ConfigDirectory] = now.AddMinutes(5);
            }
            string claude;
            try { claude = AccountLogin.ClaudePath(); }
            catch (UsageException) { return credentials; }
            var info = new ProcessStartInfo(claude, "auth status") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (credentials.HasExplicitHome) ApplyAccountEnvironment(info, false, credentials.ConfigDirectory);
            else { info.WorkingDirectory = credentials.ConfigDirectory; info.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = credentials.ConfigDirectory; }
            // ponytail: undocumented Claude Code behaviour (2.1.3xx). Under the default "cli" entrypoint `auth status` only
            // prints the cached login; under any other entrypoint it validates the login and renews an expired token.
            // If a future version stops renewing, the row falls back to "Open Claude Code to renew sign-in".
            info.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"] = "ai-usage-widget";
            try { RunRenewal(info); }
            catch (System.ComponentModel.Win32Exception) { return credentials; }
            catch (InvalidOperationException) { return credentials; }
            return ReadCredentials(false, configDirectory);
        }
        static void RunHidden(ProcessStartInfo info)
        {
            using (var process = Process.Start(info))
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
            }
        }
        public static async Task<UsageSnapshot> FetchAsync(bool codex, Credentials credentials, CancellationToken cancellation)
        {
            var result = await (codex ? FetchCodexAsync(credentials, cancellation) : FetchClaudeAsync(credentials, cancellation)).ConfigureAwait(false);
            result.AccountName = await AccountNameAsync(codex, credentials, cancellation).ConfigureAwait(false);
            result.PlanName = result.PlanName ?? credentials.PlanName;
            return result;
        }
        static async Task<string> AccountNameAsync(bool codex, Credentials credentials, CancellationToken cancellation)
        {
            var profile = credentials.Profile ?? (codex ? CodexProfile : ClaudeProfile);
            string fallback = codex ? credentials.DisplayName : null;
            bool fetch = profile.Begin(credentials.Identity, DateTimeOffset.UtcNow);
            credentials.PlanName = profile.PlanName ?? credentials.PlanName;
            if (!fetch) return profile.Name ?? fallback;
            // shortcut: These profile endpoints are internal; update the readers if their response contracts change.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var request = new HttpRequestMessage(HttpMethod.Get, codex ? "https://chatgpt.com/backend-api/me" : "https://api.anthropic.com/api/oauth/profile"))
            {
                deadline.CancelAfter(2000);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                if (!codex) request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                request.Headers.UserAgent.ParseAdd("AIUsageWidget/1.0");
                try
                {
                    using (var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var retry = response.Headers.RetryAfter;
                            if (retry != null)
                            {
                                var retryAt = retry.Date ?? (retry.Delta.HasValue ? DateTimeOffset.UtcNow.Add(retry.Delta.Value) : profile.NextAttempt);
                                if (retryAt > profile.NextAttempt) profile.NextAttempt = retryAt;
                            }
                        }
                        else
                        {
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (body.Length > 2097152) throw UsageParser.BadData();
                            string name = codex ? UsageParser.NameFromCodexProfile(body) : UsageParser.NameFromClaudeProfile(body);
                            if (name != null) profile.Accept(name);
                            if (!codex)
                            {
                                string plan = UsageParser.PlanFromClaudeProfile(body);
                                if (plan != null) profile.PlanName = credentials.PlanName = plan;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); }
                catch (HttpRequestException) { }
                catch (InvalidDataException) { }
            }
            return profile.Name ?? fallback;
        }
        static async Task<UsageSnapshot> FetchClaudeAsync(Credentials credentials, CancellationToken cancellation)
        {
            if (credentials.ExpiresAt.HasValue && credentials.ExpiresAt <= DateTimeOffset.UtcNow) throw new UsageException("Open Claude Code to renew sign-in");
            // shortcut: Claude's usage endpoint is internal; update this reader if its response contract changes.
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                request.Headers.UserAgent.ParseAdd("AIUsageWidget/1.0");
                using (var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        int status = (int)response.StatusCode;
                        var error = new UsageException(status == 401 || status == 403 ? "Open Claude Code to renew sign-in" : status == 429 ? "Claude is rate limited" : "Claude usage is unavailable (HTTP " + status + ")");
                        var retry = response.Headers.RetryAfter;
                        if (retry != null) error.RetryAt = retry.Date ?? (retry.Delta.HasValue ? DateTimeOffset.UtcNow.Add(retry.Delta.Value) : (DateTimeOffset?)null);
                        if (status == 429 && !error.RetryAt.HasValue) error.RetryAt = DateTimeOffset.UtcNow.AddMinutes(2);
                        throw error;
                    }
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Length > 2097152) throw UsageParser.BadData();
                    return UsageParser.ParseClaude(body);
                }
            }
        }
        internal static string CodexPath()
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            if (File.Exists(installed)) return installed;
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), "codex.exe"); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
            throw new UsageException("Install or open the Codex app to connect");
        }
        static async Task<Dictionary<string, object>> ReadReplyAsync(Process process, int id, CancellationToken cancellation)
        {
            Task stopped = Task.Delay(Timeout.Infinite, cancellation);
            for (int lines = 0; lines < 200; lines++)
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, stopped).ConfigureAwait(false) != read) cancellation.ThrowIfCancellationRequested();
                string line = await read.ConfigureAwait(false);
                if (line == null) throw new UsageException("Codex closed before returning usage");
                var message = UsageParser.Json(line);
                var responseId = UsageParser.Get(message, "id");
                if (responseId == null || UsageParser.Number(responseId) != id) continue;
                if (UsageParser.Get(message, "error") != null) throw new UsageException("Codex could not read usage; check its sign-in");
                return UsageParser.Object(message, "result");
            }
            throw new UsageException("Codex returned too many notifications");
        }
        internal static void ApplyAccountEnvironment(ProcessStartInfo info, bool codex, string directory)
        {
            foreach (string key in info.EnvironmentVariables.Keys.Cast<string>().ToArray())
            {
                string upper = key.ToUpperInvariant();
                if (upper.StartsWith("OPENAI_", StringComparison.Ordinal) || upper.StartsWith("ANTHROPIC_", StringComparison.Ordinal) ||
                    upper.StartsWith("CLAUDE_CODE_OAUTH_", StringComparison.Ordinal) || upper.StartsWith("CLAUDE_CODE_API_KEY_", StringComparison.Ordinal) ||
                    upper.StartsWith("CLAUDE_CODE_CUSTOM_OAUTH_", StringComparison.Ordinal) || upper.StartsWith("CLAUDE_CODE_USE_", StringComparison.Ordinal) ||
                    upper.StartsWith("CODEX_AUTH_", StringComparison.Ordinal) || upper == "CODEX_API_KEY" || upper == "CODEX_ACCESS_TOKEN" ||
                    upper == "CHATGPT_ACCESS_TOKEN" || upper == "CHATGPT_BASE_URL" || upper == "CODEX_CHATGPT_BASE_URL") info.EnvironmentVariables.Remove(key);
            }
            info.WorkingDirectory = directory;
            info.EnvironmentVariables[codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] = directory;
            if (codex) info.EnvironmentVariables["CODEX_SQLITE_HOME"] = directory;
        }
        internal static ProcessStartInfo CreateCodexStartInfo(Credentials credentials)
        {
            var info = new ProcessStartInfo(CodexPath(), "app-server --stdio") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (!String.IsNullOrWhiteSpace(credentials.ConfigDirectory))
            {
                info.EnvironmentVariables["CODEX_HOME"] = credentials.ConfigDirectory;
                if (credentials.HasExplicitHome) ApplyAccountEnvironment(info, true, credentials.ConfigDirectory);
            }
            return info;
        }
        static async Task<UsageSnapshot> FetchCodexAsync(Credentials credentials, CancellationToken cancellation)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var process = new Process())
            {
                deadline.CancelAfter(20000);
                process.StartInfo = CreateCodexStartInfo(credentials);
                bool started = false;
                try
                {
                    process.Start(); started = true;
                    process.ErrorDataReceived += delegate { };
                    process.BeginErrorReadLine();
                    process.StandardInput.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai_usage_widget\",\"version\":\"1.0\"},\"capabilities\":{\"experimentalApi\":false}}}");
                    await ReadReplyAsync(process, 1, deadline.Token).ConfigureAwait(false);
                    process.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                    process.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
                    var reply = await ReadReplyAsync(process, 2, deadline.Token).ConfigureAwait(false);
                    return UsageParser.ParseCodex(new JavaScriptSerializer().Serialize(reply));
                }
                catch (OperationCanceledException)
                {
                    if (cancellation.IsCancellationRequested) throw;
                    throw new UsageException("Codex usage request timed out");
                }
                finally
                {
                    deadline.Cancel();
                    if (started)
                    {
                        try { process.StandardInput.Close(); }
                        catch (IOException) { }
                        catch (InvalidOperationException) { }
                        try
                        {
                            if (!process.WaitForExit(500)) process.Kill();
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
            }
        }
    }
}
