using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Creates the Azure Speech resource and keeps its key, without the portal.
    ///
    /// <para><b>Why this window exists at all.</b> `AzureProvision` has been able
    /// to do the whole job since 2026-08-15 — sign in with a device code, create
    /// the resource, read the key back — and it has never been reachable from the
    /// app: no dialog ever called it. It was proven by a harness. So the engine is
    /// old and only this is new.</para>
    ///
    /// <para><b>The portal is what this replaces</b>, and that is the whole point:
    /// Gordan's judgement is that the Azure and Google portals are brutal with a
    /// screen reader. What is left is one sign-in page and a code to type into
    /// it — a plain form, not a console.</para>
    ///
    /// <para><b>Accessibility, all of it from rules this project already
    /// learned.</b> Every line the reader needs is a read-only TABBABLE TextBox
    /// and never a Label, because a reader driven by Tab never visits a Label
    /// (§8b). The CODE is the exception to "focus starts on the action": there is
    /// no action until the reader has read it, so focus starts on the code, it is
    /// selected so one keystroke copies it, and the step is announced. Elsewhere
    /// focus sits on Cancel — the status line is under §2's focus echo guard and
    /// would freeze if a reader stood in it, exactly as
    /// `AnalysisProgressForm` measured (its line sat on "Starting" for twenty
    /// seconds). Progress is spoken at the four real steps rather than per tick.</para>
    ///
    /// <para><b>Nothing irreversible happens without the sign-in.</b> The resource
    /// is created on the reader's own subscription under their own account; NBR
    /// never sees a password, only a token Microsoft hands back.</para>
    /// </summary>
    internal sealed class AzureSpeechSetupForm : Form
    {
        public bool Made { get; private set; }

        private readonly TextBox status, code;
        private readonly Button act, cancel, openLink;
        private readonly ComboBox subs;
        private readonly TextBox tenant, tenantHint;
        private readonly Label tenantLabel;
        private readonly Label subsLabel;
        private readonly TextBox resource, key;
        private readonly Label manualLabel, resourceLabel, keyLabel;
        private readonly Button keep;

        private volatile bool stop;
        private Thread worker;
        private DeviceCodeRequest request;
        private string token;
        private List<KeyValuePair<string, string>> subscriptions;

        // Set from the worker, read by the timer -- the same shape every long job
        // in this app uses, so nothing but the UI thread ever touches a control.
        private volatile string pendingStatus;
        private volatile string pendingSpeak;
        private volatile int phase;          // 0 idle, 1 code shown, 2 signed in, 3 creating, 4 done, 5 failed
        private volatile string failure;
        private readonly System.Windows.Forms.Timer poll;

        public AzureSpeechSetupForm()
        {
            Text = Localization.T("Azure.Setup.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(520, 480);

            status = Line(14, 14, 492, 76);
            // Azure's own refusals run long and are the whole point of reading
            // this line, so it has to be possible to reach the end of one.
            status.ScrollBars = ScrollBars.Vertical;
            status.Text = Localization.T("Azure.Setup.Intro");
            status.AccessibleName = status.Text;
            status.TabIndex = 0;

            code = Line(14, 98, 492, 28);
            code.AccessibleName = Localization.T("Azure.Setup.CodeLabel");
            code.TabIndex = 1;
            code.Visible = false;

            // A BUTTON, NOT A LINK IN THE TEXT. Gordan had to copy the address out
            // of the line and paste it into a browser, which is a step nobody
            // should have to work out. A LinkLabel would also be reachable — it
            // takes a tab stop, unlike a plain Label — but this is an ACTION, and
            // the app's convention is that actions are buttons: a reader hears
            // "Open the sign-in page, button" and knows what pressing it does.
            //
            // It does NOT open by itself when the code appears. That would throw
            // the browser in front of a reader who is still reading the code, and
            // the code is the thing they need first.
            openLink = new Button();
            openLink.Text = Localization.T("Azure.Setup.OpenPage");
            openLink.AccessibleName = openLink.Text;
            openLink.SetBounds(14, 132, 240, 26);
            openLink.TabIndex = 2;
            openLink.Visible = false;
            openLink.Click += (s, e) =>
            {
                // The address comes from Microsoft's own device-code reply, and
                // only ever after the reader pressed Start — never from anything
                // typed into this window.
                try
                {
                    if (request != null && !string.IsNullOrEmpty(request.VerificationUri))
                        System.Diagnostics.Process.Start(request.VerificationUri);
                }
                catch { ScreenReader.Announce(this, Localization.T("Azure.Setup.OpenFailed")); }
            };

            // ASKED UP FRONT, because it is the one thing that decides whether the
            // sign-in can happen at all. A personal Microsoft account cannot sign
            // in at the front door for an Azure scope — see AzureProvision.Tenant
            // — and finding that out after the code has been typed wastes the
            // reader's time and the code.
            tenantLabel = new Label();
            tenantLabel.Text = Localization.T("Azure.Setup.Directory");
            tenantLabel.AutoSize = false;
            tenantLabel.SetBounds(14, 172, 160, 20);

            tenant = new TextBox();
            tenant.SetBounds(180, 170, 326, 24);
            tenant.AccessibleName = tenantLabel.Text;
            tenant.TabIndex = 3;
            tenant.Text = AzureProvision.Tenant == "common" ? "" : AzureProvision.Tenant;

            tenantHint = Line(14, 200, 492, 40);
            tenantHint.Text = Localization.T("Azure.Setup.DirectoryHint");
            tenantHint.AccessibleName = tenantHint.Text;
            tenantHint.TabIndex = 4;

            subsLabel = new Label();
            subsLabel.Text = Localization.T("Azure.Setup.Subscription");
            subsLabel.AutoSize = false;
            subsLabel.SetBounds(14, 248, 160, 20);
            subsLabel.Visible = false;

            subs = new ComboBox();
            subs.DropDownStyle = ComboBoxStyle.DropDownList;
            subs.SetBounds(180, 246, 326, 24);
            subs.AccessibleName = subsLabel.Text;
            subs.TabIndex = 5;
            subs.Visible = false;

            // THE OTHER DOOR, and it is not a nicety (restored 2026-08-17). Everything
            // above makes a resource for someone who has none. A reader who ALREADY
            // has one — made in the portal, made on another machine, or made here
            // before a reinstall — needs only to hand over the pair, and this dialog
            // is the whole Azure job now that Settings keeps just the switch.
            //
            // It matters more for Azure than it would for anything else, because the
            // provisioning path is the one with a history of refusing: a personal
            // account outside its own directory, a region closed to new customers, a
            // provider registration that lags. When that path fails, this is the way
            // through, and losing it would have made a bad day unrecoverable.
            manualLabel = new Label();
            manualLabel.Text = Localization.T("Azure.Setup.HaveOne");
            manualLabel.AutoSize = false;
            manualLabel.SetBounds(14, 290, 492, 22);

            resourceLabel = new Label();
            resourceLabel.Text = Localization.T("Settings.Azure.Resource");
            resourceLabel.AutoSize = false;
            resourceLabel.SetBounds(14, 320, 160, 20);

            resource = new TextBox();
            resource.SetBounds(180, 318, 326, 24);
            resource.AccessibleName = resourceLabel.Text;
            resource.TabIndex = 6;
            resource.Text = AzureVoices.Resource;

            keyLabel = new Label();
            keyLabel.Text = Localization.T("Settings.Azure.Key");
            keyLabel.AutoSize = false;
            keyLabel.SetBounds(14, 352, 160, 20);

            // Shown, not masked, exactly as every other key field in this app: dots
            // are unreadable to a screen reader and this is the reader's own machine.
            key = new TextBox();
            key.SetBounds(180, 350, 326, 24);
            key.AccessibleName = keyLabel.Text;
            key.TabIndex = 7;

            keep = new Button();
            keep.Text = Localization.T("Settings.Azure.Save");
            keep.AccessibleName = keep.Text;
            keep.SetBounds(180, 382, 200, 30);
            keep.TabIndex = 8;
            keep.Click += (s, e) => KeepTypedPair();

            act = new Button();
            act.Text = Localization.T("Azure.Setup.Start");
            act.AccessibleName = act.Text;
            act.SetBounds(14, 430, 200, 30);
            act.TabIndex = 9;
            act.Click += (s, e) => Act();

            cancel = new Button();
            cancel.Text = Localization.T("Btn.Cancel");
            cancel.AccessibleName = cancel.Text;
            cancel.SetBounds(316, 430, 190, 30);
            cancel.TabIndex = 10;
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(status);
            Controls.Add(code);
            Controls.Add(openLink);
            Controls.Add(tenantLabel);
            Controls.Add(tenant);
            Controls.Add(tenantHint);
            Controls.Add(subsLabel);
            Controls.Add(subs);
            Controls.Add(manualLabel);
            Controls.Add(resourceLabel);
            Controls.Add(resource);
            Controls.Add(keyLabel);
            Controls.Add(key);
            Controls.Add(keep);
            Controls.Add(act);
            Controls.Add(cancel);
            AcceptButton = act;
            CancelButton = cancel;

            poll = new System.Windows.Forms.Timer { Interval = 120 };
            poll.Tick += Poll_Tick;
            poll.Start();

            // Focus on the action, which is where a reader has something to DO.
            Shown += (s, e) => { try { act.Focus(); } catch { } };
            FormClosing += (s, e) => { stop = true; poll.Stop(); };
        }

        /// <summary>Keeps a typed resource-and-key pair, having ASKED Azure whether
        /// it works — a key is not a string that can be checked by looking at it. A
        /// refusal says so and changes nothing, so a credential that was already
        /// good is never lost to a typo in the field beside it.</summary>
        private void KeepTypedPair()
        {
            string why = AzureVoices.Save(resource.Text, key.Text);
            if (why != null)
            {
                MessageForm.ShowInfo(this, why, Localization.T("Settings.Azure.Group"));
                return;
            }
            resource.Text = AzureVoices.Resource;
            // Says so in the line a reader is already using to follow this dialog,
            // rather than in a box they have to dismiss to carry on.
            status.Text = Localization.T("Azure.Setup.Kept");
            status.AccessibleName = status.Text;
            ScreenReader.Announce(this, status.Text);
            DialogResult = DialogResult.OK;
        }

        private static TextBox Line(int x, int y, int w, int h)
        {
            var t = new TextBox();
            t.Multiline = h > 30;
            t.ReadOnly = true;
            t.TabStop = true;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = SystemColors.Window;
            t.SetBounds(x, y, w, h);
            return t;
        }

        /// <summary>One button, three jobs, because the reader is walked through
        /// one step at a time and a window of five buttons that are mostly
        /// disabled is harder to use than a window with one that changes.</summary>
        private void Act()
        {
            if (phase == 0) { StartSignIn(); return; }
            if (phase == 2) { StartCreate(); return; }
            if (phase == 4 || phase == 5) { DialogResult = phase == 4 ? DialogResult.OK : DialogResult.Cancel; Close(); }
        }

        private void StartSignIn()
        {
            act.Enabled = false;
            tenant.Enabled = false;
            // Kept before the sign-in, so a reader who gets it right once never
            // types it again — and so a failed attempt still leaves the value in
            // the field to correct rather than to retype.
            AzureProvision.Tenant = tenant.Text;
            Say(Localization.T("Azure.Setup.SigningIn"), true);
            worker = new Thread(() =>
            {
                var req = AzureProvision.BeginSignIn();
                if (req == null || !string.IsNullOrEmpty(req.Error))
                {
                    failure = req != null && !string.IsNullOrEmpty(req.Error)
                        ? req.Error : "sign-in could not be started";
                    phase = 5; return;
                }
                request = req;
                pendingStatus = Localization.T("Azure.Setup.EnterCode", req.VerificationUri);
                pendingSpeak = pendingStatus;
                phase = 1;

                var signed = AzureProvision.CompleteSignIn(req, () => stop);
                if (signed == null || !signed.Ok)
                {
                    failure = Why(signed, "sign-in did not finish");
                    phase = 5; return;
                }
                token = signed.Value;

                string err;
                subscriptions = AzureProvision.Subscriptions(token, out err);
                if (subscriptions == null || subscriptions.Count == 0)
                {
                    failure = err ?? Localization.T("Azure.Setup.NoSubscription");
                    phase = 5; return;
                }
                pendingStatus = Localization.T("Azure.Setup.SignedIn", subscriptions.Count);
                pendingSpeak = pendingStatus;
                phase = 2;
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void StartCreate()
        {
            if (subs.SelectedIndex < 0) return;
            string subId = subscriptions[subs.SelectedIndex].Key;
            act.Enabled = false;
            subs.Enabled = false;
            phase = 3;
            Say(Localization.T("Azure.Setup.Creating"), true);

            // A name of our own, unique enough not to collide with anything the
            // reader made by hand, and legal as a subdomain: letters, digits and
            // hyphens only, 2..64 characters.
            string name = "nemoviz-speech-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            worker = new Thread(() =>
            {
                var made = AzureProvision.CreateSpeech(token, subId, "nemoviz", name);
                if (made == null || !made.Ok)
                {
                    failure = Why(made, "the resource could not be created");
                    phase = 5; return;
                }
                AzureVoices.SaveProvisioned(name, made.Region, made.Value);
                pendingStatus = AzureVoices.Have
                    ? Localization.T("Azure.Setup.Done", AzureVoices.Voices().Count)
                    : Localization.T("Azure.Setup.DoneNoList");
                pendingSpeak = pendingStatus;
                phase = 4;
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Poll_Tick(object sender, EventArgs e)
        {
            string s = pendingStatus;
            if (s != null)
            {
                pendingStatus = null;
                status.Text = s;
                status.AccessibleName = s;
            }
            string speak = pendingSpeak;
            if (speak != null) { pendingSpeak = null; ScreenReader.Announce(this, speak); }

            switch (phase)
            {
                case 1:
                    if (!code.Visible && request != null)
                    {
                        code.Visible = true;
                        openLink.Visible = true;
                        code.Text = request.UserCode;
                        code.AccessibleName = Localization.T("Azure.Setup.CodeLabel") + " " + request.UserCode;
                        // THE ONE PLACE FOCUS DOES NOT START ON THE ACTION: there
                        // is nothing to do until this has been read, and selected
                        // means one keystroke copies it.
                        try { code.Focus(); code.SelectAll(); } catch { }
                    }
                    break;
                case 2:
                    if (!subs.Visible && subscriptions != null)
                    {
                        subs.Items.Clear();
                        foreach (var kv in subscriptions) subs.Items.Add(kv.Value);
                        subs.SelectedIndex = 0;
                        subsLabel.Visible = true;
                        subs.Visible = true;
                        act.Text = Localization.T("Azure.Setup.Create");
                        act.AccessibleName = act.Text;
                        act.Enabled = true;
                        // One subscription is not a choice; do not make them make it.
                        if (subscriptions.Count == 1) { try { act.Focus(); } catch { } }
                        else { try { subs.Focus(); } catch { } }
                    }
                    break;
                case 4:
                    if (!Made)
                    {
                        Made = true;
                        code.Visible = false;
                        openLink.Visible = false;
                        act.Text = Localization.T("Btn.Close");
                        act.AccessibleName = act.Text;
                        act.Enabled = true;
                        cancel.Enabled = false;
                        try { act.Focus(); } catch { }
                    }
                    break;
                case 5:
                    if (act.Enabled == false || act.Text != Localization.T("Btn.Close"))
                    {
                        string why = failure ?? "";
                        status.Text = Localization.T("Azure.Setup.Failed", why);
                        status.AccessibleName = status.Text;
                        ScreenReader.Announce(this, status.Text);
                        code.Visible = false;
                        openLink.Visible = false;
                        act.Text = Localization.T("Btn.Close");
                        act.AccessibleName = act.Text;
                        act.Enabled = true;
                        try { act.Focus(); } catch { }
                    }
                    break;
            }
        }

        /// <summary>Both halves of a refusal, and this exists because the first
        /// version showed neither useful one.
        ///
        /// <para><c>AzureResult.Error</c> is always set and always generic —
        /// "Azure refused the request (403)" — while <c>Detail</c> carries the
        /// sentence Azure actually sent, dug out of <c>error.message</c>. Written
        /// as <c>Error ?? Detail</c>, the coalesce could never fire, so the one
        /// line that explains the failure was thrown away every time. Gordan got
        /// the bare 403 and neither of us could tell whether it was a permission,
        /// a quota, a policy or a lapsed subscription.</para></summary>
        private static string Why(AzureResult r, string fallback)
        {
            if (r == null) return fallback;
            string head = string.IsNullOrEmpty(r.Error) ? fallback : r.Error;
            return string.IsNullOrEmpty(r.Detail) ? head : head + " — " + r.Detail;
        }

        private void Say(string text, bool speak)
        {
            status.Text = text;
            status.AccessibleName = text;
            if (speak) ScreenReader.Announce(this, text);
        }
    }
}
