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

        Panel BuildTrackerPanel()
        {
            LoadFavs(); LoadHiddenTags(); LoadRecents();
            NameDb.Load(); NameDb.Enabled = settings.RevealHidden;   // experiment/reveal-hidden-names
            GodBoard.Load();   // god-leaderboard id-leak → smite.guru name cache (experiment 2026-06-25)
            MctsPortal.Load();
            { int n = MctsPortal.AutoImport(); if (n > 0) System.Diagnostics.Debug.WriteLine($"[MctsPortal] Auto-imported {n} match(es)"); }
            GameLog.Init(); GameLog.Enabled = settings.LogReveal;   // EXACT reveal from local game logs (combat log)
            if (settings.RevealHidden && settings.Harvest) StartHarvester();
            TagSync.Init(); TagSync.Enabled = settings.CommunityTags;   // crowdsourced shared tags
            if (settings.CommunityTags) _ = TagSync.Pull(true);
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            {
                // --- primary tab strip (Track / Favorites / Recent) ---
                var subBar = new Panel { Dock = DockStyle.Top, Height = S(42), BackColor = Theme.Panel };
                var subBarLine = new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line };
                var subFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(S(14), S(2), 0, 0) };
                var primaryTabs = new[] { MkSubTab("My profile"), MkSubTab("Track"), MkSubTab("Favorites"), MkSubTab("Recent Profiles"), MkSubTab("Hidden Tags"), MkSubTab("Encounters") };   // index 5 = Encounters (top-level now, was a Track sub-tab)
                // 6 primary tabs now (Encounters added) — at high DPI the default tab padding overflows the strip and clips the
                // last tab, so pack the primary row tighter (the secondary strip keeps the roomier MkSubTab default).
                foreach (var t in primaryTabs) { t.Width = Math.Max(S(72), TextRenderer.MeasureText(t.Text, t.Font).Width + S(20)); subFlow.Controls.Add(t); }
                subBar.Controls.Add(subFlow); subBar.Controls.Add(subBarLine);

                // --- secondary (player-context) sub-tab strip (Overview / Achievements / Friends) — only while a player is loaded ---
                var subBar2 = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Theme.Bg, Visible = false };
                var subBar2Line = new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line };
                var subFlow2 = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(S(28), S(0), 0, 0) };
                var secondaryTabs = new[] { MkSubTab("Overview"), MkSubTab("Masteries"), MkSubTab("Matches"), MkSubTab("Achievements"), MkSubTab("Friend List") };   // Encounters moved up to a primary tab
                foreach (var t in secondaryTabs) { t.Height = S(36); subFlow2.Controls.Add(t); }
                int curPrimary = 1;   // 0 My profile · 1 Track · 2 Favorites · 3 Recent Profiles · 4 Hidden Tags · 5 Encounters (declared early: used by Lookup/_trkLoadPlayer closures)
                bool curFromMyProfile = false;   // the loaded player was loaded BY the My-profile tab → don't bleed it into Track
                subBar2.Controls.Add(subFlow2); subBar2.Controls.Add(subBar2Line);

                // --- search bar ---
                var top = new Panel { Dock = DockStyle.Top, Height = S(54), BackColor = Theme.Panel };
                var lbl = new Label { Text = "Player:", AutoSize = true, ForeColor = Theme.Dim, Location = new Point(S(14), S(18)) };
                var box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f) };
                try { box.PlaceholderText = "SMITE player name (partial / any case works)…"; } catch { }
                var bhost = WrapInput(box, S(280)); bhost.Location = new Point(S(80), S(13));
                var track = MkBtn("Search", 84, false, Theme.Blue, Color.White); track.Location = new Point(S(372), S(12));
                favSaveBtn = MkBtn("☆ Save", 104, false, Theme.Input, Theme.Dim); favSaveBtn.Location = new Point(S(464), S(12)); favSaveBtn.Enabled = false;
                friendAddBtn = MkBtn("＋ Friend List", 136, false, Theme.Input, Theme.Dim); friendAddBtn.Location = new Point(S(574), S(12)); friendAddBtn.Enabled = false;
                var addAllFriendsBtn = MkBtn("＋ Add all to Friend List", 184, false, Theme.Input, Theme.Green); addAllFriendsBtn.Location = new Point(S(720), S(12)); addAllFriendsBtn.Visible = false;
                // The Set/Change-my-profile button lives on the SECONDARY (sub-menu) bar, right-aligned, only on the My-profile
                // tab. (The primary "My profile" tab already names the view, so no separate title is needed.)
                var myProfBtn = MkBtn("＋ Set my profile", 184, false, Theme.Input, Theme.Blue); myProfBtn.Visible = false;
                subBar2.Controls.Add(myProfBtn); myProfBtn.BringToFront();   // sits over the (left-aligned) secondary tab flow
                void LayoutMyProfBar() { myProfBtn.Top = S(4); myProfBtn.Left = Math.Max(S(240), subBar2.ClientSize.Width - myProfBtn.Width - S(16)); }
                subBar2.SizeChanged += (s, e) => LayoutMyProfBar();   // keep it right-aligned even if a resize happened while hidden
                var lastFriends = new List<PlayerRow>();   // the FRIENDS section of the currently-shown friends list (for "add all")
                var friendCats = new List<(string key, string cap, List<PlayerRow> list)>();   // collapsible friend sections
                var collapsedFriendSecs = new HashSet<string>();
                int friendsHiddenOpaque = 0;
                // Status line: its own full-width strip docked just under the search bar. A fixed Location on the search
                // row would land off-screen at high DPI (the row is already full to the window edge), so it lives here.
                var hint = new Label { Dock = DockStyle.Top, Height = S(22), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(14), 0, S(14), 0), ForeColor = Theme.Dim, Font = Theme.F(8.5f), BackColor = Theme.Panel, Text = "Live data from the official Hi-Rez SMITE API." };
                top.Controls.Add(lbl); top.Controls.Add(bhost); top.Controls.Add(track); top.Controls.Add(favSaveBtn); top.Controls.Add(friendAddBtn); top.Controls.Add(addAllFriendsBtn);

                // --- overview card ---
                var card = new Panel { Dock = DockStyle.Top, Height = S(214), BackColor = Theme.Bg };
                // name row: [SMITE] in-game name (with clan tag), then one [platform logo]+name per linked account
                string igName = "";
                var linkedAccts = new List<(int portal, string name)>();   // primary store persona + MergedPlayers platforms
                var nameFont = Theme.F(15f, FontStyle.Bold);
                var personaFont = Theme.F(10.5f);
                var namePanel = new Panel { Location = new Point(S(14), S(8)), Size = new Size(S(900), S(32)), BackColor = Theme.Bg };
                namePanel.Paint += (s, e) =>
                {
                    if (string.IsNullOrEmpty(igName)) return;   // nothing loaded → don't draw a lone SMITE logo
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    int h = namePanel.Height, x = 0;
                    int lg = S(26), cy = (h - lg) / 2;
                    var smite = PlatformLogo("smite", lg);
                    if (smite != null) { g.DrawImage(smite, x, cy, lg, lg); x += lg + S(9); }
                    if (!string.IsNullOrEmpty(igName))
                    {
                        var nsz = TextRenderer.MeasureText(g, igName, nameFont);
                        TextRenderer.DrawText(g, igName, nameFont, new Rectangle(x, 0, nsz.Width + S(6), h), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        x += nsz.Width + S(20);
                    }
                    int plg = S(20), pcy = (h - plg) / 2;
                    foreach (var (portal, pname) in linkedAccts)
                    {
                        var pkey = LogoKeyForPortal(portal);
                        var plogo = pkey != null ? PlatformLogo(pkey, plg) : null;
                        if (plogo != null) { g.DrawImage(plogo, x, pcy, plg, plg); x += plg + S(6); }
                        else
                        {
                            var (code, col) = PlatformChip(portal);
                            var csz = TextRenderer.MeasureText(g, code, personaFont); int cw = csz.Width + S(10), ch = S(18), chy = (h - ch) / 2;
                            using (var cb = new SolidBrush(col)) g.FillRectangle(cb, x, chy, cw, ch);
                            TextRenderer.DrawText(g, code, personaFont, new Rectangle(x, chy, cw, ch), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                            x += cw + S(6);
                        }
                        if (!string.IsNullOrEmpty(pname))
                        {
                            var psz = TextRenderer.MeasureText(g, pname, personaFont);
                            TextRenderer.DrawText(g, pname, personaFont, new Rectangle(x, 0, psz.Width + S(6), h), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                            x += psz.Width + S(16);
                        }
                        else x += S(8);
                    }
                };
                var statusLbl = new Label { AutoSize = false, UseMnemonic = false, Location = new Point(S(640), S(14)), Size = new Size(S(160), S(24)), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = Theme.F(9f, FontStyle.Bold), Visible = false };
                // Profile stats — owner-drawn tile band + ranked pills + meta row + status quote (replaces the old flat
                // dim-gray label that had no hierarchy and clipped the status line). Paint reads outer-scope fields that
                // ShowPlayer fills, then Invalidate()s — the same pattern achPanel uses (a build-time Paint lambda can't
                // capture ShowPlayer's async locals).
                int sLevel = 0, sMastery = 0, sWins = 0, sLosses = 0, sWorship = 0, sHours = 0, sAch = 0;
                string sRegion = "", sClan = "", sCreated = "", sLastSeen = "", sStatusMsg = "";
                var sRanked = new List<(string mode, string tier, int tierNum, int mmr, string rec)>();
                bool sLoaded = false;
                Color TierColor(string tier)
                {
                    if (tier.StartsWith("Bronze")) return Color.FromArgb(176, 124, 78);
                    if (tier.StartsWith("Silver")) return Color.FromArgb(186, 194, 204);
                    if (tier.StartsWith("Gold")) return Theme.Yellow;
                    if (tier.StartsWith("Platinum")) return Color.FromArgb(79, 208, 197);
                    if (tier.StartsWith("Diamond")) return Color.FromArgb(111, 195, 255);
                    if (tier.StartsWith("Master")) return Theme.Purple;
                    if (tier.StartsWith("Grandmaster")) return Theme.AccentHi;
                    return Theme.Dim;
                }
                void DrawPill(Graphics g, Rectangle r, int rad, Color fill, Color border)
                {
                    using var pth = new GraphicsPath();
                    pth.AddArc(r.X, r.Y, rad, rad, 180, 90);
                    pth.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
                    pth.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
                    pth.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
                    pth.CloseFigure();
                    using (var b = new SolidBrush(fill)) g.FillPath(b, pth);
                    using (var pen = new Pen(border)) g.DrawPath(pen, pth);
                }
                var statsPanel = new Panel { Location = new Point(S(14), S(44)), Size = new Size(S(920), S(164)), BackColor = Theme.Bg };
                statsPanel.Paint += (s, e) =>
                {
                    if (!sLoaded) return;
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    int W = statsPanel.Width, games = sWins + sLosses, winPct = games > 0 ? sWins * 100 / games : 0;
                    // ---- six stat tiles: big value + small all-caps label (the achPanel idiom) ----
                    var tiles = new (string label, string val, Color col)[]
                    {
                        ("LEVEL",        sLevel.ToString(),                  Theme.Yellow),
                        ("WIN RATE",     games > 0 ? winPct + "%" : "—",      games == 0 ? Theme.Dim : (winPct >= 50 ? Theme.Green : Theme.AccentHi)),
                        ("MASTERY",      sMastery.ToString(),                Theme.Yellow),
                        ("HOURS",        sHours.ToString("N0"),              Theme.Yellow),
                        ("WORSHIPPERS",  sWorship.ToString("N0"),            Theme.Yellow),
                        ("ACHIEVEMENTS", sAch.ToString(),                    Theme.Yellow),
                    };
                    int n = tiles.Length, tileW = Math.Max(S(118), W / n);
                    var valFont = Theme.F(19f, FontStyle.Bold);
                    var capFont = Theme.F(8f, FontStyle.Bold);
                    for (int i = 0; i < n; i++)
                    {
                        int x = i * tileW;
                        TextRenderer.DrawText(g, tiles[i].val, valFont, new Point(x, S(0)), tiles[i].col, TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, tiles[i].label, capFont, new Point(x + S(1), S(33)), Theme.Dim, TextFormatFlags.NoPrefix);
                        if (i == 1)   // win-rate tile gets a thin record bar + the raw W/L beneath it
                        {
                            int barY = S(50), barW = tileW - S(22), barH = S(4);
                            using (var tb = new SolidBrush(Theme.Input)) g.FillRectangle(tb, x, barY, barW, barH);
                            if (games > 0) using (var fb = new SolidBrush(winPct >= 50 ? Theme.Green : Theme.AccentHi)) g.FillRectangle(fb, x, barY, barW * winPct / 100, barH);
                            TextRenderer.DrawText(g, sWins.ToString("N0") + "W / " + sLosses.ToString("N0") + "L", capFont, new Point(x + S(1), S(58)), Theme.Dim, TextFormatFlags.NoPrefix);
                        }
                    }
                    // ---- divider ----
                    int dy = S(82);
                    using (var lp = new Pen(Theme.Line)) g.DrawLine(lp, S(0), dy, W - S(8), dy);
                    // ---- ranked: tier-coloured pills on their own line (top-5 info, no longer buried in gray) ----
                    int py = dy + S(11), px = S(0);
                    var pillFont = Theme.F(8.5f, FontStyle.Bold);
                    if (sRanked.Count == 0)
                    {
                        string txt = "Unranked this season";
                        int pw = TextRenderer.MeasureText(g, txt, pillFont, Size.Empty, TextFormatFlags.NoPrefix).Width + S(20);
                        DrawPill(g, new Rectangle(px, py, pw, S(26)), S(7), Theme.Input, Color.FromArgb(70, 150, 150, 150));
                        TextRenderer.DrawText(g, txt, pillFont, new Rectangle(px, py, pw, S(26)), Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                    else
                        foreach (var (mode, tier, tierNum, mmr, rec) in sRanked)
                        {
                            var tc = TierColor(tier);
                            var emb = RankEmblem(tierNum, S(20));
                            string txt = mode + " · " + tier + (mmr > 0 ? " · " + mmr + " MMR" : "") + (rec.Length > 0 ? " · " + rec : "");
                            int ph = S(26), embW = emb != null ? S(20) : 0;
                            int tw = TextRenderer.MeasureText(g, txt, pillFont, Size.Empty, TextFormatFlags.NoPrefix).Width;
                            int pw = S(9) + embW + (embW > 0 ? S(6) : 0) + tw + S(12);
                            DrawPill(g, new Rectangle(px, py, pw, ph), S(7), Theme.Input, Color.FromArgb(130, tc));
                            int ix = px + S(9);
                            if (emb != null) { g.DrawImage(emb, ix, py + (ph - S(20)) / 2, S(20), S(20)); ix += S(20) + S(6); }
                            TextRenderer.DrawText(g, txt, pillFont, new Rectangle(ix, py, px + pw - ix, ph), tc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                            px += pw + S(8);
                        }
                    // ---- meta row: [flag] Region · Clan · Member since · Last seen (platform omitted — its logo is in the name row) ----
                    int my = py + S(31);
                    var metaFont = Theme.F(8.5f);
                    int mh = TextRenderer.MeasureText(g, "Ag", metaFont, Size.Empty, TextFormatFlags.NoPrefix).Height, mx = S(0);
                    if (sRegion.Length > 0)
                    {
                        var flag = RegionFlag(sRegion, S(13));
                        if (flag != null) { g.DrawImage(flag, mx, my + (mh - flag.Height) / 2, flag.Width, flag.Height); mx += flag.Width + S(7); }
                        TextRenderer.DrawText(g, sRegion, metaFont, new Point(mx, my), Theme.Dim, TextFormatFlags.NoPrefix);
                        mx += TextRenderer.MeasureText(g, sRegion, metaFont, Size.Empty, TextFormatFlags.NoPrefix).Width;
                    }
                    var rest = new List<string>();
                    if (sClan.Length > 0) rest.Add("Clan: " + sClan);
                    if (sCreated.Length > 0) rest.Add("Member since " + sCreated);
                    if (sLastSeen.Length > 0) rest.Add("Last seen " + sLastSeen);
                    if (rest.Count > 0)
                        TextRenderer.DrawText(g, (sRegion.Length > 0 ? "   ·   " : "") + string.Join("   ·   ", rest), metaFont, new Rectangle(mx, my, W - S(8) - mx, S(18)), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    // ---- status message: blue italic quote, its own row, only when present ----
                    if (!string.IsNullOrWhiteSpace(sStatusMsg))
                        TextRenderer.DrawText(g, "“" + sStatusMsg + "”", Theme.F(9.5f, FontStyle.Italic), new Rectangle(S(0), my + S(20), W - S(8), S(20)), Theme.Blue, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                };
                // Achievements sub-tab — a dedicated FULL-AREA view built from REAL child controls so the panel's native
                // AutoScroll just works (owner-drawing into a scrolling panel ghosts: WinForms blits old pixels and only
                // repaints the exposed strip). achStats is (section, label, value-string); RenderAch() lays it out.
                var achStats = new List<(string section, string label, string value)>();
                string achWho = "";
                var achTitleFont = Theme.F(13.5f, FontStyle.Bold);
                var achSecFont = Theme.F(10f, FontStyle.Bold);
                var achValFont = Theme.F(15f, FontStyle.Bold);
                var achLblFont = Theme.F(8f, FontStyle.Bold);
                int achRowW = -1;   // last laid-out row width (responsive rebuild guard)
                Color AchSecColor(string sec) => sec switch
                {
                    "CAREER" => Theme.Blue,
                    "COMBAT" => Theme.Accent,
                    "MULTI-KILLS" => Theme.Yellow,
                    "KILLING SPREES" => Color.FromArgb(214, 120, 40),
                    "OBJECTIVES" => Theme.Green,
                    "FARM" => Color.FromArgb(160, 130, 220),
                    _ => Theme.Accent,
                };
                var achPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, AutoScroll = true, Padding = new Padding(S(22), S(12), S(16), S(18)) };
                var achFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg, Margin = new Padding(0) };
                achPanel.Controls.Add(achFlow);
                int AchRowWidth() => Math.Max(S(360), PhysicalClientWidth() - S(190) - S(64));   // physical width: managed width inflates at mixed DPI
                // one stat card: white value, dim caps label, a slim left accent bar for section identity.
                Panel MakeAchTile(string label, string val, Color accent)
                {
                    var tile = new Panel { Size = new Size(S(168), S(58)), Margin = new Padding(0, 0, S(10), S(10)), BackColor = Theme.Bg };
                    tile.Paint += (s, e) =>
                    {
                        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                        DrawPill(g, new Rectangle(0, 0, tile.Width - 1, tile.Height - 1), S(8), Theme.Panel, Color.FromArgb(48, accent.R, accent.G, accent.B));
                        using (var ab = new SolidBrush(Color.FromArgb(170, accent.R, accent.G, accent.B))) g.FillRectangle(ab, S(1), S(11), S(3), tile.Height - S(22));
                        TextRenderer.DrawText(g, val, achValFont, new Rectangle(S(14), S(9), tile.Width - S(20), S(24)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                        TextRenderer.DrawText(g, label.ToUpperInvariant(), achLblFont, new Rectangle(S(14), S(36), tile.Width - S(20), S(16)), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    };
                    return tile;
                }
                void RenderAch()
                {
                    achRowW = AchRowWidth();
                    achFlow.SuspendLayout();
                    achFlow.Controls.Clear();
                    achFlow.Controls.Add(new Label { Text = "ACHIEVEMENTS & CAREER" + (string.IsNullOrEmpty(achWho) ? "" : "   —   " + achWho), AutoSize = true, UseMnemonic = false, ForeColor = Theme.Text, Font = achTitleFont, Margin = new Padding(0, 0, 0, S(14)) });
                    if (achStats.Count == 0)
                        achFlow.Controls.Add(new Label { Text = "Load a player on the Track tab to see their career stats and achievements.", AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(9f) });
                    else
                    {
                        var order = new List<string>(); var bySec = new Dictionary<string, List<(string label, string val)>>();
                        foreach (var (sec, label, val) in achStats) { if (!bySec.TryGetValue(sec, out var l)) { order.Add(sec); bySec[sec] = l = new(); } l.Add((label, val)); }
                        foreach (var sec in order)
                        {
                            var col = AchSecColor(sec);
                            var hdr = new Panel { Size = new Size(achRowW, S(26)), Margin = new Padding(0, S(4), 0, S(8)), BackColor = Theme.Bg };
                            hdr.Paint += (s, e) =>
                            {
                                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                                TextRenderer.DrawText(g, sec, achSecFont, new Point(0, S(3)), col, TextFormatFlags.NoPrefix);
                                using (var lp = new Pen(Color.FromArgb(64, col.R, col.G, col.B))) g.DrawLine(lp, 0, S(23), hdr.Width, S(23));
                            };
                            achFlow.Controls.Add(hdr);
                            var rowPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(achRowW, 0), Margin = new Padding(0, 0, 0, S(12)), BackColor = Theme.Bg };
                            foreach (var (label, val) in bySec[sec]) rowPanel.Controls.Add(MakeAchTile(label, val, col));
                            achFlow.Controls.Add(rowPanel);
                        }
                    }
                    achFlow.ResumeLayout(true);
                }
                achPanel.Resize += (s, e) => { if (achPanel.Visible && Math.Abs(AchRowWidth() - achRowW) > S(24)) RenderAch(); };   // re-wrap on significant resize only

                // ===== Encounters — SMITE-GURU head-to-head ("how many times did A play with/against B, across years") =====
                var encPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
                var encTop = new Panel { Dock = DockStyle.Top, Height = S(156), BackColor = Theme.Bg };
                encTop.Controls.Add(new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Text, Font = Theme.F(13.5f, FontStyle.Bold), Location = new Point(S(22), S(8)), Text = "Encounters  (experimental)" });
                var encTip = new ToolTip();
                // Two players side by side. Each: a name box + "+" to tie extra accounts/smurfs + "★" presets. Compare scans
                // every account on each side and finds games where ANY A-account met ANY B-account (union by match id).
                var encBtn = MkBtn("Compare", 100, false, Theme.Blue, Color.White); encBtn.Location = new Point(S(330), S(9));
                var encRefresh = MkBtn("↻ Refresh", 96, false); encRefresh.Location = new Point(S(438), S(9));
                var encCancel = MkBtn("✕ Cancel", 96, false, Theme.Input, Theme.Accent); encCancel.Location = new Point(S(540), S(9)); encCancel.Visible = false;   // shown only while a scan is running
                var encStatus = new Label { AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Location = new Point(S(24), S(128)), MaximumSize = new Size(S(940), 0), Text = "" };
                var boxA = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f) };
                var boxB = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f) };
                try { boxA.PlaceholderText = "player A…"; boxB.PlaceholderText = "player B…"; } catch { }
                var boxAhost = WrapInput(boxA, S(208)); boxAhost.Location = new Point(S(24), S(50));
                var boxBhost = WrapInput(boxB, S(208)); boxBhost.Location = new Point(S(360), S(50));
                encTop.Controls.Add(new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Dim, Font = Theme.F(8f, FontStyle.Bold), Location = new Point(S(26), S(36)), Text = "PLAYER A" });
                encTop.Controls.Add(new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Dim, Font = Theme.F(8f, FontStyle.Bold), Location = new Point(S(362), S(36)), Text = "PLAYER B" });
                encTop.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(10f, FontStyle.Bold), Location = new Point(S(332), S(54)), Text = "vs" });
                var addA = MkBtn("＋", 30, false); addA.Location = new Point(S(236), S(49)); encTip.SetToolTip(addA, "Tie another account / smurf to player A");
                var presA = MkBtn("★", 30, false); presA.Location = new Point(S(270), S(49)); encTip.SetToolTip(presA, "Presets — save or load this person's accounts");
                var addB = MkBtn("＋", 30, false); addB.Location = new Point(S(572), S(49)); encTip.SetToolTip(addB, "Tie another account / smurf to player B");
                var presB = MkBtn("★", 30, false); presB.Location = new Point(S(606), S(49)); encTip.SetToolTip(presB, "Presets — save or load this person's accounts");
                // AutoSize height: when several accounts are tied, the chips wrap to extra rows and the panel grows DOWN (a fixed
                // height clipped the 2nd row into a thin sliver — the "bar under NuclearFart" glitch). RelayoutEncTop() then pushes
                // the status/loading line below the taller of the two chip stacks.
                var chipsA = new FlowLayoutPanel { Location = new Point(S(24), S(86)), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(290), 0), MaximumSize = new Size(S(290), 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = false, BackColor = Theme.Bg };
                var chipsB = new FlowLayoutPanel { Location = new Point(S(360), S(86)), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(290), 0), MaximumSize = new Size(S(290), 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = false, BackColor = Theme.Bg };
                encTop.Controls.Add(boxAhost); encTop.Controls.Add(boxBhost); encTop.Controls.Add(addA); encTop.Controls.Add(presA); encTop.Controls.Add(addB); encTop.Controls.Add(presB); encTop.Controls.Add(chipsA); encTop.Controls.Add(chipsB); encTop.Controls.Add(encBtn); encTop.Controls.Add(encRefresh); encTop.Controls.Add(encCancel); encTop.Controls.Add(encStatus);
                // Blinking red-bordered "Loading scoreboard…" indicator — a match fetch takes a few seconds, so without this the
                // click looked dead and users clicked again → stacked windows. A re-entrancy guard (encScoreBusy) also blocks that.
                bool encBusyOn = true, encScoreBusy = false;
                var encBusy = new Panel { Visible = false, Size = new Size(S(248), S(30)), Location = new Point(S(24), S(124)), BackColor = Theme.Bg };
                encBusy.Paint += (s, e) =>
                {
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    var col = encBusyOn ? Theme.Accent : Color.FromArgb(70, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B);
                    using (var pen = new Pen(col, S(2))) g.DrawRectangle(pen, S(1), S(1), encBusy.Width - S(3), encBusy.Height - S(3));
                    TextRenderer.DrawText(g, "⏳  Loading scoreboard…", Theme.F(9.5f, FontStyle.Bold), encBusy.ClientRectangle, encBusyOn ? Theme.Text : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                };
                var encBusyTimer = new System.Windows.Forms.Timer { Interval = 450 };
                encBusyTimer.Tick += (s, e) => { encBusyOn = !encBusyOn; encBusy.Invalidate(); };
                encTop.Controls.Add(encBusy);
                void ShowEncBusy(bool on) { if (on) { encBusyOn = true; encBusy.Visible = true; encBusy.BringToFront(); encBusyTimer.Start(); } else { encBusyTimer.Stop(); encBusy.Visible = false; } }
                // Blinking red "scanning…" indicator on the RIGHT (mirrors the scoreboard one) so an in-progress history scan reads as
                // clearly LIVE. The plain encStatus label (left) is reused for the FINAL summary once the scan settles.
                bool encScanOn = true; string encScanText = "";
                var encScan = new Panel { Visible = false, Size = new Size(S(440), S(32)), BackColor = Theme.Bg };
                encScan.Paint += (s, e) =>
                {
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    var col = encScanOn ? Theme.Accent : Color.FromArgb(70, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B);
                    using (var pen = new Pen(col, S(2))) g.DrawRectangle(pen, S(1), S(1), encScan.Width - S(3), encScan.Height - S(3));
                    using (var dot = new SolidBrush(col)) g.FillEllipse(dot, S(12), encScan.Height / 2 - S(4), S(9), S(9));
                    TextRenderer.DrawText(g, encScanText, Theme.F(9f, FontStyle.Bold), new Rectangle(S(28), 0, encScan.Width - S(36), encScan.Height), encScanOn ? Theme.Text : Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                };
                var encScanTimer = new System.Windows.Forms.Timer { Interval = 450 };
                encScanTimer.Tick += (s, e) => { encScanOn = !encScanOn; encScan.Invalidate(); };
                encTop.Controls.Add(encScan);
                encTip.SetToolTip(encScan, "Reading each account's full match history from smite.guru to find games the two players shared. Very active accounts take longer — results appear as soon as the first side is scanned.");
                void ShowEncScan(bool on, string text = "") { if (on) { encScanText = text; encScanOn = true; encScan.Location = new Point(S(24), encStatus.Top - S(5)); encScan.Visible = true; encScan.BringToFront(); encScanTimer.Start(); } else { encScanTimer.Stop(); encScan.Visible = false; } }
                void SetEncScan(string text) { encScanText = text; if (encScan.Visible) encScan.Invalidate(); }
                async Task OpenScoreboard(string matchId)
                {
                    if (encScoreBusy || string.IsNullOrEmpty(matchId)) return;   // one fetch at a time → no stacked windows on repeated clicks
                    encScoreBusy = true; ShowEncBusy(true);
                    try { await ShowSguruMatch(matchId); }
                    finally { encScoreBusy = false; ShowEncBusy(false); }
                }
                var encScroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(S(22), S(4), S(16), S(16)) };
                var encFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg };
                encScroll.Controls.Add(encFlow);
                encPanel.Controls.Add(encScroll); encPanel.Controls.Add(encTop);
                BeginInvoke(new Action(() => { try { RelayoutEncTop(); } catch { } }));   // initial status/encTop placement (no chips yet)
                System.Threading.CancellationTokenSource encCts = null;
                // alias line is rendered immediately (shared-match names) then enriched in place once B's own-history names load
                Label encAliasLbl = null; List<string> encSharedAliases = new(); List<string> encExclude = new();   // encExclude = B's typed account names (kept out of the "also seen as" line)
                // extra accounts (smurfs) tied to each side, beyond the name in the box; + saved presets (named account groups)
                var accA = new List<string>(); var accB = new List<string>();
                List<EncPreset> encPresets = new();
                try { var pf0 = Path.Combine(Theme.DataDir, "enc_presets.json"); if (File.Exists(pf0)) encPresets = (JsonSerializer.Deserialize<EncPresetFile>(File.ReadAllText(pf0)) ?? new()).Presets ?? new(); } catch { }
                void SaveEncPresets() { try { Theme.AtomicWriteText(Path.Combine(Theme.DataDir, "enc_presets.json"), JsonSerializer.Serialize(new EncPresetFile { Presets = encPresets })); } catch { } }   // atomic
                // Reflow the status/loading line to sit just below the (possibly multi-row) chip stacks, and size encTop to match,
                // so wrapped chips push everything down instead of being clipped or overlapping the status text.
                void RelayoutEncTop()
                {
                    int chH = Math.Max(chipsA.Height, chipsB.Height);          // 0 when no accounts are tied
                    int top = S(86) + Math.Max(S(34), chH) + S(6);
                    encStatus.Location = new Point(S(24), top);
                    encBusy.Location = new Point(S(24), top - S(4));
                    encScan.Location = new Point(S(24), top - S(5));   // blinking scan box sits at the status line (left, always on-screen)
                    encTop.Height = top + S(46);                                // room for a (possibly 2-line) status / the loading box
                }
                void RebuildChips(FlowLayoutPanel host, List<string> acc)
                {
                    host.SuspendLayout(); host.Controls.Clear();
                    foreach (var nm in acc.ToList())
                    {
                        var chip = MkBtn(nm + "  ✕", 60, false);
                        chip.AutoSize = true; chip.AutoSizeMode = AutoSizeMode.GrowAndShrink; chip.Margin = new Padding(0, 0, S(5), S(3)); chip.Font = Theme.F(8.5f); chip.Padding = new Padding(S(6), S(1), S(6), S(1));
                        string cap = nm; chip.Click += (s, e) => { acc.RemoveAll(a => string.Equals(a, cap, StringComparison.OrdinalIgnoreCase)); RebuildChips(host, acc); };
                        encTip.SetToolTip(chip, "Remove " + nm);
                        host.Controls.Add(chip);
                    }
                    host.ResumeLayout(true);
                    RelayoutEncTop();
                }
                void AddAccount(TextBox box, List<string> acc, FlowLayoutPanel host)
                {
                    var n = box.Text.Trim(); if (n.Length == 0) { box.Focus(); return; }
                    if (!acc.Any(a => string.Equals(a, n, StringComparison.OrdinalIgnoreCase))) acc.Add(n);
                    box.Clear(); RebuildChips(host, acc); box.Focus();
                }
                void ShowPresetMenu(Button anchor, TextBox box, List<string> acc, FlowLayoutPanel host)
                {
                    var menu = new ContextMenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Text };
                    var save = new ToolStripMenuItem("Save these accounts as a preset…") { ForeColor = Theme.Text };
                    save.Click += (s, e) =>
                    {
                        var all = new List<string>(acc); var t = box.Text.Trim();
                        if (t.Length > 0 && !all.Any(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase))) all.Add(t);
                        if (all.Count == 0) { MessageBox.Show(this, "Type or add at least one account first.", "Presets"); return; }
                        var nm = PromptText("Save preset", "Name this group of accounts (e.g. all of one person's smurfs)", all[0]);
                        if (string.IsNullOrWhiteSpace(nm)) return;
                        encPresets.RemoveAll(p => string.Equals(p.Name, nm.Trim(), StringComparison.OrdinalIgnoreCase));
                        encPresets.Add(new EncPreset { Name = nm.Trim(), Accounts = all }); SaveEncPresets();
                    };
                    menu.Items.Add(save);
                    if (encPresets.Count > 0)
                    {
                        menu.Items.Add(new ToolStripSeparator());
                        foreach (var p in encPresets.OrderBy(p => p.Name))
                        {
                            var pp = p; var it = new ToolStripMenuItem(pp.Name + "   (" + pp.Accounts.Count + ")") { ForeColor = Theme.Text };
                            it.Click += (s, e) => { acc.Clear(); acc.AddRange(pp.Accounts); box.Clear(); RebuildChips(host, acc); };
                            menu.Items.Add(it);
                        }
                        menu.Items.Add(new ToolStripSeparator());
                        var del = new ToolStripMenuItem("Delete a preset") { ForeColor = Theme.Dim };
                        foreach (var p in encPresets.OrderBy(p => p.Name)) { var pp = p; var di = new ToolStripMenuItem(pp.Name) { ForeColor = Theme.Text }; di.Click += (s, e) => { encPresets.RemoveAll(x => x == pp); SaveEncPresets(); }; del.DropDownItems.Add(di); }
                        menu.Items.Add(del);
                    }
                    menu.Closed += (s, e) => menu.BeginInvoke((Action)menu.Dispose);   // dispose the one-shot menu after it closes (else it leaks every open)
                    menu.Show(anchor, new Point(0, anchor.Height));
                }
                addA.Click += (s, e) => AddAccount(boxA, accA, chipsA);
                addB.Click += (s, e) => AddAccount(boxB, accB, chipsB);
                presA.Click += (s, e) => ShowPresetMenu(presA, boxA, accA, chipsA);
                presB.Click += (s, e) => ShowPresetMenu(presB, boxB, accB, chipsB);
                string EncQueue(int q) => q switch { 426 => "Conquest", 451 => "Ranked Conquest", 459 => "Conquest", 435 => "Arena", 448 => "Joust", 450 => "Ranked Joust", 440 => "Ranked Duel", 445 => "Assault", 466 => "Clash", 10189 => "Slash", 504 => "Slash", _ => "Queue " + q };
                string EncDate(string iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToString("yyyy-MM-dd") : (iso ?? "");
                Panel MakeEncRow(SmiteGuru.Match m, bool allied, bool aWon, string acctTag = "")
                {
                    int w = AchRowWidth();
                    bool hasTag = !string.IsNullOrEmpty(acctTag);
                    // Two-line row when an account tag is present: the main columns sit in a fixed S(34) top band and the "which
                    // account met which" line goes underneath (full width, so it never clips at the right edge like a wider row would).
                    int band = S(34);
                    var row = new Panel { Size = new Size(Math.Min(w, S(620)), hasTag ? S(54) : band), Margin = new Padding(0, 0, 0, S(5)), BackColor = Theme.Bg, Cursor = Cursors.Hand };
                    var accent = allied ? Theme.Green : Theme.Accent;
                    string dateStr = EncDate(m.Time), queueStr = EncQueue(m.QueueId);   // precompute once — Paint fires on every hover/scroll
                    bool hover = false;   // click a row → open that game's scoreboard (smite.guru match_id == Hi-Rez match id)
                    row.Paint += (s, e) =>
                    {
                        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                        var border = Color.FromArgb(hover ? 150 : 46, accent.R, accent.G, accent.B);
                        DrawPill(g, new Rectangle(0, 0, row.Width - 1, row.Height - 1), S(6), hover ? Theme.Input : Theme.Panel, border);
                        using (var ab = new SolidBrush(accent)) g.FillRectangle(ab, S(1), S(7), S(3), row.Height - S(14));
                        TextRenderer.DrawText(g, dateStr, Theme.F(9f), new Rectangle(S(14), 0, S(104), band), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, queueStr, Theme.F(9f), new Rectangle(S(126), 0, S(170), band), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, allied ? "ALLIES" : "ENEMIES", Theme.F(8f, FontStyle.Bold), new Rectangle(S(304), 0, S(86), band), accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, aWon ? "WIN" : "LOSS", Theme.F(8.5f, FontStyle.Bold), new Rectangle(S(396), 0, S(70), band), aWon ? Theme.Green : Theme.AccentHi, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, hover ? "View scoreboard ›" : "›", Theme.F(8.5f, FontStyle.Bold), new Rectangle(row.Width - S(154), 0, S(146), band), hover ? accent : Theme.Dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        if (hasTag)   // second line: which tied account this encounter is from (only when a side has smurfs)
                            TextRenderer.DrawText(g, acctTag, Theme.F(8.5f, FontStyle.Bold), new Rectangle(S(14), band - S(2), row.Width - S(28), row.Height - band), Theme.Blue, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    };
                    row.MouseEnter += (s, e) => { hover = true; row.Invalidate(); };
                    row.MouseLeave += (s, e) => { hover = false; row.Invalidate(); };
                    row.Click += async (s, e) => await OpenScoreboard(m.MatchId);   // SmiteGuru scoreboard (guarded + blinking "Loading…" so repeated clicks don't stack windows)
                    return row;
                }
                void RenderEnc(HashSet<long> aIds, HashSet<long> bIds, List<string> aNames, List<string> bNames, List<SmiteGuru.Match> all, IReadOnlyCollection<string> bOwnNames)
                {
                    encFlow.SuspendLayout(); encFlow.Controls.Clear(); encAliasLbl = null; encSharedAliases = new(); encExclude = new List<string>(bNames);
                    string Lbl(List<string> ns) => ns.Count == 0 ? "?" : ns[0] + (ns.Count > 1 ? "  +" + (ns.Count - 1) : "");
                    string aLabel = Lbl(aNames), bLabel = Lbl(bNames);
                    encFlow.Controls.Add(new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Text, Font = Theme.F(12.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(10)), Text = aLabel + "   vs   " + bLabel });
                    // Match by STABLE id (survives renames) OR any typed account name. Each side can be MULTIPLE accounts (smurfs);
                    // also fold in any roster id ever seen under a typed name (catches renames on either side).
                    var aNameSet = new HashSet<string>(aNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                    var bNameSet = new HashSet<string>(bNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                    foreach (var m in all) if (m.Players != null) foreach (var p in m.Players) { if (p.Id == 0 || string.IsNullOrWhiteSpace(p.Name)) continue; var nm = p.Name.Trim(); if (bNameSet.Contains(nm)) bIds.Add(p.Id); if (aNameSet.Contains(nm)) aIds.Add(p.Id); }
                    bool IsA(SmiteGuru.Player p) => (p.Id != 0 && aIds.Contains(p.Id)) || (!string.IsNullOrWhiteSpace(p.Name) && aNameSet.Contains(p.Name.Trim()));
                    bool IsB(SmiteGuru.Player p) => (p.Id != 0 && bIds.Contains(p.Id)) || (!string.IsNullOrWhiteSpace(p.Name) && bNameSet.Contains(p.Name.Trim()));
                    // when more than one account is tied to a side, each row tags WHICH account met which (aAt = the A-account that
                    // played that game, bAt = the B-account) so the user can see the encounter's source smurf.
                    bool multiA = aNames.Count > 1, multiB = bNames.Count > 1;
                    var hits = new List<(SmiteGuru.Match m, bool allied, bool aWon, string aAt, string bAt)>();
                    foreach (var m in all)
                    {
                        if (m.Players == null) continue;
                        var ap = m.Players.FirstOrDefault(IsA); if (ap == null) continue;
                        var bp = m.Players.FirstOrDefault(p => p != ap && IsB(p) && !IsA(p)); if (bp == null) continue;   // opponent must be a DIFFERENT identity (guards same-person-on-both-sides)
                        hits.Add((m, ap.Team == bp.Team, m.WinningTeam == ap.Team, ap.Name, bp.Name));
                    }
                    if (hits.Count == 0)
                    {
                        encFlow.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(9.5f), MaximumSize = new Size(S(640), 0), Text = "No shared matches between \"" + aLabel + "\" and \"" + bLabel + "\" in " + all.Count.ToString("N0") + " scanned games." });
                        encFlow.ResumeLayout(true); return;
                    }
                    int total = hits.Count, allied = hits.Count(h => h.allied), against = total - allied;
                    int agW = hits.Count(h => !h.allied && h.aWon), agL = against - agW;
                    int alW = hits.Count(h => h.allied && h.aWon), alL = allied - alW;
                    var tileRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(AchRowWidth(), 0), Margin = new Padding(0, 0, 0, S(12)) };
                    tileRow.Controls.Add(MakeAchTile("ENCOUNTERS", total.ToString(), Theme.Blue));
                    tileRow.Controls.Add(MakeAchTile("AS ENEMIES", against.ToString(), Theme.Accent));
                    tileRow.Controls.Add(MakeAchTile("AS ALLIES", allied.ToString(), Theme.Green));
                    tileRow.Controls.Add(MakeAchTile("VS THEM  W-L", agW + "-" + agL, Theme.Accent));
                    tileRow.Controls.Add(MakeAchTile("WITH THEM  W-L", alW + "-" + alL, Theme.Green));
                    encFlow.Controls.Add(tileRow);
                    // name history (same id, different names over the years). Two sources, unioned:
                    //   • shared-match rosters (h.bAt) — names B used in games A was also in (available now)
                    //   • B's OWN full history (bOwnNames) — every name B ever used, incl. games A wasn't in (the complete set;
                    //     loaded a moment later → EnrichAliases updates this label in place, no re-render / scroll jump)
                    encSharedAliases = hits.Select(h => (h.bAt ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var aliasSet = new List<string>(encSharedAliases);
                    if (bOwnNames != null) aliasSet.AddRange(bOwnNames.Select(n => (n ?? "").Trim()));
                    var aliases = aliasSet.Where(n => n.Length > 0 && !bNameSet.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    encAliasLbl = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Margin = new Padding(S(2), 0, 0, S(8)), MaximumSize = new Size(AchRowWidth(), 0), Visible = aliases.Count > 0, Text = aliases.Count > 0 ? "Same account, also seen as: " + string.Join(", ", aliases) : "" };
                    encFlow.Controls.Add(encAliasLbl);
                    string span = EncDate(hits[hits.Count - 1].m.Time) + "   →   " + EncDate(hits[0].m.Time);
                    encFlow.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8.5f, FontStyle.Bold), Margin = new Padding(S(2), 0, 0, S(6)), Text = "MATCHES   ·   " + span });
                    // filter chips — show only "as enemies", "as allies", wins or losses (re-fills just the row list, keeps tiles)
                    int wins = agW + alW, losses = agL + alL;
                    var filterRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(AchRowWidth(), 0), Margin = new Padding(S(2), 0, 0, S(6)) };
                    var rowsHost = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
                    string curFilter = "all";
                    var chips = new List<(string key, Button btn)>();
                    void StyleChips() { foreach (var (k, b) in chips) { bool on = k == curFilter; b.BackColor = on ? Theme.Blue : Theme.Input; b.ForeColor = on ? Color.White : Theme.Dim; b.FlatAppearance.BorderColor = on ? Theme.Blue : Theme.Line; } }
                    void Repop()
                    {
                        rowsHost.SuspendLayout(); rowsHost.Controls.Clear();
                        IEnumerable<(SmiteGuru.Match m, bool allied, bool aWon, string aAt, string bAt)> sel = curFilter switch
                        {
                            "enemies" => hits.Where(h => !h.allied),
                            "allies" => hits.Where(h => h.allied),
                            "wins" => hits.Where(h => h.aWon),
                            "losses" => hits.Where(h => !h.aWon),
                            _ => hits
                        };
                        foreach (var h in sel)
                        {
                            string a = (h.aAt ?? "").Trim(), b = (h.bAt ?? "").Trim();
                            // when either side has smurfs, label every row with the exact pairing so the source account is unambiguous
                            string tag = (multiA || multiB) ? ((a.Length > 0 ? a : "(hidden)") + "  vs  " + (b.Length > 0 ? b : "(hidden)")) : "";
                            rowsHost.Controls.Add(MakeEncRow(h.m, h.allied, h.aWon, tag));
                        }
                        rowsHost.ResumeLayout(true);
                    }
                    void AddChip(string key, string label)
                    {
                        var b = MkBtn(label, Math.Max(60, 18 + label.Length * 7), false); b.Margin = new Padding(0, 0, S(6), S(4));
                        b.Click += (s, e) => { curFilter = key; StyleChips(); Repop(); };
                        chips.Add((key, b)); filterRow.Controls.Add(b);
                    }
                    AddChip("all", "All " + total); AddChip("enemies", "As enemies " + against); AddChip("allies", "As allies " + allied);
                    AddChip("wins", "Wins " + wins); AddChip("losses", "Losses " + losses);
                    encFlow.Controls.Add(filterRow);
                    encFlow.Controls.Add(rowsHost);
                    StyleChips(); Repop();
                    encFlow.ResumeLayout(true);
                }
                // Update the "also seen as" line in place once B's own-history names finish loading (no full re-render).
                void EnrichAliases(IReadOnlyCollection<string> bOwnNames)
                {
                    if (encAliasLbl == null || encAliasLbl.IsDisposed || bOwnNames == null) return;
                    var ex = new HashSet<string>(encExclude, StringComparer.OrdinalIgnoreCase);
                    var set = new List<string>(encSharedAliases);
                    set.AddRange(bOwnNames.Select(n => (n ?? "").Trim()));
                    var aliases = set.Where(n => n.Length > 0 && !ex.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (aliases.Count == 0) return;
                    encAliasLbl.Text = "Same account, also seen as: " + string.Join(", ", aliases);
                    encAliasLbl.Visible = true;
                }
                // Gather one side's accounts: the chips PLUS whatever is currently typed in its box.
                List<string> GatherSide(List<string> chipAcc, TextBox box)
                {
                    var l = new List<string>(chipAcc); var t = box.Text.Trim();
                    if (t.Length > 0 && !l.Any(a => string.Equals(a, t, StringComparison.OrdinalIgnoreCase))) l.Add(t);
                    return l;
                }
                async Task RunCompare(bool forceRefresh)
                {
                    var aAccts = GatherSide(accA, boxA); var bAccts = GatherSide(accB, boxB);
                    if (aAccts.Count == 0) { encStatus.ForeColor = Theme.Yellow; encStatus.Text = "Enter player A."; boxA.Focus(); return; }
                    if (bAccts.Count == 0) { encStatus.ForeColor = Theme.Yellow; encStatus.Text = "Enter player B."; boxB.Focus(); return; }
                    encCts?.Cancel();   // signal any in-flight compare to stop; only the newest run owns the UI (guarded below)
                    var myCts = new System.Threading.CancellationTokenSource(); encCts = myCts; var ct = myCts.Token;
                    _sguru ??= new SmiteGuru(this);
                    encBtn.Enabled = false; encRefresh.Enabled = false; encCancel.Visible = true; encCancel.Enabled = true; encCancel.BringToFront();
                    encStatus.Text = ""; ShowEncScan(true, "Resolving accounts…");
                    try
                    {
                        // resolve every account on each side to a STABLE Hi-Rez id (keeps the typed names for fallback / renames).
                        // `order` keeps the typed name alongside each unique id so the live scan indicator can name who it's reading.
                        async Task<(HashSet<long> ids, List<string> names, List<(long id, string nm)> order)> ResolveSide(List<string> accts)
                        {
                            var ids = new HashSet<long>(); var names = new List<string>(); var order = new List<(long, string)>();
                            foreach (var n in accts)
                            {
                                names.Add(n);
                                try { using var pd = JsonDocument.Parse(await SmiteApi.Call("getplayer", n)); if (pd.RootElement.ValueKind == JsonValueKind.Array && pd.RootElement.GetArrayLength() > 0) { var r0 = pd.RootElement[0]; string idv = GS(r0, "Id"); if (string.IsNullOrEmpty(idv) || idv == "0") idv = GS(r0, "ActivePlayerId"); if (long.TryParse(idv, out var id) && id > 0 && ids.Add(id)) order.Add((id, n)); } } catch { }
                            }
                            return (ids, names, order);
                        }
                        var (aIds, aNames, aOrder) = await ResolveSide(aAccts);
                        var (bIds, bNames, bOrder) = await ResolveSide(bAccts);
                        if (aIds.Count == 0 && bIds.Count == 0) { encStatus.ForeColor = Theme.Yellow; encStatus.Text = "Couldn't find those names on the SMITE API."; return; }
                        if (forceRefresh) foreach (var id in aIds.Concat(bIds)) await _sguru.WipeAsync(id, ct);   // gated wipe so it can't race an in-flight SaveCache
                        // scan EVERY account on both sides (season-by-season, back to 2020), union all matches by match_id.
                        var pool = new Dictionary<string, SmiteGuru.Match>();
                        var bOwn = new List<string>();
                        bool allComplete = true; int done = 0, totalAccts = aIds.Count + bIds.Count;
                        int NonZero(SmiteGuru.Match m) => m.Players == null ? 0 : m.Players.Count(p => p.Id != 0);
                        async Task Scan(long id, string nm, bool isB)
                        {
                            done++; int who = done;
                            var h = await _sguru.GetHistory(id, 400, (p, m) => { try { SetEncScan(p < 0 ? ("Scanning " + nm + " — filling in missed pages…") : ("Scanning " + nm + " — page " + p + " of ~" + m + "   (account " + who + " of " + totalAccts + ")")); } catch { } }, ct);
                            allComplete &= h.Complete;
                            // union by match_id; prefer the roster with MORE resolved (non-zero) ids so B's de-anonymized copy of a
                            // shared game upgrades A's anonymized one (the "one side was hidden" recovery).
                            foreach (var mm in h.Matches) { if (mm.MatchId == null) continue; if (!pool.TryGetValue(mm.MatchId, out var ex) || NonZero(mm) > NonZero(ex)) pool[mm.MatchId] = mm; }
                            if (isB) bOwn.AddRange(h.Matches.Where(mm => mm.Players != null).SelectMany(mm => mm.Players).Where(p => p.Id == id && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name.Trim()));
                        }
                        // encounter count mirrors RenderEnc's matching (id OR typed name) so the re-render gate can't miss name-only / hidden games
                        var aNameSetE = new HashSet<string>(aNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                        var bNameSetE = new HashSet<string>(bNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
                        bool IsAE(SmiteGuru.Player p) => (p.Id != 0 && aIds.Contains(p.Id)) || (!string.IsNullOrWhiteSpace(p.Name) && aNameSetE.Contains(p.Name.Trim()));
                        bool IsBE(SmiteGuru.Player p) => (p.Id != 0 && bIds.Contains(p.Id)) || (!string.IsNullOrWhiteSpace(p.Name) && bNameSetE.Contains(p.Name.Trim()));
                        int EncCount(List<SmiteGuru.Match> ms) => ms.Count(m => m.Players != null && m.Players.Any(IsAE) && m.Players.Any(p => IsBE(p) && !IsAE(p)));
                        // A side first → show results as soon as A is scanned
                        foreach (var (id, nm) in aOrder) await Scan(id, nm, false);
                        var listA = pool.Values.OrderByDescending(m => m.Time ?? "", StringComparer.Ordinal).ToList();
                        RenderEnc(new HashSet<long>(aIds), new HashSet<long>(bIds), aNames, bNames, listA, null);
                        int aOnly = EncCount(listA);
                        string Yr(string t) => !string.IsNullOrEmpty(t) && t.Length >= 4 ? t.Substring(0, 4) : "?";
                        void Finalize(List<SmiteGuru.Match> shown)
                        {
                            string span = shown.Count > 0 ? " (" + Yr(shown[shown.Count - 1].Time) + "–" + Yr(shown[0].Time) + ")" : "";
                            if (allComplete) { encStatus.ForeColor = Theme.Green; encStatus.Text = shown.Count.ToString("N0") + " games scanned" + span + "  ·  full history ✓"; }
                            else { encStatus.ForeColor = Theme.Yellow; encStatus.Text = shown.Count.ToString("N0") + " games scanned" + span + "  ·  ⚠ a very active account exceeds smite.guru's page limit — a slice of recent games may be missing (older seasons fully scanned)."; }
                        }
                        if (ct.IsCancellationRequested) return;
                        // then B side → fills any shared games missing from A's record (gaps / one side hidden) + B's rename history
                        foreach (var (id, nm) in bOrder) { if (ct.IsCancellationRequested) break; await Scan(id, nm, true); }
                        if (!ct.IsCancellationRequested)
                        {
                            var bOwnNames = bOwn.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            var listAll = pool.Values.OrderByDescending(m => m.Time ?? "", StringComparer.Ordinal).ToList();
                            if (EncCount(listAll) > aOnly) RenderEnc(new HashSet<long>(aIds), new HashSet<long>(bIds), aNames, bNames, listAll, bOwnNames);   // B added shared games → re-render
                            else EnrichAliases(bOwnNames);                                                                                                  // same encounters → just enrich names in place
                            Finalize(listAll);
                        }
                    }
                    catch (OperationCanceledException) { if (encCts == myCts) { encStatus.ForeColor = Theme.Dim; encStatus.Text = "Paused (progress saved — Compare again to resume)."; } }
                    catch (Exception ex) { if (encCts == myCts) { encStatus.ForeColor = Theme.Yellow; encStatus.Text = "Lookup failed: " + ex.Message; } }
                    finally { if (encCts == myCts) { ShowEncScan(false); encBtn.Enabled = true; encRefresh.Enabled = true; encCancel.Visible = false; } }   // only the newest run owns the buttons/status (superseded runs stay quiet)
                }
                encBtn.Click += async (s, e) => await RunCompare(false);
                encRefresh.Click += async (s, e) => await RunCompare(true);   // ↻ = wipe caches + full rescan (new games / retry gaps)
                encCancel.Click += (s, e) => { encCancel.Enabled = false; encCts?.Cancel(); SetEncScan("Cancelling…"); };   // stop the in-flight scan (progress is saved → Compare resumes)
                boxA.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunCompare(false); } };
                boxB.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunCompare(false); } };
                void ShowEncounters()
                {
                    addAllFriendsBtn.Visible = false; ShowStage(6);
                    bool loaded = !string.IsNullOrEmpty(curPid) && curPid != "0";
                    if (loaded && boxA.Text.Trim().Length == 0 && accA.Count == 0) boxA.Text = curName;   // default player A to the loaded player (editable)
                    hint.ForeColor = Theme.Dim; hint.Text = "Head-to-head between two players (with smurfs) across their full match history — via SmiteGuru.";
                    if (boxB.Text.Trim().Length == 0) boxB.Focus(); else encBtn.Focus();
                }

                // Owner-drawn list (search results / favorites / recents / friends). It lives in a FIXED-WIDTH column —
                // a Dock=Fill owner-draw list reads an inflated width under mixed DPI and draws its right-aligned glyphs
                // (trash / ☆ / status) off-screen; a fixed width keeps them in view (same fix as the rail Friend List).
                var plist = new PlayerList { Dock = DockStyle.Fill, Font = Theme.F(10.5f) };
                var listCol = new Panel { Dock = DockStyle.Left, Width = S(740), BackColor = Theme.Bg, Visible = false, Padding = new Padding(S(14), S(8), 0, S(8)) };
                listCol.Controls.Add(plist);
                var hiddenHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };   // "Custom Hidden Tags" primary tab
                // persistent search + sort toolbar over a re-renderable list (so typing in search never loses focus).
                var hidList = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(0, S(4), 0, S(8)) };
                var hidBar = new Panel { Dock = DockStyle.Top, Height = S(50), BackColor = Theme.Panel };
                hidBar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line });
                var hidSearch = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f) };
                try { hidSearch.PlaceholderText = "Search by name, clan, or god…"; } catch { }
                var hidSearchHost = WrapInput(hidSearch, S(240)); hidSearchHost.Location = new Point(S(14), S(13));
                hidBar.Controls.Add(new Label { Location = new Point(S(266), S(18)), AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Text = "Sort:" });
                var hidSortName = MkBtn("Name", 78, false, Theme.Input, Theme.Dim); hidSortName.Location = new Point(S(308), S(11));
                var hidSortConf = MkBtn("Confidence", 104, false, Theme.Input, Theme.Dim); hidSortConf.Location = new Point(S(390), S(11));
                var hidSortDate = MkBtn("Date tagged", 110, false, Theme.Input, Theme.Dim); hidSortDate.Location = new Point(S(498), S(11));
                hidBar.Controls.Add(hidSearchHost); hidBar.Controls.Add(hidSortName); hidBar.Controls.Add(hidSortConf); hidBar.Controls.Add(hidSortDate);
                hiddenHost.Controls.Add(hidList); hiddenHost.Controls.Add(hidBar);   // Fill added before Top → toolbar takes the top edge, list fills below
                int hidSort = 1;   // 0 Name · 1 Confidence · 2 Date tagged
                void StyleHidSort() { var bs = new[] { hidSortName, hidSortConf, hidSortDate }; for (int k = 0; k < bs.Length; k++) { bs[k].BackColor = k == hidSort ? Theme.Accent : Theme.Input; bs[k].ForeColor = k == hidSort ? Color.White : Theme.Dim; } }
                hidSearch.TextChanged += (s, e) => RenderHiddenList();
                hidSortName.Click += (s, e) => { hidSort = 0; StyleHidSort(); RenderHiddenList(); };
                hidSortConf.Click += (s, e) => { hidSort = 1; StyleHidSort(); RenderHiddenList(); };
                hidSortDate.Click += (s, e) => { hidSort = 2; StyleHidSort(); RenderHiddenList(); };
                StyleHidSort();
                card.Controls.Add(namePanel); card.Controls.Add(statusLbl); card.Controls.Add(statsPanel);
                card.Resize += (s, e) => { statsPanel.Width = card.Width - S(28); statsPanel.Invalidate(); statusLbl.Left = card.Width - S(180); namePanel.Width = Math.Max(S(200), card.Width - S(200)); namePanel.Invalidate(); };

                // --- two lists: god masteries | recent matches (deterministic 50/50 grid) ---
                ListView MkLv(params (string, int)[] cols)
                {
                    var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                        BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.F(9f), HideSelection = true };
                    foreach (var (h, w) in cols) lv.Columns.Add(h, S(w));
                    return lv;
                }
                var godLv = MkLv(("God", 150), ("Mastery", 60), ("Worshippers", 88), ("W", 48), ("L", 48), ("K/D/A", 140));
                var matchLv = MkLv(("God", 140), ("Queue", 150), ("Result", 64), ("K/D/A", 110), ("When", 150));
                trackGodImgs = new ImageList { ImageSize = new Size(S(22), S(22)), ColorDepth = ColorDepth.Depth32Bit };
                godLv.SmallImageList = trackGodImgs; matchLv.SmallImageList = trackGodImgs;
                var godHdr = new Label { Dock = DockStyle.Top, Height = S(26), ForeColor = Theme.Accent, Font = Theme.F(10f, FontStyle.Bold), Text = "  GOD MASTERIES", TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel };
                var matchHdr = new Label { Dock = DockStyle.Top, Height = S(26), ForeColor = Theme.Accent, Font = Theme.F(10f, FontStyle.Bold), Text = "  RECENT MATCHES  (double-click a row for the scoreboard)", TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel };
                var leftPane = new Panel { Dock = DockStyle.Left, Width = S(560), BackColor = Theme.Bg };
                var rightPane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
                // A real draggable Splitter (user can rebalance at small windows); resizes the masteries pane to its left.
                var divider = new Splitter { Dock = DockStyle.Left, Width = S(5), BackColor = Theme.Line, MinSize = S(340), MinExtra = S(380) };
                leftPane.Controls.Add(godLv); leftPane.Controls.Add(godHdr);
                rightPane.Controls.Add(matchLv); rightPane.Controls.Add(matchHdr);
                var splitHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
                splitHost.Controls.Add(rightPane); splitHost.Controls.Add(divider); splitHost.Controls.Add(leftPane);   // fill first, then splitter, then left pane

                // full-width expanded tables for the Masteries / Matches sub-tabs (more columns; one at a time)
                var godLvFull = MkLv(("God", 158), ("Mastery", 58), ("Worshippers", 100), ("W", 50), ("L", 50), ("Win %", 64), ("K/D/A", 140), ("KDA", 60), ("Minions", 84));
                var matchLvFull = MkLv(("God", 150), ("Queue", 150), ("Result", 72), ("K/D/A", 120), ("Level", 60), ("Damage", 92), ("Gold", 88), ("When", 170));
                godLvFull.SmallImageList = trackGodImgs; matchLvFull.SmallImageList = trackGodImgs;
                var godFullHdr = new Label { Dock = DockStyle.Top, Height = S(26), ForeColor = Theme.Accent, Font = Theme.F(10f, FontStyle.Bold), Text = "  GOD MASTERIES", TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel };
                var matchFullHdr = new Label { Dock = DockStyle.Top, Height = S(26), ForeColor = Theme.Accent, Font = Theme.F(10f, FontStyle.Bold), Text = "  RECENT MATCHES  (double-click a row for the scoreboard)", TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel };
                var godFullHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
                godFullHost.Controls.Add(godLvFull); godFullHost.Controls.Add(godFullHdr);
                var matchFullHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
                matchFullHost.Controls.Add(matchLvFull); matchFullHost.Controls.Add(matchFullHdr);

                var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
                body.Controls.Add(encPanel); body.Controls.Add(listCol); body.Controls.Add(hiddenHost); body.Controls.Add(achPanel); body.Controls.Add(matchFullHost); body.Controls.Add(godFullHost); body.Controls.Add(splitHost);
                // Stage = the whole area below the search bar. Only Overview shows the profile card + masteries|matches split;
                // every other tab hides the card and fills the area with one dedicated view.
                // 0 Overview · 1 Masteries · 2 Matches · 3 Achievements · 4 List (search results / favorites / recents / friends)
                void ShowStage(int st)
                {
                    bool ov = st == 0, loaded = !string.IsNullOrEmpty(curPid) && curPid != "0";
                    card.Visible = ov && loaded;
                    splitHost.Visible = ov && loaded;
                    godFullHost.Visible = st == 1;
                    matchFullHost.Visible = st == 2;
                    achPanel.Visible = st == 3;
                    listCol.Visible = st == 4;
                    hiddenHost.Visible = st == 5;
                    encPanel.Visible = st == 6;
                    (st == 1 ? (Control)godFullHost : st == 2 ? matchFullHost : st == 3 ? achPanel : st == 4 ? listCol : st == 5 ? hiddenHost : st == 6 ? encPanel : splitHost).BringToFront();
                    if (st == 3) achPanel.Invalidate();
                }
                // Default split width is in S() space; a ClientSize-derived /2 reads device px under PerMonitorV2 and collapses the right pane, so we DON'T auto-resize — the Splitter lets the user adjust.

                host.Controls.Add(body); host.Controls.Add(card); host.Controls.Add(hint); host.Controls.Add(top); host.Controls.Add(subBar2); host.Controls.Add(subBar);   // primary strip topmost, then secondary, search, status line
                trackerBox = box; trackGodLv = godLv; trackMatchLv = matchLv; trackSuggest = plist;   // fields for theming/focus from OnLoad/SwitchMode

                // Ensures the shared god ImageList holds an icon for this API god name; returns its index (or -1).
                int GodImg(string apiName)
                {
                    string k = NormName(apiName);
                    if (k.Length == 0) return -1;
                    if (godImgIdx.TryGetValue(k, out var idx)) return idx;
                    var img = GodListIcon(apiName);
                    if (img == null) { godImgIdx[k] = -1; return -1; }
                    int i = trackGodImgs.Images.Count; trackGodImgs.Images.Add(img); godImgIdx[k] = i; return i;
                }
                PlayerRow MakeRow(string name, string id, int portal, bool priv, bool deletable)
                {
                    var (code, col) = PlatformChip(portal);
                    return new PlayerRow { Name = name, Id = id, Portal = portal, Priv = priv, Deletable = deletable, Plat = code, PlatCol = col };
                }

                void ResetCard()
                {
                    igName = ""; linkedAccts.Clear(); namePanel.Invalidate();
                    achStats.Clear(); achWho = ""; RenderAch();
                    subBar2.Visible = false;   // no player → hide the player-context sub-tabs
                    sLoaded = false; statsPanel.Invalidate(); statusLbl.Visible = false; statusLbl.Cursor = Cursors.Default;
                    godLv.Items.Clear(); matchLv.Items.Clear(); godLvFull.Items.Clear(); matchLvFull.Items.Clear();
                    godHdr.Text = "  GOD MASTERIES"; matchHdr.Text = "  RECENT MATCHES  (double-click a row for the scoreboard)";
                    curPid = ""; curName = ""; curPortal = 0; curLiveMatch = ""; UpdateFavStar(); UpdateFriendBtn();
                }

                void UpdateFavStar()
                {
                    bool can = !string.IsNullOrEmpty(curPid) && curPid != "0";
                    bool fav = can && IsFav(curPid);
                    favSaveBtn.Enabled = can;
                    favSaveBtn.Text = fav ? "★ Saved" : "☆ Save";
                    favSaveBtn.ForeColor = fav ? Theme.Yellow : Theme.Dim;
                }

                void UpdateFriendBtn()
                {
                    bool can = !string.IsNullOrEmpty(curPid) && curPid != "0";
                    bool added = can && IsFriendListed(curPid);
                    friendAddBtn.Enabled = can;
                    friendAddBtn.Text = added ? "✓ On Friend List" : "＋ Friend List";
                    friendAddBtn.ForeColor = added ? Theme.Green : Theme.Dim;
                }

                void ShowFavorites()
                {
                    addAllFriendsBtn.Visible = false; ShowStage(4);
                    plist.SetRows(favorites.Select(f => MakeRow(f.Name, f.Id, f.Portal, false, true)));
                    hint.ForeColor = Theme.Dim;
                    hint.Text = favorites.Count == 0 ? "No favorites yet — load a player and click ☆ Save."
                        : favorites.Count + " favorite" + (favorites.Count == 1 ? "" : "s") + " — click to load, trash to remove.";
                }
                void ShowRecents()
                {
                    addAllFriendsBtn.Visible = false; ShowStage(4);
                    plist.SetRows(recents.Select(f => { var row = MakeRow(f.Name, f.Id, f.Portal, false, false); row.Savable = !IsFav(f.Id); return row; }));
                    hint.ForeColor = Theme.Dim;
                    hint.Text = recents.Count == 0 ? "No recent lookups yet — search a player and they'll appear here."
                        : recents.Count + " recent lookup" + (recents.Count == 1 ? "" : "s") + " — click to load, ☆ to add to Favorites.";
                }
                void ShowSearchView()   // Overview: profile card + masteries|matches split (or the idle search prompt when no player)
                {
                    addAllFriendsBtn.Visible = false; ShowStage(0);
                    if (string.IsNullOrEmpty(curPid) || curPid == "0") { hint.ForeColor = Theme.Dim; hint.Text = "Search for a SMITE player above."; box.Focus(); }
                }
                // "My profile" tab: always loads the user's own pinned account (set only here). Not set → prompt to set it.
                // Shows the My-profile button on the sub-menu bar, then loads the pinned account. curFromMyProfile flags the
                // load so it never bleeds into Track (handled in SelectPrimary(1) and the load-complete callback).
                void ShowMyProfile()
                {
                    addAllFriendsBtn.Visible = false;
                    curFromMyProfile = true;   // set up-front (the load is async) so a quick switch to Track won't show this player
                    if (string.IsNullOrEmpty(settings.MyProfileId))
                    {
                        ResetCard(); ShowStage(0);   // ResetCard hides subBar2 — re-show it (no tabs) just for the action button
                        subBar2.Visible = true; subFlow2.Visible = false;
                        myProfBtn.Visible = true; myProfBtn.Text = "＋ Set my profile"; myProfBtn.BringToFront(); LayoutMyProfBar();
                        hint.ForeColor = Theme.Dim; hint.Text = "Set your own SMITE profile here — it'll always be one click away on this tab.";
                        return;
                    }
                    myProfBtn.Text = "↻ Change my profile";
                    _ = Guarded(() => LoadKey(settings.MyProfileId, settings.MyProfileName, fromMyProfile: true));
                    // LoadKey's ResetCard (run synchronously up to its first await) hid subBar2; restore the bar + button now
                    // so they stay put during the load. The load-complete callback re-asserts this too.
                    subBar2.Visible = true; subFlow2.Visible = true;
                    myProfBtn.Visible = true; myProfBtn.BringToFront(); LayoutMyProfBar();
                }
                void ShowAchievements()
                {
                    addAllFriendsBtn.Visible = false; ShowStage(3);
                    if (Math.Abs(AchRowWidth() - achRowW) > S(24)) RenderAch();   // re-wrap if the window changed while away
                    hint.ForeColor = Theme.Dim; hint.Text = achStats.Count > 0 ? "Career stats and achievements for " + achWho + "." : "Load a player on the Track tab first.";
                }

                // Renders the full profile for a resolved player. `key` (player name or numeric id) is used for the sub-calls.
                async Task ShowPlayer(JsonElement p, string key)
                {
                    int wins = GI(p, "Wins"), losses = GI(p, "Losses");
                    // in-game name = hz_player_name; Name is the linked store persona (console: hz_player_name empty → Name IS the in-game name).
                    // The "[tag]" prefix on Name is the clan tag — move it onto the in-game name (as shown in-game) and clean the persona.
                    string ig = GS(p, "hz_player_name"); string persona = GS(p, "Name");
                    if (string.IsNullOrEmpty(ig)) { ig = persona; persona = ""; }
                    string clan = GS(p, "Team_Name");
                    string clanTag = "";
                    if (!string.IsNullOrEmpty(clan) && !string.IsNullOrEmpty(persona))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(persona, @"^\[([^\]]{1,12})\]\s*(.*)$");
                        if (m.Success) { clanTag = m.Groups[1].Value; persona = m.Groups[2].Value; }
                    }
                    curPortal = PortalFromName(GS(p, "Platform"));
                    igName = string.IsNullOrEmpty(clanTag) ? ig : "[" + clanTag + "]" + ig;
                    // linked accounts: primary store persona (name shown only if it differs from the in-game name) + each MergedPlayers platform (icon only)
                    linkedAccts.Clear();
                    var seenPortals = new HashSet<int>();
                    string primaryName = (!string.IsNullOrEmpty(persona) && !persona.Equals(ig, StringComparison.OrdinalIgnoreCase)) ? persona : "";
                    linkedAccts.Add((curPortal, primaryName)); seenPortals.Add(curPortal);
                    if (p.TryGetProperty("MergedPlayers", out var mpEl) && mpEl.ValueKind == JsonValueKind.Array)
                        foreach (var mp in mpEl.EnumerateArray())
                        {
                            int mport = GI(mp, "portalId");
                            if (mport > 0 && seenPortals.Add(mport)) linkedAccts.Add((mport, ""));
                        }
                    if (linkedAccts.Count == 1 && string.IsNullOrEmpty(linkedAccts[0].name)) linkedAccts.Clear();   // nothing multi-account to convey
                    namePanel.Invalidate();
                    curName = ig;
                    curPid = GS(p, "Id"); if (string.IsNullOrEmpty(curPid) || curPid == "0") curPid = GS(p, "ActivePlayerId");
                    UpdateFavStar(); UpdateFriendBtn();
                    _trkPlayerLoaded?.Invoke();   // reveal the Overview/Achievements/Friends sub-tabs for the loaded player
                    AddRecent(curName, curPid, curPortal);   // remember this lookup under "Saved"
                    // feed the owner-drawn stat card (parse ranked tiers + relative last-seen HERE, never inside Paint)
                    sLevel = GI(p, "Level"); sMastery = GI(p, "MasteryLevel"); sWins = wins; sLosses = losses;
                    sWorship = GI(p, "Total_Worshippers"); sHours = GI(p, "HoursPlayed"); sAch = GI(p, "Total_Achievements");
                    sRegion = GS(p, "Region"); sClan = clan;
                    var crd = ParseApiDate(GS(p, "Created_Datetime")); sCreated = crd == DateTime.MinValue ? "" : crd.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                    sLastSeen = RelTime(ParseApiDate(GS(p, "Last_Login_Datetime")));
                    sStatusMsg = GS(p, "Personal_Status_Message");
                    sRanked.Clear();
                    foreach (var rk in new[] { "RankedConquest", "RankedJoust", "RankedDuel" })
                        if (p.TryGetProperty(rk, out var ro) && ro.ValueKind == JsonValueKind.Object)
                        { int tier = GI(ro, "Tier"); if (tier > 0) sRanked.Add((rk.Replace("Ranked", ""), TierName(tier), tier, GI(ro, "Rank_Stat"), GI(ro, "Wins") + "-" + GI(ro, "Losses"))); }
                    sLoaded = true; statsPanel.Invalidate();

                    await ApplyStatus(key);   // status chip (also re-run live by statusTimer while this profile is on screen)

                    // god masteries (sorted by worshippers desc) — with god icon + god_id (for queue stats)
                    try
                    {
                        using var gdoc = JsonDocument.Parse(await SmiteApi.Call("getgodranks", key));
                        if (gdoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var rows = new List<(string god, string gid, int rank, int wor, int w, int l, int k, int d, int a, int mk)>();
                            foreach (var gr in gdoc.RootElement.EnumerateArray())
                                rows.Add((GS(gr, "god"), GS(gr, "god_id"), GI(gr, "Rank"), GI(gr, "Worshippers"), GI(gr, "Wins"), GI(gr, "Losses"),
                                          GI(gr, "Kills"), GI(gr, "Deaths"), GI(gr, "Assists"), GI(gr, "MinionKills")));
                            godLv.BeginUpdate(); godLvFull.BeginUpdate();
                            foreach (var r in rows.OrderByDescending(r => r.wor))
                            {
                                string kda = r.k + "/" + r.d + "/" + r.a;
                                int img = GodImg(r.god);
                                var it = new ListViewItem(new[] { r.god, r.rank.ToString(), r.wor.ToString("N0"), r.w.ToString(), r.l.ToString(), kda });
                                it.ImageIndex = img; it.Tag = r.gid; godLv.Items.Add(it);
                                string winp = (r.w + r.l) > 0 ? (r.w * 100 / (r.w + r.l)) + "%" : "—";
                                string kdaR = r.d > 0 ? ((r.k + r.a) / (double)r.d).ToString("0.00") : "—";
                                var itf = new ListViewItem(new[] { r.god, r.rank.ToString(), r.wor.ToString("N0"), r.w.ToString(), r.l.ToString(), winp, kda, kdaR, r.mk.ToString("N0") });
                                itf.ImageIndex = img; itf.Tag = r.gid; godLvFull.Items.Add(itf);
                            }
                            godLv.EndUpdate(); godLvFull.EndUpdate();
                            godHdr.Text = "  GOD MASTERIES  (" + rows.Count + ")";
                            godFullHdr.Text = "  GOD MASTERIES  (" + rows.Count + ")   ·   double-click a god for queue stats";
                        }
                    }
                    catch { }

                    // recent matches — god icon + the Match id stashed in Tag (for the scoreboard)
                    try
                    {
                        using var mdoc = JsonDocument.Parse(await SmiteApi.Call("getmatchhistory", key));
                        if (mdoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            int n = 0;
                            matchLv.BeginUpdate(); matchLvFull.BeginUpdate();
                            foreach (var m in mdoc.RootElement.EnumerateArray())
                            {
                                string godU = GS(m, "God"); if (string.IsNullOrEmpty(godU)) continue;   // skips the "No Match History" stub
                                string god = godU.Replace('_', ' '), queue = GS(m, "Queue"), res = GS(m, "Win_Status");
                                string kda = GI(m, "Kills") + "/" + GI(m, "Deaths") + "/" + GI(m, "Assists");
                                string mid = GS(m, "Match"); int img = GodImg(godU);
                                var it = new ListViewItem(new[] { god, queue, res, kda, FmtApiDate(GS(m, "Match_Time")) });
                                it.ImageIndex = img; it.Tag = mid; matchLv.Items.Add(it);
                                var itf = new ListViewItem(new[] { god, queue, res, kda, GI(m, "Level").ToString(),
                                    GI(m, "Damage").ToString("N0"), GI(m, "Gold").ToString("N0"), FmtApiDate(GS(m, "Match_Time")) });
                                itf.ImageIndex = img; itf.Tag = mid; matchLvFull.Items.Add(itf);
                                n++;
                            }
                            matchLv.EndUpdate(); matchLvFull.EndUpdate();
                            matchHdr.Text = "  RECENT MATCHES  (" + n + ")   ·   double-click for the scoreboard";
                            matchFullHdr.Text = "  RECENT MATCHES  (" + n + ")   ·   double-click a row for the scoreboard";
                            if (n == 0) { matchLv.Items.Add(new ListViewItem(new[] { "(no recent matches)", "", "", "", "" })); matchLvFull.Items.Add(new ListViewItem(new[] { "(no recent matches)", "", "", "", "", "", "", "" })); }
                        }
                    }
                    catch { }

                    // career achievements (needs the numeric player id, not the name).
                    // Use a local id derived from this player's `p` — not the mutable curPid field.
                    try
                    {
                        string pid = GS(p, "Id"); if (string.IsNullOrEmpty(pid) || pid == "0") pid = GS(p, "ActivePlayerId");
                        if (!string.IsNullOrEmpty(pid) && pid != "0")
                        {
                            using var adoc = JsonDocument.Parse(await SmiteApi.Call("getplayerachievements", pid));
                            JsonElement a = default; bool has = false;
                            if (adoc.RootElement.ValueKind == JsonValueKind.Array && adoc.RootElement.GetArrayLength() > 0) { a = adoc.RootElement[0]; has = true; }
                            else if (adoc.RootElement.ValueKind == JsonValueKind.Object) { a = adoc.RootElement; has = true; }
                            if (has)
                            {
                                achWho = curName;
                                achStats.Clear();
                                int aKills = GI(a, "PlayerKills"), aDeaths = GI(a, "Deaths"), aAssists = GI(a, "AssistedKills");
                                int aw = GI(p, "Wins"), al = GI(p, "Losses"), ag = aw + al;
                                string Ratio(long num, long den) => den > 0 ? ((double)num / den).ToString("0.00") : num.ToString("N0");
                                // CAREER — profile totals + derived (none of this is on the raw achievements endpoint)
                                achStats.Add(("CAREER", "Level", GI(p, "Level").ToString("N0")));
                                achStats.Add(("CAREER", "Mastery", GI(p, "MasteryLevel").ToString("N0")));
                                achStats.Add(("CAREER", "Wins", aw.ToString("N0")));
                                achStats.Add(("CAREER", "Losses", al.ToString("N0")));
                                achStats.Add(("CAREER", "Win Rate", ag > 0 ? (aw * 100 / ag) + "%" : "—"));
                                achStats.Add(("CAREER", "Games", ag.ToString("N0")));
                                achStats.Add(("CAREER", "Hours", GI(p, "HoursPlayed").ToString("N0")));
                                achStats.Add(("CAREER", "Worshippers", GI(p, "Total_Worshippers").ToString("N0")));
                                achStats.Add(("CAREER", "Leaves", GI(p, "Leaves").ToString("N0")));
                                // COMBAT — career kill/assist/death totals + derived ratios
                                achStats.Add(("COMBAT", "Player Kills", aKills.ToString("N0")));
                                achStats.Add(("COMBAT", "Assists", aAssists.ToString("N0")));
                                achStats.Add(("COMBAT", "Deaths", aDeaths.ToString("N0")));
                                achStats.Add(("COMBAT", "K / D", Ratio(aKills, aDeaths)));
                                achStats.Add(("COMBAT", "KDA", Ratio(aKills + aAssists, aDeaths)));
                                achStats.Add(("COMBAT", "Kills / Game", ag > 0 ? ((double)aKills / ag).ToString("0.0") : "—"));
                                // MULTI-KILLS
                                achStats.Add(("MULTI-KILLS", "Double", GI(a, "DoubleKills").ToString("N0")));
                                achStats.Add(("MULTI-KILLS", "Triple", GI(a, "TripleKills").ToString("N0")));
                                achStats.Add(("MULTI-KILLS", "Quadra", GI(a, "QuadraKills").ToString("N0")));
                                achStats.Add(("MULTI-KILLS", "Penta", GI(a, "PentaKills").ToString("N0")));
                                // KILLING SPREES
                                achStats.Add(("KILLING SPREES", "First Bloods", GI(a, "FirstBloods").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Killing Spree", GI(a, "KillingSpree").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Rampage", GI(a, "RampageSpree").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Shutdown", GI(a, "ShutdownSpree").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Divine", GI(a, "DivineSpree").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Godlike", GI(a, "GodLikeSpree").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Immortal", GI(a, "ImmortalSpree").ToString("N0")));
                                achStats.Add(("KILLING SPREES", "Unstoppable", GI(a, "UnstoppableSpree").ToString("N0")));
                                // OBJECTIVES
                                achStats.Add(("OBJECTIVES", "Tower Kills", GI(a, "TowerKills").ToString("N0")));
                                achStats.Add(("OBJECTIVES", "Phoenix Kills", GI(a, "PhoenixKills").ToString("N0")));
                                achStats.Add(("OBJECTIVES", "Gold Furies", GI(a, "GoldFuryKills").ToString("N0")));
                                achStats.Add(("OBJECTIVES", "Fire Giants", GI(a, "FireGiantKills").ToString("N0")));
                                achStats.Add(("OBJECTIVES", "Siege Jugg.", GI(a, "SiegeJuggernautKills").ToString("N0")));
                                achStats.Add(("OBJECTIVES", "Wild Jugg.", GI(a, "WildJuggernautKills").ToString("N0")));
                                // FARM
                                achStats.Add(("FARM", "Minion Kills", GI(a, "MinionKills").ToString("N0")));
                                achStats.Add(("FARM", "Camps Cleared", GI(a, "CampsCleared").ToString("N0")));
                                RenderAch();
                            }
                        }
                    }
                    catch { }

                    hint.ForeColor = Theme.Dim; hint.Text = "Updated " + FmtNow() + ".";
                }

                // Loads a player by a known key (numeric id from a search pick, or an exact name). fromMyProfile marks the
                // load as belonging to the My-profile tab so it isn't shown again under Track (they share one player slot).
                async Task LoadKey(string key, string display, bool fromMyProfile = false)
                {
                    curFromMyProfile = fromMyProfile;
                    track.Enabled = false; hint.ForeColor = Theme.Dim; hint.Text = "Loading " + display + "…";
                    listCol.Visible = false; ResetCard();
                    try
                    {
                        using var pdoc = JsonDocument.Parse(await SmiteApi.Call("getplayer", key));
                        if (pdoc.RootElement.ValueKind != JsonValueKind.Array || pdoc.RootElement.GetArrayLength() == 0)
                        { hint.ForeColor = Theme.AccentHi; hint.Text = "No data for \"" + display + "\"."; return; }
                        if (IsPrivateRow(pdoc.RootElement[0]))
                        { hint.ForeColor = Theme.AccentHi; hint.Text = "\"" + display + "\" has a private profile — no stats are exposed by the API."; return; }
                        await ShowPlayer(pdoc.RootElement[0], key);
                    }
                    catch (Exception ex) { hint.ForeColor = Theme.AccentHi; hint.Text = "Lookup failed: " + ex.Message; }
                    finally { track.Enabled = true; }
                }

                async Task Lookup()
                {
                    string name = box.Text.Trim();
                    if (name.Length == 0) return;
                    StylePrimary(1); curPrimary = 1; curFromMyProfile = false;   // searching belongs to the Track tab (and is its own player)
                    track.Enabled = false; hint.ForeColor = Theme.Dim; hint.Text = "Looking up " + name + "…";
                    listCol.Visible = false; ResetCard();
                    try
                    {
                        // exact name first (one call, no fuzzy round-trip for the common case)
                        using var pdoc = JsonDocument.Parse(await SmiteApi.Call("getplayer", name));
                        if (pdoc.RootElement.ValueKind == JsonValueKind.Array && pdoc.RootElement.GetArrayLength() > 0)
                        {
                            if (IsPrivateRow(pdoc.RootElement[0]))
                            { hint.ForeColor = Theme.AccentHi; hint.Text = "\"" + name + "\" has a private profile — no stats are exposed by the API."; return; }
                            await ShowPlayer(pdoc.RootElement[0], name); return;
                        }

                        // no exact hit → forgiving search (partial name / wrong case) via searchplayers.
                        // searchplayers returns the SMITE in-game name in hz_player_name (fall back to Name for console).
                        using var qdoc = JsonDocument.Parse(await SmiteApi.Call("searchplayers", name));
                        var rows = new List<PlayerRow>();
                        if (qdoc.RootElement.ValueKind == JsonValueKind.Array)
                            foreach (var r in qdoc.RootElement.EnumerateArray())
                            {
                                string disp = GS(r, "hz_player_name"); if (string.IsNullOrEmpty(disp)) disp = GS(r, "Name");
                                string id = GS(r, "player_id");
                                if (string.IsNullOrEmpty(disp) || string.IsNullOrEmpty(id) || id == "0") continue;
                                rows.Add(MakeRow(disp, id, GI(r, "portal_id"), GS(r, "privacy_flag") == "y", false));
                            }
                        if (rows.Count == 0)
                        { hint.ForeColor = Theme.AccentHi; hint.Text = "No players found matching \"" + name + "\"."; return; }
                        if (rows.Count == 1)
                        { await LoadKey(rows[0].Id, rows[0].Name); return; }

                        if (!host.Visible) return;   // user navigated away mid-lookup — don't pop results onto a hidden view
                        // searchplayers can return up to 500 rows; cap the picker so it stays usable.
                        const int searchCap = 40;
                        int matchTotal = rows.Count;
                        if (matchTotal > searchCap) rows = rows.GetRange(0, searchCap);
                        plist.SetRows(rows); ShowStage(4);
                        hint.ForeColor = Theme.Dim;
                        hint.Text = matchTotal > searchCap
                            ? "Showing " + searchCap + " of " + matchTotal + " matches — type more of the name to narrow it."
                            : matchTotal + " players match — click the right platform to load.";
                    }
                    catch (Exception ex)
                    {
                        hint.ForeColor = Theme.AccentHi; hint.Text = "Lookup failed: " + ex.Message;
                    }
                    finally { track.Enabled = true; }
                }

                async Task ShowFriends()
                {
                    addAllFriendsBtn.Visible = false; lastFriends.Clear(); ShowStage(4); plist.SetRows(new List<PlayerRow>());
                    string pid = curPid, who = curName;   // snapshot (don't read the fields after the await)
                    if (string.IsNullOrEmpty(pid) || pid == "0") { hint.ForeColor = Theme.AccentHi; hint.Text = "Load a player first to see their friends."; return; }
                    hint.ForeColor = Theme.Dim; hint.Text = "Loading friends of " + who + "…";
                    try
                    {
                        using var fdoc = JsonDocument.Parse(await SmiteApi.Call("getfriends", pid));
                        if (curPid != pid) return;   // the player changed while this was loading → don't render a stale friends list over the new profile
                        // friend_flags decoded by reciprocity + the user's ground truth (CORRECTED direction):
                        //   1 = confirmed friend
                        //   2 = OUTGOING — the VIEWED player sent a request to this person (e.g. tinkerbell52→them)
                        //   4 = INCOMING — this person sent a request to the viewed player
                        //   32/33 = blocked
                        // (bugenki74 sent to NuclearFαrt → flag-2 on bugenki74's list, flag-4 on NuclearFαrt's list.)
                        // NOTE: the legacy API may keep stale request records the live client has cleared; empty name = hidden.
                        var friends = new List<PlayerRow>(); var sent = new List<PlayerRow>(); var incoming = new List<PlayerRow>();
                        var blocked = new List<PlayerRow>(); var hidden = new List<PlayerRow>();
                        int hiddenOpaque = 0;
                        if (fdoc.RootElement.ValueKind == JsonValueKind.Array)
                            foreach (var f in fdoc.RootElement.EnumerateArray())
                            {
                                string nm = GS(f, "name"); string id = GS(f, "player_id");
                                string status = GS(f, "status"); int flags = GI(f, "friend_flags");
                                bool isBlocked = status == "Blocked" || flags >= 32;
                                if (string.IsNullOrEmpty(nm))   // hidden: the API does not expose this account's name
                                {
                                    if (isBlocked) continue;    // hidden + blocked: nothing useful to show
                                    if (string.IsNullOrEmpty(id) || id == "0") { hiddenOpaque++; continue; }
                                    var h = MakeRow("(hidden)", id, GI(f, "portal_id"), false, false);
                                    hidden.Add(h); continue;
                                }
                                var row = MakeRow(nm, id, GI(f, "portal_id"), false, false);
                                if (isBlocked) blocked.Add(row);
                                else if (flags == 2) sent.Add(row);        // viewed player SENT to them (outgoing)
                                else if (flags == 4) incoming.Add(row);    // they sent to the viewed player (incoming)
                                else friends.Add(row);
                            }
                        int total = friends.Count + incoming.Count + sent.Count + blocked.Count + hidden.Count;
                        if (total == 0 && hiddenOpaque == 0)
                        { plist.SetRows(new List<PlayerRow>()); hint.ForeColor = Theme.AccentHi; hint.Text = "No public friends list for " + who + "."; return; }

                        if (!host.Visible) return;   // navigated away mid-load — don't pop the friends overlay onto a hidden view
                        // collapsible sections — flag-2 = requests the viewed player SENT, flag-4 = requests they RECEIVED
                        friendCats.Clear();
                        friendCats.Add(("friends", "FRIENDS", friends));
                        friendCats.Add(("sent", who.ToUpperInvariant() + " SENT A FRIEND REQUEST TO", sent));
                        friendCats.Add(("incoming", "SENT A FRIEND REQUEST TO " + who.ToUpperInvariant(), incoming));
                        friendCats.Add(("blocked", "BLOCKED", blocked));
                        friendCats.Add(("hidden", "HIDDEN — names not exposed by the API", hidden));
                        friendsHiddenOpaque = hiddenOpaque;
                        lastFriends.Clear(); lastFriends.AddRange(friends.Where(r => !string.IsNullOrEmpty(r.Id) && r.Id != "0"));   // "Add all" targets named FRIENDS
                        addAllFriendsBtn.Visible = lastFriends.Count > 0; addAllFriendsBtn.BringToFront();
                        RenderFriends(); ShowStage(4);
                        hint.ForeColor = Theme.Dim;
                        hint.Text = who + ": " + friends.Count + " friends"
                            + (sent.Count > 0 ? " · " + sent.Count + " sent" : "")
                            + (incoming.Count > 0 ? " · " + incoming.Count + " received" : "")
                            + " · " + blocked.Count + " blocked · " + (hidden.Count + hiddenOpaque) + " hidden";
                    }
                    catch (Exception ex) { hint.ForeColor = Theme.AccentHi; hint.Text = "Friends failed: " + ex.Message; }
                }
                // (re)build the friends list rows from friendCats honoring per-section collapse state
                void RenderFriends()
                {
                    var rows = new List<PlayerRow>();
                    foreach (var (key, cap, list) in friendCats)
                    {
                        int cnt = list.Count + (key == "hidden" ? friendsHiddenOpaque : 0);
                        if (cnt == 0) continue;
                        bool col = collapsedFriendSecs.Contains(key);
                        var hdr = PlayerRow.Section("  " + cap + " (" + cnt + ")", key); hdr.Collapsed = col;
                        rows.Add(hdr);
                        if (!col) rows.AddRange(list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
                    }
                    plist.SetRows(rows);
                }

                // One operation at a time: every user-facing entry point goes through this so concurrent
                // awaits can't interleave (they share curPid/curName + the two ListViews). Lookup's internal
                // call to LoadKey is NOT wrapped, so the single-result auto-load still works.
                async Task Guarded(Func<Task> op)
                {
                    if (trackBusy) return;
                    trackBusy = true;
                    try { await op(); }
                    finally { trackBusy = false; }
                }

                track.Click += async (s, e) => await Guarded(Lookup);
                box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Guarded(Lookup); } };
                plist.Activated += async r => { if (trackBusy) return; listCol.Visible = false; StylePrimary(1); curPrimary = 1; box.Text = r.Name.StartsWith("(hidden") ? "" : r.Name; await Guarded(() => LoadKey(r.Id, r.Name)); };   // loading a row shows a Track profile
                plist.Deleted += r =>   // trash (Favorites): remove from favorites — confirm first (Recents use ☆, no trash)
                {
                    if (MessageBox.Show(this, "Remove “" + r.Name + "” from your Favorites?", "Remove favorite",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    RemoveFav(r.Id); SaveFavs(); UpdateFavStar(); ShowFavorites();
                };
                plist.HeaderClicked += r =>   // collapse/expand a friends section
                {
                    if (string.IsNullOrEmpty(r.Key)) return;
                    if (!collapsedFriendSecs.Remove(r.Key)) collapsedFriendSecs.Add(r.Key);
                    RenderFriends();
                };
                plist.Saved += r =>     // ☆ (Recents): add to favorites
                {
                    if (string.IsNullOrEmpty(r.Id) || r.Id == "0") return;
                    if (!IsFav(r.Id)) { favorites.Add(new FavPlayer { Name = r.Name, Id = r.Id, Portal = r.Portal }); SaveFavs(); UpdateFavStar(); }
                    ShowRecents();   // refresh so the now-favorited row drops its ☆
                    hint.ForeColor = Theme.Dim; hint.Text = "★ Added " + r.Name + " to Favorites.";
                };
                favSaveBtn.Click += (s, e) =>
                {
                    if (string.IsNullOrEmpty(curPid) || curPid == "0") return;
                    if (IsFav(curPid)) RemoveFav(curPid);
                    else favorites.Add(new FavPlayer { Name = string.IsNullOrWhiteSpace(curName) ? curPid : curName, Id = curPid, Portal = curPortal });
                    SaveFavs(); UpdateFavStar();
                };
                friendAddBtn.Click += (s, e) =>
                {
                    if (string.IsNullOrEmpty(curPid) || curPid == "0") return;
                    if (IsFriendListed(curPid)) RemoveFriendList(curPid);
                    else { friendList.Add(new FavPlayer { Name = string.IsNullOrWhiteSpace(curName) ? curPid : curName, Id = curPid, Portal = curPortal }); SaveFriendList(); }
                    UpdateFriendBtn();
                    // no immediate fetch: the Friend List tab seeds/reconciles this add when it's next shown (it's hidden now)
                };
                // "My profile" tab: set/change the user's own pinned account (the only place a profile is pinned).
                myProfBtn.Click += async (s, e) =>
                {
                    string name = PromptText("Set my profile", "Enter your SMITE in-game name (the account you play).", settings.MyProfileName ?? "");
                    if (string.IsNullOrWhiteSpace(name) || trackBusy) return;
                    await Guarded(async () =>
                    {
                        hint.ForeColor = Theme.Dim; hint.Text = "Looking up " + name + "…";
                        try
                        {
                            string id = null, disp = name.Trim(); int portal = 0;
                            using (var pdoc = JsonDocument.Parse(await SmiteApi.Call("getplayer", name.Trim())))
                                if (pdoc.RootElement.ValueKind == JsonValueKind.Array && pdoc.RootElement.GetArrayLength() > 0 && !IsPrivateRow(pdoc.RootElement[0]))
                                {
                                    var p0 = pdoc.RootElement[0];
                                    id = GS(p0, "Id"); if (string.IsNullOrEmpty(id) || id == "0") id = GS(p0, "ActivePlayerId");
                                    string ig = GS(p0, "hz_player_name"); disp = string.IsNullOrEmpty(ig) ? GS(p0, "Name") : ig;
                                    portal = PortalFromName(GS(p0, "Platform"));
                                }
                            if (string.IsNullOrEmpty(id))   // no exact hit → first forgiving search result
                                using (var qdoc = JsonDocument.Parse(await SmiteApi.Call("searchplayers", name.Trim())))
                                    if (qdoc.RootElement.ValueKind == JsonValueKind.Array)
                                        foreach (var r in qdoc.RootElement.EnumerateArray())
                                        {
                                            string rid = GS(r, "player_id"), rdisp = GS(r, "hz_player_name"); if (string.IsNullOrEmpty(rdisp)) rdisp = GS(r, "Name");
                                            if (!string.IsNullOrEmpty(rid) && rid != "0" && !string.IsNullOrEmpty(rdisp)) { id = rid; disp = rdisp; portal = GI(r, "portal_id"); break; }
                                        }
                            if (string.IsNullOrEmpty(id)) { hint.ForeColor = Theme.AccentHi; hint.Text = "Couldn't find \"" + name.Trim() + "\"."; return; }
                            settings.MyProfileId = id; settings.MyProfileName = disp; settings.MyProfilePortal = portal; SaveSettings();
                        }
                        catch (Exception ex) { hint.ForeColor = Theme.AccentHi; hint.Text = "Lookup failed: " + ex.Message; return; }
                    });
                    if (!string.IsNullOrEmpty(settings.MyProfileId)) { curPrimary = 0; ShowMyProfile(); }
                };
                addAllFriendsBtn.Click += (s, e) =>
                {
                    if (lastFriends.Count == 0) return;
                    int newOnes = lastFriends.Count(r => !IsFriendListed(r.Id));
                    if (newOnes == 0) { hint.ForeColor = Theme.Dim; hint.Text = "All " + lastFriends.Count + " of these friends are already on your Friend List."; return; }
                    var ans = MessageBox.Show(this, "Add " + newOnes + " player" + (newOnes == 1 ? "" : "s") + " to your Friend List?", "Add all friends", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ans != DialogResult.Yes) return;
                    foreach (var r in lastFriends)
                        if (!IsFriendListed(r.Id)) friendList.Add(new FavPlayer { Name = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name, Id = r.Id, Portal = r.Portal });
                    SaveFriendList(); UpdateFriendBtn();   // shown/reconciled when the Friend List tab is next opened
                    hint.ForeColor = Theme.Dim; hint.Text = "Added " + newOnes + " friend" + (newOnes == 1 ? "" : "s") + " to your Friend List.";
                };
                // load a player into the tracker by (id, name) — used by the Friend List tab's row-click
                _trkLoadPlayer = (id, name) => Guarded(async () => { box.Text = name; StylePrimary(1); curPrimary = 1; await LoadKey(id, name); });
                // PRIMARY tabs (Track / Favorites / Recent) drive the top-level view; the SECONDARY strip
                // (Overview / Achievements / Friends) is player-scoped and only shows while a player is loaded.
                bool PlayerLoaded() => !string.IsNullOrEmpty(curPid) && curPid != "0";
                void StylePrimary(int a) { for (int k = 0; k < primaryTabs.Length; k++) StyleSubTab(primaryTabs[k], k == a); }
                void StyleSecondary(int a) { for (int k = 0; k < secondaryTabs.Length; k++) StyleSubTab(secondaryTabs[k], k == a); }
                int curSecondary = 0;
                void ShowExpanded(int w) { addAllFriendsBtn.Visible = false; ShowStage(w); hint.ForeColor = Theme.Dim; hint.Text = (w == 1 ? "God masteries for " : "Recent matches for ") + curName + (w == 2 ? " — double-click a row for the scoreboard." : "."); }
                void SelectSecondary(int j)
                {
                    if (j == 4 && trackBusy) return;   // can't open Friends mid-load
                    // The Change-my-profile button is on the sub-menu bar itself, so it stays right-aligned across every
                    // My-profile sub-tab (no need to hide it per sub-tab the way the old top-strip placement did).
                    if (curPrimary == 0) { myProfBtn.Visible = true; myProfBtn.BringToFront(); LayoutMyProfBar(); }
                    curSecondary = j; StyleSecondary(j);
                    switch (j) { case 0: ShowSearchView(); break; case 1: ShowExpanded(1); break; case 2: ShowExpanded(2); break; case 3: ShowAchievements(); break; case 4: _ = Guarded(ShowFriends); break; }
                }
                Control MakeHiddenCard(HiddenTag t, int y)
                {
                    int conf = HiddenConfidence(t);
                    string lvl = conf >= 70 ? "High" : conf >= 45 ? "Medium" : "Low";
                    Color cc = conf >= 70 ? Theme.Green : conf >= 45 ? Theme.Yellow : Color.FromArgb(210, 95, 95);
                    var card = new Panel { Location = new Point(S(14), y), Size = new Size(S(620), S(104)), BackColor = Theme.Panel, Cursor = Cursors.Hand };
                    var nick = new Label { Location = new Point(S(16), S(10)), AutoSize = true, Font = Theme.F(13f, FontStyle.Bold), ForeColor = Theme.Blue, BackColor = Theme.Panel, Text = "★ " + t.Nick };
                    var pill = new Panel { Location = new Point(S(456), S(12)), Size = new Size(S(148), S(28)), BackColor = Theme.Panel };
                    pill.Paint += (s, e) =>
                    {
                        var gg = e.Graphics; gg.SmoothingMode = SmoothingMode.AntiAlias;
                        using (var pen = new Pen(cc, S(2))) gg.DrawRectangle(pen, S(1), S(1), pill.Width - S(3), pill.Height - S(3));
                        TextRenderer.DrawText(gg, "Confidence " + conf + "%", Theme.F(9f, FontStyle.Bold), pill.ClientRectangle, cc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    };
                    string clanTxt = string.IsNullOrEmpty(t.Clan) ? "no clan" : "[" + t.Clan + "]";
                    var d1 = new Label { Location = new Point(S(16), S(44)), AutoSize = true, Font = Theme.F(9.5f), ForeColor = Theme.Text, BackColor = Theme.Panel, Text = clanTxt + "    ·    Level " + t.Level + "    ·    Mastery " + t.Mastery + "    ·    " + lvl + " confidence" };
                    string godsTxt = (t.Gods != null && t.Gods.Count > 0) ? "Gods: " + string.Join(", ", t.Gods.Take(6)) : "Gods: —";
                    var d2 = new Label { Location = new Point(S(16), S(66)), AutoSize = true, Font = Theme.F(9f), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = godsTxt };
                    string comp = (t.Companions != null && t.Companions.Count > 0) ? "    ·    " + t.Companions.Count + " known party-mate" + (t.Companions.Count == 1 ? "" : "s") : "";
                    var d3 = new Label { Location = new Point(S(16), S(84)), AutoSize = true, Font = Theme.F(8.5f), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = "Seen " + t.Seen + "×" + (string.IsNullOrEmpty(t.LastSeen) ? "" : "    ·    last " + t.LastSeen) + comp + "    ·    click for full details" };
                    EventHandler openIt = (s, e) => ShowHiddenDetail(t);
                    card.Click += openIt; nick.Click += openIt; d1.Click += openIt; d2.Click += openIt; d3.Click += openIt;
                    card.Controls.Add(nick); card.Controls.Add(pill); card.Controls.Add(d1); card.Controls.Add(d2); card.Controls.Add(d3);
                    return card;
                }
                // Full structured view for one tag: everything the algo + DB hold (clan/level/mastery/gods/party-mates/dates/
                // confidence + how it re-identifies), with rename / delete. Party-mate ids are resolved to names where known.
                void ShowHiddenDetail(HiddenTag t)
                {
                    int conf = HiddenConfidence(t);
                    string lvl = conf >= 70 ? "High" : conf >= 45 ? "Medium" : "Low";
                    Color cc = conf >= 70 ? Theme.Green : conf >= 45 ? Theme.Yellow : Color.FromArgb(210, 95, 95);
                    using (var dlg = new Form())
                    {
                        dlg.Text = "★ " + t.Nick;
                        dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                        dlg.StartPosition = FormStartPosition.CenterParent;
                        dlg.FormBorderStyle = FormBorderStyle.FixedDialog; dlg.MinimizeBox = false; dlg.MaximizeBox = false;
                        dlg.ClientSize = new Size(S(560), S(540));
                        try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }
                        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(S(18), S(14), S(18), S(14)) };
                        dlg.Controls.Add(body);
                        int y = 0;
                        void Row(string label, string val, float size = 10f)
                        {
                            body.Controls.Add(new Label { Location = new Point(0, y), AutoSize = true, Font = Theme.F(8f), ForeColor = Theme.Dim, Text = label.ToUpperInvariant() });
                            body.Controls.Add(new Label { Location = new Point(0, y + S(16)), Size = new Size(S(512), S(20)), Font = Theme.F(size), ForeColor = Theme.Text, Text = string.IsNullOrWhiteSpace(val) ? "—" : val, AutoEllipsis = true });
                            y += S(42);
                        }
                        body.Controls.Add(new Label { Location = new Point(0, y), AutoSize = true, Font = Theme.F(15f, FontStyle.Bold), ForeColor = Theme.Blue, Text = "★ " + t.Nick });
                        body.Controls.Add(new Label { Location = new Point(S(330), y + S(5)), AutoSize = true, Font = Theme.F(10f, FontStyle.Bold), ForeColor = cc, Text = "Confidence " + conf + "%  (" + lvl + ")" });
                        y += S(42);
                        Row("Clan", string.IsNullOrEmpty(t.Clan) ? "no clan" : "[" + t.Clan + "]" + (t.ClanId != 0 ? "      id " + t.ClanId : ""));
                        Row("Account level", t.Level.ToString());
                        Row("Total mastery", t.Mastery.ToString());
                        Row("Gods seen", (t.Gods != null && t.Gods.Count > 0) ? string.Join(", ", t.Gods) : "—", 9.5f);
                        int mc = t.Companions?.Count ?? 0;
                        string mates = mc > 0 ? string.Join(", ", t.Companions.Select(id => NameDb.NameById(id) ?? ("id " + id))) : "—";
                        Row("Known party-mates (" + mc + ")", mates, 9f);
                        Row("Times seen", t.Seen + "×");
                        Row("First tagged", string.IsNullOrEmpty(t.Tagged) ? "(before this was tracked)" : t.Tagged);
                        Row("Last seen", string.IsNullOrEmpty(t.LastSeen) ? "—" : t.LastSeen);
                        if (!string.IsNullOrWhiteSpace(t.Note)) Row("Note", t.Note, 9.5f);
                        string how = "Re-identified by " + (string.IsNullOrEmpty(t.Clan) ? "" : "clan + ") + "account level + mastery"
                            + (mc > 0 ? " + " + mc + " party-mate" + (mc == 1 ? "" : "s") : "") + ((t.Gods?.Count ?? 0) > 0 ? " + god pool" : "")
                            + ".  Higher when more of these match.";
                        body.Controls.Add(new Label { Location = new Point(0, y), Size = new Size(S(512), S(34)), Font = Theme.F(8.5f), ForeColor = Theme.Dim, Text = how });
                        y += S(42);
                        var bRename = MkBtn("Rename", 96, false, Theme.Input, Theme.Text); bRename.Location = new Point(0, y);
                        var bDelete = MkBtn("Delete", 96, false, Theme.Input, Color.FromArgb(210, 95, 95)); bDelete.Location = new Point(S(106), y);
                        var bClose = MkBtn("Close", 96, false, Theme.Blue, Color.White); bClose.Location = new Point(S(212), y);
                        bRename.Click += (s, e) => { string n = PromptText("Rename hidden player “" + t.Nick + "”", "Edit the nickname.", t.Nick); if (!string.IsNullOrWhiteSpace(n)) { t.Nick = n.Trim(); SaveHiddenTags(); dlg.Close(); RenderHiddenList(); } };
                        bDelete.Click += (s, e) => { hiddenTags.Remove(t); SaveHiddenTags(); dlg.Close(); RenderHiddenList(); };
                        bClose.Click += (s, e) => dlg.Close();
                        body.Controls.Add(bRename); body.Controls.Add(bDelete); body.Controls.Add(bClose);
                        dlg.ShowDialog(this);
                    }
                }
                void RenderHiddenList()
                {
                    hidList.Controls.Clear();
                    string q = (hidSearch.Text ?? "").Trim();
                    IEnumerable<HiddenTag> items = hiddenTags;
                    if (q.Length > 0)
                        items = items.Where(t => (t.Nick ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || (t.Clan ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || (t.Gods != null && t.Gods.Any(g => g.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)));
                    items = hidSort == 0 ? items.OrderBy(t => t.Nick, StringComparer.OrdinalIgnoreCase)
                          : hidSort == 2 ? items.OrderByDescending(t => string.IsNullOrEmpty(t.Tagged) ? t.LastSeen : t.Tagged).ThenByDescending(t => t.LastSeen)
                          : items.OrderByDescending(HiddenConfidence).ThenByDescending(t => t.Seen);
                    var list = items.ToList();
                    if (hiddenTags.Count == 0)
                        hidList.Controls.Add(new Label { Location = new Point(S(20), S(16)), AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(10.5f),
                            Text = "No nicknamed hidden players yet.\r\n\r\nOpen a match scoreboard (double-click a Recent Match) or a live game, click a\r\n“Private/Hidden” row, and give them a nickname — they'll show up here with a confidence score." });
                    else if (list.Count == 0)
                        hidList.Controls.Add(new Label { Location = new Point(S(20), S(16)), AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(10.5f), Text = "No tags match “" + q + "”." });
                    else { int y = S(8); foreach (var t in list) { hidList.Controls.Add(MakeHiddenCard(t, y)); y += S(116); } }
                    hint.ForeColor = Theme.Dim;
                    hint.Text = hiddenTags.Count == 0 ? "Hidden players you've nicknamed."
                        : (q.Length > 0 ? list.Count + " of " + hiddenTags.Count : hiddenTags.Count.ToString()) + " nicknamed hidden player" + (hiddenTags.Count == 1 ? "" : "s") + " — click a card for full details.";
                }
                void ShowHidden()
                {
                    StylePrimary(4); subBar2.Visible = false;
                    RenderHiddenList();
                    ShowStage(5);
                }
                void SelectPrimary(int i)
                {
                    curPrimary = i;
                    StylePrimary(i);
                    // The player-LOOKUP bar (Player: … / Search / ★ Save / ＋ Friend List) belongs ONLY to Track. My profile
                    // keeps the bar visible for its "Set/Change my profile" button; Favorites / Recent Profiles / Custom Hidden
                    // Tags hide it entirely (they each have their own list + controls). Track-only search controls:
                    bool onTrack = i == 1;
                    lbl.Visible = bhost.Visible = track.Visible = favSaveBtn.Visible = friendAddBtn.Visible = onTrack;
                    top.Visible = onTrack;                 // search strip is Track-only now (My-profile button moved to the sub-menu bar)
                    if (i != 0) myProfBtn.Visible = false;
                    if (i == 0) ShowMyProfile();           // My profile: the user's own pinned account (set only here)
                    else if (i == 1)                        // Track: player-context strip + current player view (or idle search)
                    {
                        // Don't bleed the My-profile account into Track: if the loaded player came from the My-profile tab,
                        // clear it so Track starts BLANK (we track a different person here). ResetCard wipes curPid + the card.
                        if (curFromMyProfile) ResetCard();
                        subFlow2.Visible = true;
                        bool showHere = PlayerLoaded();
                        subBar2.Visible = showHere;
                        if (showHere) SelectSecondary(curSecondary); else ShowSearchView();
                    }
                    else if (i == 5) { subBar2.Visible = false; ShowEncounters(); }   // Encounters: top-level, self-contained (own A/B inputs, no sub-tabs, no lookup bar)
                    else { subBar2.Visible = false; if (i == 2) ShowFavorites(); else if (i == 3) ShowRecents(); else ShowHidden(); }
                }
                // Ignore primary-tab switches while a load is in flight (same trackBusy guard the search/friends/double-click
                // paths use). This closes the mid-load races where a completing load would render under the wrong tab.
                for (int i = 0; i < primaryTabs.Length; i++) { int k = i; primaryTabs[i].Click += (s, e) => { if (trackBusy) return; SelectPrimary(k); }; }
                for (int j = 0; j < secondaryTabs.Length; j++) { int k = j; secondaryTabs[j].Click += (s, e) => SelectSecondary(k); }
                // when a player finishes loading, reveal the secondary strip and default it to Overview; highlight My profile
                // if the load was initiated from that tab (curPrimary==0), otherwise Track (1). A player can be opened from
                // Favorites/Recents/Encounters (where SelectPrimary hid the Track lookup bar), so ACTUALLY land on the styled
                // tab (set curPrimary) and restore the ☆ Save / ＋ Friend List bar — otherwise those buttons stay hidden.
                _trkPlayerLoaded = () => {
                    // RACE GUARD (defence-in-depth; the trackBusy tab-click guard prevents most of these): if a My-profile
                    // load finishes while we're NOT on My profile, never paint it here. Track → blank idle prompt; the
                    // list/encounters tabs (2-5) just drop the result and keep their own view (don't call ShowSearchView).
                    if (curFromMyProfile && curPrimary != 0) { ResetCard(); if (curPrimary == 1) ShowSearchView(); return; }
                    int hp = curPrimary == 0 ? 0 : 1; curPrimary = hp; StylePrimary(hp);
                    bool onTrack = hp == 1;
                    lbl.Visible = bhost.Visible = track.Visible = favSaveBtn.Visible = friendAddBtn.Visible = onTrack;
                    top.Visible = onTrack;                                  // search strip is Track-only; My-profile button is on the sub-menu bar
                    subBar2.Visible = true; subFlow2.Visible = true;
                    myProfBtn.Visible = hp == 0; if (hp == 0) { myProfBtn.BringToFront(); LayoutMyProfBar(); }
                    curSecondary = 0; StyleSecondary(0); ShowStage(0);
                };
                _trkResetSecondary = () => { curSecondary = 0; StyleSecondary(0); };   // so SelectNav restores Overview (non-blocking), not a stale Guarded Friends view
                _trkSubTab = SelectPrimary;
                _trkSubTab2 = SelectSecondary;
                _trkEncCompare = nm => { try { SelectPrimary(5); if (boxA.Text.Trim().Length == 0 && accA.Count == 0) boxA.Text = curName; boxB.Text = nm; _ = RunCompare(false); } catch { } };   // test/screenshot hook (Encounters is primary tab 5 now)
                StylePrimary(1); StyleSecondary(0);
                godLv.DoubleClick += async (s, e) =>
                {
                    if (trackBusy || godLv.SelectedItems.Count == 0 || string.IsNullOrEmpty(curPid)) return;
                    var it = godLv.SelectedItems[0];
                    string gid = it.Tag as string; if (string.IsNullOrEmpty(gid) || gid == "0") return;
                    string pid = curPid, who = curName, godName = it.SubItems[0].Text;
                    await Guarded(() => ShowGodQueues(pid, who, godName, gid));
                };
                matchLv.DoubleClick += async (s, e) =>
                {
                    if (trackBusy || matchLv.SelectedItems.Count == 0) return;
                    string mid = matchLv.SelectedItems[0].Tag as string;
                    if (string.IsNullOrEmpty(mid) || mid == "0") return;
                    await Guarded(() => ShowMatchDetails(mid));
                };
                godLvFull.DoubleClick += async (s, e) =>   // expanded Masteries tab → queue stats
                {
                    if (trackBusy || godLvFull.SelectedItems.Count == 0 || string.IsNullOrEmpty(curPid)) return;
                    var it = godLvFull.SelectedItems[0];
                    string gid = it.Tag as string; if (string.IsNullOrEmpty(gid) || gid == "0") return;
                    string pid = curPid, who = curName, godName = it.SubItems[0].Text;
                    await Guarded(() => ShowGodQueues(pid, who, godName, gid));
                };
                matchLvFull.DoubleClick += async (s, e) =>   // expanded Matches tab → scoreboard
                {
                    if (trackBusy || matchLvFull.SelectedItems.Count == 0) return;
                    string mid = matchLvFull.SelectedItems[0].Tag as string;
                    if (string.IsNullOrEmpty(mid) || mid == "0") return;
                    await Guarded(() => ShowMatchDetails(mid));
                };
                statusLbl.Click += async (s, e) =>   // chip is clickable only while the player is in a live game
                {
                    if (trackBusy || string.IsNullOrEmpty(curLiveMatch)) return;
                    await Guarded(() => ShowLiveMatch(curLiveMatch));
                };

                UpdateFavStar(); UpdateFriendBtn();

                // Fetches getplayerstatus(key) and paints the profile status chip. Re-runnable: ShowPlayer calls
                // it on load; statusTimer re-runs it on a cadence so Overview / My profile reflect online/in-game
                // changes live (same idea as the Friend List poller, scoped to the one viewed player).
                async Task ApplyStatus(string key)
                {
                    if (string.IsNullOrEmpty(key)) return;
                    string forPid = curPid;   // guard: don't clobber the chip if the user switches players mid-await
                    try
                    {
                        using var sdoc = JsonDocument.Parse(await SmiteApi.Call("getplayerstatus", key));
                        if (curPid != forPid) return;
                        if (sdoc.RootElement.ValueKind == JsonValueKind.Array && sdoc.RootElement.GetArrayLength() > 0)
                        {
                            var st = sdoc.RootElement[0]; int code = GI(st, "status"); string ss = GS(st, "status_string");
                            Color chip = code == 3 ? Color.FromArgb(60, 180, 90) : code == 4 || code == 1 ? Theme.Blue : code == 2 ? Theme.Yellow : Color.FromArgb(70, 70, 70);
                            statusLbl.BackColor = chip; statusLbl.ForeColor = (code == 2) ? Color.Black : Color.White;
                            string lm = GS(st, "Match");
                            bool inGame = code == 3 && !string.IsNullOrEmpty(lm) && lm != "0";   // live match → chip is clickable
                            curLiveMatch = inGame ? lm : "";
                            string label = string.IsNullOrEmpty(ss) ? ("Status " + code) : ss.ToUpperInvariant();
                            statusLbl.Text = inGame ? "● " + label + "  ▸" : label;
                            statusLbl.Cursor = inGame ? Cursors.Hand : Cursors.Default;
                            tip.SetToolTip(statusLbl, inGame ? "Click to view the live match roster" : null);
                            statusLbl.Visible = true;
                        }
                    }
                    catch { }
                }

                // Live status refresh: while a profile is on screen, re-poll its chip on a cadence.
                var statusTimer = new System.Windows.Forms.Timer { Interval = 15000 };
                statusTimer.Tick += async (s, e) =>
                {
                    if (!host.Visible || trackBusy) return;                      // not viewing the tracker, or a load is in flight
                    if (string.IsNullOrEmpty(curPid) || curPid == "0") return;   // no real player loaded
                    if (!statusLbl.Visible) return;                              // chip not shown (search view / hidden player)
                    await ApplyStatus(curPid);
                };
                statusTimer.Start();
                host.Disposed += (s, e) => statusTimer.Dispose();
            }
            return host;
        }
    }
}
