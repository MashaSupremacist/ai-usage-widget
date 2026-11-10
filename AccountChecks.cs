using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace UsageWidget
{
    internal static class AccountChecks
    {
        const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
        static void WithDirectory(Action<string> test)
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "account-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); }
            finally { Directory.Delete(directory, true); }
        }
        sealed class ProfileHandler : HttpMessageHandler
        {
            public int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"account\":{\"display_name\":\"" + request.Headers.Authorization.Parameter + "\"}}") });
            }
        }
        static void Rejects(Action action, Action<bool, string> check)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed account metadata was accepted");
        }
        static Task FakeSignIn(string directory, string command, CancellationToken cancellation, int deadline)
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(executable, "-NoLogo -NoProfile -NonInteractive -Command \"" + command + "\"") {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            return (Task)typeof(AccountLogin).GetMethod("SignInCoreAsync", HiddenStatic).Invoke(null, new object[] { true, directory, info, cancellation, deadline });
        }
        public static void Run(Action<string, Action> run, Action<bool, string> check)
        {
            run("subscription metadata is explicit and unknown plans stay hidden", () => WithDirectory(directory => {
                var normalize = typeof(UsageParser).GetMethod("NormalizePlan");
                var plan = typeof(UsageSnapshot).GetField("PlanName"); var credentialPlan = typeof(Credentials).GetField("PlanName");
                check(normalize != null && plan != null && credentialPlan != null, "Per-account subscription metadata is missing");
                string[] identifiers = { "pro", "plus", "max", "team", "business", "enterprise", "free", "go", "edu", "claude_pro", "claude_max" };
                string[] labels = { "Pro", "Plus", "Max", "Team", "Business", "Enterprise", "Free", "Go", "Edu", "Pro", "Max" };
                for (int index = 0; index < identifiers.Length; index++) check((string)normalize.Invoke(null, new object[] { identifiers[index] }) == labels[index], "Known plan identifier was not normalized");
                foreach (object unknown in new object[] { null, "unknown", "new_plan", "200", 200, true, "max_20x" }) check(normalize.Invoke(null, new[] { unknown }) == null, "Unknown metadata invented a plan");
                var current = UsageParser.ParseCodex("{\"rateLimits\":{\"planType\":\"free\"},\"rateLimitsByLimitId\":{\"codex\":{\"planType\":\"plus\"}}}");
                check((string)plan.GetValue(current) == "Plus", "Plan came from the wrong usage bucket");
                check(plan.GetValue(UsageParser.ParseCodex("{\"rateLimits\":{\"planType\":800}}")) == null, "Numeric usage metadata became a subscription");
                File.WriteAllText(Path.Combine(directory, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"fixture\",\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"}}");
                check((string)credentialPlan.GetValue(UsageClient.ReadCredentials(false, directory)) == "Max", "Claude subscriptionType was ignored");
                string claims = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"https://api.openai.com/auth\":{\"chatgpt_plan_type\":\"pro\"}}"));
                File.WriteAllText(Path.Combine(directory, "auth.json"), "{\"tokens\":{\"access_token\":\"fixture\",\"id_token\":\"x." + claims + ".x\"}}");
                check((string)credentialPlan.GetValue(UsageClient.ReadCredentials(true, directory)) == "Pro", "Codex signed-in claim fallback was ignored");
                check(UsageParser.PlanFromClaudeProfile("{\"organization\":{\"organization_type\":\"claude_pro\"}}") == "Pro", "Current Claude organization plan was ignored");
                check(UsageParser.PlanFromClaudeProfile("{\"organization\":{\"organization_type\":\"new_plan\",\"rate_limit_tier\":200}}") == null, "Claude quota tier invented a plan");
            }));
            run("expired Claude sign-in is renewed by Claude Code itself, throttled", () => WithDirectory(directory => {
                string file = Path.Combine(directory, ".credentials.json");
                Func<string, double, string> credentials = (token, hours) => "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"expiresAt\":" + DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeMilliseconds() + "}}";
                var calls = new List<ProcessStartInfo>();
                try { AccountLogin.ClaudePath(); }
                catch (UsageException) { Console.WriteLine("  (Claude Code not installed; renewal check skipped)"); return; }
                var original = UsageClient.RunRenewal;
                UsageClient.RunRenewal = info => { calls.Add(info); File.WriteAllText(file, credentials("renewed", 8)); };
                // Run as if launched from Explorer: a Claude Code host shell would leak its own entrypoint into the child.
                string inheritedEntry = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT");
                Environment.SetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT", null);
                try
                {
                    File.WriteAllText(file, credentials("fresh", 2));
                    check(UsageClient.ReadClaudeCredentials(directory).AccessToken == "fresh" && calls.Count == 0, "Valid sign-in started a renewal");
                    File.WriteAllText(file, credentials("expired", -1));
                    var renewed = UsageClient.ReadClaudeCredentials(directory);
                    check(calls.Count == 1 && renewed.AccessToken == "renewed", "Expired Claude sign-in was not renewed");
                    check(calls[0].Arguments == "auth status" && Path.GetFileName(calls[0].FileName) == "claude.exe" && calls[0].CreateNoWindow && !calls[0].UseShellExecute, "Renewal is not a hidden claude auth status run");
                    check(String.Equals(calls[0].EnvironmentVariables["CLAUDE_CONFIG_DIR"], Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase), "Renewal targeted the wrong Claude profile");
                    // With the default "cli" entrypoint, `auth status` only reports the cached login and never renews it.
                    string entry = calls[0].EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"];
                    check(!String.IsNullOrEmpty(entry) && entry != "cli", "Renewal runs with the cli entrypoint, which does not renew");
                    File.WriteAllText(file, credentials("expired-again", -1));
                    check(UsageClient.ReadClaudeCredentials(directory).AccessToken == "expired-again" && calls.Count == 1, "Failed renewals are retried without a cooldown");
                }
                finally { UsageClient.RunRenewal = original; Environment.SetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT", inheritedEntry); }
            }));
            run("extra account metadata has an isolated validated profile model", () => {
                var model = typeof(UsageClient).Assembly.GetType("UsageWidget.AccountProfile");
                check(model != null, "Additional account profile model is missing");
                var create = model.GetMethod("Create", new[] { typeof(bool) });
                var first = create.Invoke(null, new object[] { true }); var second = create.Invoke(null, new object[] { false });
                string firstId = (string)model.GetField("Id").GetValue(first), secondId = (string)model.GetField("Id").GetValue(second);
                check(firstId.Length == 32 && firstId != secondId, "New accounts share an identity");
                check((string)model.GetProperty("ConfigDirectory").GetValue(first, null) == Path.Combine(WidgetSettings.Folder, "accounts", firstId), "Profile home escaped the app's accounts folder");
                check((string)model.GetProperty("CachePath").GetValue(first, null) != (string)model.GetProperty("CachePath").GetValue(second, null), "Different accounts share a usage cache");
            });
            run("account metadata is bounded, validates paths and preserves corruption", () => WithDirectory(directory => {
                string path = Path.Combine(directory, "accounts.json");
                var first = new AccountProfile("0123456789abcdef0123456789abcdef", true);
                var second = new AccountProfile("fedcba9876543210fedcba9876543210", false);
                AccountStore.Save(new[] { first, second }, path);
                var loaded = AccountStore.Load(path);
                check(loaded.Count == 2 && loaded[0].Id == first.Id && loaded[0].Codex && !loaded[1].Codex, "Metadata roundtrip lost profile/provider");
                string serialized = File.ReadAllText(path);
                check(!serialized.Contains("ConfigDirectory") && !serialized.Contains("CachePath") && !serialized.Contains("token"), "Metadata contains more than opaque IDs/providers");
                AccountStore.Save(new[] { second }, path);
                check(AccountStore.Load(path).Count == 1 && Directory.GetFiles(directory).Length == 1, "Atomic replacement left obsolete metadata or temporary files");
                Rejects(() => AccountStore.Save(new[] { first, first }, path), check);
                var tooMany = new List<AccountProfile>();
                for (int index = 0; index < 129; index++) tooMany.Add(AccountProfile.Create(true));
                Rejects(() => AccountStore.Save(tooMany, path), check);
                foreach (string id in new[] { "../outside", new string('A', 32), new string('g', 32), "", new string('a', 33) }) Rejects(() => new AccountProfile(id, true), check);
                foreach (string corrupt in new[] { "not json", "{}", "{\"accounts\":null}", "{\"accounts\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"provider\":true}]}", "{\"accounts\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"provider\":\"unknown\"}]}", "{\"accounts\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"provider\":\"codex\"},{\"id\":\"0123456789abcdef0123456789abcdef\",\"provider\":\"claude\"}]}", new string(' ', 65537) }) {
                    File.WriteAllText(path, corrupt);
                    Rejects(() => AccountStore.Load(path), check);
                    Rejects(() => AccountStore.Save(new[] { first }, path), check);
                    check(File.ReadAllText(path) == corrupt, "Save overwrote corrupt existing metadata");
                }
                Rejects(() => AccountStore.Load(directory), check);
                check(AccountStore.Load(Path.Combine(directory, "missing.json")).Count == 0, "First launch could not load an absent account list");
            }));
            run("native login uses isolated browser auth without inherited auth overrides", () => {
                var login = typeof(UsageClient).Assembly.GetType("UsageWidget.AccountLogin");
                check(login != null, "Additional account native login is missing");
                var create = login.GetMethod("CreateStartInfo");
                string[] keys = { "OPENAI_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CODEX_SQLITE_HOME" };
                var previous = new Dictionary<string, string>();
                try {
                    foreach (string key in keys) { previous[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, "account-fixture-override"); }
                    foreach (bool codex in new[] { false, true }) {
                        var profile = AccountProfile.Create(codex);
                        var info = (ProcessStartInfo)create.Invoke(null, new object[] { profile });
                        check(Path.GetExtension(info.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(info.FileName) == (codex ? "codex.exe" : "claude.exe"), "Login uses a shell wrapper or wrong provider");
                        check(info.Arguments == (codex ? "login" : "auth login --claudeai"), "Login can start a model request or wrong auth mode");
                        check(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardOutput && info.RedirectStandardError && info.RedirectStandardInput, "Login output can escape its hidden owned process");
                        check(info.WorkingDirectory == profile.ConfigDirectory && info.EnvironmentVariables[codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] == profile.ConfigDirectory, "Native sign-in can overwrite an original profile");
                        foreach (string key in keys) {
                            if (key != "CODEX_SQLITE_HOME") check(info.EnvironmentVariables[key] == null, "Inherited auth override bypasses added profile");
                            check(Environment.GetEnvironmentVariable(key) == "account-fixture-override", "Login changed parent environment");
                        }
                        if (codex) check(info.EnvironmentVariables["CODEX_SQLITE_HOME"] == profile.ConfigDirectory, "Codex login shares SQLite state");
                    }
                } finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
            });
            run("failed and cancelled native children clean only newly incomplete homes", () => WithDirectory(directory => {
                foreach (bool existing in new[] { false, true }) {
                    string own = Path.Combine(directory, existing ? "existing" : "new");
                    if (existing) { Directory.CreateDirectory(own); File.WriteAllText(Path.Combine(own, "preserve.txt"), "existing profile"); }
                    bool failed = false;
                    try { FakeSignIn(own, "exit 7", CancellationToken.None, 5000).GetAwaiter().GetResult(); } catch (UsageException) { failed = true; }
                    check(failed, "Failed native process was accepted as a login");
                    check(existing ? File.ReadAllText(Path.Combine(own, "preserve.txt")) == "existing profile" : !Directory.Exists(own), "Failed sign-in deleted an existing home or retained an incomplete new one");
                }
                string cancelled = Path.Combine(directory, "cancelled");
                using (var stop = new CancellationTokenSource()) {
                    var login = FakeSignIn(cancelled, "[IO.File]::WriteAllText('pid.tmp', $PID.ToString()); Move-Item -LiteralPath 'pid.tmp' -Destination 'pid.txt'; Start-Sleep -Seconds 30", stop.Token, 10000);
                    string marker = Path.Combine(cancelled, "pid.txt"); var wait = Stopwatch.StartNew(); int processId = 0; bool rejected = false;
                    try {
                        while (!File.Exists(marker) && !login.IsCompleted && wait.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                        check(File.Exists(marker), "Fixture child did not start"); processId = Int32.Parse(File.ReadAllText(marker).Trim());
                    } finally {
                        stop.Cancel();
                        try { login.GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                    }
                    check(rejected && !Directory.Exists(cancelled), "Cancellation left an incomplete home");
                    bool running;
                    try { using (var child = Process.GetProcessById(processId)) running = !child.HasExited; } catch (ArgumentException) { running = false; }
                    check(!running, "Profile was deleted before its owned process exited");
                }
                string timedOut = Path.Combine(directory, "timeout"); bool expired = false; var elapsed = Stopwatch.StartNew();
                try { FakeSignIn(timedOut, "Start-Sleep -Seconds 30", CancellationToken.None, 200).GetAwaiter().GetResult(); } catch (UsageException) { expired = true; }
                check(expired && elapsed.ElapsedMilliseconds < 5000 && !Directory.Exists(timedOut), "Login deadline was unbounded or left a new home");
                string completed = Path.Combine(directory, "completed"); bool missing = false;
                try { FakeSignIn(completed, "exit 0", CancellationToken.None, 5000).GetAwaiter().GetResult(); } catch (UsageException) { missing = true; }
                check(missing && !Directory.Exists(completed), "Zero exit without account credentials was accepted");
                string saved = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"fixture-account-token\"}}"));
                foreach (int exitCode in new[] { 0, 7 }) {
                    string complete = Path.Combine(directory, "credential-result-" + exitCode); bool failed = false;
                    string command = "[Console]::Out.Write(('x' * 80000)); [Console]::Error.Write(('y' * 80000)); [IO.File]::WriteAllBytes('auth.json', [Convert]::FromBase64String('" + saved + "')); exit " + exitCode;
                    try { FakeSignIn(complete, command, CancellationToken.None, 5000).GetAwaiter().GetResult(); } catch (UsageException) { failed = true; }
                    check(failed == (exitCode != 0), "Native result was ignored or output pipes blocked");
                    check(UsageClient.ReadCredentials(true, complete).AccessToken == "fixture-account-token", "Complete new credentials were deleted on a late native failure");
                    check(File.ReadAllText(Path.Combine(complete, "config.toml")).Contains("cli_auth_credentials_store = \"file\""), "Codex extra profile can store credentials outside its own file");
                }
            }));
            run("explicit credential roots override inherited homes without changing parent environment", () => WithDirectory(directory => {
                var method = typeof(UsageClient).GetMethod("ReadCredentials", new[] { typeof(bool), typeof(string) });
                check(method != null, "Per-account credential reader is missing");
                foreach (bool codex in new[] { false, true }) {
                    string root = Path.Combine(directory, codex ? "codex" : "claude"); Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, codex ? "auth.json" : ".credentials.json"), codex ? "{\"tokens\":{\"access_token\":\"codex-fixture\"}}" : "{\"claudeAiOauth\":{\"accessToken\":\"claude-fixture\"}}");
                    string variable = codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR", before = Environment.GetEnvironmentVariable(variable), inherited = Path.Combine(directory, "inherited");
                    try {
                        Environment.SetEnvironmentVariable(variable, inherited);
                        var credentials = (Credentials)method.Invoke(null, new object[] { codex, root });
                        check(credentials.AccessToken == (codex ? "codex-fixture" : "claude-fixture"), "Another source supplied the token");
                        var field = typeof(Credentials).GetField("ConfigDirectory");
                        check(field != null && (string)field.GetValue(credentials) == Path.GetFullPath(root), "Resolved home was discarded");
                        check(Environment.GetEnvironmentVariable(variable) == inherited, "Credential read changed the parent home");
                    } finally { Environment.SetEnvironmentVariable(variable, before); }
                }
            }));
            run("account names use each view's independent profile cache", () => {
                var field = typeof(Credentials).GetField("Profile");
                check(field != null, "Per-view profile state is missing");
                var profileA = new ProfileState(); profileA.Begin("same-identity", DateTimeOffset.UtcNow); profileA.Accept("Cached A"); profileA.PlanName = "Max"; profileA.NextAttempt = DateTimeOffset.UtcNow.AddHours(1);
                var profileB = new ProfileState(); profileB.Begin("same-identity", DateTimeOffset.UtcNow); profileB.Accept("Cached B"); profileB.PlanName = "Pro"; profileB.NextAttempt = DateTimeOffset.UtcNow.AddHours(1);
                var first = new Credentials { Identity = "same-identity", AccessToken = "Fixture A" };
                var second = new Credentials { Identity = "same-identity", AccessToken = "Fixture B" };
                field.SetValue(first, profileA); field.SetValue(second, profileB);
                var name = typeof(UsageClient).GetMethod("AccountNameAsync", HiddenStatic);
                var httpField = typeof(UsageClient).GetField("Http", HiddenStatic); var oldHttp = (HttpClient)httpField.GetValue(null);
                var handler = new ProfileHandler();
                using (var http = new HttpClient(handler)) {
                    try {
                        httpField.SetValue(null, http);
                        string a = ((Task<string>)name.Invoke(null, new object[] { false, first, CancellationToken.None })).GetAwaiter().GetResult();
                        string b = ((Task<string>)name.Invoke(null, new object[] { false, second, CancellationToken.None })).GetAwaiter().GetResult();
                        check(a == "Cached A" && b == "Cached B" && handler.Requests == 0, "View caches contaminated one another");
                        check(first.PlanName == "Max" && second.PlanName == "Pro", "Account plans crossed profile caches");
                        profileA.Begin("new-identity", DateTimeOffset.UtcNow);
                        check(profileA.PlanName == null, "Previous account's plan survived an identity change");
                    } finally { httpField.SetValue(null, oldHttp); }
                }
            });
            run("Codex usage child retains its matching home and SQLite isolation", () => WithDirectory(directory => {
                var factory = typeof(UsageClient).GetMethod("CreateCodexStartInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var root = typeof(Credentials).GetField("ConfigDirectory");
                check(factory != null && root != null, "Per-account Codex process factory is missing");
                File.WriteAllText(Path.Combine(directory, "auth.json"), "{\"tokens\":{\"access_token\":\"fixture\"}}");
                var credentials = UsageClient.ReadCredentials(true, directory);
                var info = (ProcessStartInfo)factory.Invoke(null, new object[] { credentials });
                check(info.EnvironmentVariables["CODEX_HOME"] == directory && info.EnvironmentVariables["CODEX_SQLITE_HOME"] == directory, "Codex child can read another account's state");
                check(info.WorkingDirectory == directory && !info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError, "Codex child lost its owned process contract");
                check(info.Arguments == "app-server --stdio", "Usage started an inference command");
            }));
        }
    }
}
