using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UsageWidget
{
    internal sealed class AccountDialog : Form
    {
        readonly ComboBox provider;
        readonly Button connect;
        readonly Label status;
        readonly bool reconnect;
        CancellationTokenSource cancellation;
        Task operation = Task.FromResult(0);
        bool busy, cancelRequested, closeReady, connected;
        public AccountProfile Profile { get; private set; }
        public bool Antigravity { get; private set; }
        public AccountDialog(AccountProfile existing)
        {
            Profile = existing; reconnect = existing != null;
            Text = reconnect ? "Reconnect account" : "Add account";
            ClientSize = new Size(350, 205); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f); BackColor = Color.FromArgb(14, 19, 29); ForeColor = Color.FromArgb(235, 240, 248);
            var caption = new Label { Text = "Choose the account provider", Bounds = new Rectangle(18, 16, 314, 22) };
            provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(18, 42, 314, 28), AccessibleName = "Account provider", TabIndex = 0 };
            provider.Items.AddRange(new object[] { "Claude Code", "Codex", "Antigravity (current sign-in)", "Cursor (browser sign-in)" });
            provider.SelectedIndex = existing == null ? 0 : existing.Cursor ? 3 : existing.Codex ? 1 : 0;
            provider.Enabled = !reconnect;
            status = new Label { Text = "Sign in to the account you want to add in your browser.", Bounds = new Rectangle(18, 85, 314, 58), AccessibleName = "Account connection status" };
            connect = new Button { Text = "Sign in", Bounds = new Rectangle(18, 158, 150, 30), TabIndex = 1 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(182, 158, 150, 30), TabIndex = 2 };
            Controls.AddRange(new Control[] { caption, provider, status, connect, cancel }); AcceptButton = connect; CancelButton = cancel;
            provider.SelectedIndexChanged += delegate {
                connect.Text = provider.SelectedIndex == 2 ? "Connect" : "Sign in";
                status.Text = provider.SelectedIndex == 2 ? "Uses the account already signed in to Antigravity. Keep Antigravity running."
                    : provider.SelectedIndex == 3 ? "Sign in at cursor.com in your browser. No Cursor app needed; if Cursor is installed, its account also appears automatically."
                    : "Sign in to the account you want to add in your browser.";
            };
            connect.Click += async delegate {
                if (busy) return;
                if (cancellation != null) cancellation.Dispose(); cancellation = new CancellationTokenSource();
                busy = true; provider.Enabled = connect.Enabled = false; status.Text = "Complete sign-in in your browser. This window will update when you're connected.";
                operation = ConnectAsync(); await operation;
            };
            FormClosing += async delegate(object sender, FormClosingEventArgs e) {
                if (!busy || closeReady) return;
                e.Cancel = true; if (cancelRequested) return;
                cancelRequested = true; cancellation.Cancel();
                await operation; closeReady = true; DialogResult = DialogResult.Cancel; Close();
            };
        }
        async Task ConnectAsync()
        {
            try {
                if (provider.SelectedIndex == 2) {
                    await AntigravityClient.ConnectAsync(cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
                    Antigravity = true; busy = false; DialogResult = DialogResult.OK; Close(); return;
                }
                if (Profile == null) Profile = AccountProfile.Create(provider.SelectedIndex == 1, provider.SelectedIndex == 3);
                if (!connected && Profile.Cursor) {
                    string token = await CursorClient.BrowserLoginAsync(cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    CursorClient.SaveToken(Profile.ConfigDirectory, token); connected = true;
                }
                if (!connected) { await AccountLogin.SignInAsync(Profile, cancellation.Token); connected = true; }
                cancellation.Token.ThrowIfCancellationRequested();
                if (!reconnect) {
                    var profiles = AccountStore.Load();
                    if (!profiles.Any(p => p.Id == Profile.Id)) profiles.Add(Profile);
                    AccountStore.Save(profiles);
                }
                busy = false; DialogResult = DialogResult.OK; Close();
            }
            catch (OperationCanceledException) { status.Text = "Sign-in canceled."; }
            catch (UsageException e) { status.Text = e.Message; }
            catch (Exception e) {
                if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is Win32Exception || e is InvalidOperationException || e is ArgumentException)) throw;
                status.Text = connected ? "Connected, but the account list could not be saved. Try again." : "Sign-in could not finish. Check that the provider's app is installed, then try again.";
            }
            finally {
                busy = false;
                if (!cancelRequested && !IsDisposed) { connect.Text = "Try again"; connect.Enabled = true; provider.Enabled = !reconnect && Profile == null; }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && cancellation != null) { cancellation.Cancel(); cancellation.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
