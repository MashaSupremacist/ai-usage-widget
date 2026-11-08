using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace UsageWidget
{
    internal sealed class AccountProfile
    {
        public readonly string Id;
        public readonly bool Codex, Cursor;
        public AccountProfile(string id, bool codex, bool cursor = false)
        {
            if (id == null || id.Length != 32 || id.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) throw new InvalidDataException("Invalid account ID");
            Id = id; Codex = codex && !cursor; Cursor = cursor;
        }
        public static AccountProfile Create(bool codex) { return Create(codex, false); }
        public static AccountProfile Create(bool codex, bool cursor) { return new AccountProfile(Guid.NewGuid().ToString("N"), codex, cursor); }
        public string ConfigDirectory { get { return Path.Combine(WidgetSettings.Folder, "accounts", Id); } }
        public string CachePath { get { return Path.Combine(ConfigDirectory, "usage.json"); } }
    }
    internal static class AccountStore
    {
        static readonly object Gate = new object();
        static string MetadataPath(string path) { return Path.GetFullPath(path ?? Path.Combine(WidgetSettings.Folder, "accounts.json")); }
        public static List<AccountProfile> Load(string path = null)
        {
            path = MetadataPath(path);
            if (Directory.Exists(path)) throw new InvalidDataException("Account metadata path is a directory");
            if (!File.Exists(path)) return new List<AccountProfile>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream))
            {
                if (stream.Length > 65536) throw new InvalidDataException("Account list is too large");
                var root = UsageParser.Json(reader.ReadToEnd());
                var accounts = UsageParser.Get(root, "accounts") as object[];
                if (root.Count != 1 || accounts == null || accounts.Length > 128) throw new InvalidDataException("Invalid account list");
                var result = new List<AccountProfile>(); var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (object item in accounts)
                {
                    var entry = item as Dictionary<string, object>;
                    string id = UsageParser.Get(entry, "id") as string, provider = UsageParser.Get(entry, "provider") as string;
                    if (entry == null || entry.Count != 2 || !(provider == "codex" || provider == "claude" || provider == "cursor") || !ids.Add(id ?? "")) throw new InvalidDataException("Invalid or duplicate account entry");
                    result.Add(new AccountProfile(id, provider == "codex", provider == "cursor"));
                }
                return result;
            }
        }
        public static void Save(IEnumerable<AccountProfile> profiles, string path = null)
        {
            if (profiles == null) throw new ArgumentNullException("profiles");
            var entries = new List<object>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                if (profile == null || !ids.Add(profile.Id) || entries.Count >= 128) throw new InvalidDataException("Invalid or duplicate account entry");
                entries.Add(new { id = profile.Id, provider = profile.Cursor ? "cursor" : profile.Codex ? "codex" : "claude" });
            }
            path = MetadataPath(path);
            lock (Gate)
            {
                Load(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(new JavaScriptSerializer().Serialize(new { accounts = entries })); writer.Flush(); stream.Flush(true);
                    }
                    Load(path);
                    WidgetSettings.ReplaceFile(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
    internal static class AccountLogin
    {
        internal static string ClaudePath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directories = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"), Path.Combine(local, "Programs", "Claude"), Path.Combine(local, "hermes", "node"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") };
            directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            foreach (string directory in directories)
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                foreach (string relative in new[] { "claude.exe", "node_modules/@anthropic-ai/claude-code/bin/claude.exe", "node_modules/@anthropic-ai/claude-code/vendor/x64-win32/claude.exe" })
                {
                    string candidate;
                    try { candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), relative)); }
                    catch (ArgumentException) { continue; }
                    catch (NotSupportedException) { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            throw new UsageException("Install Claude Code before adding a Claude account");
        }
        public static ProcessStartInfo CreateStartInfo(AccountProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            var info = new ProcessStartInfo(profile.Codex ? UsageClient.CodexPath() : ClaudePath(), profile.Codex ? "login" : "auth login --claudeai") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            UsageClient.ApplyAccountEnvironment(info, profile.Codex, profile.ConfigDirectory);
            return info;
        }
        public static Task SignInAsync(AccountProfile profile, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return SignInCoreAsync(profile.Codex, profile.ConfigDirectory, CreateStartInfo(profile), cancellation, 600000);
        }
        static bool HasCredentials(bool codex, string directory)
        {
            try {
                var credentials = UsageClient.ReadCredentials(codex, directory);
                return !credentials.ExpiresAt.HasValue || credentials.ExpiresAt > DateTimeOffset.UtcNow;
            }
            catch (UsageException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        static void RejectReparsePoint(string directory)
        {
            for (string path = Path.GetFullPath(directory); path != null; path = Path.GetDirectoryName(path))
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Account folder must not be a linked folder");
        }
        static bool ContainsLinkedEntry(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                var child = entry as DirectoryInfo;
                if (child != null && ContainsLinkedEntry(child)) return true;
            }
            return false;
        }
        static async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            try { while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { } }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        static Task SignInCoreAsync(bool codex, string directory, ProcessStartInfo info, CancellationToken cancellation, int timeoutMilliseconds)
        {
            return Task.Run(async delegate {
                cancellation.ThrowIfCancellationRequested();
                RejectReparsePoint(directory);
                bool created = !Directory.Exists(directory), success = false;
                Directory.CreateDirectory(directory);
                using (var process = new Process { StartInfo = info })
                {
                    bool started = false;
                    try
                    {
                        if (codex)
                        {
                            string config = Path.Combine(directory, "config.toml");
                            if (File.Exists(config) && (File.GetAttributes(config) & FileAttributes.ReparsePoint) != 0) throw new IOException("Account config must not be a linked file");
                            File.WriteAllText(config, "cli_auth_credentials_store = \"file\"\r\n", new UTF8Encoding(false));
                        }
                        process.Start(); started = true;
                        process.StandardInput.Close();
                        Task output = DrainAsync(process.StandardOutput), error = DrainAsync(process.StandardError);
                        var elapsed = Stopwatch.StartNew();
                        while (!process.WaitForExit(100))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds) throw new UsageException("Sign-in timed out; try again");
                        }
                        cancellation.ThrowIfCancellationRequested();
                        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(1000)).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        if (process.ExitCode != 0) throw new UsageException("Sign-in did not finish; try again");
                        if (!HasCredentials(codex, directory)) throw new UsageException("Sign-in finished without a readable subscription account; try again");
                        success = true;
                    }
                    catch (System.ComponentModel.Win32Exception) { throw new UsageException("The sign-in client could not start; reinstall or reopen it"); }
                    finally
                    {
                        if (started)
                        {
                            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
                            catch (InvalidOperationException) { }
                            catch (System.ComponentModel.Win32Exception) { }
                        }
                        if (created && !success && (!started || process.HasExited) && !HasCredentials(codex, directory))
                        {
                            try {
                                RejectReparsePoint(directory);
                                if (!ContainsLinkedEntry(new DirectoryInfo(directory))) Directory.Delete(directory, true);
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                }
            });
        }
    }
}
