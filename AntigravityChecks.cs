using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace UsageWidget
{
    internal static class AntigravityChecks
    {
        static string Summary(string groups) { return "{\"response\":{\"groups\":[" + groups + "]}}"; }
        static string Group(string title, string buckets) { return "{\"displayName\":\"" + title + "\",\"buckets\":[" + buckets + "]}"; }
        static string Bucket(string window, string fraction, string reset = null)
        {
            return "{\"bucketId\":\"fixture-" + window + "\",\"displayName\":\"Fixture limit\",\"window\":\"" + window + "\",\"remainingFraction\":" + fraction + (reset == null ? "" : ",\"resetTime\":" + reset) + "}";
        }
        static string Status(string email, string name = "Fixture User", string plan = "Google AI Pro")
        {
            return "{\"userStatus\":{\"email\":\"" + email + "\",\"name\":\"" + name + "\",\"planStatus\":{\"planInfo\":{\"planName\":\"" + plan + "\"}},\"userTier\":{\"name\":\"Unselected tier\"}}}";
        }
        static AntigravityConnection Connection(int port = 12345, string token = "fixture-csrf")
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "resources", "bin", "language_server.exe");
            return new AntigravityConnection(12345, path, 1, port, token);
        }
        static void Rejects(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Malformed Antigravity data was accepted");
        }
        public static void Run(Action<string, Action> run, Action<bool, string> check)
        {
            run("Antigravity keeps four real quota windows separate", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini Models", Bucket("weekly", "0.25") + "," + Bucket("5h", "0.75")) + "," + Group("Claude and GPT models", Bucket("weekly", "0.5") + "," + Bucket("5h", "1"))));
                check(result.FiveHour == null && result.Weekly == null && result.CustomLanes.Count == 4, "Aggregate or missing quota lane");
                check(result.CustomLanes[0].Label == "Gemini 5h" && result.CustomLanes[0].Window.Used == 25, "Gemini session mapping");
                check(result.CustomLanes[1].Label == "Gemini wk" && result.CustomLanes[1].Window.Used == 75, "Gemini weekly mapping");
                check(result.CustomLanes[2].Label == "Claude 5h" && result.CustomLanes[2].Window.Used == 0 && result.CustomLanes[3].Window.Used == 50, "Claude/GPT pool mapping");
            });
            run("Antigravity weekly-only quota never invents five hours", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini Models", Bucket("weekly", "0", "\"2026-11-14T16:05:00Z\""))));
                check(result.CustomLanes.Count == 1 && result.CustomLanes[0].Label == "Gemini wk" && result.CustomLanes[0].Window.Used == 100, "Missing cadence was fabricated");
                check(result.CustomLanes[0].Window.ResetAt == new DateTimeOffset(2026, 11, 14, 16, 5, 0, TimeSpan.Zero), "Reset time changed");
            });
            run("Antigravity distinct versioned quota groups keep their labels", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini 4 Argon", Bucket("weekly", "0.5"))));
                check(result.CustomLanes.Single().Label == "Gemini 4 Argon wk", "Distinct Gemini pool was merged");
            });
            run("Antigravity accepts the nested measured fraction variant", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini Models", "{\"window\":\"5h\",\"remaining\":{\"remainingFraction\":0.5}}")));
                check(result.CustomLanes.Single().Window.Used == 50, "Nested quota fraction was lost");
            });
            run("Antigravity unknown and disabled quota never becomes zero usage", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini Models", "{\"window\":\"5h\"},{\"window\":\"weekly\",\"disabled\":true,\"remainingFraction\":0}," + Bucket("weekly", "0.5"))));
                check(result.CustomLanes.Count == 1 && result.CustomLanes[0].Window.Used == 50, "Unknown quota was treated as measured");
            });
            run("Antigravity rejects invalid quota fractions", () => {
                foreach (string value in new[] { "-0.1", "1.1", "true", "\"0.5\"", "NaN", "1e309" })
                    Rejects(() => AntigravityClient.ParseQuota(Summary(Group("Gemini Models", Bucket("5h", value)))));
            });
            run("Antigravity rejects malformed reset timestamps", () => {
                foreach (string value in new[] { "1234", "\"tomorrow\"", "\"2026-11-14\"", "\"2026-11-14T16:05:00\"", "\"2026-02-30T16:05:00Z\"" })
                    Rejects(() => AntigravityClient.ParseQuota(Summary(Group("Gemini Models", Bucket("5h", "0.5", value)))));
            });
            run("Antigravity rejects malformed quota shapes", () => {
                foreach (string value in new[] { "{}", "{\"response\":{\"groups\":{}}}", "{\"response\":{\"groups\":[null]}}", Summary(Group("Gemini Models", "true")) })
                    Rejects(() => AntigravityClient.ParseQuota(value));
            });
            run("Antigravity rejects a non-text explicit cadence", () => {
                Rejects(() => AntigravityClient.ParseQuota(Summary(Group("Gemini Models", "{\"window\":true,\"displayName\":\"Weekly limit\",\"remainingFraction\":0.5}"))));
            });
            run("Antigravity uses explicit cadence before display text", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini Models", "{\"displayName\":\"Weekly limit\",\"window\":\"5h\",\"remainingFraction\":0.5}")));
                check(result.CustomLanes.Single().Label == "Gemini 5h", "Display text overrode measured cadence");
            });
            run("Antigravity unknown cadence stays named without a fabricated duration", () => {
                var result = AntigravityClient.ParseQuota(Summary(Group("Gemini Models", "{\"displayName\":\"Daily limit\",\"window\":\"daily\",\"remainingFraction\":0.5}")));
                check(result.CustomLanes.Single().Label == "Gemini Daily limit", "Unknown cadence became a five-hour or weekly quota");
            });
            run("Antigravity identity is a stable normalized account hash", () => {
                var first = Connection(); var second = Connection(token: "another-fixture-csrf");
                AntigravityClient.ApplyUserStatus(first, Status(" First@Example.Invalid "));
                AntigravityClient.ApplyUserStatus(second, Status("first@example.invalid", "Changed fixture name", "Changed fixture plan"));
                check(first.Identity == "antigravity:83cc8c93422746a1f67afbdd5fef66dc5276903a529a8a6c8edbf7744e3701c9" && second.Identity == first.Identity, "Account identity changes with metadata or token");
            });
            run("Antigravity different accounts cannot share a cached identity", () => {
                var first = Connection(); var second = Connection();
                AntigravityClient.ApplyUserStatus(first, Status("first@example.invalid")); AntigravityClient.ApplyUserStatus(second, Status("second@example.invalid"));
                check(first.Identity != second.Identity, "Different accounts share an identity");
            });
            run("Antigravity rejects missing account identity", () => {
                Rejects(() => AntigravityClient.ApplyUserStatus(Connection(), "{\"userStatus\":{\"name\":\"Fixture\"}}"));
                Rejects(() => AntigravityClient.ApplyUserStatus(Connection(), Status("not an email")));
            });
            run("Antigravity preserves readable bounded verified plan metadata", () => {
                var connection = Connection();
                AntigravityClient.ApplyUserStatus(connection, Status("first@example.invalid", " Fixture\\nUser ", "  Google\\tAI Pro  "));
                check(connection.AccountName == "Fixture User" && connection.PlanName == "Google AI Pro", "Profile metadata was guessed or not sanitized");
                AntigravityClient.ApplyUserStatus(connection, Status("first@example.invalid", new string('x', 200), new string('p', 100)));
                check(connection.AccountName.Length <= 160 && connection.PlanName.Length <= 48, "Unbounded metadata reached the UI");
            });
            run("Antigravity quota requests accept only the verified loopback target", () => {
                string endpoint = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
                check(AntigravityClient.IsAllowedEndpoint(new Uri("https://127.0.0.1:12345" + endpoint), 12345), "Valid loopback endpoint rejected");
                foreach (string value in new[] { "https://example.invalid:12345", "https://localhost:12345", "http://127.0.0.1:12345", "https://127.0.0.1:12346", "https://user@127.0.0.1:12345" })
                    check(!AntigravityClient.IsAllowedEndpoint(new Uri(value + endpoint), 12345), "Unverified endpoint accepted");
                check(!AntigravityClient.IsAllowedEndpoint(new Uri("https://127.0.0.1:12345" + endpoint + "?redirect=1"), 12345), "Query-bearing endpoint accepted");
            });
            run("Antigravity TLS exception is request-local and cannot redirect credentials", () => {
                var global = ServicePointManager.ServerCertificateValidationCallback;
                var connection = Connection(); var request = connection.CreateRequest("GetUserStatus");
                check(!request.AllowAutoRedirect && request.Proxy == null && !request.UseDefaultCredentials, "Credentials can escape the local request");
                check(request.Headers["X-Codeium-Csrf-Token"] == "fixture-csrf" && request.ServerCertificateValidationCallback != null, "Missing local authorization or TLS scope");
                check(Object.ReferenceEquals(global, ServicePointManager.ServerCertificateValidationCallback), "Global certificate policy changed");
                using (var certificate = new X509Certificate()) {
                    check(!request.ServerCertificateValidationCallback(WebRequest.Create("https://example.invalid"), certificate, null, SslPolicyErrors.None), "TLS exception leaked to another request");
                }
                Rejects(() => connection.CreateRequest("https://example.invalid"));
            });
            run("Antigravity stale process metadata cannot authorize a TLS connection", () => {
                var request = Connection().CreateRequest("GetUserStatus");
                using (var certificate = new X509Certificate())
                    check(!request.ServerCertificateValidationCallback(request, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors), "A stale or unrelated process can receive credentials");
            });
            run("Antigravity token parser rejects ambiguity and header injection", () => {
                check(AntigravityClient.ReadCsrf("service --csrf_token=fixture-token --https_server_port 0") == "fixture-token", "Equals-form CSRF flag was lost");
                check(AntigravityClient.ReadCsrf("service --csrf_token \"fixture-token\"") == "fixture-token", "Quoted CSRF flag was lost");
                check(AntigravityClient.ReadCsrf("service --csrf_token first --csrf_token second") == null, "Ambiguous token accepted");
                check(AntigravityClient.ReadCsrf("service --csrf_token \"bad\r\nheader\"") == null, "Header injection accepted");
            });
            run("Antigravity discovery requires the installed native executable path", () => {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "resources", "bin", "language_server.exe");
                check(AntigravityClient.IsNativeExecutablePath(path) && !AntigravityClient.IsNativeExecutablePath(path + ".bak") && !AntigravityClient.IsNativeExecutablePath(Path.Combine(Path.GetTempPath(), "language_server.exe")), "An unrelated server can receive quota credentials");
            });
        }
    }
}
