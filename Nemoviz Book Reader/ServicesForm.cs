using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Nemoviz_Book_Reader
{
    /// <summary>
    /// Services and accounts — one place for everything that lives on somebody
    /// else's computer.
    ///
    /// <para><b>Why it is under Help and not in Settings</b> (Gordan,
    /// 2026-08-17). Settings → Advanced had grown five groups, three of them
    /// credential dialogs, on a page a reader visits once and then never again.
    /// Setting a service up is not a setting: it is a job with steps, done on a
    /// web site, once. So the JOB moves here and Advanced keeps only the
    /// switches.</para>
    ///
    /// <para><b>Cloud only.</b> The four that need an account with somebody. OCR
    /// languages and OneCore voices are not here and never will be — his call,
    /// and the right one: *"za Windows usluge je dovoljan hint, ne zahtijeva
    /// izlazak na web niti kakve registracije, samo se čekira i update odradi
    /// svoje."*</para>
    ///
    /// <para><b>The step-by-step guides are the point of the window.</b> His
    /// reason is the strongest argument in the whole feature: *"sam sam se kao
    /// iskusan korisnik pogubio, manje iskusni korisnici će posijediti."* So the
    /// text is numbered, literal, and says what to click.</para>
    ///
    /// <para><b>Shape:</b> a list of services, a read-only TABBABLE field with
    /// that service's words, and one button that opens its setup. The field is
    /// never a Label — a reader driven by Tab never visits one, and here the text
    /// is the whole window. Focus starts on the list, because choosing which
    /// service is the first thing anyone does here.</para>
    /// </summary>
    internal sealed class ServicesForm : Form
    {
        private sealed class Service
        {
            public string NameKey, GuideKey;
            public Func<bool> IsSet;
            public Action<IWin32Window> SetUp;
        }

        private readonly List<Service> services = new List<Service>();
        private readonly ListBox list;
        private readonly TextBox body;
        private readonly Button setUp, close;

        public ServicesForm()
        {
            Text = Localization.T("Services.Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(640, 470);

            BuildServices();

            list = new ListBox();
            list.SetBounds(14, 14, 200, 180);
            list.AccessibleName = Localization.T("Services.List.Accessible");
            list.TabIndex = 0;
            foreach (Service s in services) list.Items.Add(Localization.T(s.NameKey));
            list.SelectedIndexChanged += (s, e) => ShowChosen();

            body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.TabStop = true;
            body.ScrollBars = ScrollBars.Vertical;
            body.BorderStyle = BorderStyle.FixedSingle;
            body.BackColor = SystemColors.Window;
            body.SetBounds(228, 14, 398, 396);
            body.TabIndex = 1;

            setUp = new Button();
            setUp.Text = Localization.T("Services.SetUp");
            setUp.AccessibleName = setUp.Text;
            setUp.SetBounds(14, 206, 200, 30);
            setUp.TabIndex = 2;
            setUp.Click += (s, e) => SetUpChosen();

            close = new Button();
            close.Text = Localization.T("Btn.Close");
            close.AccessibleName = close.Text;
            close.SetBounds(14, 380, 200, 30);
            close.TabIndex = 3;
            close.DialogResult = DialogResult.Cancel;

            Controls.Add(list);
            Controls.Add(body);
            Controls.Add(setUp);
            Controls.Add(close);
            CancelButton = close;

            if (list.Items.Count > 0) list.SelectedIndex = 0;
            Shown += (s, e) => { try { list.Focus(); } catch { } };
        }

        /// <summary>The four, and what each one's "set up" actually opens. Every
        /// one of these dialogs already existed — this window is a way IN to
        /// them, not a second copy of them.</summary>
        private void BuildServices()
        {
            services.Add(new Service
            {
                NameKey = "Services.Item.GoogleVoices",
                GuideKey = "GoogleVoices",
                IsSet = () => GoogleCloudVoices.Have,
                SetUp = o => LoadGoogleAccount(o),
            });
            services.Add(new Service
            {
                NameKey = "Services.Item.AzureVoices",
                GuideKey = "AzureVoices",
                IsSet = () => AzureVoices.Have,
                SetUp = o => { using (var d = new AzureSpeechSetupForm()) d.ShowDialog(o); },
            });
            AddEngine("Services.Item.DeepSeek", "DeepSeek", TranslationEngines.DeepSeek);
            AddEngine("Services.Item.Gemini", "Gemini", TranslationEngines.Gemini);
        }

        private void AddEngine(string nameKey, string guideKey, string engineId)
        {
            TranslationEngine engine = null;
            foreach (TranslationEngine e in TranslationEngines.All)
                if (e.Id == engineId) { engine = e; break; }
            if (engine == null) return;

            services.Add(new Service
            {
                NameKey = nameKey,
                GuideKey = guideKey,
                IsSet = () => TranslationKeys.Has(engine.KeyId ?? engine.Id),
                SetUp = o => TranslationKeyForm.Show(o, engine),
            });
        }

        private void LoadGoogleAccount(IWin32Window owner)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Localization.T("Settings.Cloud.Load");
                dlg.Filter = Localization.T("Settings.Cloud.Filter");
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                string why = GoogleCloudVoices.LoadFrom(dlg.FileName);
                if (why != null)
                    MessageForm.ShowInfo(this, why, Localization.T("Settings.Cloud.Group"));
            }
        }

        private Service Chosen
        {
            get
            {
                int i = list.SelectedIndex;
                return i >= 0 && i < services.Count ? services[i] : null;
            }
        }

        private void ShowChosen()
        {
            Service s = Chosen;
            if (s == null) { body.Text = ""; return; }

            bool ready = false;
            try { ready = s.IsSet(); } catch { }
            // FOUR PARTS, and the order is Gordan's (2026-08-17). The STATE
            // first, because whether they already did this is what a reader wants
            // before a page of steps. Then the PROSE — what the thing is and what
            // has to happen in what order. Then the DISCLAIMER, and only then the
            // steps: the prose stays true when a web site is redesigned and the
            // numbered steps are the half that goes stale, so the warning belongs
            // between them rather than at the top where it would shade
            // everything.
            string nl = Environment.NewLine;
            string text = Localization.T(ready ? "Services.State.Ready" : "Services.State.NotSet")
                          + nl + nl + Localization.T("Services.About." + s.GuideKey)
                          + nl + nl + Localization.T("Services.Disclaimer")
                          + nl + nl + Localization.T("Services.Steps." + s.GuideKey);
            body.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            body.AccessibleName = Localization.T(s.NameKey);
            body.Select(0, 0);
        }

        private void SetUpChosen()
        {
            Service s = Chosen;
            if (s == null) return;
            try { s.SetUp(this); } catch { }
            ShowChosen();          // it may have just become set up
        }
    }
}
