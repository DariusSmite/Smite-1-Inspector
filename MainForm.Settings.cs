using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmiteGodLab
{
    partial class MainForm
    {

        Panel BuildSettingsPanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(S(30), S(24), S(30), S(24)) };
            int y = S(4);
            void Add(Control c) => host.Controls.Add(c);
            Label Lbl(string t, Color col, float sz, int yy, FontStyle st = FontStyle.Regular)
                => new Label { Location = new Point(S(2), yy), Size = new Size(S(640), S(sz > 12 ? 30 : 20)), ForeColor = col, Font = Theme.F(sz, st), Text = t };

            Add(Lbl("Settings", Theme.Text, 16f, y, FontStyle.Bold)); y += S(46);

            // -- Open on startup --
            Add(Lbl("OPEN ON STARTUP", Theme.Accent, 10f, y, FontStyle.Bold)); y += S(24);
            Add(Lbl("Which tab the app shows when it launches.", Theme.Dim, 8.5f, y)); y += S(24);
            var startGrp = new Panel { Location = new Point(S(2), y), Size = new Size(S(460), S(28)), BackColor = Theme.Bg };
            var rbInsp = MkRadio("God Inspector", 0, S(3)); var rbTrk = MkRadio("Player Tracker", S(190), S(3));
            rbInsp.Checked = settings.StartupTab == 0; rbTrk.Checked = settings.StartupTab == 1;
            rbInsp.CheckedChanged += (s, e) => { if (rbInsp.Checked) { settings.StartupTab = 0; SaveSettings(); } };
            rbTrk.CheckedChanged += (s, e) => { if (rbTrk.Checked) { settings.StartupTab = 1; SaveSettings(); } };
            startGrp.Controls.Add(rbInsp); startGrp.Controls.Add(rbTrk); Add(startGrp); y += S(44);

            // -- Time format --
            Add(Lbl("TIME FORMAT", Theme.Accent, 10f, y, FontStyle.Bold)); y += S(24);
            Add(Lbl("Applies to the “updated” stamp, created/last-login and match times.", Theme.Dim, 8.5f, y)); y += S(24);
            var timeGrp = new Panel { Location = new Point(S(2), y), Size = new Size(S(620), S(28)), BackColor = Theme.Bg };
            var rbSys = MkRadio("System default", 0, S(3)); var rb12 = MkRadio("12-hour (3:05 PM)", S(170), S(3)); var rb24 = MkRadio("24-hour (15:05)", S(370), S(3));
            rbSys.Checked = settings.TimeFormat == 0; rb12.Checked = settings.TimeFormat == 1; rb24.Checked = settings.TimeFormat == 2;
            rbSys.CheckedChanged += (s, e) => { if (rbSys.Checked) { settings.TimeFormat = 0; SaveSettings(); } };
            rb12.CheckedChanged += (s, e) => { if (rb12.Checked) { settings.TimeFormat = 1; SaveSettings(); } };
            rb24.CheckedChanged += (s, e) => { if (rb24.Checked) { settings.TimeFormat = 2; SaveSettings(); } };
            timeGrp.Controls.Add(rbSys); timeGrp.Controls.Add(rb12); timeGrp.Controls.Add(rb24); Add(timeGrp); y += S(44);

            // -- Data --
            Add(Lbl("DATA", Theme.Accent, 10f, y, FontStyle.Bold)); y += S(24);
            Add(Lbl("Stored as JSON in Documents\\Smite Inspector. Clearing cannot be undone.", Theme.Dim, 8.5f, y)); y += S(26);
            var clrRec = MkBtn("Clear recent lookups", 160, false); clrRec.Location = new Point(S(2), y);
            var clrFav = MkBtn("Clear favorites", 130, false); clrFav.Location = new Point(S(170), y);
            var clrFrnd = MkBtn("Clear friend list", 140, false); clrFrnd.Location = new Point(S(308), y);
            var clrTag = MkBtn("Clear hidden-player tags", 188, false); clrTag.Location = new Point(S(456), y);
            clrRec.Click += (s, e) => { recents.Clear(); SaveRecents(); clrRec.Text = "Recents cleared"; };
            clrFav.Click += (s, e) => { favorites.Clear(); SaveFavs(); clrFav.Text = "Favorites cleared"; };
            clrFrnd.Click += (s, e) => { friendList.Clear(); SaveFriendList(); clrFrnd.Text = "List cleared"; };
            clrTag.Click += (s, e) => { hiddenTags.Clear(); SaveHiddenTags(); clrTag.Text = "Tags cleared"; };
            Add(clrRec); Add(clrFav); Add(clrFrnd); Add(clrTag); y += S(50);

            // -- Updates --
            Add(Lbl("UPDATES", Theme.Accent, 10f, y, FontStyle.Bold)); y += S(24);
            Add(Lbl("Current version: v" + AppVersion + ".  Checks this app's GitHub releases.", Theme.Dim, 8.5f, y)); y += S(26);
            var chkUpd = MkChk("Check for updates on startup", settings.CheckUpdates); chkUpd.BackColor = Theme.Bg; chkUpd.Location = new Point(S(2), y);
            chkUpd.CheckedChanged += (s, e) => { settings.CheckUpdates = chkUpd.Checked; SaveSettings(); }; Add(chkUpd); y += S(28);
            var chkAuto = MkChk("Install updates automatically (no prompt)", settings.AutoUpdate); chkAuto.BackColor = Theme.Bg; chkAuto.Location = new Point(S(2), y);
            chkAuto.CheckedChanged += (s, e) => { settings.AutoUpdate = chkAuto.Checked; SaveSettings(); }; Add(chkAuto); y += S(28);
            var chkBeta = MkChk("Get beta releases (pre-release test builds — newer, but less stable)", settings.BetaChannel); chkBeta.BackColor = Theme.Bg; chkBeta.Location = new Point(S(2), y);
            chkBeta.CheckedChanged += (s, e) => { settings.BetaChannel = chkBeta.Checked; settings.SkippedVersion = ""; SaveSettings(); }; Add(chkBeta); y += S(34);   // clear the skip on either toggle: the candidate "latest" changes meaning with the channel
            var btnUpd = MkBtn("Check for updates now", 184, false, Theme.Blue, Color.White); btnUpd.Location = new Point(S(2), y);
            btnUpd.Click += async (s, e) => await CheckForUpdate(true); Add(btnUpd); y += S(52);

            // -- Experimental: reveal privacy-hidden players (experiment/reveal-hidden-names) --
            Add(Lbl("HIDDEN-PLAYER REVEAL  (EXPERIMENTAL)", Theme.Accent, 10f, y, FontStyle.Bold)); y += S(24);
            Add(Lbl("Puts names to players who hide behind the privacy flag — exactly from your local game logs (✔), or as a", Theme.Dim, 8.5f, y)); y += S(18);
            Add(Lbl("fingerprint best-guess (≈, with a confidence %) from names it has already learned. Local only.", Theme.Dim, 8.5f, y)); y += S(26);

            // Live status / fail-safe labels — so the user can SEE each switch is actually working. Refs kept so the
            // toggles and the reset buttons can refresh them live.
            Label lblLog = null, lblCounts = null, lblHarv = null;
            void RefreshReveal()
            {
                var st = GameLog.Status();
                if (lblLog != null) { lblLog.Text = st.detail; lblLog.ForeColor = st.found ? Theme.Green : Theme.Yellow; }
                if (lblCounts != null) lblCounts.Text = "Learned names: " + NameDb.PlayerCount.ToString("N0") + "   ·   live rosters cached: " + NameDb.LiveCount.ToString("N0") + (MctsPortal.MatchCount > 0 ? "   ·   MCTS matches: " + MctsPortal.MatchCount : "");
                if (lblHarv != null) { bool run = _harvestCts != null; lblHarv.Text = run ? "✓ Harvester running — growing the name DB from live rosters" : (settings.Harvest && settings.RevealHidden ? "Harvester is enabled but not running — toggle it off and on" : "Harvester off"); lblHarv.ForeColor = run ? Theme.Green : Theme.Dim; }
            }

            var chkLog = MkChk("Reveal from your game logs  (EXACT — reads SMITE's local combat log, no API, no anti-cheat)", settings.LogReveal); chkLog.BackColor = Theme.Bg; chkLog.Location = new Point(S(2), y);
            chkLog.CheckedChanged += (s, e) => { settings.LogReveal = chkLog.Checked; GameLog.Enabled = chkLog.Checked; if (chkLog.Checked) GameLog.EnsureWatching(); SaveSettings(); RefreshReveal(); };
            Add(chkLog); y += S(24);
            Add(Lbl("Turn the combat log on in-game once (chat: /combatlog, or PageUp) so it records each match's roster.", Theme.Dim, 8.5f, y)); y += S(18);
            lblLog = Lbl("", Theme.Dim, 8.5f, y); Add(lblLog); y += S(26);

            var chkReveal = MkChk("Also fingerprint-guess hidden players from learned names (for matches you weren't in)", settings.RevealHidden); chkReveal.BackColor = Theme.Bg; chkReveal.Location = new Point(S(2), y);
            var chkHarv = MkChk("Run background name harvester (uses API quota to grow the DB)", settings.Harvest); chkHarv.BackColor = Theme.Bg; chkHarv.Enabled = settings.RevealHidden;
            var chkRanked = MkChk("De-anonymize hidden RANKED players via leaderboards (queries smite.guru)", settings.RankedReveal); chkRanked.BackColor = Theme.Bg; chkRanked.Enabled = settings.RevealHidden;
            chkReveal.CheckedChanged += (s, e) => { settings.RevealHidden = chkReveal.Checked; NameDb.Enabled = chkReveal.Checked; chkHarv.Enabled = chkReveal.Checked; chkRanked.Enabled = chkReveal.Checked; if (!chkReveal.Checked) { chkHarv.Checked = false; chkRanked.Checked = false; settings.RankedReveal = false; StopHarvester(); } SaveSettings(); RefreshReveal(); };
            Add(chkReveal); y += S(28);
            chkHarv.Location = new Point(S(2), y);
            chkHarv.CheckedChanged += (s, e) => { settings.Harvest = chkHarv.Checked; SaveSettings(); if (chkHarv.Checked && settings.RevealHidden) StartHarvester(); else StopHarvester(); RefreshReveal(); };
            Add(chkHarv); y += S(26);
            chkRanked.Location = new Point(S(2), y);
            chkRanked.CheckedChanged += (s, e) => { settings.RankedReveal = chkRanked.Checked; SaveSettings(); };
            Add(chkRanked); y += S(26);
            lblHarv = Lbl("", Theme.Dim, 8.5f, y); Add(lblHarv); y += S(24);

            lblCounts = Lbl("", Theme.Dim, 8.5f, y); Add(lblCounts); y += S(24);
            var btnClrDb = MkBtn("Clear learned names", 184, false); btnClrDb.Location = new Point(S(2), y);
            btnClrDb.Click += (s, e) => { NameDb.Clear(); btnClrDb.Text = "Cleared"; RefreshReveal(); };
            var btnNuke = MkBtn("Nuke everything (start fresh)", 224, false, Theme.Accent, Color.White); btnNuke.Location = new Point(S(196), y);
            btnNuke.Click += (s, e) =>
            {
                if (MessageBox.Show(this,
                    "Wipe ALL hidden-player reveal data and start fresh?\n\nThis clears the learned-name fingerprint DB AND every captured combat-log roster — the whole reveal algorithm starts from zero.\n\nYour hand-made nicknames on the Custom Hidden Tags tab are NOT affected. This cannot be undone.",
                    "Nuke reveal data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                NameDb.Clear(); GameLog.Clear(); btnNuke.Text = "Wiped — fresh start"; btnClrDb.Text = "Clear learned names"; RefreshReveal();
            };
            Add(btnClrDb); Add(btnNuke); y += S(40);

            var btnMcts = MkBtn("Import MCTS probe data…", 200, false); btnMcts.Location = new Point(S(2), y);
            btnMcts.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog { Title = "Select MCTS probe response file", Filter = "Text files|*.txt|All files|*.*", InitialDirectory = Theme.DataDir };
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                int n = MctsPortal.ImportProbeFile(ofd.FileName);
                if (n > 0) { MctsPortal.Save(); btnMcts.Text = $"Imported {n} match(es)"; RefreshReveal(); }
                else btnMcts.Text = "No matches found in file";
            };
            Add(btnMcts); y += S(40);

            // link to the in-app Codex explanation of these three switches
            var lnkCodex = new Label { Text = "▸  How these three switches work — open the Codex", AutoSize = true, ForeColor = Theme.Blue, Font = Theme.F(9f, FontStyle.Underline | FontStyle.Bold), Location = new Point(S(2), y), Cursor = Cursors.Hand, BackColor = Theme.Bg };
            lnkCodex.Click += (s, e) => { SelectNav(4); BeginInvoke(new Action(() => { try { _codexJumpReveal?.Invoke(); } catch { } })); };
            Add(lnkCodex); y += S(42);

            RefreshReveal();

            // -- Uninstall --
            Add(Lbl("UNINSTALL", Theme.Accent, 10f, y, FontStyle.Bold)); y += S(24);
            Add(Lbl("Removes Smite 1 Inspector from this PC. You'll be asked whether to also delete your saved data.", Theme.Dim, 8.5f, y)); y += S(26);
            var btnUninst = MkBtn("Uninstall Smite 1 Inspector", 224, false, Theme.Accent, Color.White); btnUninst.Location = new Point(S(2), y);
            btnUninst.Click += (s, e) => UninstallApp(); Add(btnUninst); y += S(50);

            Add(Lbl("Data folder: " + Theme.DataDir, Theme.Dim, 8.5f, y)); y += S(24);
            return host;
        }
    }
}
