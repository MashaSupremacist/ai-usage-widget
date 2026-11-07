using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    // Reads Cursor's monthly plan usage. Auto-detected from the Cursor desktop app's local sign-in, or from a browser sign-in this widget owns.
    // shortcut: cursor.com/api/usage-summary, /api/auth/me and the loginDeepControl/auth/poll login are internal Cursor interfaces; update here if they change.
    internal static class CursorClient
    {
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(18) };
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIUsageWidget.Cursor");
        public static string DesktopDatabase { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb"); } }
        public static bool DesktopInstalled { get { return File.Exists(DesktopDatabase); } }
        static string TokenPath(string directory) { return Path.Combine(directory, "cursor-auth.dat"); }

        public static Credentials ReadCredentials(string configDirectory)
        {
            if (configDirectory == null)
            {
                if (!DesktopInstalled) throw new UsageException("Open Cursor and sign in to connect");
                return FromToken(Sqlite.ReadValue(DesktopDatabase, "cursorAuth/accessToken"), true);
            }
            string path = TokenPath(configDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new UsageException("Sign-in expired · right-click this account to reconnect");
            byte[] plain;
            try { plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { throw new UsageException("Sign-in expired · right-click this account to reconnect"); }
            return FromToken(Encoding.UTF8.GetString(plain), false);
        }
        public static void SaveToken(string directory, string token)
        {
            FromToken(token, false);
            Directory.CreateDirectory(directory);
            string path = TokenPath(directory), temporary = path + ".tmp";
            File.WriteAllBytes(temporary, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
            WidgetSettings.ReplaceFile(temporary, path);
        }
        static Dictionary<string, object> Claims(string token)
        {
            var parts = (token ?? "").Split('.');
            if (parts.Length != 3) return null;
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                return UsageParser.Json(Encoding.UTF8.GetString(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '='))));
            }
            catch (FormatException) { return null; }
            catch (InvalidDataException) { return null; }
        }
        static string UserId(string token)
        {
            string sub = UsageParser.Get(Claims(token), "sub") as string;
            string id = sub == null ? null : sub.Split('|').Last();
            if (String.IsNullOrEmpty(id) || id.Length > 128 || id.Any(c => !(Char.IsLetterOrDigit(c) && c < 128 || c == '.' || c == '_' || c == '-'))) return null;
            return id;
        }
        public static string Cookie(string token) { return "WorkosCursorSessionToken=" + UserId(token) + "%3A%3A" + token; }
        public static Credentials FromToken(string token, bool desktop)
        {
            string id = UserId(token);
            if (id == null) throw new UsageException(desktop ? "Open Cursor and sign in to connect" : "Sign-in expired · right-click this account to reconnect");
            string fingerprint;
            using (var hash = SHA256.Create()) fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes("cursor:" + id)));
            var result = new Credentials { Identity = fingerprint, AccessToken = token, HasExplicitHome = !desktop };
            object exp = UsageParser.Get(Claims(token), "exp");
            if (exp != null)
            {
                double seconds = UsageParser.Number(exp);
                if (seconds > 0 && seconds < 253402300799d) result.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
            }
            return result;
        }
        static string Plan(object value)
        {
            switch ((value as string ?? "").Trim().ToLowerInvariant())
            {
                case "free": return "Free";
                case "free_trial": return "Pro Trial";
                case "pro": return "Pro";
                case "pro_plus": return "Pro+";
                case "ultra": return "Ultra";
                case "business": return "Business";
                case "team": return "Team";
                case "enterprise": return "Enterprise";
                default: return null;
            }
        }
        public static UsageSnapshot ParseSummary(string json)
        {
            var root = UsageParser.Json(json);
            var plan = UsageParser.Object(UsageParser.Object(root, "individualUsage"), "plan");
            if (plan == null) throw UsageParser.BadData();
            double used;
            object total = UsageParser.Get(plan, "totalPercentUsed"), auto = UsageParser.Get(plan, "autoPercentUsed"), api = UsageParser.Get(plan, "apiPercentUsed");
            if (total != null) used = UsageParser.Number(total);
            else if (auto != null && api != null) used = (UsageParser.Number(auto) + UsageParser.Number(api)) / 2;
            else
            {
                object spent = UsageParser.Get(plan, "used"), limit = UsageParser.Get(plan, "limit");
                if (spent == null || limit == null || UsageParser.Number(limit) <= 0) throw UsageParser.BadData();
                used = UsageParser.Number(spent) / UsageParser.Number(limit) * 100;
            }
            var window = new UsageWindow { Used = Math.Max(0, Math.Min(100, used)) };
            DateTimeOffset reset;
            string end = UsageParser.Get(root, "billingCycleEnd") as string;
            if (end != null && DateTimeOffset.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out reset)) window.ResetAt = reset;
            return new UsageSnapshot { PlanName = Plan(UsageParser.Get(root, "membershipType")), CustomLanes = new List<UsageLane> { new UsageLane { Label = "Monthly", Window = window } } };
        }
        static HttpRequestMessage Request(string url, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", Cookie(token)); request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("AIUsageWidget/1.0");
            return request;
        }
        public static async Task<UsageSnapshot> FetchAsync(Credentials credentials, CancellationToken cancellation)
        {
            string renew = credentials.HasExplicitHome ? "Sign-in expired · right-click this account to reconnect" : "Open Cursor to renew sign-in";
            if (credentials.ExpiresAt.HasValue && credentials.ExpiresAt <= DateTimeOffset.UtcNow) throw new UsageException(renew);
            UsageSnapshot result;
            using (var request = Request("https://cursor.com/api/usage-summary", credentials.AccessToken))
            using (var response = await Http.SendAsync(request, cancellation).ConfigureAwait(false))
            {
                int status = (int)response.StatusCode;
                if (status == 401 || status == 403) throw new UsageException(renew);
                if (status == 429) throw new UsageException("Cursor is rate limited") { RetryAt = DateTimeOffset.UtcNow.AddMinutes(2) };
                if (!response.IsSuccessStatusCode) throw new UsageException("Cursor usage is unavailable (HTTP " + status + ")");
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (body.Length > 2097152) throw UsageParser.BadData();
                result = ParseSummary(body);
            }
            var profile = credentials.Profile ?? new ProfileState();
            if (profile.Begin(credentials.Identity, DateTimeOffset.UtcNow))
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var request = Request("https://cursor.com/api/auth/me", credentials.AccessToken))
                {
                    deadline.CancelAfter(2000);
                    try
                    {
                        using (var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false))
                        {
                            string body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
                            string name = body == null || body.Length > 65536 ? null : UsageParser.NameFromCodexProfile(body);
                            if (name != null) profile.Accept(name);
                        }
                    }
                    catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); }
                    catch (HttpRequestException) { }
                    catch (InvalidDataException) { }
                }
            }
            result.AccountName = profile.Name;
            return result;
        }
        static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        public static string Challenge(string verifier) { using (var hash = SHA256.Create()) return Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier))); }
        public static string LoginUrl(string challenge, string uuid) { return "https://cursor.com/loginDeepControl?challenge=" + challenge + "&uuid=" + uuid + "&mode=login"; }
        // Same browser sign-in the Cursor app uses: open cursor.com with a PKCE challenge, then poll until the browser approves it.
        public static async Task<string> BrowserLoginAsync(CancellationToken cancellation)
        {
            var random = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            string verifier = Base64Url(random), uuid = Guid.NewGuid().ToString();
            using (Process.Start(new ProcessStartInfo(LoginUrl(Challenge(verifier), uuid)) { UseShellExecute = true })) { }
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromMinutes(10))
            {
                await Task.Delay(2000, cancellation).ConfigureAwait(false);
                try
                {
                    using (var response = await Http.GetAsync("https://api2.cursor.sh/auth/poll?uuid=" + uuid + "&verifier=" + verifier, cancellation).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound) continue;
                        if (!response.IsSuccessStatusCode) continue;
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        string token = body.Length > 65536 ? null : UsageParser.Get(UsageParser.Json(body), "accessToken") as string;
                        if (UserId(token) != null) return token;
                    }
                }
                catch (HttpRequestException) { }
                catch (InvalidDataException) { }
            }
            throw new UsageException("Sign-in timed out; try again");
        }
    }
    // Windows 10+ ships SQLite as winsqlite3.dll; used read-only for Cursor's VS Code-style state database.
    internal static class Sqlite
    {
        const string Lib = "winsqlite3.dll";
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int bytes, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr statement);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);
        static byte[] Z(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
        static IntPtr Open(string path, int flags)
        {
            IntPtr db;
            if (sqlite3_open_v2(Z(path), out db, flags, IntPtr.Zero) != 0) { sqlite3_close_v2(db); throw new UsageException("Cursor's sign-in data could not be opened"); }
            sqlite3_busy_timeout(db, 500); return db;
        }
        public static string ReadValue(string path, string key)
        {
            IntPtr db = Open(path, 1 /* SQLITE_OPEN_READONLY */), statement = IntPtr.Zero;
            try
            {
                var sql = Z("SELECT value FROM ItemTable WHERE key = ?1 LIMIT 1");
                if (sqlite3_prepare_v2(db, sql, sql.Length, out statement, IntPtr.Zero) != 0) throw new UsageException("Cursor's sign-in data could not be read");
                var text = Encoding.UTF8.GetBytes(key);
                sqlite3_bind_text(statement, 1, text, text.Length, new IntPtr(-1) /* SQLITE_TRANSIENT */);
                int step = sqlite3_step(statement);
                if (step == 101) return null;
                if (step != 100) throw new UsageException("Cursor's sign-in data is busy · retrying");
                int length = sqlite3_column_bytes(statement, 0);
                if (length <= 0 || length > 65536) return null;
                var bytes = new byte[length]; Marshal.Copy(sqlite3_column_blob(statement, 0), bytes, 0, length);
                bool utf16 = length % 2 == 0 && Enumerable.Range(0, length / 2).All(i => bytes[i * 2 + 1] == 0);
                return (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).Trim().Trim('"');
            }
            finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); sqlite3_close_v2(db); }
        }
        internal static void Execute(string path, string sql)
        {
            IntPtr db = Open(path, 6 /* READWRITE | CREATE */);
            try { if (sqlite3_exec(db, Z(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new InvalidOperationException("SQLite statement failed"); }
            finally { sqlite3_close_v2(db); }
        }
    }
}
