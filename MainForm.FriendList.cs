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

        // The Friend List tab: your saved buddies + their live status (online / in game / offline).
        Panel BuildFriendListPanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var top = new Panel { Dock = DockStyle.Top, Height = S(54), BackColor = Theme.Panel };
            var title = new Label { Text = "Friend List", AutoSize = true, ForeColor = Theme.Text, Font = Theme.F(13f, FontStyle.Bold), Location = new Point(S(16), S(15)) };
            var refresh = MkBtn("↻ Refresh", 104, false, Theme.Blue, Color.White); refresh.Location = new Point(S(150), S(12));
            var sortLbl = new Label { Text = "Sort:", AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(9f), Location = new Point(S(276), S(18)) };
            int flSort = 1;   // 0 = A-Z, 1 = Status, 2 = Last seen
            var sortBtns = new[] { MkBtn("A-Z", 58, false, Theme.Input, Theme.Dim), MkBtn("Status", 78, false, Theme.Input, Theme.Dim), MkBtn("Last seen", 96, false, Theme.Input, Theme.Dim) };
            sortBtns[0].Location = new Point(S(320), S(12)); sortBtns[1].Location = new Point(S(382), S(12)); sortBtns[2].Location = new Point(S(464), S(12));
            // Status strip docked under the toolbar. A fixed right-aligned label on the toolbar lands off-screen at
            // high DPI (the bug fixed in the tracker), so the "N online · updated …" feedback gets its own strip here.
            var hint = new Label { Dock = DockStyle.Top, Height = S(22), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(16), 0, S(14), 0), ForeColor = Theme.Dim, Font = Theme.F(8.5f), BackColor = Theme.Panel, Text = "Add players from the Player Tracker (＋ Friend List)." };
            // Red-outlined progress box: shows "12/58" while statuses are being checked, hidden otherwise.
            var progBox = new Panel { Location = new Point(S(576), S(12)), Size = new Size(S(78), S(30)), BackColor = Theme.Panel, Visible = false };
            progBox.Paint += (s, e) =>
            {
                var gg = e.Graphics; gg.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Theme.Accent, S(2))) gg.DrawRectangle(pen, S(1), S(1), progBox.Width - S(3), progBox.Height - S(3));
                TextRenderer.DrawText(gg, progBox.Tag as string ?? "", Theme.F(9.5f, FontStyle.Bold), progBox.ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            void SetProgress(int done, int total) { progBox.Tag = done + "/" + total; progBox.Visible = true; progBox.Invalidate(); }
            top.Controls.Add(title); top.Controls.Add(refresh); top.Controls.Add(sortLbl); top.Controls.Add(progBox);
            foreach (var b in sortBtns) top.Controls.Add(b);
            var upChk = MkChk("Online time", settings.ShowFriendUptime); upChk.Location = new Point(S(660), S(18)); top.Controls.Add(upChk);   // handler wired below (after flRows/RowExtra exist)
            void StyleSort() { for (int i = 0; i < sortBtns.Length; i++) { bool on = i == flSort; sortBtns[i].ForeColor = on ? Color.White : Theme.Dim; sortBtns[i].BackColor = on ? Theme.Accent : Theme.Input; sortBtns[i].FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Line; } }

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(S(14), S(10), S(14), S(14)) };
            // Fixed-width column: a Dock=Fill list stretches to the AutoScale-inflated parent and draws its
            // right-aligned content (status, trash) off the physical window edge. A fixed width keeps it in view.
            var col = new Panel { Dock = DockStyle.Left, Width = S(430), BackColor = Theme.Bg };
            var flist = new PlayerList { Dock = DockStyle.Fill, Font = Theme.F(10.5f), AutoSelectFirst = false };
            col.Controls.Add(flist);

            // Right-side preview "frame": clicking a friend shows their in-game icon + status here with an Open-profile prompt.
            var detail = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            Image dImg = null;
            // Monogram fallback for players with no in-game avatar (Avatar_URL empty) — their initial on a name-derived tile.
            string dMono = ""; Color dMonoCol = Theme.Input;
            var dMonoPalette = new[] { Color.FromArgb(64, 88, 120), Color.FromArgb(112, 72, 92), Color.FromArgb(66, 108, 92), Color.FromArgb(120, 98, 58), Color.FromArgb(92, 80, 124), Color.FromArgb(58, 102, 112) };
            var dAvatar = new Panel { Location = new Point(S(22), S(22)), Size = new Size(S(96), S(96)), BackColor = Theme.Panel };
            dAvatar.Paint += (s, e) =>
            {
                var gg = e.Graphics; gg.SmoothingMode = SmoothingMode.AntiAlias;
                var rc = new Rectangle(0, 0, dAvatar.Width - 1, dAvatar.Height - 1);
                int rad = S(12);
                using var path = new GraphicsPath();
                path.AddArc(rc.X, rc.Y, rad, rad, 180, 90);
                path.AddArc(rc.Right - rad, rc.Y, rad, rad, 270, 90);
                path.AddArc(rc.Right - rad, rc.Bottom - rad, rad, rad, 0, 90);
                path.AddArc(rc.X, rc.Bottom - rad, rad, rad, 90, 90);
                path.CloseFigure();
                if (dImg != null) { gg.SetClip(path); gg.DrawImage(dImg, rc); gg.ResetClip(); }
                else
                {
                    using (var bb = new SolidBrush(dMonoCol)) gg.FillPath(bb, path);   // no avatar → colored monogram tile
                    if (!string.IsNullOrEmpty(dMono))
                        TextRenderer.DrawText(gg, dMono, Theme.F(32f, FontStyle.Bold), new Rectangle(0, 0, dAvatar.Width, dAvatar.Height), Color.FromArgb(235, 235, 235), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                using var pen = new Pen(Theme.Line); gg.DrawPath(pen, path);
            };
            var dName = new Label { Location = new Point(S(132), S(26)), AutoSize = true, Font = Theme.F(14f, FontStyle.Bold), ForeColor = Theme.Text, BackColor = Theme.Panel };
            var dSub = new Label { Location = new Point(S(132), S(60)), AutoSize = true, Font = Theme.F(9.5f), ForeColor = Theme.Dim, BackColor = Theme.Panel };
            var dSeen = new Label { Location = new Point(S(132), S(84)), AutoSize = true, Font = Theme.F(9f), ForeColor = Theme.Dim, BackColor = Theme.Panel };
            var dPrompt = new Label { Location = new Point(S(22), S(134)), AutoSize = true, Font = Theme.F(9.5f), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = "Open this player's full profile?" };
            var dOpen = MkBtn("Open profile  →", 150, false, Theme.Blue, Color.White); dOpen.Location = new Point(S(22), S(160));
            var dWhisper = MkBtn("💬  Whisper", 132, false, Theme.Input, Theme.Accent); dWhisper.Location = new Point(S(180), S(160));
            var dViewGame = MkBtn("● View current game", 184, false, Theme.Input, Theme.Green); dViewGame.Location = new Point(S(22), S(198));
            var dNoteLbl = new Label { Location = new Point(S(22), S(246)), AutoSize = true, Font = Theme.F(9f, FontStyle.Bold), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = "NOTES" };
            var dNote = new TextBox { Location = new Point(S(22), S(268)), Size = new Size(S(320), S(120)), Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(9.5f) };
            var dHint = new Label { Location = new Point(S(22), S(26)), AutoSize = true, Font = Theme.F(10f), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = "Click a friend to preview their profile." };
            detail.Controls.Add(dAvatar); detail.Controls.Add(dName); detail.Controls.Add(dSub); detail.Controls.Add(dSeen); detail.Controls.Add(dPrompt); detail.Controls.Add(dOpen); detail.Controls.Add(dWhisper); detail.Controls.Add(dViewGame); detail.Controls.Add(dNoteLbl); detail.Controls.Add(dNote); detail.Controls.Add(dHint);

            string detailId = null;
            string noteId = null;   // the friend whose note is currently in dNote (so we can flush it on switch/leave)
            void FlushNote() { if (noteId != null) { var fe = friendList.FirstOrDefault(f => f.Id == noteId); if (fe != null && (fe.Note ?? "") != dNote.Text) { fe.Note = dNote.Text; SaveFriendList(); } } }
            dNote.Leave += (s, e) => FlushNote();
            void HideDetail() { FlushNote(); noteId = null; detailId = null; dImg = null; dAvatar.Visible = dName.Visible = dSub.Visible = dSeen.Visible = dPrompt.Visible = dOpen.Visible = dWhisper.Visible = dViewGame.Visible = dNoteLbl.Visible = dNote.Visible = false; dHint.Visible = true; }
            async void ShowDetail(PlayerRow r)   // async void → wrap the whole body so a stray throw can't crash the message loop
            {
                try
                {
                    FlushNote();   // save the previously-shown friend's note before switching
                    detailId = r.Id;
                    noteId = r.Id;
                    dNote.Text = friendList.FirstOrDefault(f => f.Id == r.Id)?.Note ?? "";
                    dName.Text = r.Name;
                    // monogram fallback: first letter/digit of the name on a stable name-derived colour
                    var nm0 = r.Name ?? "";
                    char mc = nm0.FirstOrDefault(ch => char.IsLetterOrDigit(ch));
                    dMono = mc == '\0' ? "?" : char.ToUpperInvariant(mc).ToString();
                    int hsh = 0; foreach (var ch in nm0) hsh = hsh * 31 + ch;
                    dMonoCol = dMonoPalette[(hsh & 0x7fffffff) % dMonoPalette.Length];
                    var (pc, _) = PlatformChip(r.Portal);
                    dSub.Text = pc + "    ·    " + (string.IsNullOrEmpty(r.Status) || r.Status == "…" ? "—" : r.Status);
                    dSub.ForeColor = r.StatusCol;
                    dSeen.Text = r.LastLogin > DateTime.MinValue ? "Last seen " + RelTime(r.LastLogin) : "";
                    dOpen.Tag = r; dViewGame.Tag = r;
                    bool inGame = r.StatusSort == 0;   // 0 = In Game (set in RefreshFriendList)
                    dViewGame.Enabled = inGame;
                    dViewGame.ForeColor = inGame ? Theme.Green : Color.FromArgb(95, 95, 95);
                    dViewGame.Text = inGame ? "● View current game" : "Not in a game";
                    dHint.Visible = false;
                    dWhisper.Tag = r;
                    dAvatar.Visible = dName.Visible = dSub.Visible = dSeen.Visible = dPrompt.Visible = dOpen.Visible = dWhisper.Visible = dViewGame.Visible = dNoteLbl.Visible = dNote.Visible = true;
                    dImg = null; dAvatar.Invalidate();
                    var img = await LoadAvatar(r.Avatar);
                    if (detailId == r.Id) { dImg = img; dAvatar.Invalidate(); }   // ignore if the user clicked a different friend meanwhile
                }
                catch { }
            }
            dOpen.Click += async (s, e) => { if (dOpen.Tag is PlayerRow rr) { _trkResetSecondary?.Invoke(); SelectNav(1); if (_trkLoadPlayer != null) await _trkLoadPlayer(rr.Id, rr.Name); } };
            dWhisper.Click += (s, e) => { if (dWhisper.Tag is PlayerRow rr) { SelectNav(5); _openWhisper?.Invoke(rr.Name, rr.Id); } };
            dViewGame.Click += async (s, e) => { if (dViewGame.Enabled && dViewGame.Tag is PlayerRow rr) await ViewLiveGame(rr.Id); };
            HideDetail();

            body.Controls.Add(detail); body.Controls.Add(col);   // Fill (detail) added before the Left (col) so it fills the remainder
            host.Controls.Add(body); host.Controls.Add(hint); host.Controls.Add(top);

            PlayerRow Row(FavPlayer f) { var (code, col) = PlatformChip(f.Portal); return new PlayerRow { Name = f.Name, Id = f.Id, Portal = f.Portal, Deletable = true, Plat = code, PlatCol = col, Status = "…", StatusCol = Theme.Dim, StatusSort = 9 }; }
            var flRows = new List<PlayerRow>();
            void ApplySort()
            {
                IEnumerable<PlayerRow> o = flSort == 0 ? flRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : flSort == 1 ? flRows.OrderBy(r => r.StatusSort).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : flRows.OrderByDescending(r => r.LastLogin).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
                flist.SetRows(o.ToList());
            }
            bool flBusy = false;
            bool flAgain = false;   // a refresh was requested while one was in flight → re-run once when it finishes
            bool sortPending = false;
            // --- live poller state (continuous, priority-tiered, runs only while the Friend List tab is visible) ---
            var flPoll = new System.Windows.Forms.Timer { Interval = 2000 };   // wakes every 2s; polls only rows that are actually due
            bool flSeeded = false;     // the first full pass has completed at least once (so re-entry never rebuilds)
            bool flTicking = false;    // re-entrancy guard: a tick's awaits can outlast the 2s interval
            double flTokens = 0;       // token bucket: smooths + caps the call rate regardless of roster size
            DateTime flLastFill = DateTime.UtcNow;
            DateTime flLastPoll = DateTime.MinValue;   // wall-clock of the most recent successful status poll (drives the "updated Xs ago" hint)
            const double FlCallsPerMin = 120;  // sustained ceiling on getplayerstatus calls/min while the tab is open
            const int FlTickBudget = 16;       // max calls started per tick (burst cap; processed concurrently)
            const int FlConcurrency = 12;      // concurrent getplayerstatus requests per tick (parallel = fast sweep, not one-at-a-time)
            const int FlSeedConcurrency = 20;  // first-boot status sweep runs hotter (a one-time burst) so the list is usable fast
            var flRng = new Random();
            // tier cadences (seconds): 0 god-select · 1 online/lobby · 2 in-game · 3 offline (see OfflineSeconds) · 4 unknown
            int TierInterval(int tier) => tier == 0 ? 10 : tier == 1 ? 15 : tier == 2 ? 20 : tier == 3 ? 60 : 90;
            int Jitter(int sec) => (int)((flRng.NextDouble() - 0.5) * 0.2 * sec);   // ±10% so rows don't all re-fire in lockstep
            // Offline players back off the longer they've been gone: ~1 min per day (6 days → 6 min) up to a 10-min cap
            // that holds through ~6 months, then ramps to 20 min at 1 year. They snap back to a fast tier the moment they log in.
            int OfflineSeconds(PlayerRow r)
            {
                double d = r.LastLogin == DateTime.MinValue ? 0 : (DateTime.Now - r.LastLogin).TotalDays;
                double mins = d <= 180 ? Math.Max(1, Math.Min(10, d)) : d <= 365 ? 10 + (d - 180) / 185.0 * 10 : 20;
                return (int)Math.Round(mins * 60);
            }
            // Compact session-uptime for an online friend ("how long logged in"): now − their last login.
            string UptimeShort(TimeSpan t)
            {
                if (t.TotalMinutes < 1) return "just on";
                if (t.TotalHours < 1) return (int)t.TotalMinutes + "m on";
                if (t.TotalDays < 1) return (int)t.TotalHours + "h on";
                return (int)t.TotalDays + "d on";
            }
            // The right-aligned secondary text for a row: offline → "last seen" age; online → session uptime (only if enabled).
            string RowExtra(PlayerRow r)
            {
                if (r.Header || r.LastLogin == DateTime.MinValue) return "";
                if (r.StatusSort == 2) return RelTime(r.LastLogin);                                          // offline
                if (r.StatusSort <= 1 && settings.ShowFriendUptime) return UptimeShort(DateTime.Now - r.LastLogin);   // online
                return "";
            }
            // Compact "freshness" string for the status hint so it's obvious the list is live + when it last checked.
            string AgoShort(DateTime when)
            {
                if (when == DateTime.MinValue) return "checking…";
                int s = (int)(DateTime.Now - when).TotalSeconds;
                if (s < 4) return "just now";
                if (s < 60) return s + "s ago";
                if (s < 3600) return (s / 60) + "m ago";
                return (s / 3600) + "h ago";
            }
            void SetFlHint()
            {
                if (IsDisposed || curMode != 2) return;
                int online = flRows.Count(r => !r.Header && r.StatusSort <= 1);
                hint.ForeColor = Theme.Dim;
                hint.Text = friendList.Count + " friends · " + online + " online · updated " + AgoShort(flLastPoll);
            }
            // Re-sort live as statuses arrive so the list stays ordered in real time. Coalesced via a single
            // pending post (a burst of completions triggers at most one re-sort per message-pump cycle), and a
            // no-op for A-Z (a status change can't affect alphabetical order).
            void LiveSort()
            {
                if (flSort == 0 || sortPending || !flist.IsHandleCreated) return;
                sortPending = true;
                flist.BeginInvoke(new Action(() => { sortPending = false; if ((flBusy || curMode == 2) && flist.IsHandleCreated && !flist.IsDisposed) ApplySort(); }));
            }
            // Cheap heartbeat: one getplayerstatus → sets Status/StatusCol/StatusSort/Tier. Returns the raw status code.
            async Task<int> PullStatus(PlayerRow row)
            {
                int code = -1;
                using (var sdoc = JsonDocument.Parse(await SmiteApi.Call("getplayerstatus", row.Id)))
                {
                    if (sdoc.RootElement.ValueKind == JsonValueKind.Array && sdoc.RootElement.GetArrayLength() > 0)
                    { var st = sdoc.RootElement[0]; code = GI(st, "status"); var (t, c) = StatusInfo(code, GS(st, "status_string")); row.Status = t; row.StatusCol = c; }
                    else { row.Status = "?"; row.StatusCol = Theme.Dim; }
                }
                row.StatusSort = code == 3 ? 0 : (code == 1 || code == 2 || code == 4) ? 1 : code == 0 ? 2 : 3;   // drives the Status sort button
                // refresh priority is separate: god-select (a match is forming) is the most time-sensitive, in-game the least (locked in).
                // offline (tier 3) backs off by days-idle in OfflineSeconds(); status snapping to online moves them straight to a fast tier.
                row.Tier = code == 2 ? 0 : (code == 1 || code == 4) ? 1 : code == 3 ? 2 : code == 0 ? 3 : 4;
                return code;
            }
            // Slow, rarely-changing details: name self-heal + last login + avatar. Returns true if the display name changed.
            async Task<bool> PullPlayer(PlayerRow row)
            {
                bool nameChanged = false;
                using var pdoc = JsonDocument.Parse(await SmiteApi.Call("getplayer", row.Id));
                if (pdoc.RootElement.ValueKind == JsonValueKind.Array && pdoc.RootElement.GetArrayLength() > 0)
                {
                    var pp = pdoc.RootElement[0];
                    var ig = GS(pp, "hz_player_name"); if (string.IsNullOrEmpty(ig)) ig = GS(pp, "Name");   // self-heal display name
                    if (!string.IsNullOrEmpty(ig) && ig != row.Name)
                    { row.Name = ig; var fe = friendList.FirstOrDefault(f => f.Id == row.Id); if (fe != null) fe.Name = ig; nameChanged = true; }
                    row.LastLogin = ParseApiDate(GS(pp, "Last_Login_Datetime"));
                    row.Avatar = GS(pp, "Avatar_URL");   // in-game icon for the preview panel
                }
                return nameChanged;
            }
            async Task RefreshFriendList()
            {
                // Coalesce FIRST: if a pass is in flight, request a re-run rather than mutating flRows mid-flight
                // (the running pass holds row refs; its trailing flAgain re-run picks up adds/removes/the empty state).
                if (flBusy || flTicking) { flAgain = true; return; }   // also yield to an in-flight poll tick (it's async; its rows are mid-resolve)
                if (friendList.Count == 0) { flRows.Clear(); flist.SetRows(new List<PlayerRow>()); hint.ForeColor = Theme.Dim; hint.Text = "No friends yet — add players from the Player Tracker (＋ Friend List)."; return; }
                flBusy = true;
                try
                {
                    flRows.Clear(); flRows.AddRange(friendList.Select(Row));
                    int progDone = 0, progTotal = flRows.Count; SetProgress(0, progTotal);
                    ApplySort();
                    hint.ForeColor = Theme.Dim; hint.Text = "Checking statuses…";
                    bool fetchDetails = friendList.Count <= 100;   // getplayer per friend (name + last login); skip for huge lists to spare the rate limit

                    // PASS 1 — STATUS ONLY (one call/row, higher concurrency). This is the data the list exists to show, so
                    // it goes first and alone: online/offline lands in roughly half the round-trips of a combined sweep that
                    // also pulled the slow getplayer details. The list is usable the moment this finishes.
                    using (var sem = new SemaphoreSlim(FlSeedConcurrency))
                        await Task.WhenAll(flRows.Select(async row =>
                        {
                            await sem.WaitAsync();
                            try { await PullStatus(row); row.ErrBackoff = 0; row.Extra = RowExtra(row); }
                            catch { row.ErrBackoff++; row.Tier = 4; if (string.IsNullOrEmpty(row.Status) || row.Status == "…") { row.Status = "?"; row.StatusCol = Theme.Dim; } }
                            finally { sem.Release(); }
                            flist.UpdateRow(row);   // repaint just this row (no whole-list flash) …
                            LiveSort();             // … and re-order live as statuses land (throttled; A-Z is a no-op)
                            progDone++; SetProgress(progDone, progTotal);
                        }));
                    ApplySort();
                    flLastPoll = DateTime.Now; SetFlHint();
                    flSeeded = true;
                    progBox.Visible = false;   // statuses are in → the list is usable now; details refine quietly below

                    // hand off to the live poller now (it no-ops while flBusy is still set for pass 2, then takes over):
                    // each row's next status check is per tier cadence, not immediately (which would re-scan the whole list).
                    var seedNow = DateTime.UtcNow;
                    foreach (var r in flRows) { r.NextDueUtc = seedNow.AddSeconds(TierInterval(r.Tier) + Jitter(TierInterval(r.Tier))); if (r.NextDetailUtc == DateTime.MinValue) r.NextDetailUtc = seedNow.AddMinutes(30); }
                    if (curMode == 2 && !flPoll.Enabled) flPoll.Start();

                    // PASS 2 — SLOW DETAILS (name self-heal + last login + avatar). These only feed the "last seen"/uptime
                    // text and the preview avatar, so they refine rows that are already showing live status rather than
                    // blocking them. Runs after the list is usable; the poller waits on flBusy until this completes.
                    if (fetchDetails)
                    {
                        bool nameChanged = false;
                        using var sem2 = new SemaphoreSlim(FlConcurrency);
                        await Task.WhenAll(flRows.Select(async row =>
                        {
                            await sem2.WaitAsync();
                            try { if (await PullPlayer(row)) nameChanged = true; row.NextDetailUtc = DateTime.UtcNow.AddMinutes(30); row.Extra = RowExtra(row); }
                            catch { }
                            finally { sem2.Release(); }
                            if (curMode == 2 && !IsDisposed && flRows.Contains(row)) flist.UpdateRow(row);
                        }));
                        if (nameChanged) SaveFriendList();
                        ApplySort(); SetFlHint();
                    }
                }
                catch (Exception ex) { hint.ForeColor = Theme.AccentHi; hint.Text = "Status check failed: " + ex.Message; }
                finally { flBusy = false; progBox.Visible = false; }
                if (flAgain) { flAgain = false; await RefreshFriendList(); }   // pick up adds/removes that arrived during this pass
            }
            // Bring flRows into sync with friendList without a full network refresh: append new friends (due immediately),
            // drop removed ones. Called on tab re-entry and at the top of every poll tick so adds/removes are seamless.
            void ReconcileRows()
            {
                bool changed = false;
                foreach (var f in friendList)
                    if (!flRows.Any(r => r.Id == f.Id)) { flRows.Add(Row(f)); changed = true; }   // NextDueUtc = MinValue → polled next tick
                if (flRows.RemoveAll(r => !r.Header && !friendList.Any(f => f.Id == r.Id)) > 0) changed = true;
                if (changed) ApplySort();
            }
            // The continuous priority-tiered poller. Forms.Timer fires on the UI thread and awaits resume on it, so every
            // flRows/flist access here is lock-free. Each tick: refill the bucket, free-refresh offline "last seen",
            // reconcile adds/removes, then concurrently poll the most-overdue rows (god-select refreshes fastest, offline slowest).
            flPoll.Tick += async (s, e) =>
            {
                if (curMode != 2 || IsDisposed) { flPoll.Stop(); return; }
                if (flBusy || flTicking || friendList.Count == 0) return;   // a full manual pass owns the list; never overlap ticks
                flTicking = true;
                try
                {
                    var now = DateTime.UtcNow;
                    // local daily-budget backstop (no extra API calls): throttle this app as it nears the key's request cap.
                    // The embedded key is a 300k/day tier, so these only ever engage for an aberrant runaway — the per-tick
                    // token bucket + pause-when-hidden are the real limiters. (A user on their own free key relies on the bucket.)
                    double effRate = FlCallsPerMin;
                    int usedToday = SmiteApi.RequestsToday;
                    if (usedToday >= 295000) effRate = 0; else if (usedToday >= 270000) effRate = 2;
                    double add = (now - flLastFill).TotalMinutes * effRate; flLastFill = now;
                    flTokens = Math.Min(effRate, flTokens + (add > 0 ? add : 0));
                    // free local refresh of the "last seen" text — costs no API call. Gate on rows that already show one
                    // (Extra non-empty ⇒ currently offline, or errored while offline) so we never paint it on online/in-game rows.
                    foreach (var r in flRows) if (!r.Header) { var ex = RowExtra(r); if (ex != r.Extra) { r.Extra = ex; flist.UpdateRow(r); } }
                    ReconcileRows();
                    SetFlHint();   // refresh "updated Xs ago" every tick so the freshness is never stale — even on a no-op tick
                    // FlTickBudget caps calls per tick; flTokens is the per-minute cap. Pick the most-overdue rows up to budget.
                    int callsLeft = Math.Min(FlTickBudget, (int)Math.Floor(flTokens));
                    if (callsLeft <= 0) { if (usedToday >= 295000) { hint.ForeColor = Theme.Dim; hint.Text = "Daily API budget reached — live updates paused."; } return; }
                    bool fetchDetails = friendList.Count <= 100;
                    var batch = flRows.Where(r => !r.Header && !r.Polling && r.NextDueUtc <= now).OrderBy(r => r.NextDueUtc).Take(callsLeft).ToList();
                    if (batch.Count == 0) return;
                    bool nameDirty = false;
                    // Poll the batch CONCURRENTLY (not one-at-a-time) so a sweep finishes in ~one round-trip, not N of them.
                    using (var psem = new SemaphoreSlim(FlConcurrency))
                        await Task.WhenAll(batch.Select(async row =>
                        {
                            await psem.WaitAsync();
                            try
                            {
                                if (flBusy || !flRows.Contains(row)) return;   // a manual pass took over, or the row was removed
                                row.Polling = true; flTokens -= 1;
                                int oldSort = row.StatusSort;
                                try
                                {
                                    int code = await PullStatus(row);
                                    row.ErrBackoff = 0;
                                    bool boundary = (oldSort <= 1) != (row.StatusSort <= 1);   // crossed the online/offline line
                                    if (fetchDetails && flTokens >= 1 && (boundary || now >= row.NextDetailUtc))
                                    { flTokens -= 1; if (await PullPlayer(row)) nameDirty = true; row.NextDetailUtc = DateTime.UtcNow.AddMinutes(30); }
                                    row.Extra = RowExtra(row);
                                    flLastPoll = DateTime.Now;
                                }
                                catch { row.ErrBackoff++; row.Tier = 4; if (string.IsNullOrEmpty(row.Status) || row.Status == "…") { row.Status = "?"; row.StatusCol = Theme.Dim; } }
                                finally
                                {
                                    row.Polling = false;
                                    int iv = row.Tier == 3 ? OfflineSeconds(row) : TierInterval(row.Tier);
                                    if (row.ErrBackoff > 0) iv = (int)Math.Min(600, iv * Math.Pow(2, row.ErrBackoff));   // back off a flapping/dead id
                                    row.NextDueUtc = DateTime.UtcNow.AddSeconds(iv + Jitter(iv));
                                    if (curMode == 2 && !IsDisposed && flRows.Contains(row)) flist.UpdateRow(row);
                                }
                            }
                            finally { psem.Release(); }
                        }));
                    if (nameDirty) SaveFriendList();
                    LiveSort();
                    SetFlHint();
                }
                catch { }   // async-void handler: never let a stray error crash the app — the next tick retries
                finally
                {
                    flTicking = false;
                    if (flAgain && !flBusy && curMode == 2 && !IsDisposed) { flAgain = false; _ = RefreshFriendList(); }   // run a Refresh that was deferred while this tick was mid-await
                }
            };
            // Entering the Friend List tab: seed once (full pass), or just resume the poller on the persisted list — never rebuild.
            void FlOnShow()
            {
                if (!flSeeded) { _ = RefreshFriendList(); return; }   // first visit: full pass seeds rows + starts the poller
                ReconcileRows();                                     // catch adds/removes that happened while the tab was hidden
                if (friendList.Count == 0) { hint.ForeColor = Theme.Dim; hint.Text = "No friends yet — add players from the Player Tracker (＋ Friend List)."; return; }
                // Don't re-poll everyone just because their due-time lapsed while the tab was hidden — that bursts the whole
                // list at once (looks like a full rescan). Restart each row's priority clock from NOW so updates trickle in
                // per tier cadence; brand-new rows (NextDueUtc == MinValue, just reconciled in) stay due immediately.
                var resumeNow = DateTime.UtcNow;
                foreach (var r in flRows) if (!r.Header && r.NextDueUtc != DateTime.MinValue)
                    r.NextDueUtc = resumeNow.AddSeconds(TierInterval(r.Tier) + Jitter(TierInterval(r.Tier)));
                SetFlHint();   // show the genuine "updated Xs ago" for the cached data until the poller refreshes it
                if (!flPoll.Enabled) flPoll.Start();
            }

            for (int i = 0; i < sortBtns.Length; i++) { int k = i; sortBtns[i].Click += (s, e) => { flSort = k; StyleSort(); ApplySort(); }; }
            StyleSort();
            upChk.CheckedChanged += (s, e) => { settings.ShowFriendUptime = upChk.Checked; SaveSettings(); foreach (var r in flRows) if (!r.Header) r.Extra = RowExtra(r); flist.Invalidate(); };
            flist.Activated += ShowDetail;   // click a friend → preview frame on the right (Open profile loads the tracker)
            void ConfirmDelete(PlayerRow r)
            {
                if (MessageBox.Show(this, "Remove “" + r.Name + "” from your Friend List?", "Remove friend",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                RemoveFriendList(r.Id);
                flRows.RemoveAll(x => x.Id == r.Id);   // drop locally + re-sort instead of a full network refresh → seamless
                if (detailId == r.Id) HideDetail();    // clear the preview if it was showing the deleted friend
                ApplySort();
                int online = flRows.Count(x => x.StatusSort <= 1);
                hint.ForeColor = Theme.Dim;
                hint.Text = friendList.Count == 0
                    ? "No friends yet — add players from the Player Tracker (＋ Friend List)."
                    : friendList.Count + " friends · " + online + " online";
            }
            flist.Deleted += ConfirmDelete;
            refresh.Click += async (s, e) => await RefreshFriendList();
            _flShow = FlOnShow;
            _flPause = () => flPoll.Stop();
            // Warn before quitting with unsaved God-Inspector config edits (FormClosed can't cancel; FormClosing can).
            this.FormClosing += (s, e) =>
            {
                if (current != null && prms != null)
                {
                    int d = prms.Count(p => p.IsNew || p.Value != p.Original);
                    if (d > 0 && MessageBox.Show(this, d + " unsaved change" + (d > 1 ? "s" : "") + " to " + current.FileName + " will be lost.\n\nQuit anyway?",
                            "Smite 1 Inspector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        e.Cancel = true;
                }
            };
            this.FormClosed += (s, e) => { try { _archiveCts?.Cancel(); } catch { } FlushNote(); flPoll.Stop(); flPoll.Dispose(); NameDb.Save(true); GameLog.Shutdown(); try { _sguru?.Shutdown(); } catch { } try { SaveFavs(); SaveJson(FriendListFile, friendList); SaveJson(HiddenFile, hiddenTags); } catch { } };   // force-flush + release the WebView2 process/profile lock + final save-on-close so a transiently-dropped list save isn't lost
            return host;
        }
    }
}
