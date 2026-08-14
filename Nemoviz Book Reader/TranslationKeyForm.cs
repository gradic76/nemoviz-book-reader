using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Where a reader pastes the key for one translation service, and — the part
    /// that matters — finds out whether it works.
    ///
    /// <para><b>The check button is not a convenience.</b> Without it a mistyped or
    /// half-pasted key is indistinguishable from a network failure, from a service
    /// that is down, and from a key that authenticates but is barred from the model
    /// — and none of those differences is visible to somebody who cannot see the
    /// screen. Everything this feature can go wrong at is invisible; this is the one
    /// place it can be made audible.</para>
    ///
    /// <para><b>The field is NOT masked, deliberately.</b> Masking a key while the
    /// store beside it is plain text is theatre, and this project does not do
    /// protection that only looks like protection. It also costs the one reader who
    /// needs it most: a blind user pasting from the clipboard hears "star star star"
    /// and learns nothing about whether the paste landed.</para>
    ///
    /// <para>Plain Windows chrome, like <c>NoVoiceForm</c> and the dictionary
    /// dialogs — the skinned password shell expects exactly one field and two
    /// buttons, and this has four.</para>
    /// </summary>
    internal static class TranslationKeyForm
    {
        /// <returns>true when the stored key changed, so the caller can refresh
        /// what it says about this service.</returns>
        public static bool Show(IWin32Window owner, TranslationEngine engine)
        {
            if (engine == null) return false;
            bool changed = false;

            using (Form dlg = new Form())
            {
                dlg.Text = Localization.T("Dialog.TranslateKey.Title", engine.DisplayName);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(470, 210);

                Label lbl = new Label();
                lbl.Text = Localization.T("Dialog.TranslateKey.Prompt", engine.DisplayName);
                lbl.SetBounds(12, 12, 446, 52);

                TextBox tb = new TextBox();
                tb.SetBounds(12, 70, 446, 24);
                tb.AccessibleName = Localization.T("Dialog.TranslateKey.Field");
                tb.Text = TranslationKeys.Get(engine.Id) ?? "";

                // ONE FIELD FOR EVERY SERVICE, AZURE INCLUDED (Gordan, 2026-08-15).
                // Azure is the only one that can want a second value — the region —
                // but a single-service GLOBAL resource sends no region header AT
                // ALL, so "global" is not a value to type, it is the absence of
                // one. An empty box and "global" are the same request, and a box
                // whose right answer is always empty is a box that should not be
                // there.
                //
                // The setup guidance therefore says to make the resource Global,
                // and this dialog asks for a key and nothing else.
                //
                // WHAT IS NOT COVERED YET, and it is written down rather than
                // hidden: somebody who already has a REGIONAL resource has no way
                // to enter its region. The right answer is not to put the box back
                // for everyone but to let the Check button say so — Azure's own
                // 401 names the missing region header — and reveal it only then.
                // That path is unwritten because it cannot be tested without an
                // Azure account, and untested branches on an error nobody has seen
                // are how a dialog acquires a dead end.
                bool needsRegion = false;
                TextBox tbRegion = null;
                int drop = 0;

                // A read-only, TABBABLE line: the whole point is that the answer to
                // "did that work" can be reached and read. A Label would never be
                // visited by a reader driven by Tab.
                TextBox status = new TextBox();
                status.SetBounds(12, 102 + drop, 446, 24);
                status.ReadOnly = true;
                status.AccessibleName = Localization.T("Dialog.TranslateKey.Status");
                status.Text = TranslationKeys.Has(engine.Id)
                    ? Localization.T("Dialog.TranslateKey.Stored")
                    : Localization.T("Dialog.TranslateKey.NotStored");

                Button test = new Button();
                test.Text = Localization.T("Dialog.TranslateKey.Test");
                test.AccessibleName = test.Text;
                test.SetBounds(12, 140 + drop, 140, 30);

                Button remove = new Button();
                remove.Text = Localization.T("Dialog.TranslateKey.Remove");
                remove.AccessibleName = remove.Text;
                remove.SetBounds(158, 140 + drop, 100, 30);
                remove.Enabled = TranslationKeys.Has(engine.Id);

                Button ok = new Button();
                ok.Text = Localization.T("Btn.OK");
                ok.SetBounds(248, 176 + drop, 100, 30);
                ok.DialogResult = DialogResult.OK;

                Button cancel = new Button();
                cancel.Text = Localization.T("Btn.Cancel");
                cancel.SetBounds(358, 176 + drop, 100, 30);
                cancel.DialogResult = DialogResult.Cancel;

                test.Click += (s, e) =>
                {
                    string key = tb.Text.Trim();
                    string regionNow = needsRegion ? tbRegion.Text.Trim() : null;
                    if (key.Length == 0)
                    {
                        status.Text = Localization.T("Settings.Translate.Test.NoKey");
                        ScreenReader.Announce(dlg, status.Text);
                        return;
                    }
                    test.Enabled = false;
                    ok.Enabled = false;
                    status.Text = Localization.T("Dialog.TranslateKey.Testing");
                    ScreenReader.Announce(dlg, status.Text);

                    // On a worker, because this is a network call. A second of a
                    // frozen modal is a second in which a screen reader says nothing
                    // and the reader cannot tell a slow answer from a dead one.
                    Thread t = new Thread(() =>
                    {
                        TranslationResult r = Translator.TestKey(engine, key, regionNow);
                        try
                        {
                            dlg.BeginInvoke((MethodInvoker)delegate
                            {
                                status.Text = r.Ok
                                    ? Localization.T("Dialog.TranslateKey.Works")
                                    : (r.Error + (string.IsNullOrEmpty(r.Detail) ? "" : " — " + r.Detail));
                                // Spoken as well as shown: this is the answer the
                                // reader pressed the button for, and it must not
                                // depend on them going looking for it.
                                ScreenReader.Announce(dlg, status.Text);
                                test.Enabled = true;
                                ok.Enabled = true;
                            });
                        }
                        catch { /* dialog closed while the call was in flight */ }
                    });
                    t.IsBackground = true;
                    t.Start();
                };

                remove.Click += (s, e) =>
                {
                    TranslationKeys.Set(engine.Id, null);
                    tb.Text = "";
                    remove.Enabled = false;
                    changed = true;
                    status.Text = Localization.T("Dialog.TranslateKey.NotStored");
                    ScreenReader.Announce(dlg, status.Text);
                };

                dlg.Controls.Add(lbl);
                dlg.Controls.Add(tb);
                dlg.Controls.Add(status);
                dlg.Controls.Add(test);
                dlg.Controls.Add(remove);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                // Focus in the FIELD, said here rather than left to the tab order —
                // the lesson the archive password prompt paid for, where focus
                // landed on the message and six typed passwords went nowhere.
                dlg.Shown += (s, e) => { try { tb.Focus(); tb.SelectAll(); } catch { } };

                if (dlg.ShowDialog(owner) == DialogResult.OK)
                {
                    string key = tb.Text.Trim();
                    string existing = TranslationKeys.Get(engine.Id) ?? "";
                    if (needsRegion)
                    {
                        string reg = tbRegion.Text.Trim();
                        if (reg != (TranslationKeys.Get(TranslationEngines.AzureRegion) ?? ""))
                        {
                            TranslationKeys.Set(TranslationEngines.AzureRegion, reg.Length == 0 ? null : reg);
                            changed = true;
                        }
                    }
                    if (key != existing)
                    {
                        // An empty field on OK means "no key for this service",
                        // which is a legitimate thing to want and needs no separate
                        // button — Remove is there for the reader who thinks of it
                        // that way round.
                        TranslationKeys.Set(engine.Id, key.Length == 0 ? null : key);
                        changed = true;
                    }
                }
                return changed;
            }
        }
    }
}
