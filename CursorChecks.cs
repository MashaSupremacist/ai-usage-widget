using System;
using System.IO;
using System.Linq;
using System.Text;

namespace UsageWidget
{
    internal static class CursorChecks
    {
        static string Jwt(string payload)
        {
            Func<string, string> b64 = s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return b64("{\"alg\":\"none\"}") + "." + b64(payload) + ".sig";
        }
        static void Rejects<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Invalid Cursor data was accepted");
        }
        public static void Run(Action<string, Action> run, Action<bool, string> check)
        {
            run("Cursor usage summary becomes one monthly remaining lane", () => {
                var snapshot = CursorClient.ParseSummary("{\"billingCycleEnd\":\"2026-12-01T00:00:00.000Z\",\"membershipType\":\"pro\",\"individualUsage\":{\"plan\":{\"enabled\":true,\"used\":750,\"limit\":2000,\"totalPercentUsed\":37.5}}}");
                check(snapshot.FiveHour == null && snapshot.Weekly == null && snapshot.CustomLanes.Count == 1, "Cursor usage is not a single custom lane");
                check(snapshot.CustomLanes[0].Label == "Monthly" && snapshot.CustomLanes[0].Window.Used == 37.5, "Cursor total percent was not used");
                check(snapshot.CustomLanes[0].Window.ResetAt == new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), "Billing cycle end is not the reset time");
                check(snapshot.PlanName == "Pro", "Cursor membership was not mapped");
                var fallback = CursorClient.ParseSummary("{\"membershipType\":\"pro_plus\",\"individualUsage\":{\"plan\":{\"used\":1000,\"limit\":2000}}}");
                check(fallback.CustomLanes[0].Window.Used == 50 && fallback.PlanName == "Pro+", "Cursor used/limit fallback failed");
                var over = CursorClient.ParseSummary("{\"individualUsage\":{\"plan\":{\"totalPercentUsed\":140}}}");
                check(over.CustomLanes[0].Window.Used == 100, "Overspent Cursor plan escaped 0-100");
                Rejects<InvalidDataException>(() => CursorClient.ParseSummary("{\"membershipType\":\"pro\"}"));
                Rejects<InvalidDataException>(() => CursorClient.ParseSummary("{\"individualUsage\":{\"plan\":{\"totalPercentUsed\":\"lots\"}}}"));
            });
            run("Cursor session cookie uses the token's user id", () => {
                string token = Jwt("{\"sub\":\"auth0|user_01ABC\",\"exp\":4102444800}");
                var credentials = CursorClient.FromToken(token, true);
                check(CursorClient.Cookie(token) == "WorkosCursorSessionToken=user_01ABC%3A%3A" + token, "Cursor cookie format changed");
                check(credentials.ExpiresAt == DateTimeOffset.FromUnixTimeSeconds(4102444800) && !String.IsNullOrEmpty(credentials.Identity) && !credentials.Identity.Contains("user_01ABC"), "Cursor identity/expiry is wrong or exposes the id");
                Rejects<UsageException>(() => CursorClient.FromToken(Jwt("{\"sub\":\"auth0|bad id;\"}"), true));
                Rejects<UsageException>(() => CursorClient.FromToken("not-a-jwt", false));
            });
            run("Cursor browser login uses an S256 PKCE challenge on cursor.com", () => {
                check(CursorClient.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE challenge does not match RFC 7636");
                string url = CursorClient.LoginUrl("abc", "11111111-2222-3333-4444-555555555555");
                check(url == "https://cursor.com/loginDeepControl?challenge=abc&uuid=11111111-2222-3333-4444-555555555555&mode=login", "Cursor login URL changed");
            });
            run("Cursor desktop token is read from its state database", () => {
                string directory = Path.Combine(Path.GetTempPath(), "AIUsageWidget-cursor-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try {
                    string path = Path.Combine(directory, "state.vscdb");
                    Sqlite.Execute(path, "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB); INSERT INTO ItemTable VALUES ('cursorAuth/accessToken', 'fixture.token.value'); INSERT INTO ItemTable VALUES ('other', 'x');");
                    check(Sqlite.ReadValue(path, "cursorAuth/accessToken") == "fixture.token.value", "Cursor token was not read");
                    check(Sqlite.ReadValue(path, "missing") == null, "Missing key returned a value");
                } finally { Directory.Delete(directory, true); }
            });
            run("browser-login Cursor token is stored encrypted per account", () => {
                string directory = Path.Combine(Path.GetTempPath(), "AIUsageWidget-cursor-" + Guid.NewGuid().ToString("N"));
                try {
                    string token = Jwt("{\"sub\":\"auth0|user_01XYZ\",\"exp\":4102444800}");
                    CursorClient.SaveToken(directory, token);
                    string raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "cursor-auth.dat")));
                    check(!raw.Contains(token) && !raw.Contains("user_01XYZ"), "Cursor token was saved in plain text");
                    check(CursorClient.ReadCredentials(directory).AccessToken == token, "Saved Cursor token did not round-trip");
                } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            });
            run("Cursor accounts persist in the account list", () => {
                string path = Path.Combine(Path.GetTempPath(), "AIUsageWidget-accounts-" + Guid.NewGuid().ToString("N") + ".json");
                try {
                    var profile = AccountProfile.Create(false, true);
                    AccountStore.Save(new[] { profile, AccountProfile.Create(true) }, path);
                    var loaded = AccountStore.Load(path);
                    check(loaded.Count == 2 && loaded[0].Cursor && !loaded[0].Codex && loaded[0].Id == profile.Id && !loaded[1].Cursor && loaded[1].Codex, "Cursor provider did not round-trip");
                } finally { File.Delete(path); }
            });
        }
    }
}
