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

        // The Whispers tab: a WhatsApp-style messenger that whispers live SMITE players with the game CLOSED.
        // Left = conversation list + new-whisper; right = the selected thread; top = connection status.
        Panel BuildWhispersPanel()
        {
            string wDir       = Path.Combine(Theme.DataDir, "Whispers");
            string relayDir   = Path.Combine(wDir, "relay");
            string convFile   = Path.Combine(wDir, "conversations.json");
            try { Directory.CreateDirectory(relayDir); } catch { }

            string FindProbe()
            {
                foreach (var p in new[] {
                    Path.Combine(Theme.AppDir, "whisper", "Probe5.exe"),
                    Path.Combine(Theme.AppDir, "_work", "mctsprobe", "Probe5.exe"),
                    @"E:\Claude\Apps\Smite Ressurection\_work\mctsprobe\Probe5.exe",
                }) { try { if (File.Exists(p)) return p; } catch { } }
                return null;
            }
            string probeExe = FindProbe();

            // Fold confusable Cyrillic/Greek lookalikes to ASCII so "Darius" and "Darіus" are the SAME conversation.
            string NormKey(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                var sb = new StringBuilder(s.Length);
                foreach (char c0 in s.Trim().ToLowerInvariant())
                {
                    char c = c0;
                    switch (c)
                    {
                        case 'а': c = 'a'; break; case 'е': c = 'e'; break; case 'о': c = 'o'; break; case 'р': c = 'p'; break;
                        case 'с': c = 'c'; break; case 'х': c = 'x'; break; case 'у': c = 'y'; break; case 'к': c = 'k'; break;
                        case 'м': c = 'm'; break; case 'т': c = 't'; break; case 'н': c = 'h'; break; case 'в': c = 'b'; break;
                        case 'і': c = 'i'; break; case 'ї': c = 'i'; break; case 'ј': c = 'j'; break; case 'ѕ': c = 's'; break;
                        case 'α': c = 'a'; break; case 'ο': c = 'o'; break; case 'ν': c = 'v'; break; case 'ρ': c = 'p'; break;
                        case 'τ': c = 't'; break; case 'ι': c = 'i'; break; case 'κ': c = 'k'; break; case 'χ': c = 'x'; break;
                        case 'υ': c = 'u'; break; case 'ε': c = 'e'; break; case 'β': c = 'b'; break;
                    }
                    sb.Append(c);
                }
                return sb.ToString();
            }
            var convs = new Dictionary<string, WConv>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(convFile)) convs = JsonSerializer.Deserialize<Dictionary<string, WConv>>(File.ReadAllText(convFile)) ?? convs; } catch { }
            // migrate old (un-normalized) keys → normalized, merging any duplicates
            {
                var merged = new Dictionary<string, WConv>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in convs)
                {
                    string k = NormKey(string.IsNullOrEmpty(kv.Value.Display) ? kv.Key : kv.Value.Display);
                    if (k.Length == 0) continue;
                    if (merged.TryGetValue(k, out var ex)) { ex.Msgs.AddRange(kv.Value.Msgs); ex.Msgs.Sort((a, b) => a.T.CompareTo(b.T)); ex.Last = Math.Max(ex.Last, kv.Value.Last); if (string.IsNullOrEmpty(ex.Id)) ex.Id = kv.Value.Id; ex.Pin = ex.Pin || kv.Value.Pin; ex.Hidden = ex.Hidden && kv.Value.Hidden; }
                    else { kv.Value.Key = k; merged[k] = kv.Value; }
                }
                convs = merged;
            }
            // A "queued" marker only means "waiting for this session's login to finish" — stale across restarts, so clear it.
            foreach (var c in convs.Values) foreach (var m in c.Msgs) if (m.St == "queued") m.St = "";
            void SaveConvs() { try { Directory.CreateDirectory(wDir); Theme.AtomicWriteText(convFile, JsonSerializer.Serialize(convs)); } catch { } }

            WhisperEngine engine = probeExe != null ? new WhisperEngine(probeExe, relayDir) : null;
            string activeKey = null;

            // ---- login method (Steam ticket vs Hi-Rez username/password) ----
            // Persist the choice + username; the password is kept in memory only (re-entered each app session).
            string loginFile = Path.Combine(wDir, "login.json");
            string loginMode = "steam", loginUser = "", loginPass = "";
            bool loginAuto = false;       // connect the engine in the background when the app opens
            bool loginRemember = false;   // remember the Hi-Rez password (stored DPAPI-encrypted, per Windows user)
            try { if (File.Exists(loginFile)) { using var ld = JsonDocument.Parse(File.ReadAllText(loginFile));
                if (ld.RootElement.TryGetProperty("mode", out var lm)) loginMode = lm.GetString() ?? "steam";
                if (ld.RootElement.TryGetProperty("user", out var lu)) loginUser = lu.GetString() ?? "";
                if (ld.RootElement.TryGetProperty("auto", out var la)) loginAuto = la.GetBoolean();
                if (ld.RootElement.TryGetProperty("remember", out var lr)) loginRemember = lr.GetBoolean();
                if (loginRemember && ld.RootElement.TryGetProperty("pass", out var lp)) loginPass = DpapiUnprotect(lp.GetString() ?? "");
            } } catch { }
            if (loginMode != "hirez") loginMode = "steam";
            void SaveLogin() { try { Directory.CreateDirectory(wDir); Theme.AtomicWriteText(loginFile, JsonSerializer.Serialize(new { mode = loginMode, user = loginUser, auto = loginAuto, remember = loginRemember, pass = loginRemember ? DpapiProtect(loginPass) : "" })); } catch { } }
            void ApplyLogin() { if (engine != null) engine.SetLogin(loginMode, loginUser, loginPass); }
            ApplyLogin();
            // Hi-Rez mode can only connect once a password is supplied (it's never persisted).
            bool LoginReady() { return loginMode == "steam" || (loginMode == "hirez" && loginUser.Length > 0 && loginPass.Length > 0); }

            var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            // ---- top status bar ----
            var statusBar = new Panel { Dock = DockStyle.Top, Height = S(40), BackColor = Theme.Panel };
            var statusLine = new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line };
            var dot = new Panel { Size = new Size(S(11), S(11)), Location = new Point(S(16), S(15)), BackColor = Theme.Dim };
            var statusLbl = new Label { AutoSize = true, Location = new Point(S(34), S(12)), ForeColor = Theme.Dim, Font = Theme.F(10f, FontStyle.Bold), BackColor = Theme.Panel, Text = "disconnected" };
            var topBtns = new Panel { Dock = DockStyle.Right, Width = S(238), BackColor = Theme.Panel };
            var logsBtn = new Button { Dock = DockStyle.Left, Width = S(122), Text = "Export Logs", FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Dim, Font = Theme.F(9f, FontStyle.Bold), Cursor = Cursors.Hand };
            logsBtn.FlatAppearance.BorderColor = Theme.Line;
            var loginBtn = new Button { Dock = DockStyle.Right, Width = S(108), Text = "⚙ Login", FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Dim, Font = Theme.F(9f, FontStyle.Bold), Cursor = Cursors.Hand };
            loginBtn.FlatAppearance.BorderColor = Theme.Line;
            topBtns.Controls.Add(logsBtn); topBtns.Controls.Add(loginBtn);
            statusBar.Controls.Add(dot); statusBar.Controls.Add(statusLbl); statusBar.Controls.Add(topBtns); statusBar.Controls.Add(statusLine);
            var blink = new System.Windows.Forms.Timer { Interval = 450 };
            bool blinkOn = false;
            blink.Tick += (s, e) => { blinkOn = !blinkOn; dot.BackColor = blinkOn ? Theme.Accent : Theme.Panel; };
            bool _gameOpen = false;   // a SAME-ACCOUNT SMITE game is running -> its chat session conflicts with ours
            // Only a STEAM SMITE can be the messenger's own account (NuclearFart logs in via Steam) and steal our chat
            // session. An Epic install is necessarily a DIFFERENT Hi-Rez account (e.g. CEOofSlash) and doesn't conflict —
            // so we must NOT warn for it. Distinguish by the running exe's install path.
            bool CheckGameOpen()
            {
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("Smite"))
                    {
                        string path = "";
                        try { path = (p.MainModule != null ? p.MainModule.FileName : "") ?? ""; } catch { }
                        try { p.Dispose(); } catch { }
                        if (path.Replace('/', '\\').ToLowerInvariant().Contains("steamapps")) return true;
                    }
                }
                catch { }
                return false;
            }

            void SetStatus(string st)
            {
                // The messenger and a SAME-ACCOUNT live SMITE game can't share one chat session — the server
                // CLOSE_CONNECTIONs the duplicate, so whispers silently stop delivering. Warn only for that case.
                if (_gameOpen)
                {
                    blink.Stop(); dot.BackColor = Theme.Accent;
                    statusLbl.Text = "⚠ SMITE is open on Steam — close it to whisper";
                    statusLbl.ForeColor = Theme.Accent;
                    return;
                }
                if (st == "connected") { blink.Stop(); dot.BackColor = Theme.Green; statusLbl.Text = "connected"; statusLbl.ForeColor = Theme.Green; return; }
                int n = QueuedCount();
                if (st == "connecting") { statusLbl.ForeColor = Theme.Accent; if (!blink.Enabled) blink.Start(); statusLbl.Text = n > 0 ? "connecting… (" + n + " queued)" : "connecting…"; }
                // "disconnected" = we WERE connected and silently lost it (dead socket, sleep/resume, hung process) —
                // distinct from "stopped" (never started / explicitly stopped). Read as urgent + actively recovering,
                // since the engine auto-reconnects on this signal (see engine.Status wiring below).
                else if (st == "disconnected") { statusLbl.ForeColor = Theme.AccentHi; if (!blink.Enabled) blink.Start(); statusLbl.Text = n > 0 ? "connection lost — reconnecting… (" + n + " queued)" : "connection lost — reconnecting…"; }
                else { blink.Stop(); dot.BackColor = Color.FromArgb(120, 50, 50); statusLbl.ForeColor = Theme.Dim; statusLbl.Text = n > 0 ? "disconnected (" + n + " queued)" : "disconnected"; }
            }

            // ---- left column: new-whisper + conversation list ----
            var left = new Panel { Dock = DockStyle.Left, Width = S(280), BackColor = Theme.Panel };
            var leftLine = new Panel { Dock = DockStyle.Right, Width = S(1), BackColor = Theme.Line };
            var newRow = new Panel { Dock = DockStyle.Top, Height = S(46), BackColor = Theme.Panel, Padding = new Padding(S(10), S(8), S(10), S(6)) };
            var pickBtn = new Button { Dock = DockStyle.Right, Width = S(34), Text = "▾", FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Dim, Font = Theme.F(10.5f, FontStyle.Bold), Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(S(4), 0, 0, 0) };
            pickBtn.FlatAppearance.BorderSize = 1;
            pickBtn.FlatAppearance.BorderColor = Theme.Line;
            pickBtn.FlatAppearance.MouseOverBackColor = Theme.Lighten(Theme.Input);
            pickBtn.MouseEnter += (s, e) => { pickBtn.FlatAppearance.BorderColor = Theme.Accent; pickBtn.ForeColor = Theme.Text; };
            pickBtn.MouseLeave += (s, e) => { pickBtn.FlatAppearance.BorderColor = Theme.Line; pickBtn.ForeColor = Theme.Dim; };
            new ToolTip().SetToolTip(pickBtn, "Pick from your Friend List");
            var newName = new TextBox { Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(10f), Text = "" };
            var newHint = new Label { Text = "New whisper — type a name + Enter", Dock = DockStyle.Bottom, Height = S(0), ForeColor = Theme.Dim, Font = Theme.F(8f) };
            newRow.Controls.Add(newName); newRow.Controls.Add(pickBtn);
            var convList = new BufPanel { Dock = DockStyle.Fill, BackColor = Theme.Panel, AutoScroll = true };
            left.Controls.Add(convList); left.Controls.Add(newRow); left.Controls.Add(leftLine);

            // ---- right column: thread header + messages + input ----
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var threadHead = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Theme.Bg };
            var threadHeadLine = new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line };
            var peerLbl = new Label { AutoSize = true, Location = new Point(S(16), S(9)), ForeColor = Theme.Text, Font = Theme.F(12f, FontStyle.Bold), BackColor = Theme.Bg, Text = "" };
            var peerDot = new Panel { Size = new Size(S(9), S(9)), BackColor = Theme.Dim, Visible = false };
            var peerStatus = new Label { AutoSize = true, Location = new Point(S(40), S(11)), ForeColor = Theme.Dim, Font = Theme.F(9.5f, FontStyle.Bold), BackColor = Theme.Bg, Text = "" };
            threadHead.Controls.Add(peerLbl); threadHead.Controls.Add(peerDot); threadHead.Controls.Add(peerStatus); threadHead.Controls.Add(threadHeadLine);
            // runtime presence: conv key -> last status code (3 in-game,4 online,1 lobby,2 god-select,0 offline,-1 unknown); + last-check time
            var presCode = new Dictionary<string, int>();
            var presWhen = new Dictionary<string, DateTime>();
            var lastSeen = new Dictionary<string, DateTime>();   // last time we received a msg from them = they're ONLINE now (real-time, no API)
            var qstatus = new Dictionary<string, (int code, DateTime when)>();   // live status from the backend (REQUEST_PLAYER_INFO -> token 780)
            // Live conversation-row controls, kept across renders so the list updates IN PLACE (no flicker / no scroll-jump).
            var rowMap = new Dictionary<string, (Panel row, Label nm, Label sub, Panel dot, Panel pin)>();
            var rowOrder = new List<string>();
            // Char ranges of clickable "✕ cancel" tokens in the thread -> the queued WMsg they cancel (rebuilt each RenderThread).
            var cancelRanges = new List<(int a, int b, WMsg m)>();
            void LayoutPresence()
            {
                int x = peerLbl.Left + TextRenderer.MeasureText(peerLbl.Text, peerLbl.Font).Width + S(14);
                peerDot.Location = new Point(x, S(15)); peerStatus.Location = new Point(x + S(15), S(11));
            }
            void ShowPresence(int code, string txt, Color col)
            {
                peerDot.Visible = true;
                peerDot.BackColor = code <= 0 ? Theme.Dim : col;
                peerStatus.ForeColor = code <= 0 ? Theme.Dim : col;
                peerStatus.Text = txt;   // caller supplies the exact wording
                LayoutPresence();
            }
            string AgoText(DateTime t)
            {
                double s = (DateTime.Now - t).TotalSeconds;
                if (s < 60) return "active just now";
                if (s < 3600) return "active " + (int)(s / 60) + "m ago";
                if (s < 86400) return "active " + (int)(s / 3600) + "h ago";
                return "active " + (int)(s / 86400) + "d ago";
            }
            // Presence: instant Online on recent activity; otherwise the LIVE backend status (REQUEST_PLAYER_INFO ->
            // token 780) which works for ANY player cross-platform; else "active X ago" / unknown.
            (int code, string txt, Color col) PresenceDisplay(string key)
            {
                // Presence polls can only be arriving while we're actually connected. If the link silently died, any
                // "fresh" qstatus entry is leftover from right before the drop, not a live read — trusting it would
                // misreport someone as confidently Online/Offline when really we just stopped hearing from the server.
                bool live = engine != null && engine.State == "connected";
                bool hasQ = qstatus.TryGetValue(key, out var q);
                bool hasS = lastSeen.TryGetValue(key, out var seen);
                bool qFresh = live && hasQ && (DateTime.Now - q.when).TotalSeconds < 60;   // live backend status (polled every 5s)
                bool sFresh = hasS && (DateTime.Now - seen).TotalSeconds < 60;     // they messaged us very recently
                if (qFresh)
                {
                    // backend status is authoritative; but a message NEWER than the last poll proves they're online
                    if (sFresh && seen > q.when) return (4, "Online", StatusInfo(4, "").col);
                    return q.code == 4 ? (4, "Online", StatusInfo(4, "").col) : (0, "Offline", Theme.Dim);
                }
                if (sFresh) return (4, "Online", StatusInfo(4, "").col);
                if (hasS) return (-2, AgoText(seen), Theme.Dim);
                return (-1, "status unknown", Theme.Dim);
            }
            var thread = new RichTextBox { Dock = DockStyle.Fill, BackColor = Theme.Bg, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, ReadOnly = true, Font = Theme.F(10.5f), HideSelection = true };
            var inputRow = new Panel { Dock = DockStyle.Bottom, Height = S(52), BackColor = Theme.Panel, Padding = new Padding(S(10), S(9), S(10), S(9)) };
            var sendBtn = new Button { Dock = DockStyle.Right, Width = S(86), Text = "Send", FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.F(10f, FontStyle.Bold), Cursor = Cursors.Hand, Enabled = false };
            sendBtn.FlatAppearance.BorderSize = 0;
            var input = new TextBox { Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(11f), Enabled = false };
            inputRow.Controls.Add(input); inputRow.Controls.Add(sendBtn);
            var emptyHint = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Dim, Font = Theme.F(11f), TextAlign = ContentAlignment.MiddleCenter, Text = probeExe == null ? "Whisper engine not found.\nBuild Probe5.exe into a 'whisper' folder next to the app." : "Pick or start a conversation on the left.\nThe game stays closed — you log in directly." };
            right.Controls.Add(thread); right.Controls.Add(emptyHint); right.Controls.Add(threadHead); right.Controls.Add(inputRow);
            thread.Visible = false;

            root.Controls.Add(right); root.Controls.Add(left); root.Controls.Add(statusBar);

            // ---- rendering helpers ----
            string FmtTime(long t) { try { return DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("HH:mm"); } catch { return ""; } }
            void AppendThread(WMsg m)
            {
                thread.SelectionStart = thread.TextLength; thread.SelectionLength = 0;
                if (m.Dir == "sys")
                {
                    thread.SelectionColor = Color.FromArgb(210, 150, 60); thread.SelectionFont = Theme.F(9f, FontStyle.Italic);
                    thread.AppendText("⚠  " + m.Text + "\n");
                    return;
                }
                bool outg = m.Dir == "out";
                bool queued = outg && m.St == "queued";
                bool cancelled = outg && m.St == "cancelled";
                thread.SelectionColor = Theme.Dim; thread.SelectionFont = Theme.F(8.5f);
                thread.AppendText((queued ? "⏳ " : "") + FmtTime(m.T) + "  ");
                thread.SelectionColor = outg ? Theme.Blue : Theme.Green; thread.SelectionFont = Theme.F(10.5f, FontStyle.Bold);
                thread.AppendText((outg ? "You" : (activeKey != null && convs.ContainsKey(activeKey) ? convs[activeKey].Display : "")) + ": ");
                thread.SelectionColor = cancelled ? Theme.Dim : Theme.Text;
                thread.SelectionFont = Theme.F(10.5f, cancelled ? FontStyle.Strikeout : FontStyle.Regular);
                thread.AppendText(m.Text);
                if (queued)
                {
                    // "queued — will send when connected", plus a clickable ✕ cancel (mapped to this WMsg by char range)
                    thread.SelectionColor = Theme.Dim; thread.SelectionFont = Theme.F(8.5f, FontStyle.Italic);
                    thread.AppendText("   queued · ");
                    int a = thread.TextLength;
                    thread.SelectionColor = Theme.Accent; thread.SelectionFont = Theme.F(8.5f, FontStyle.Bold);
                    thread.AppendText("✕ cancel");
                    cancelRanges.Add((a, thread.TextLength, m));
                }
                else if (cancelled)
                {
                    thread.SelectionColor = Theme.Dim; thread.SelectionFont = Theme.F(8.5f, FontStyle.Italic);
                    thread.AppendText("   cancelled");
                }
                thread.SelectionColor = Theme.Text; thread.SelectionFont = Theme.F(10.5f); thread.AppendText("\n");
            }
            // Jump the thread straight to the newest message (no visible scroll-through).
            void ScrollThreadToBottom() { thread.SelectionStart = thread.TextLength; thread.SelectionLength = 0; thread.ScrollToCaret(); }
            void RenderThread(string key)
            {
                cancelRanges.Clear();
                // Freeze painting while we refill, so a long history renders instantly at the bottom instead of
                // visibly scrolling top-to-bottom as each line is appended.
                SuspendDrawing(thread);
                try
                {
                    thread.Clear();
                    if (key != null && convs.TryGetValue(key, out var c))
                    {
                        // Render only the most recent messages — a thread with thousands of lines (heavy testing/spam)
                        // is otherwise slow to rebuild segment-by-segment. The newest are what you want; older stay saved.
                        const int CAP = 300;
                        int total = c.Msgs.Count, start = total > CAP ? total - CAP : 0;
                        if (start > 0)
                        {
                            thread.SelectionStart = thread.TextLength; thread.SelectionColor = Theme.Dim; thread.SelectionFont = Theme.F(8.5f, FontStyle.Italic);
                            thread.AppendText("— showing the last " + CAP + " of " + total + " messages —\n\n");
                        }
                        for (int i = start; i < total; i++) AppendThread(c.Msgs[i]);
                    }
                }
                finally { ResumeDrawing(thread); }
                ScrollThreadToBottom();   // after redraw resumes, so it positions on the last line
            }
            // Click the red "✕ cancel" on a queued message to retract it (works until the engine actually sends it).
            void CancelQueued(WMsg m)
            {
                string key = activeKey;   // snapshot (this runs on the UI thread, but keep it stable through the call)
                if (m == null || m.St != "queued" || key == null) return;
                string disp = convs.TryGetValue(key, out var c) ? c.Display : null;
                bool removed = engine != null && disp != null && engine.Cancel(disp, m.Text);
                m.St = removed ? "cancelled" : "";   // couldn't retract (already left for the server) -> treat as sent
                SaveConvs();
                RenderThread(key); RenderConvList(true);
                RefreshConnLabel();
            }
            thread.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || cancelRanges.Count == 0) return;
                int ci = thread.GetCharIndexFromPosition(e.Location);
                if (ci < 0) return;   // click outside the text bounds
                foreach (var r in cancelRanges) if (ci >= r.a && ci < r.b) { CancelQueued(r.m); break; }
            };
            // The preview line under each conversation name (last message, with a ⏳/✕ marker for queued/cancelled sends).
            string SnippetFor(WConv c)
            {
                if (c.Msgs.Count == 0) return "";
                var lm = c.Msgs[c.Msgs.Count - 1];
                string s = (lm.Dir == "out" ? "You: " : "") + lm.Text;
                if (lm.St == "queued") s = "⏳ " + s;
                else if (lm.St == "cancelled") s = "✕ " + s;
                if (s.Length > 34) s = s.Substring(0, 33) + "…";
                return s;
            }
            ContextMenuStrip BuildRowMenu(string key)
            {
                var ctx = new ContextMenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Text };
                bool pinned = convs.TryGetValue(key, out var c) && c.Pin;
                var pin = new ToolStripMenuItem(pinned ? "Unpin conversation" : "Pin conversation") { ForeColor = Theme.Text };
                pin.Click += (s, e) => TogglePin(key);
                var del = new ToolStripMenuItem("Delete conversation") { ForeColor = Theme.Text };
                del.Click += (s, e) => DeleteConv(key);
                ctx.Items.Add(pin); ctx.Items.Add(del);
                return ctx;
            }
            void TogglePin(string key)
            {
                if (!convs.TryGetValue(key, out var c)) return;
                c.Pin = !c.Pin; SaveConvs(); RenderConvList(true);   // order changed -> full rebuild
            }
            // Refresh only the mutable bits of an existing row (highlight, name, snippet, presence dot, pin bar) — no repaint
            // unless a value actually changed. This is what keeps the list from flickering on every 5s presence poll.
            void UpdateRow(string key)
            {
                if (!rowMap.TryGetValue(key, out var r) || !convs.TryGetValue(key, out var c)) return;
                var bg = key == activeKey ? Color.FromArgb(34, 34, 40) : Theme.Panel;
                if (r.row.BackColor != bg) r.row.BackColor = bg;
                if (r.nm.BackColor != bg) r.nm.BackColor = bg;
                if (r.sub.BackColor != bg) r.sub.BackColor = bg;
                if (r.nm.Text != c.Display) r.nm.Text = c.Display;
                string snip = SnippetFor(c);
                if (r.sub.Text != snip) r.sub.Text = snip;
                var pdd = PresenceDisplay(key);
                var dc = pdd.code > 0 ? pdd.col : Theme.Dim;
                if (r.dot.BackColor != dc) r.dot.BackColor = dc;
                if (r.pin.Visible != c.Pin) r.pin.Visible = c.Pin;
            }
            // Pinned first, then most-recent, then name (stable so the order never flaps between identical-timestamp ticks).
            void RenderConvList(bool force = false)
            {
                var ordered = convs.Where(k => !k.Value.Hidden)
                                   .OrderByDescending(k => k.Value.Pin)
                                   .ThenByDescending(k => k.Value.Last)
                                   .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                                   .Select(k => k.Key).ToList();
                // Fast path: same set+order as last render -> just update the rows in place (no Clear, no flicker, no scroll-jump).
                bool sameShape = !force && ordered.Count == rowOrder.Count;
                if (sameShape) for (int i = 0; i < ordered.Count; i++) if (ordered[i] != rowOrder[i] || !rowMap.ContainsKey(ordered[i])) { sameShape = false; break; }
                if (sameShape) { foreach (var key in ordered) UpdateRow(key); return; }

                // Structural change (new/removed/reordered conversation) -> rebuild.
                convList.SuspendLayout(); convList.Controls.Clear(); rowMap.Clear(); rowOrder.Clear();
                int y = S(4);
                foreach (var key in ordered)
                {
                    var c = convs[key];
                    var rowp = new BufPanel { Location = new Point(S(6), y), Size = new Size(left.Width - S(20), S(52)), BackColor = key == activeKey ? Color.FromArgb(34, 34, 40) : Theme.Panel, Cursor = Cursors.Hand };
                    var pinBar = new Panel { Size = new Size(S(3), S(52)), Location = new Point(0, 0), BackColor = Theme.Accent, Visible = c.Pin };   // accent edge = pinned
                    var nm = new Label { AutoSize = true, Location = new Point(S(12), S(7)), ForeColor = Theme.Text, Font = Theme.F(10.5f, FontStyle.Bold), BackColor = rowp.BackColor, Text = c.Display };
                    var sub = new Label { AutoSize = true, Location = new Point(S(12), S(28)), ForeColor = Theme.Dim, Font = Theme.F(8.5f), BackColor = rowp.BackColor, Text = SnippetFor(c) };
                    var pdot = new Panel { Size = new Size(S(9), S(9)), Location = new Point(rowp.Width - S(22), S(21)), BackColor = Theme.Dim };
                    { var pdd = PresenceDisplay(key); if (pdd.code > 0) pdot.BackColor = pdd.col; }
                    rowp.Controls.Add(pinBar); rowp.Controls.Add(nm); rowp.Controls.Add(sub); rowp.Controls.Add(pdot);
                    string k2 = key;
                    EventHandler open = (s, e) => OpenConv(k2);
                    rowp.Click += open; nm.Click += open; sub.Click += open;
                    var ctx = BuildRowMenu(k2);
                    rowp.ContextMenuStrip = ctx; nm.ContextMenuStrip = ctx; sub.ContextMenuStrip = ctx;
                    convList.Controls.Add(rowp);
                    rowMap[key] = (rowp, nm, sub, pdot, pinBar); rowOrder.Add(key);
                    y += S(56);
                }
                convList.ResumeLayout();
            }
            void AppendSystem(string text)
            {
                thread.SelectionStart = thread.TextLength; thread.SelectionLength = 0;
                thread.SelectionColor = Theme.Dim; thread.SelectionFont = Theme.F(8.5f, FontStyle.Italic);
                thread.AppendText("ℹ  " + text + "\n");
                thread.SelectionStart = thread.TextLength; thread.ScrollToCaret();
            }
            // The chat engine knows each player's REAL Hi-Rez id (the number in "0-9-0:<id>") from the message itself
            // — reliable cross-platform, unlike name lookup which fails for Epic accounts. Read chatcap's id->name map.
            string LookupChatId(string display)
            {
                try
                {
                    string f = Path.Combine(relayDir, "idname.tsv");
                    if (!File.Exists(f)) return "";
                    string want = NormKey(display);
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var p = line.Split('\t');
                        if (p.Length >= 2 && NormKey(p[1]) == want)
                        { string id = p[0]; int ci = id.LastIndexOf(':'); return ci >= 0 ? id.Substring(ci + 1) : id; }
                    }
                }
                catch { }
                return "";
            }
            async System.Threading.Tasks.Task<string> ResolveId(WConv c)
            {
                string chatId = LookupChatId(c.Display);   // prefer the real id the chat gave us (works for Epic/cross-platform; beats a bad name-lookup)
                if (!string.IsNullOrEmpty(chatId)) { if (c.Id != chatId) { c.Id = chatId; SaveConvs(); } return chatId; }
                if (!string.IsNullOrEmpty(c.Id)) return c.Id;
                try
                {
                    using var doc = JsonDocument.Parse(await SmiteApi.Call("getplayeridbyname", c.Display));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        string id = GS(doc.RootElement[0], "player_id");
                        if (!string.IsNullOrEmpty(id) && id != "0") { c.Id = id; SaveConvs(); return id; }
                    }
                }
                catch { }
                return "";
            }
            // Ask the chat backend for live status of ALL open conversations in ONE batch (REQUEST_PLAYER_INFO each).
            // Replies land in presence.tsv -> engine.Presence event -> qstatus -> PresenceDisplay. Works for ANY player.
            void CheckPresence(string key) { QueryAllPresence(); }
            void QueryAllPresence()
            {
                if (engine == null || convs.Count == 0) return;
                var names = new List<string>();
                foreach (var c in convs.Values) if (!c.Hidden) names.Add(c.Display);   // don't poll soft-deleted threads
                if (names.Count > 0) engine.Query(names);
            }
            // Soft delete: hide the row but KEEP the full message history. Reopening (whisper them again, pick from
            // Friends, or an incoming message) restores the thread exactly as it was.
            void DeleteConv(string key)
            {
                if (key == null || !convs.TryGetValue(key, out var c)) return;
                c.Hidden = true; c.Pin = false; SaveConvs();   // keep c.Msgs intact
                if (activeKey == key)
                {
                    activeKey = null; thread.Clear(); thread.Visible = false; emptyHint.Visible = true;
                    peerLbl.Text = ""; peerDot.Visible = false; peerStatus.Text = ""; input.Enabled = sendBtn.Enabled = false;
                }
                RenderConvList(true);
            }
            void OpenConv(string key)
            {
                if (key == null || !convs.TryGetValue(key, out var oc)) return;
                bool wasHidden = oc.Hidden;
                if (wasHidden) { oc.Hidden = false; SaveConvs(); }   // reopening a soft-deleted thread brings it back, history and all
                activeKey = key;
                peerLbl.Text = convs[key].Display;
                var pd0 = PresenceDisplay(key); ShowPresence(pd0.code, pd0.txt, pd0.col);
                CheckPresence(key);
                emptyHint.Visible = false; thread.Visible = true;
                input.Enabled = sendBtn.Enabled = engine != null;
                RenderThread(key); RenderConvList(wasHidden);   // unhiding changes the list shape -> force rebuild
                input.Focus();
            }
            string EnsureConv(string display, string id = null)
            {
                string key = NormKey(display);
                if (key.Length == 0) return null;
                if (!convs.ContainsKey(key)) { convs[key] = new WConv { Key = key, Display = display.Trim(), Id = id ?? "", Last = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }; SaveConvs(); }
                else if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(convs[key].Id)) { convs[key].Id = id; SaveConvs(); }
                return key;
            }
            WMsg AddMsg(string key, string dir, string text, string st = "")
            {
                if (!convs.TryGetValue(key, out var c)) return null;
                if (c.Hidden && dir != "sys") c.Hidden = false;   // a real message (in or out) restores a soft-deleted thread
                var m = new WMsg { T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Dir = dir, Text = text, St = st };
                c.Msgs.Add(m); c.Last = m.T; SaveConvs();
                if (key == activeKey) { AppendThread(m); ScrollThreadToBottom(); }
                RenderConvList();
                return m;
            }

            // Pending sends awaiting the server's delivery echo. If the echo (\x01SENT) doesn't arrive within a
            // few seconds, the message was rejected (spam filter) or undeliverable (offline) -> warn.
            var pending = new List<(string text, string key, DateTime at)>();

            int QueuedCount() { int n = 0; foreach (var c in convs.Values) foreach (var m in c.Msgs) if (m.Dir == "out" && m.St == "queued") n++; return n; }
            // Re-render the whole status bar (game warning > connection state + queued count).
            void RefreshConnLabel() { SetStatus(engine == null ? "stopped" : engine.State); }
            // Login finished: every message we queued during connect now actually sends — flip it to a normal send and
            // start the delivery watch so spam/offline still gets flagged.
            void FlushQueued()
            {
                bool any = false;
                foreach (var kv in convs)
                    foreach (var m in kv.Value.Msgs)
                        if (m.Dir == "out" && m.St == "queued") { m.St = ""; any = true; pending.Add((m.Text, kv.Key, DateTime.Now)); }
                if (any) { SaveConvs(); if (activeKey != null) RenderThread(activeKey); RenderConvList(true); }
            }

            // Auto-recovery from a silently dropped connection ("disconnected" — see WhisperEngine's connected=False /
            // hang-watchdog detection). The chat session can't heal itself reliably (the lib's own reconnect path is
            // the very thing Probe5 has to fight off during login — see its pump-patch comments), so we cycle the
            // whole engine: a fresh Probe5 process re-logs in cleanly. Backs off on repeated failures so a genuinely
            // offline network doesn't spin in a tight restart loop; resets the moment we're connected again.
            int reconnectAttempts = 0;
            System.Windows.Forms.Timer reconnectTimer = null;
            // Compact APP-side event log (the engine-live.log only has Probe5's side). Captures the connection lifecycle
            // + send outcomes so a bug report shows what the app DID, not just what the engine saw. Included in Export Logs.
            var wlog = new List<string>();
            void WLog(string s) { try { wlog.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); if (wlog.Count > 2000) wlog.RemoveRange(0, wlog.Count - 2000); } catch { } }
            // Drop any pending auto-reconnect and reset its backoff. Called whenever we reach a genuinely healthy
            // "connected" state, or whenever something ELSE already restarted the engine (a manual login-settings
            // change) — otherwise a stale scheduled restart fires later and disrupts an already-fine connection.
            void CancelPendingReconnect() { reconnectTimer?.Stop(); reconnectTimer?.Dispose(); reconnectTimer = null; reconnectAttempts = 0; }
            void ScheduleReconnect()
            {
                if (engine == null) return;
                if (reconnectTimer != null) return;   // already scheduled — a duplicate "disconnected" raise for the same outage must not double the backoff
                // SMITE open on the same Steam account is the one disconnect cause a restart can NEVER fix (the
                // server will just close the fresh login again); don't churn the engine while that's true. Reopening
                // the tab / app already re-attempts the connection once the conflict clears.
                if (_gameOpen) { WLog("reconnect skipped — SMITE open on this account"); return; }
                int attempt = reconnectAttempts++;
                int delayMs = Math.Min(2000 * (int)Math.Pow(2, Math.Min(attempt, 4)), 30000);   // 2s,4s,8s,16s,30s cap
                WLog("reconnect scheduled — attempt " + (attempt + 1) + " in " + (delayMs / 1000) + "s");
                reconnectTimer = new System.Windows.Forms.Timer { Interval = delayMs };
                reconnectTimer.Tick += (s, e) =>
                {
                    reconnectTimer.Stop(); reconnectTimer.Dispose(); reconnectTimer = null;
                    // Re-check the game right before restarting (it may have opened during the backoff delay) so a
                    // reconnect never logs in and kicks a now-running game's chat.
                    if (engine == null || !LoginReady() || CheckGameOpen()) { if (CheckGameOpen()) { _gameOpen = true; SetStatus(engine != null ? engine.State : "stopped"); } return; }
                    WLog("reconnecting now — restarting engine");
                    try { engine.Stop(); } catch { }
                    ApplyLogin(); engine.Start();
                };
                reconnectTimer.Start();
            }

            // ---- engine wiring (events arrive off-thread -> marshal to UI) ----
            void Ui(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }
            if (engine != null)
            {
                engine.Status += st => Ui(() =>
                {
                    WLog("engine state -> " + st + (st == "connected" ? "  (login=" + loginMode + ")" : ""));
                    SetStatus(st);
                    if (st == "connected")
                    {
                        CancelPendingReconnect();
                        FlushQueued();
                        // Presence reads taken while disconnected are deliberately suppressed (see PresenceDisplay's
                        // "live" gate) — force a fresh round so the conv list / open thread don't sit on a stale
                        // "status unknown" display until the next 5s poll happens to land (FlushQueued only re-renders
                        // when something was actually queued, which isn't true for the common "friend just idle" case).
                        RenderConvList(true);
                        if (activeKey != null) { var pd = PresenceDisplay(activeKey); ShowPresence(pd.code, pd.txt, pd.col); }
                        QueryAllPresence();
                    }
                    else if (st == "disconnected") ScheduleReconnect();
                    else RefreshConnLabel();
                });
                engine.Inbound += (sender, text) => Ui(() =>
                {
                    if (!string.IsNullOrEmpty(sender) && sender[0] == (char)1)   // delivery confirmation, not a message
                    {
                        // NOTE: the server echoes ACCEPTED messages even for OFFLINE recipients (it queues them),
                        // so this confirms acceptance — NOT that the recipient is online. Do not infer presence.
                        // The wire format carries no recipient/message id on this echo (just a timestamp + the literal
                        // text), so matching is inherently text-based — but BOTH the pending-list entry and the WMsg we
                        // clear must agree on which conversation, or a duplicate-text send to two people (or two rapid
                        // identical sends to the same person) can clear the wrong one. Resolve the conversation ONCE
                        // from the OLDEST matching pending entry (sends confirm in the order they left), then scope the
                        // WMsg clear to that same conversation instead of searching every open conversation.
                        int pi = -1;
                        for (int i = 0; i < pending.Count; i++) if (pending[i].text == text) { pi = i; break; }   // oldest first
                        string matchKey = pi >= 0 ? pending[pi].key : null;
                        if (pi >= 0) pending.RemoveAt(pi);
                        // The server accepted it, so it definitely LEFT — clear any lingering "queued" marker on it
                        // (e.g. a reconnect meant the "connected" state event never fired to flush it).
                        bool cleared = false;
                        if (matchKey != null && convs.TryGetValue(matchKey, out var mc))
                            foreach (var m in mc.Msgs) if (m.Dir == "out" && m.St == "queued" && m.Text == text) { m.St = ""; cleared = true; break; }
                        if (cleared) { SaveConvs(); if (activeKey == matchKey) RenderThread(matchKey); RenderConvList(true); RefreshConnLabel(); }
                        WLog("delivery: confirmed by server (SENT echo)" + (matchKey != null ? " (" + matchKey + ")" : ""));
                        return;
                    }
                    string disp = string.IsNullOrWhiteSpace(sender) ? (activeKey != null ? convs[activeKey].Display : "?") : sender;
                    string key = EnsureConv(disp);
                    if (key != null)
                    {
                        AddMsg(key, "in", text);
                        lastSeen[key] = DateTime.Now;   // they messaged us -> online RIGHT NOW (drives PresenceDisplay)
                        if (activeKey == key) { var pd = PresenceDisplay(key); ShowPresence(pd.code, pd.txt, pd.col); }
                        RenderConvList();
                    }
                });
                engine.Presence += (name, online) => Ui(() =>
                {
                    string key = NormKey(name);
                    qstatus[key] = (online ? 4 : 0, DateTime.Now);
                    if (convs.ContainsKey(key))
                    {
                        if (activeKey == key) { var pd = PresenceDisplay(key); ShowPresence(pd.code, pd.txt, pd.col); }
                        RenderConvList();
                    }
                });
            }

            void DoSend()
            {
                if (engine == null || activeKey == null) return;
                string msg = input.Text.Trim(); if (msg.Length == 0) return;
                string key = activeKey;
                bool connected = engine.State == "connected";   // sample BEFORE Send so a mid-send flip can't double-handle it
                engine.Send(convs[key].Display, msg);
                WLog("outgoing -> " + convs[key].Display + "  connected=" + connected + (connected ? "" : " (queued)"));
                // Engine still logging in -> the message is QUEUED. Show it as queued (with a ✕ cancel) and bail; the
                // delivery/offline checks happen later, when login finishes and FlushQueued() actually sends it.
                if (!connected)
                {
                    AddMsg(key, "out", msg, "queued");
                    if (engine.State == "connected") FlushQueued();   // login finished during this send -> promote it now
                    RefreshConnLabel();
                    input.Clear(); input.Focus();
                    return;
                }
                AddMsg(key, "out", msg);
                // Instant offline feedback (mirrors in-game): if our live presence poll already
                // shows them offline, say so right now instead of waiting for the no-echo timeout.
                // Otherwise queue for the delivery watcher (catches spam/repetition rejections).
                bool knownOffline = qstatus.TryGetValue(key, out var q)
                                    && (DateTime.Now - q.when).TotalSeconds < 60 && q.code == 0;
                if (knownOffline)
                {
                    string snip = msg.Length > 40 ? msg.Substring(0, 39) + "…" : msg;
                    AddMsg(key, "sys", "\"" + snip + "\" they're offline — it wasn't delivered.");
                }
                else
                {
                    pending.Add((msg, key, DateTime.Now));
                }
                input.Clear(); input.Focus();
            }
            // warn about sends the server never echoed back (rejected / undeliverable)
            var deliverTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            deliverTimer.Tick += (s, e) =>
            {
                var now = DateTime.Now;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    // Wait 6s for the server's accept echo — under a burst it can lag a few seconds, and a delivered
                    // message that just echoes late must NOT be mislabelled "rejected".
                    if ((now - pending[i].at).TotalSeconds < 6) continue;
                    var p = pending[i]; pending.RemoveAt(i);
                    if (!convs.ContainsKey(p.key)) continue;
                    string snip = p.text.Length > 40 ? p.text.Substring(0, 39) + "…" : p.text;
                    // The missing echo proves nothing about spam/offline if the LINK itself dropped mid-send — that's
                    // not a rejection, just an unconfirmed send. Re-queue it (the engine auto-reconnects and
                    // FlushQueued() resends once it's back) instead of misreporting "spam filter rejected it".
                    if (engine == null || engine.State != "connected")
                    {
                        foreach (var m in convs[p.key].Msgs)
                            if (m.Dir == "out" && m.St == "" && m.Text == p.text) { m.St = "queued"; break; }
                        SaveConvs(); if (activeKey == p.key) RenderThread(p.key); RenderConvList(true); RefreshConnLabel();
                        AddMsg(p.key, "sys", "\"" + snip + "\" connection dropped before this was confirmed — it'll resend automatically once reconnected.");
                        WLog("delivery: no echo + link down -> re-queued (" + p.key + ")");
                        continue;
                    }
                    // No echo within the window while still connected ⇒ genuinely not accepted. Blame offline if we
                    // know they're offline, else spam.
                    {
                        bool offline = qstatus.TryGetValue(p.key, out var q) && (DateTime.Now - q.when).TotalSeconds < 90 && q.code == 0;
                        string reason = _gameOpen
                            ? "your Steam SMITE is open — close it; the messenger can't share that account's chat session."
                            : offline
                            ? "they're offline — it wasn't delivered."
                            : "SMITE's spam/repetition filter rejected it — try rephrasing.";
                        AddMsg(p.key, "sys", "\"" + snip + "\" " + reason);
                        WLog("delivery: no echo (still connected) -> " + (_gameOpen ? "game-open" : offline ? "offline" : "spam/reject") + " (" + p.key + ")");
                    }
                }
            };
            deliverTimer.Start();
            sendBtn.Click += (s, e) => DoSend();
            input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSend(); } };

            // Start the engine, honouring the chosen login method. In Hi-Rez mode we can't connect without a password
            // (never persisted), so prompt for it instead of spawning a doomed login.
            void TryStartEngine()
            {
                if (engine == null || engine.Running) return;
                // NEVER start the engine while THIS account's SMITE is open. A second chat login makes the server
                // CLOSE_CONNECTION one of the two sessions — which disconnects the running game's chat. Refuse to start
                // and show the "close SMITE" banner; the presence timer auto-starts us the moment the game closes.
                _gameOpen = CheckGameOpen();
                if (_gameOpen) { SetStatus(engine.State); return; }
                if (!LoginReady()) { OpenLoginSettings(); return; }
                ApplyLogin();
                engine.Start();
            }
            // Strong diagnostic export: one timestamped .zip on the Desktop holding a full report + the live engine log
            // + every relay log. Built so a friend can send it back when something doesn't work.
            void ExportLogs()
            {
                // Ask first, then let the user choose where to save the zip.
                if (MessageBox.Show(this,
                    "Export diagnostic logs for debugging?\n\nThis bundles the engine + relay logs and a system report into one .zip you can share. It does NOT include your password or your message history.",
                    "Export Logs", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string zipPath;
                using (var sfd = new SaveFileDialog { Title = "Save diagnostic logs", FileName = "SmiteInspector-logs-" + stamp + ".zip", Filter = "Zip archive (*.zip)|*.zip", DefaultExt = "zip", AddExtension = true, OverwritePrompt = true })
                {
                    try { sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } catch { }
                    if (sfd.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(sfd.FileName)) return;
                    zipPath = sfd.FileName;
                }
                try
                {
                    void AddText(System.IO.Compression.ZipArchive z, string name, string content)
                    { try { var en = z.CreateEntry(name); using (var w = new StreamWriter(en.Open(), new UTF8Encoding(false))) w.Write(content ?? ""); } catch { } }
                    void AddFile(System.IO.Compression.ZipArchive z, string name, string path)
                    { try { var en = z.CreateEntry(name); using (var es = en.Open()) using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) fs.CopyTo(es); } catch { } }
                    string ProcInfo(string pname)
                    {
                        try
                        {
                            var ps = System.Diagnostics.Process.GetProcessesByName(pname);
                            if (ps.Length == 0) return "not running";
                            var sb2 = new StringBuilder();
                            foreach (var pr in ps) { string mp; try { mp = pr.MainModule != null ? pr.MainModule.FileName : ""; } catch { mp = "<path unavailable>"; } sb2.Append("\r\n    pid " + pr.Id + "  " + mp); try { pr.Dispose(); } catch { } }
                            return sb2.ToString();
                        }
                        catch (Exception e) { return "err: " + e.Message; }
                    }

                    var sb = new StringBuilder();
                    var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    int visible = 0; foreach (var c in convs.Values) if (!c.Hidden) visible++;
                    sb.AppendLine("SMITE 1 Inspector — Whispers diagnostic report");
                    sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " local / " + DateTime.UtcNow.ToString("HH:mm:ss") + " UTC");
                    sb.AppendLine("=====================================================");
                    sb.AppendLine("[App]");
                    sb.AppendLine("  Version:  " + (ver != null ? ver.ToString() : "?"));
                    sb.AppendLine("  AppDir:   " + Theme.AppDir);
                    sb.AppendLine("  DataDir:  " + Theme.DataDir);
                    sb.AppendLine("[System]");
                    sb.AppendLine("  OS:       " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
                    sb.AppendLine("  .NET:     " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
                    sb.AppendLine("  Arch:     " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + "  procs=" + Environment.ProcessorCount);
                    sb.AppendLine("  TimeZone: " + TimeZoneInfo.Local.DisplayName);
                    sb.AppendLine("[Whispers engine]");
                    sb.AppendLine("  Probe5:   " + (probeExe ?? "NOT FOUND"));
                    sb.AppendLine("  Relay:    " + relayDir);
                    sb.AppendLine("  Login:    mode=" + loginMode + "  user-set=" + (loginUser.Length > 0 ? "yes" : "no") + "  auto-connect=" + loginAuto + "  login-ready=" + LoginReady());
                    sb.AppendLine("  Engine:   running=" + (engine != null && engine.Running) + "  state=" + (engine != null ? engine.State : "<no engine>"));
                    sb.AppendLine("  Status:   \"" + statusLbl.Text + "\"   game-conflict-warn=" + _gameOpen);
                    // Connection HEALTH — the signals that make a dead/degraded session obvious at a glance instead of
                    // buried in the raw logs (a "connected" state with no traffic for minutes = dead link; the exact
                    // identity the login sent = the GAMER_TAG class of bug; all-offline presence + a server CLOSE).
                    sb.AppendLine("[Connection health]");
                    if (engine != null)
                    {
                        double ta = engine.SecondsSinceTraffic;
                        sb.AppendLine("  ever-connected=" + engine.EverConnected + "  inbound-replies=" + engine.InboundCount
                            + "  last-server-reply=" + (engine.EverConnected ? ((int)ta) + "s ago" : "never"));
                        if (engine.State == "connected" && engine.EverConnected && ta > 60)
                            sb.AppendLine("  !! state is \"connected\" but NO server reply in " + (int)ta + "s — the link is likely DEAD (stale session).");
                    }
                    try
                    {
                        string idLine = null;
                        if (engine != null) foreach (var ln in engine.RecentLog()) { int ix = ln.IndexOf("SetPortalUserName(", StringComparison.Ordinal); if (ix >= 0) idLine = ln.Substring(ix); }
                        sb.AppendLine("  login-identity-sent: " + (idLine ?? "(not logged this run — Hi-Rez mode or pre-login)"));
                    }
                    catch { }
                    try
                    {
                        string pf = Path.Combine(relayDir, "presence.tsv");
                        if (File.Exists(pf))
                        {
                            int tot = 0, on = 0;
                            foreach (var ln in File.ReadLines(pf)) { var pp = ln.Split('\t'); if (pp.Length >= 3) { tot++; if (pp[2].Trim() == "0") on++; } }
                            sb.AppendLine("  presence: " + on + " online / " + tot + " reported" + (tot > 20 && on == 0 ? "   !! ALL offline — suspicious (dead/stale session, not everyone actually away)" : ""));
                        }
                    }
                    catch { }
                    try
                    {
                        string cf = Path.Combine(relayDir, "chatcap.log");
                        if (File.Exists(cf)) sb.AppendLine("  server CLOSE_CONNECTION seen: " + (File.ReadAllText(cf).IndexOf("CLOSE_CONNECTION", StringComparison.Ordinal) >= 0 ? "YES !! (server dropped the chat session)" : "no"));
                    }
                    catch { }
                    sb.AppendLine("[Running processes]");
                    sb.AppendLine("  Smite.exe:  " + ProcInfo("Smite"));
                    sb.AppendLine("  Probe5.exe: " + ProcInfo("Probe5"));
                    sb.AppendLine("  steam:      " + ProcInfo("steam"));
                    sb.AppendLine("[Conversations]");
                    sb.AppendLine("  total=" + convs.Count + "  visible=" + visible + "   (counts only — message history is NOT exported)");
                    sb.AppendLine("=====================================================");
                    sb.AppendLine("Included: report.txt, engine-live.log, app-events.log, relay/* (probe5_out.txt, chatcap.log, loginfix.log,");
                    sb.AppendLine("  eosinproc.log, presence.tsv, idname.tsv, myparams.txt, whisper_in/out.txt ...), login.json.");
                    sb.AppendLine("Privacy: relay logs can contain whisper text + your Hi-Rez username. They do NOT contain your");
                    sb.AppendLine("  password. Your conversation history (conversations.json) is NOT included.");

                    using (var zip = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
                    {
                        AddText(zip, "report.txt", sb.ToString());
                        if (engine != null) AddText(zip, "engine-live.log", string.Join("\r\n", engine.RecentLog()));
                        AddText(zip, "app-events.log", string.Join("\r\n", wlog));
                        try { if (Directory.Exists(relayDir)) foreach (var f in Directory.GetFiles(relayDir)) AddFile(zip, "relay/" + Path.GetFileName(f), f); } catch { }
                        try { if (File.Exists(loginFile)) AddFile(zip, "login.json", loginFile); } catch { }
                    }
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + zipPath + "\"") { UseShellExecute = true }); } catch { }
                    MessageBox.Show(this, "Logs exported to:\n\n" + zipPath + "\n\nSend this .zip file for debugging.", "Export Logs", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not export logs:\n" + ex.Message, "Export Logs", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            // Options: login method (Steam ticket vs Hi-Rez username/password) + auto-connect on app startup.
            // The username/password fields are only shown for Hi-Rez; the dialog resizes around them.
            void OpenLoginSettings()
            {
                int W = S(520);
                var dlg = new Form { Text = "Options", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowIcon = false, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.F(10f), ClientSize = new Size(W, S(260)) };
                var hdr = new Label { Text = "LOGIN METHOD", AutoSize = true, Location = new Point(S(20), S(16)), ForeColor = Theme.Dim, Font = Theme.F(8f, FontStyle.Bold) };
                var rSteam = new RadioButton { Text = "Steam  —  uses your Steam SMITE (Steam shows the game running)", AutoSize = true, Location = new Point(S(18), S(38)), ForeColor = Theme.Text, Checked = loginMode == "steam" };
                var rHirez = new RadioButton { Text = "Hi-Rez login  —  username + password, no Steam status (faster)", AutoSize = true, Location = new Point(S(18), S(66)), ForeColor = Theme.Text, Checked = loginMode == "hirez" };
                // Hi-Rez-only credential block (shown/hidden by Sync)
                int credY = S(98);
                var lblU = new Label { Text = "Hi-Rez username", AutoSize = true, Location = new Point(S(22), credY), ForeColor = Theme.Dim, Font = Theme.F(8.5f) };
                var tbU = new TextBox { Location = new Point(S(22), credY + S(18)), Width = W - S(44), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = loginUser };
                var lblP = new Label { Text = "Password", AutoSize = true, Location = new Point(S(22), credY + S(48)), ForeColor = Theme.Dim, Font = Theme.F(8.5f) };
                var tbP = new TextBox { Location = new Point(S(22), credY + S(66)), Width = W - S(44), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true, Text = loginPass };
                var chkRemember = new CheckBox { Text = "Remember me  (stores the password encrypted on this PC)", AutoSize = true, Location = new Point(S(20), credY + S(94)), ForeColor = Theme.Text, Checked = loginRemember };
                var note = new Label { Text = "Experimental. A SMITE account created through Steam may not have a\nHi-Rez password — if Hi-Rez login fails, use Steam.", AutoSize = true, Location = new Point(S(22), credY + S(122)), ForeColor = Theme.Dim, Font = Theme.F(8f, FontStyle.Italic) };
                int credH = S(166);   // vertical space the credential block occupies when visible
                // Auto-connect option + buttons (repositioned below the cred block when Hi-Rez is selected)
                var chkAuto = new CheckBox { Text = "Connect automatically when the app opens", AutoSize = true, ForeColor = Theme.Text, Checked = loginAuto };
                var chkNote = new Label { Text = "Ready the moment you open Whispers. In Steam mode this shows SMITE\nrunning on Steam the whole time the app is open.", AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8f, FontStyle.Italic) };
                var btnSave = new Button { Text = "Save && Connect", FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.F(9.5f, FontStyle.Bold), Width = S(160), Height = S(34), Cursor = Cursors.Hand };
                btnSave.FlatAppearance.BorderSize = 0;
                var btnCancel = new Button { Text = "Cancel", FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(9.5f), Width = S(110), Height = S(34), Cursor = Cursors.Hand };
                btnCancel.FlatAppearance.BorderColor = Theme.Line;
                void Sync()
                {
                    bool h = rHirez.Checked;
                    lblU.Visible = tbU.Visible = lblP.Visible = tbP.Visible = note.Visible = chkRemember.Visible = h;
                    int y = S(98) + (h ? credH : 0);
                    chkAuto.Location = new Point(S(20), y);
                    chkNote.Location = new Point(S(38), y + S(24));
                    btnSave.Location = new Point(S(20), y + S(58));
                    btnCancel.Location = new Point(S(20) + btnSave.Width + S(12), y + S(58));
                    dlg.ClientSize = new Size(W, y + S(58) + S(34) + S(18));
                }
                rSteam.CheckedChanged += (s, e) => Sync(); rHirez.CheckedChanged += (s, e) => Sync();
                btnCancel.Click += (s, e) => dlg.Close();
                btnSave.Click += (s, e) =>
                {
                    string newMode = rHirez.Checked ? "hirez" : "steam";
                    if (newMode == "hirez" && (tbU.Text.Trim().Length == 0 || tbP.Text.Length == 0))
                    { MessageBox.Show(dlg, "Enter your Hi-Rez username and password.", "Options", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    bool modeChanged = newMode != loginMode || (newMode == "hirez" && (tbU.Text.Trim() != loginUser || tbP.Text != loginPass));
                    loginMode = newMode; loginUser = tbU.Text.Trim(); loginPass = tbP.Text; loginAuto = chkAuto.Checked; loginRemember = chkRemember.Checked;
                    SaveLogin(); ApplyLogin();
                    dlg.Close();
                    // Reconnect only if the login method/credentials actually changed.
                    if (modeChanged) { CancelPendingReconnect(); try { if (engine != null && engine.Running) engine.Stop(); } catch { } SetStatus("stopped"); }
                    if (root.Visible && engine != null && !engine.Running && LoginReady()) TryStartEngine();
                };
                dlg.Controls.Add(hdr); dlg.Controls.Add(rSteam); dlg.Controls.Add(rHirez);
                dlg.Controls.Add(lblU); dlg.Controls.Add(tbU); dlg.Controls.Add(lblP); dlg.Controls.Add(tbP); dlg.Controls.Add(chkRemember); dlg.Controls.Add(note);
                dlg.Controls.Add(chkAuto); dlg.Controls.Add(chkNote); dlg.Controls.Add(btnSave); dlg.Controls.Add(btnCancel);
                dlg.AcceptButton = btnSave; dlg.CancelButton = btnCancel;
                Sync();
                try { dlg.ShowDialog(this); } finally { dlg.Dispose(); }
            }

            void StartConvFromName(string name, string id = null)
            {
                string key = EnsureConv(name, id);
                if (key == null) return;
                TryStartEngine();
                OpenConv(key);
            }
            newName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var n = newName.Text.Trim(); newName.Clear(); StartConvFromName(n); } };

            // ▾ -> a compact, SEARCHABLE popup of your saved friends (replaces the old full-screen native menu).
            pickBtn.Click += (s, e) =>
            {
                var all = friendList.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var pop = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, BackColor = Theme.Line, Padding = new Padding(1), Size = new Size(S(236), S(312)) };
                var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
                var search = new TextBox { Dock = DockStyle.Top, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(10f) };
                try { search.PlaceholderText = all.Count > 0 ? "Search friends…" : "Type a name…"; } catch { }
                var list = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.F(10f), IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(24) };
                var hint = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Dim, Font = Theme.F(9f), TextAlign = ContentAlignment.MiddleCenter, Text = "No saved friends.\nType a name above, then Enter." };
                list.DrawItem += (s2, e2) =>
                {
                    if (e2.Index < 0) return;
                    bool sel = (e2.State & DrawItemState.Selected) != 0;
                    using (var b = new SolidBrush(sel ? Color.FromArgb(46, 24, 26) : Theme.Panel)) e2.Graphics.FillRectangle(b, e2.Bounds);
                    if (sel) using (var b = new SolidBrush(Theme.Accent)) e2.Graphics.FillRectangle(b, e2.Bounds.X, e2.Bounds.Y, S(3), e2.Bounds.Height);
                    TextRenderer.DrawText(e2.Graphics, (string)list.Items[e2.Index], Theme.F(10f), new Rectangle(e2.Bounds.X + S(10), e2.Bounds.Y, e2.Bounds.Width - S(12), e2.Bounds.Height), sel ? Theme.Text : Color.FromArgb(200, 200, 206), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                };
                // highlighted header ("From Friend List"), then the search box, then the list
                var head = new Panel { Dock = DockStyle.Top, Height = S(30), BackColor = Color.FromArgb(46, 24, 26) };
                var headBar = new Panel { Dock = DockStyle.Left, Width = S(3), BackColor = Theme.Accent };
                var headLbl = new Label { Dock = DockStyle.Fill, Text = "From Friend List", ForeColor = Theme.Text, Font = Theme.F(10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(9), 0, 0, 0), BackColor = Color.FromArgb(46, 24, 26) };
                head.Controls.Add(headLbl); head.Controls.Add(headBar);
                // add order matters for Dock stacking (last added docks first): list/hint fill, search above them, head on top
                inner.Controls.Add(list); inner.Controls.Add(hint); inner.Controls.Add(search); inner.Controls.Add(head);
                pop.Controls.Add(inner);
                void Fill(string q)
                {
                    list.BeginUpdate(); list.Items.Clear();
                    foreach (var f in all) if (q.Length == 0 || f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) list.Items.Add(f.Name);
                    list.EndUpdate();
                    list.Visible = list.Items.Count > 0; hint.Visible = !list.Visible;
                    if (list.Items.Count > 0) list.SelectedIndex = 0;
                }
                Fill("");
                void Commit(string chosen)
                {
                    if (string.IsNullOrWhiteSpace(chosen)) return;
                    var f = all.FirstOrDefault(x => string.Equals(x.Name, chosen, StringComparison.OrdinalIgnoreCase));
                    pop.Close();
                    StartConvFromName(chosen.Trim(), f?.Id);
                }
                list.DoubleClick += (s2, e2) => { if (list.SelectedItem != null) Commit((string)list.SelectedItem); };
                list.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Enter && list.SelectedItem != null) { e2.Handled = true; Commit((string)list.SelectedItem); } else if (e2.KeyCode == Keys.Escape) { e2.Handled = true; pop.Close(); } };
                search.KeyDown += (s2, e2) =>
                {
                    if (e2.KeyCode == Keys.Enter) { e2.SuppressKeyPress = true; Commit(list.SelectedItem as string ?? search.Text); }
                    else if (e2.KeyCode == Keys.Down && list.Visible && list.Items.Count > 0) { e2.SuppressKeyPress = true; list.Focus(); }
                    else if (e2.KeyCode == Keys.Escape) { e2.SuppressKeyPress = true; pop.Close(); }
                };
                search.TextChanged += (s2, e2) => Fill(search.Text.Trim());
                pop.Deactivate += (s2, e2) => pop.Close();
                pop.FormClosed += (s2, e2) => pop.Dispose();
                pop.Location = pickBtn.PointToScreen(new Point(pickBtn.Width - pop.Width, pickBtn.Height + S(2)));
                pop.Show(this);
                search.Focus();
            };

            // refresh the active conversation's presence rapidly while the tab is open, + instantly on window focus
            // Poll EVERY open conversation in one batch (not just the active one) so none go stale.
            var presTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            presTimer.Tick += (s, e) =>
            {
                bool g = CheckGameOpen();
                if (g != _gameOpen)
                {
                    _gameOpen = g;
                    if (g)
                    {
                        // Game just opened -> yield the chat session to it (stop our engine) so IT stays connected in-game,
                        // and cancel any pending reconnect so we don't fight it.
                        CancelPendingReconnect();
                        try { if (engine != null && engine.Running) engine.Stop(); } catch { }
                        WLog("game opened -> engine stopped (yielding chat to the game)");
                    }
                    else if (root.Visible && engine != null && !engine.Running && LoginReady())
                    {
                        WLog("game closed -> starting engine");
                        TryStartEngine();   // game closed -> safe to (re)connect now
                    }
                    RefreshConnLabel();
                }
                if (root.Visible) QueryAllPresence();
            };
            presTimer.Start();
            this.Activated += (s, e) => { if (root.Visible) QueryAllPresence(); };
            loginBtn.Click += (s, e) => OpenLoginSettings();
            logsBtn.Click += (s, e) => ExportLogs();

            // exposed hooks
            _wsOnShow = () => { _gameOpen = CheckGameOpen(); SetStatus(engine == null ? "stopped" : engine.State); if (engine != null && !engine.Running && LoginReady()) TryStartEngine(); RenderConvList(); QueryAllPresence(); };
            _wsAutoStart = () => { _gameOpen = CheckGameOpen(); if (loginAuto && engine != null && !engine.Running && LoginReady()) TryStartEngine(); SetStatus(engine == null ? "stopped" : engine.State); };
            _openWhisper = (name, id) => StartConvFromName(name, id);
            _mctsRelayDir = relayDir;
            _mctsConnected = () => engine != null && engine.State == "connected";
            _mctsEnsureConnected = async (timeoutMs) =>
            {
                if (engine != null && engine.State == "connected") return true;
                if (CheckGameOpen()) { _gameOpen = true; SetStatus(engine != null ? engine.State : "stopped"); return false; }   // can't share the account's chat with a running game
                if (engine != null && !engine.Running && LoginReady()) { ApplyLogin(); engine.Start(); }
                if (engine == null || !engine.Running) return false;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (engine.State == "connected") return true;
                    if (engine.State == "stopped") return false;
                    await Task.Delay(300);
                }
                return engine.State == "connected";
            };
            _mctsPreconnect = () => { if (engine != null && !engine.Running && LoginReady() && !CheckGameOpen()) { ApplyLogin(); engine.Start(); } };
            this.FormClosing += (s, e) => { try { reconnectTimer?.Stop(); reconnectTimer?.Dispose(); } catch { } try { engine?.Stop(); } catch { } };

            RenderConvList();
            return root;
        }
    }
}
