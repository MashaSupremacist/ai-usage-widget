using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Cache;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    internal sealed class AntigravityConnection
    {
        public string Identity, AccountName, PlanName;
        internal readonly int ProcessId, Port;
        internal readonly string ExecutablePath;
        internal readonly long StartedUtcTicks;
        readonly string csrf;
        internal AntigravityConnection(int pid, string path, long started, int port, string token)
        {
            if (pid <= 0 || port < 1 || port > 65535 || String.IsNullOrEmpty(token) || token.Length > 512 || token.Any(c => Char.IsControl(c) || Char.IsWhiteSpace(c))) throw UsageParser.BadData();
            ProcessId = pid; ExecutablePath = path; StartedUtcTicks = started; Port = port; csrf = token;
        }
        internal HttpWebRequest CreateRequest(string method)
        {
            var endpoint = new Uri("https://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/exa.language_server_pb.LanguageServerService/" + method);
            if (!AntigravityClient.IsAllowedEndpoint(endpoint, Port)) throw UsageParser.BadData();
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST"; request.ContentType = "application/json";
            request.Headers.Add("Connect-Protocol-Version", "1"); request.Headers.Add("X-Codeium-Csrf-Token", csrf);
            request.AllowAutoRedirect = false; request.Proxy = null; request.UseDefaultCredentials = false;
            request.KeepAlive = false; request.Timeout = request.ReadWriteTimeout = 3000;
            request.CachePolicy = new HttpRequestCachePolicy(HttpRequestCacheLevel.NoCacheNoStore);
            // The native server uses a self-signed certificate; this exception belongs only to this verified loopback request.
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) =>
                Object.ReferenceEquals(sender, request) && certificate != null && AntigravityClient.IsAllowedEndpoint(request.RequestUri, Port) && AntigravityClient.IsCurrent(this);
            return request;
        }
    }

    internal static class AntigravityClient
    {
        const int MaxBody = 1048576;
        const string StatusMethod = "GetUserStatus", QuotaMethod = "RetrieveUserQuotaSummary";
        const string StatusBody = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}";
        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        internal static bool IsAllowedEndpoint(Uri endpoint, int port)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.Host != "127.0.0.1" || endpoint.Port != port || port < 1 || port > 65535 || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) return false;
            string prefix = "/exa.language_server_pb.LanguageServerService/";
            return endpoint.AbsolutePath == prefix + StatusMethod || endpoint.AbsolutePath == prefix + QuotaMethod;
        }
        internal static bool IsNativeExecutablePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                path = Path.GetFullPath(path);
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "resources", "bin", "language_server.exe");
                if (String.Equals(path, local, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    string root = Environment.GetFolderPath(folder);
                    if (!String.IsNullOrEmpty(root) && String.Equals(path, Path.Combine(root, "Antigravity", "resources", "bin", "language_server.exe"), StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return false;
        }
        internal static string ReadCsrf(string command)
        {
            if (command == null || command.Length > 65536) return null;
            var matches = Regex.Matches(command, @"(?:^|\s)--csrf_token(?:=|\s+)(?:""([^""]*)""|([^\s""]+))(?=\s|$)");
            if (matches.Count != 1) return null;
            string token = matches[0].Groups[1].Success ? matches[0].Groups[1].Value : matches[0].Groups[2].Value;
            return token.Length > 0 && token.Length <= 512 && !token.Any(c => Char.IsControl(c) || Char.IsWhiteSpace(c)) ? token : null;
        }
        static List<int> ListeningPorts(int pid)
        {
            int size = 0;
            uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 3, 0);
            if ((status != 0 && status != 122) || size < 4 || size > MaxBody) return new List<int>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int capacity = size;
                if (GetExtendedTcpTable(buffer, ref size, true, 2, 3, 0) != 0) return new List<int>();
                int count = Marshal.ReadInt32(buffer);
                if (count < 0 || count > (capacity - 4) / 24) return new List<int>();
                var ports = new List<int>();
                for (int index = 0; index < count; index++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + index * 24);
                    int address = Marshal.ReadInt32(row, 4);
                    if (Marshal.ReadInt32(row) != 2 || Marshal.ReadInt32(row, 20) != pid || (address != 0 && address != 0x0100007f)) continue;
                    int rawPort = Marshal.ReadInt32(row, 8);
                    int port = ((rawPort & 255) << 8) | ((rawPort >> 8) & 255);
                    if (port > 0) ports.Add(port);
                }
                return ports.Distinct().OrderBy(value => value).ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        static bool SameProcess(AntigravityConnection connection)
        {
            if (connection == null || !IsNativeExecutablePath(connection.ExecutablePath)) return false;
            try
            {
                using (var process = Process.GetProcessById(connection.ProcessId))
                    return process.StartTime.ToUniversalTime().Ticks == connection.StartedUtcTicks && String.Equals(process.MainModule.FileName, connection.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }
        internal static bool IsCurrent(AntigravityConnection connection)
        {
            return SameProcess(connection) && ListeningPorts(connection.ProcessId).Contains(connection.Port);
        }
        static List<AntigravityConnection> Discover(CancellationToken cancellation)
        {
            var connections = new List<AntigravityConnection>();
            var options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(2) };
            using (var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'"), options))
            using (var results = searcher.Get())
            foreach (ManagementObject source in results)
            using (source)
            {
                cancellation.ThrowIfCancellationRequested();
                string path = source["ExecutablePath"] as string;
                if (!IsNativeExecutablePath(path) || !File.Exists(path)) continue;
                string token = ReadCsrf(source["CommandLine"] as string);
                if (token == null) continue;
                int pid = Convert.ToInt32(source["ProcessId"], CultureInfo.InvariantCulture);
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        long started = process.StartTime.ToUniversalTime().Ticks;
                        foreach (int port in ListeningPorts(pid))
                        {
                            var candidate = new AntigravityConnection(pid, path, started, port, token);
                            if (SameProcess(candidate)) connections.Add(candidate);
                        }
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            return connections.OrderBy(value => value.ProcessId).ThenBy(value => value.Port).ToList();
        }
        public static async Task<AntigravityConnection> ConnectAsync(CancellationToken cancellation)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(8000);
                try
                {
                    var connections = await Task.Run(() => Discover(deadline.Token), deadline.Token).ConfigureAwait(false);
                    UsageException failure = null;
                    foreach (var connection in connections)
                    {
                        try
                        {
                            ApplyUserStatus(connection, await ReadAsync(connection, StatusMethod, StatusBody, deadline.Token).ConfigureAwait(false));
                            return connection;
                        }
                        catch (UsageException error) { failure = error; }
                        catch (InvalidDataException) { failure = new UsageException("Antigravity account is unavailable; check its sign-in"); }
                    }
                    throw failure ?? new UsageException("Open Antigravity to connect");
                }
                catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); throw new UsageException("Antigravity connection timed out"); }
                catch (ManagementException) { throw new UsageException("Antigravity process information is unavailable"); }
                catch (UnauthorizedAccessException) { throw new UsageException("Antigravity process information is unavailable"); }
                catch (COMException) { throw new UsageException("Antigravity process information is unavailable"); }
            }
        }
        public static async Task<UsageSnapshot> FetchAsync(AntigravityConnection connection, CancellationToken cancellation)
        {
            // shortcut: Native Antigravity must stay open; add a separate approved source if closed-app quotas are needed.
            var result = ParseQuota(await ReadAsync(connection, QuotaMethod, "{\"forceRefresh\":false}", cancellation).ConfigureAwait(false));
            if (result.CustomLanes.Count == 0) throw new UsageException("Antigravity did not report usage limits");
            result.AccountName = connection.AccountName; result.PlanName = connection.PlanName;
            return result;
        }
        static UsageException HttpFailure(int status)
        {
            var error = new UsageException(status == 401 || status == 403 ? "Open Antigravity to check its sign-in" : status == 429 ? "Antigravity is rate limited" : "Antigravity usage is unavailable (HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")");
            if (status == 429) error.RetryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            return error;
        }
        static async Task<string> ReadAsync(AntigravityConnection connection, string method, string body, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsCurrent(connection)) throw new UsageException("Open Antigravity to connect");
            var request = connection.CreateRequest(method);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (deadline.Token.Register(request.Abort))
            {
                deadline.CancelAfter(3000);
                try
                {
                    byte[] payload = Encoding.UTF8.GetBytes(body); request.ContentLength = payload.Length;
                    using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                        await stream.WriteAsync(payload, 0, payload.Length, deadline.Token).ConfigureAwait(false);
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        if (!IsAllowedEndpoint(response.ResponseUri, connection.Port)) throw UsageParser.BadData();
                        if (response.StatusCode != HttpStatusCode.OK) throw HttpFailure((int)response.StatusCode);
                        if (response.ContentLength > MaxBody) throw UsageParser.BadData();
                        using (var stream = response.GetResponseStream())
                        using (var bytes = new MemoryStream())
                        {
                            byte[] buffer = new byte[8192]; int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                            {
                                if (bytes.Length + count > MaxBody) throw UsageParser.BadData();
                                bytes.Write(buffer, 0, count);
                            }
                            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
                        }
                    }
                }
                catch (OperationCanceledException) { cancellation.ThrowIfCancellationRequested(); throw new UsageException("Antigravity usage request timed out"); }
                catch (WebException error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) throw new UsageException("Antigravity usage request timed out");
                    using (var response = error.Response as HttpWebResponse)
                        if (response != null) throw HttpFailure((int)response.StatusCode);
                    throw new UsageException("Antigravity local usage service is unavailable");
                }
                catch (IOException) { cancellation.ThrowIfCancellationRequested(); throw new UsageException("Antigravity local usage service is unavailable"); }
                catch (DecoderFallbackException) { throw UsageParser.BadData(); }
            }
        }
        static string Readable(object value, int length)
        {
            string text = value as string;
            if (String.IsNullOrWhiteSpace(text)) return null;
            text = Regex.Replace(text, @"\s+", " ");
            text = new string(text.Where(c => !Char.IsControl(c) && Char.GetUnicodeCategory(c) != UnicodeCategory.Format).Take(length).ToArray()).Trim();
            return text.Length == 0 ? null : text;
        }
        internal static void ApplyUserStatus(AntigravityConnection connection, string json)
        {
            var status = UsageParser.Object(UsageParser.Json(json), "userStatus");
            string email = UsageParser.Get(status, "email") as string;
            if (email == null) throw UsageParser.BadData();
            email = email.Trim().ToLowerInvariant();
            if (email.Length > 320 || !Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$")) throw UsageParser.BadData();
            using (var hash = SHA256.Create()) connection.Identity = "antigravity:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(email))).Replace("-", "").ToLowerInvariant();
            connection.AccountName = Readable(UsageParser.Get(status, "name"), 160) ?? Readable(email, 160);
            var plan = UsageParser.Object(UsageParser.Object(status, "planStatus"), "planInfo");
            connection.PlanName = Readable(UsageParser.Get(plan, "planName"), 48) ?? Readable(UsageParser.Get(UsageParser.Object(status, "userTier"), "name"), 48);
        }
        static object[] ArrayValue(Dictionary<string, object> value, string key, int limit)
        {
            var result = UsageParser.Get(value, key) as object[];
            if (result == null || result.Length > limit) throw UsageParser.BadData();
            return result;
        }
        static DateTimeOffset? ResetTime(object value)
        {
            if (value == null) return null;
            string text = value as string; DateTimeOffset reset;
            if (text == null || !Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out reset)) throw UsageParser.BadData();
            return reset;
        }
        internal static UsageSnapshot ParseQuota(string json)
        {
            var source = UsageParser.Object(UsageParser.Json(json), "response");
            var result = new UsageSnapshot { CustomLanes = new List<UsageLane>() };
            foreach (var item in ArrayValue(source, "groups", 16))
            {
                var group = item as Dictionary<string, object>;
                string title = Readable(UsageParser.Get(group, "displayName"), 64);
                if (group == null || title == null) throw UsageParser.BadData();
                if (String.Equals(title, "Gemini Models", StringComparison.OrdinalIgnoreCase) || String.Equals(title, "Gemini", StringComparison.OrdinalIgnoreCase)) title = "Gemini";
                else if (String.Equals(title, "Claude and GPT models", StringComparison.OrdinalIgnoreCase) || String.Equals(title, "Claude/GPT", StringComparison.OrdinalIgnoreCase)) title = "Claude";
                var lanes = new List<UsageLane>();
                foreach (var raw in ArrayValue(group, "buckets", 32))
                {
                    var bucket = raw as Dictionary<string, object>;
                    if (bucket == null) throw UsageParser.BadData();
                    object disabled = UsageParser.Get(bucket, "disabled");
                    if (disabled != null && !(disabled is bool)) throw UsageParser.BadData();
                    if (disabled is bool && (bool)disabled) continue;
                    object fraction = UsageParser.Get(bucket, "remainingFraction") ?? UsageParser.Get(UsageParser.Object(bucket, "remaining"), "remainingFraction");
                    if (fraction == null) continue;
                    double remaining = UsageParser.Number(fraction);
                    if (remaining < 0 || remaining > 1) throw UsageParser.BadData();
                    object cadence = UsageParser.Get(bucket, "window");
                    if (cadence != null && !(cadence is string)) throw UsageParser.BadData();
                    string window = cadence as string;
                    string suffix;
                    switch ((window ?? "").Trim().ToLowerInvariant())
                    {
                        case "5h": case "session": case "5-hour": case "five-hour": suffix = "5h"; break;
                        case "weekly": suffix = "wk"; break;
                        default: suffix = Readable(UsageParser.Get(bucket, "displayName"), 48) ?? Readable(window, 48); if (suffix == null) throw UsageParser.BadData(); break;
                    }
                    lanes.Add(new UsageLane { Label = title + " " + suffix, Window = new UsageWindow { Used = (1 - remaining) * 100, ResetAt = ResetTime(UsageParser.Get(bucket, "resetTime")) } });
                }
                result.CustomLanes.AddRange(lanes.OrderBy(lane => lane.Label.EndsWith(" 5h", StringComparison.Ordinal) ? 0 : lane.Label.EndsWith(" wk", StringComparison.Ordinal) ? 1 : 2));
                if (result.CustomLanes.Count > 64) throw UsageParser.BadData();
            }
            return result;
        }
    }
}
