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

        // In-depth reference for every feature + algorithm, structured like documentation (TOC sidebar + sections +
        // formula blocks). Deliberately does NOT print the real daily API quota numbers.
        Panel BuildCodexPanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var top = new Panel { Dock = DockStyle.Top, Height = S(54), BackColor = Theme.Panel };
            top.Controls.Add(new Label { Text = "Codex", AutoSize = true, ForeColor = Theme.Text, Font = Theme.F(13f, FontStyle.Bold), Location = new Point(S(16), S(9)) });
            top.Controls.Add(new Label { Text = "How every feature and algorithm works, in depth.", AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Location = new Point(S(18), S(33)) });
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(S(26), S(14), S(20), S(30)) };
            var toc = new Panel { Dock = DockStyle.Left, Width = S(212), BackColor = Theme.Panel };
            var tocLine = new Panel { Dock = DockStyle.Right, Width = S(1), BackColor = Theme.Line };
            var tocFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Panel, Padding = new Padding(S(12), S(14), S(6), S(16)) };
            toc.Controls.Add(tocFlow); toc.Controls.Add(tocLine);
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg };
            content.Controls.Add(flow);
            int wrap = S(520);
            var bodyCol = Color.FromArgb(202, 202, 208);
            var mono = new Font("Consolas", 9.5f);
            bool first = true;
            // Expandable TOC tree: H2/H3 register nodes here; BuildToc() renders the sidebar after every anchor exists.
            var nodes = new List<TocNode>();   // flat, in document order
            TocNode curSection = null;         // most-recent H2 so an H3 attaches to it
            TocNode active = null;             // the section/sub currently highlighted (follows scroll)
            TocNode revealSection = null;      // the "hidden-player reveal switches" H2 — the Settings link jumps here
            void H2(string title)
            {
                if (!first) flow.Controls.Add(new Panel { Width = wrap, Height = 1, BackColor = Theme.Line, Margin = new Padding(0, S(20), 0, S(8)) });
                first = false;
                var hdr = new Label { Text = title, AutoSize = true, ForeColor = Theme.Accent, Font = Theme.F(13.5f, FontStyle.Bold), Margin = new Padding(0, S(2), 0, S(7)) };
                flow.Controls.Add(hdr);
                curSection = new TocNode { Title = title, Anchor = hdr, IsSection = true };
                nodes.Add(curSection);
            }
            void H3(string t)
            {
                var sub = new Label { Text = t, AutoSize = true, ForeColor = Theme.Text, Font = Theme.F(10.5f, FontStyle.Bold), Margin = new Padding(0, S(12), 0, S(4)) };
                flow.Controls.Add(sub);
                var n = new TocNode { Title = t, Anchor = sub, IsSection = false, Parent = curSection };
                curSection?.Children.Add(n);
                nodes.Add(n);
            }
            void P(string t) => flow.Controls.Add(new Label { Text = t, AutoSize = true, MaximumSize = new Size(wrap, 0), ForeColor = bodyCol, Font = Theme.F(9.5f), Margin = new Padding(0, 0, 0, S(6)) });
            void Math(string code)
            {
                var box = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.FromArgb(22, 22, 26), Padding = new Padding(S(12), S(9), S(14), S(9)), Margin = new Padding(0, S(2), 0, S(10)) };
                box.Controls.Add(new Label { Text = code, AutoSize = true, Font = mono, ForeColor = Color.FromArgb(206, 214, 226), BackColor = Color.Transparent });
                box.Paint += (s, e) => { using var p = new Pen(Color.FromArgb(64, 64, 72)); e.Graphics.DrawRectangle(p, 0, 0, box.Width - 1, box.Height - 1); };
                flow.Controls.Add(box);
            }

            H2("Overview");
            P("Smite 1 Inspector is a single self-contained Windows app with three sides: a God Inspector that edits the game's god .ini tuning files offline, a Player Tracker that pulls live stats from the official Hi-Rez SMITE 1 API, and Whispers — a standalone messenger that talks to live players while the game is closed. The left rail switches between Player Tracker, Friend List, God Inspector, Whispers, this Codex, and Settings. Everything is offline-first except the tracker, friend, and whisper features, which reach the network only when you ask.");

            H2("God Inspector");
            P("Point it at your SMITE config folder and it loads every god's .ini. Each tunable — ability scaling, cooldowns, costs and so on — becomes an editable row. Change values, add new keys from the embedded UE3 SDK definition list, then Apply (write back to the .ini), Reload, or Restore Defaults.");
            P("On first load it snapshots the pristine value of every key per file, so a restore is always possible even after you have saved. Engine and system files are hidden unless you tick Show all entities. Ability icons and names come from a bundled media-kit asset pipeline.");

            H2("Player Tracker");
            H3("Search & profile");
            P("Type a name and Search. Lookup first tries an EXACT getplayer match (case-insensitive); if that finds nothing it falls back to a prefix search and shows a disambiguation picker when several accounts share the name. The profile card shows level, total mastery, region, platform, win/loss with win rate, worshippers, hours, ranked tiers, account created and last login, and career achievements. The name row renders the in-game name beside a SMITE coin plus a platform coin and any linked accounts. A privacy-flagged profile is detected and labelled private instead of shown blank.");
            H3("Masteries, matches & achievements");
            P("God Masteries lists every god played with rank, worshippers, KDA, win rate and minion kills. Recent Matches lists your latest games with god, queue, result, KDA, level, damage and gold. Achievements is a full grid of career stats. Double-click a mastery for that god's per-queue breakdown; double-click a match for the full scoreboard, which colour-codes premade parties and shows each player's build.");
            H3("Friends & live matches");
            P("The Friends sub-tab lists a tracked player's Hi-Rez friends plus incoming/outgoing requests, decoded from friend flags (direction is relative to the viewed player). When a player is in a game, their status chip opens the in-progress scoreboard. Public team-mates are named normally there, but privacy-flagged players are anonymized in a live match exactly as in a completed one — the API hides them everywhere — so a hidden slot can only be put to a name by your local combat log or a fingerprint guess.");

            H2("Hidden players");
            P("A privacy-flagged player hides their name and every id, but a match row still leaks their clan, account level, total mastery, the gods they played, and their premade party-mates. You can nickname such a player; the app then re-recognizes them next time from that fingerprint. This is best-effort recognition of a player YOU named — never recovery of a hidden name from the API, which is impossible.");
            H3("The matching algorithm");
            P("Each saved tag is scored against the hidden row. Same clan is the strong anchor; two clanless players get a weaker one; account level and total mastery must stay close (they only ever grow); each shared NAMED party-mate is strong evidence; a previously-seen god nudges it up. The best tag wins if its score clears the threshold:");
            Math("score = 0\n"
               + "  same clan id ................ +100\n"
               + "  both clanless ...............  +30\n"
               + "  clan mismatch ............... -55\n"
               + "  |dLevel|<=8 and |dMastery|<=6  +40 - (2*dLevel + 2*dMastery)\n"
               + "  else within +/-25 / +/-15 ...   +6\n"
               + "  beyond ......................  -20\n"
               + "  each shared party-mate ......  +60\n"
               + "  a previously-seen god .......  +12\n"
               + "\n"
               + "gate: with no shared party-mate, the loose\n"
               + "      +/-25 / +/-15 window must hold, else reject.\n"
               + "\n"
               + "match when  score >= 60");
            P("So same-clan with sane stats matches easily; a clan change still re-links if two party-mates agree; and a different same-clan player with a big level gap and no shared friends is correctly rejected by the gate.");
            H3("Confidence score");
            P("A percentage summarizes how reliably a tag can be re-found — more sightings and more cross-evidence (party-mates, gods) mean higher confidence; a lone first tag is modest:");
            Math("confidence% = min( 99,\n"
               + "      25\n"
               + "    + 18 * sightings\n"
               + "    +  8 * min(party-mates, 4)\n"
               + "    +  4 * min(gods seen,   3) )");
            P("Every confident sighting folds the new evidence back in — advancing level and mastery, accumulating party-mates and gods, and bumping the sighting count — so both recognition and confidence strengthen over time.");

            H2("Hidden-player reveal — the three switches");
            revealSection = curSection;   // anchor for the Settings "How these work" link
            P("Settings → HIDDEN-PLAYER REVEAL has three independent switches. They stack from strongest to weakest evidence and are entirely local — nothing is ever uploaded. A reveal marked ✔ is EXACT (proven); a reveal marked ≈ is a fingerprint best-guess with a confidence %, shown only when the evidence corroborates. When in doubt the app prints \"Hidden\" rather than risk a wrong name — the whole system is tuned for precision over coverage.");

            H3("1 · Reveal from your game logs  (EXACT ✔)");
            P("SMITE itself writes a per-match combat log to Documents\\My Games\\Smite\\BattleGame\\Logs as plain text. At every spawn it records each player's real id, name, god and team — INCLUDING players the stats API hides behind the privacy flag, because the client still has to draw them on your screen. This switch reads that file (the newest CombatLog_*.log, skipping rotated \"backup\" copies) and matches the captured roster to the completed scoreboard by god + team, producing an EXACT ✔ reveal. It touches no API and no game memory — it only reads a text file the game already wrote, so it is invisible to anti-cheat.");
            P("You must turn the log on once inside SMITE: open the chat and type /combatlog, or press PageUp. The file is decoded as Latin-1 and the parser tolerates raw control bytes inside names. Safety: if two captured rosters could both fit the same match (e.g. a premade that re-queued), the app refuses to guess and shows Hidden. This is the strongest source — it only works for matches you personally played.");

            H3("2 · Fingerprint-guess from learned names  (≈)");
            P("For matches you were NOT in there is no combat log, so a hidden row can only be GUESSED. A privacy-flagged completed row still leaks a surprising amount: clan, account level, total and per-god mastery, the gods played, any non-default (bought/mastery) skin, the premade party-mates, and — newly — the ranked TIER and MMR for each queue. The app scores every learned public player against the hidden row and shows ≈ name + confidence only when the evidence corroborates:");
            Math("score from a candidate in the name DB:\n"
               + "  shared premade party-mate .. + IDF-weighted (rare mate = strong)\n"
               + "  trusted same-team neighbour  + IDF-weighted (after repeats)\n"
               + "  same clan .................. +35     clan mismatch .. -35\n"
               + "  same god, mastery within 2 . +28..+30 (drops with distance)\n"
               + "  mastery regression ......... -40   (mastery only rises)\n"
               + "  account level (level-band aware, exact=+28, far jump=0)\n"
               + "  matching non-default skin .. +10\n"
               + "  ranked MMR within 25 ....... +25    <=75 +14   <=150 +6\n"
               + "       far apart, same queue . -10    same tier .... +5\n"
               + "\n"
               + "GATE: needs real corroboration (a party-mate, OR same\n"
               + "  clan + same god at close mastery + close level, OR\n"
               + "  >=2 trusted neighbours, OR a tight MMR match).\n"
               + "MARGIN GUARD: if the top two names are within 12, it's\n"
               + "  ambiguous -> show Hidden, never the wrong name.");
            P("MMR is the most discriminating signal because it is nearly unique per account — two random players almost never share an exact MMR, so a tight match strongly pins identity, while a large gap on a shared ranked queue is positive evidence they are DIFFERENT people. Stale entries are mildly penalised so a fresh corroborated match outranks a year-old stat-twin. This switch can only ever re-identify accounts already in your local DB; it can never pull a name out of the API (that is impossible).");

            H3("3 · Run background name harvester");
            P("The fingerprint guesser can only match a hidden player to a PUBLIC player it has already learned, so this switch grows that pool at scale: a background loop scrapes match rosters across the ranked and casual queues and records every PUBLIC (non-hidden) player's name and fingerprint. Important — privacy-flagged players are anonymized EVERYWHERE in the API, live matches and completed matches alike, so this never captures a hidden name. What it does is enlarge the library of known public accounts, so that when one of them later turns up hidden (a privacy-toggler, or a hidden slot in a match you open) the fingerprint can put a name to them. It self-throttles well under the daily API request cap and pauses as the day's usage climbs; it uses your API quota, so it is optional and only runs while switch 2 is on. Learned names never leave your machine.");

            H3("How they combine + resetting");
            P("Order of trust: an EXACT ✔ game-log reveal always wins over a fingerprint ≈ guess, and a name already visible elsewhere in the same match is never re-suggested for a different slot. \"Clear learned names\" wipes the fingerprint DB only; \"Nuke everything (start fresh)\" wipes the fingerprint DB AND every captured combat-log roster, for a completely clean slate. Your hand-made nicknames on the Custom Hidden Tags tab are separate and are NOT touched by either button.");

            H2("Friend List");
            P("Your curated buddy list with live status. Instead of re-scanning everyone on a fixed timer, it runs a continuous priority poller: every friend has its own next-check time driven by a tier, so the people who matter refresh fastest while dormant friends cost almost nothing.");
            H3("Priority tiers");
            P("The status (god-select, online, in-game, offline) sets how often a friend is re-checked. God-select is the most actionable (a match is forming); in-game is the least (they are committed for a while):");
            Math("god-select ...... every  10 s\n"
               + "online / lobby .. every  15 s\n"
               + "in-game ......... every  20 s\n"
               + "offline ......... see backoff below\n"
               + "error / unknown . every  90 s, doubling per\n"
               + "                  failure (capped at 600 s)");
            H3("Offline backoff");
            P("Offline friends back off the longer they have been gone — roughly one extra minute per day idle, holding at a ten-minute cap for months, then stretching toward twenty minutes after about a year. They snap straight back to a fast tier the instant they appear online:");
            Math("d = days since last login\n"
               + "\n"
               + "d <= 180   : minutes = clamp(d, 1, 10)\n"
               + "180<d<=365 : minutes = 10 + (d-180)/185 * 10\n"
               + "d  > 365   : minutes = 20\n"
               + "\n"
               + "interval = minutes * 60 s\n"
               + "\n"
               + "1d->1m   6d->6m   10d..6mo->10m\n"
               + "~9mo->15m         1yr+->20m");
            H3("Rate limiting");
            P("A token bucket smooths and caps the overall call rate so even a very large roster can never burst, and each cycle's checks run concurrently so a sweep finishes in roughly one round-trip rather than one-at-a-time:");
            Math("bucket refills at a fixed ceiling R checks/min.\n"
               + "each status check spends 1 token; a cycle spends\n"
               + "  min(tokens available, per-cycle burst cap)\n"
               + "checks at once -> the real rate can never exceed\n"
               + "R/min for ANY roster size.\n"
               + "\n"
               + "as the day's usage nears the API limit, R is cut,\n"
               + "then paused, so the app stays under the daily cap.");
            H3("Uptime, caching & notes");
            P("Last login serves two displays, labelled by current status:");
            Math("online  : uptime    = now - last login\n"
               + "offline : last seen = now - last login");
            P("Toggle Show online time to display uptime on online rows. Leaving the tab pauses the poller and caches the list exactly as you left it; returning shows it instantly and resumes by priority (online first, then offline) instead of re-scanning. The slow getplayer call (name, avatar, last login) is cached and only refreshed on an online/offline transition or every half hour. The preview panel shows the in-game avatar (or a coloured initial when none is set), an Open-profile button, a View-current-game button when they are in a match, and a per-friend Notes box.");

            H2("Whispers");
            P("Whispers is a standalone messenger that lets you message live SMITE players while the game is completely closed. It connects to SMITE's own chat service — the same backend the in-game whisper window uses — so the messages you send arrive as ordinary in-game whispers, and replies appear here in real time. Everything needed to reach that service is bundled with the app, so SMITE does not have to be installed on the PC running it.");

            H3("How it connects");
            P("Opening the Whispers tab starts a small background engine. It signs in to SMITE's chat backend and holds an encrypted connection open to the chat server, speaking the same login and messaging protocol the game client uses (via SMITE's own networking library, bundled alongside the app). Once signed in you appear online to the chat service exactly as if the game were running — which is what lets the people you message reply to you, and lets you see who is online.");
            Math("engine  -> sign in  (Steam ticket OR Hi-Rez login)\n"
               + "        -> open an encrypted link to the chat server\n"
               + "        -> register as ONLINE with the chat service\n"
               + "        <- incoming whispers + presence pushed back\n"
               + "        -> your messages sent as chat whispers");

            H3("Sign-in: Steam or Hi-Rez");
            P("There are two ways to sign in, chosen in Whispers -> Options. Steam login reuses your running Steam session (Steam must be open and signed in to your SMITE account); it is one click, but because it borrows a live Steam session, Steam shows you as \"playing SMITE\" for as long as the chat connection is held. Hi-Rez login uses your Hi-Rez account name and password directly — it never touches Steam, connects a little faster, and shows no Steam game status. A saved password is encrypted with Windows' own per-user encryption (DPAPI) and is never written or logged in plain text. By default the engine connects only while the Whispers tab is open; Options also has a \"Connect automatically when the app opens\" toggle that keeps it connected for the whole session (so in Steam mode the SMITE status shows the entire time the app is running).");

            H3("Sending, queuing and delivery");
            P("Type a name, write a message, send. Message someone who is offline and you get an instant offline notice — the same immediate feedback the game gives — because the engine checks that player's presence with the chat service before sending. Messages you send during the few seconds while the engine is still signing in are not lost: they are held in a queue, shown with a queued marker, and sent automatically the moment the connection is ready; a queued message can be cancelled before it goes out. Delivery feedback is best-effort — the app marks a message sent once the chat service accepts it, but SMITE's chat backend does not return a hard read receipt.");

            H3("Presence and conversations");
            P("The engine periodically refreshes the presence of each open conversation, so you can see who is online without launching the game. Conversations behave like any chat app: pin the ones that matter to the top, and remove ones you don't want in the list. Delete is a soft delete — the history is kept and comes straight back if that person messages you again or you reopen the conversation. All conversations and history live locally as JSON in your data folder; nothing is uploaded.");

            H3("Requirements & limitations");
            P("Whispers talks to a live Hi-Rez service, so there are real constraints:");
            Math("- SMITE itself must be CLOSED while you whisper: one\n"
               + "  account cannot be signed in to chat twice at once.\n"
               + "- One account at a time per running app.\n"
               + "- Steam login needs Steam open + signed in; Hi-Rez\n"
               + "  login needs your Hi-Rez account name + password.\n"
               + "- The first sign-in can take ~10-30 seconds.\n"
               + "- Presence is best-effort and cross-platform: a player\n"
               + "  on console may report differently than on PC.\n"
               + "- It depends on Hi-Rez's chat servers staying online; if\n"
               + "  Hi-Rez retires them, Whispers stops working.");

            H3("Staying connected — and recovering automatically");
            P("A chat connection left open for a long time (a sleeping laptop, a network blip, an idle stretch of hours) can silently die without either side sending a clean close — the app would otherwise keep showing \"connected\" with no real link underneath, which used to surface as messages wrongly blamed on SMITE's spam filter and everyone in the list looking offline. Whispers actively watches for this: it notices when the chat session reports itself dead, or when the engine goes quiet for too long, and shows \"connection lost — reconnecting…\" while it automatically restarts the link — no need to reopen the tab or relaunch the app. A message caught mid-drop is queued and resent the moment it's back, and presence stops claiming anyone is online or offline until a fresh read actually comes in.");

            H3("Known issues (beta)");
            P("Whispers is the newest and most experimental feature and ships as a beta. Known rough edges: the first connection after launch can be slow or, rarely, fail and need a reconnect (reopen the tab); a long-dropped connection takes a few seconds to detect and a moment to auto-reconnect, during which sends are held rather than lost; presence can lag a few seconds behind reality; and because delivery is inferred rather than confirmed by the server, a message shown as sent may in rare cases not have been routed. If something misbehaves, use Export Logs on the Whispers tab and share the zip. It never includes your password or your saved conversation history, but the diagnostic logs can contain recent whisper text and your Hi-Rez username — so only share it with someone you trust to help.");

            H2("The Hi-Rez API");
            H3("Request signing");
            P("Stats come from the official SMITE 1 API. Each request is signed with an MD5 of the developer id, the method name, an auth key and a UTC timestamp:");
            Math("signature = md5( devId + method + authKey + utcTimestamp )\n"
               + "url = base / methodJson / devId / signature\n"
               + "          / sessionId / timestamp / args...");
            H3("Sessions & the cap");
            P("A session is created first and reused for its short lifetime; responses come back as JSON arrays. Hi-Rez enforces a daily request limit and a session limit that the app must respect — the exact numbers are not shown here. The friend poller is built to stay well within them via tiered cadences, the offline backoff, pausing while its tab is hidden, caching the slow calls, and the self-throttle above. You can drop your own developer key into an api.txt file to use your own quota instead of the built-in one.");
            H3("Limitations");
            P("A few things the API cannot do, by design. It cannot reveal a privacy-flagged player's name or id from a completed match, a live match, or a friends list — privacy-flagged players are anonymized everywhere the API returns them (the app can only put a name to them from your local combat log, or as a fingerprint best-guess). It does not expose newer in-client avatars, only an older avatar set, so many active players return no avatar (the app shows a coloured initial instead). Custom and scrim matches never appear in a player's match history, and Hi-Rez hides their detailed scoreboards for about 7 days after the match (an anti-scouting measure), so Recent Matches cannot list them (normal and ranked history is also capped at roughly the 50 most recent games). And it does not resolve linked-account names beyond the primary account. These are server-side limits, not app bugs.");

            H2("Your data");
            P("Everything you save — favorites, recent lookups, hidden-player tags, the friend list with its per-friend notes, your Whispers conversations, your settings, and the god default snapshots — is stored as plain JSON in your Documents folder under Smite Inspector, so a shared copy of the app in a read-only location still works. Nothing is uploaded anywhere; the only network traffic is to the Hi-Rez API and chat service, and only when you ask. Settings → Uninstall removes the app and can optionally erase this folder.");

            // --- expandable sidebar tree (owner-drawn rows; chevron toggle; red accent bar follows the scroll) ---
            void SetExpanded(TocNode sec, bool exp)
            {
                sec.Expanded = exp;
                tocFlow.SuspendLayout();
                foreach (var c in sec.Children) if (c.Row != null) c.Row.Visible = exp;
                tocFlow.ResumeLayout(true);
                sec.Row?.Invalidate();
            }
            void SetActive(TocNode n)
            {
                if (n == active) return;
                var prev = active; active = n;
                if (n != null && !n.IsSection && n.Parent != null && !n.Parent.Expanded) SetExpanded(n.Parent, true);   // reveal the section we scrolled into
                prev?.Row?.Invalidate(); prev?.Parent?.Row?.Invalidate();
                n?.Row?.Invalidate(); n?.Parent?.Row?.Invalidate();
            }
            void SyncActiveNow()
            {
                if (nodes.Count == 0 || IsDisposed || !content.IsHandleCreated) return;
                int top = -flow.Top + S(48);   // flow is the single AutoScroll child, so -flow.Top is the scroll offset
                TocNode best = nodes[0];
                foreach (var n in nodes) { if (n.Anchor.Top <= top) best = n; else break; }
                SetActive(best);
            }
            TocRow MakeRow(TocNode n, int rowW)
            {
                var row = new TocRow { Width = rowW, Height = n.IsSection ? S(30) : S(26), Margin = new Padding(0, 0, 0, S(1)), Cursor = Cursors.Hand, BackColor = Theme.Panel };
                n.Row = row;
                row.MouseEnter += (s, e) => { row.Hovered = true; row.Invalidate(); };
                row.MouseLeave += (s, e) => { row.Hovered = false; row.Invalidate(); };
                row.MouseClick += (s, e) =>
                {
                    if (n.IsSection && e.X < S(22)) { SetExpanded(n, !n.Expanded); return; }   // chevron zone → toggle only
                    if (n.IsSection && !n.Expanded) SetExpanded(n, true);
                    content.ScrollControlIntoView(n.Anchor);
                    SyncActiveNow();
                };
                row.Paint += (s, e) =>
                {
                    var g = e.Graphics;
                    bool isActive = n == active;
                    bool isAncestor = n.IsSection && active != null && active.Parent == n;
                    Color bg = isActive ? Color.FromArgb(46, 24, 26) : row.Hovered ? Color.FromArgb(36, 36, 42) : Theme.Panel;
                    using (var b = new SolidBrush(bg)) g.FillRectangle(b, row.ClientRectangle);
                    if (isActive) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, 0, S(3), row.Height);
                    int textX;
                    if (n.IsSection)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        int cx = S(11), cy = row.Height / 2;
                        var tri = n.Expanded
                            ? new[] { new Point(cx - S(4), cy - S(2)), new Point(cx + S(4), cy - S(2)), new Point(cx, cy + S(3)) }
                            : new[] { new Point(cx - S(2), cy - S(4)), new Point(cx - S(2), cy + S(4)), new Point(cx + S(3), cy) };
                        using (var cb = new SolidBrush(isActive || isAncestor ? Theme.Text : Color.FromArgb(150, 150, 158))) g.FillPolygon(cb, tri);
                        g.SmoothingMode = SmoothingMode.Default;
                        textX = S(26);
                    }
                    else
                    {
                        using (var p = new Pen(Color.FromArgb(72, 72, 80))) g.DrawLine(p, S(28), row.Height / 2, S(33), row.Height / 2);
                        textX = S(40);
                    }
                    var col = isActive ? Theme.Text : isAncestor ? Color.FromArgb(214, 214, 220) : n.IsSection ? Color.FromArgb(190, 190, 198) : Color.FromArgb(150, 150, 158);
                    var font = n.IsSection ? Theme.F(10f, FontStyle.Bold) : Theme.F(9f);
                    TextRenderer.DrawText(g, n.Title, font, new Rectangle(textX, 0, row.Width - textX - S(6), row.Height), col,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                };
                return row;
            }
            void BuildToc()
            {
                tocFlow.SuspendLayout();
                tocFlow.Controls.Clear();
                tocFlow.Controls.Add(new Label { Text = "CONTENTS", AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8f, FontStyle.Bold), Margin = new Padding(S(4), 0, 0, S(8)) });
                int rowW = tocFlow.ClientSize.Width > S(40) ? tocFlow.ClientSize.Width - S(18) : S(194);
                foreach (var n in nodes)
                {
                    var row = MakeRow(n, rowW);
                    if (!n.IsSection) row.Visible = n.Parent == null || n.Parent.Expanded;
                    tocFlow.Controls.Add(row);
                }
                tocFlow.ResumeLayout(true);
            }

            BuildToc();
            if (nodes.Count > 0) { active = nodes[0]; nodes[0].Row?.Invalidate(); }
            // Settings "How these work" link jumps straight to the reveal section.
            _codexJumpReveal = () => { try { if (revealSection?.Anchor != null) { if (!revealSection.Expanded) SetExpanded(revealSection, true); content.ScrollControlIntoView(revealSection.Anchor); SyncActiveNow(); } } catch { } };
            // Center the reading column in the wide content area (with a comfortable minimum gutter off the sidebar),
            // so the text isn't crammed against the TOC and the empty space is balanced left/right.
            void CenterContent()
            {
                // Win32 physical width — managed ClientSize inflates at this app's mixed DPI, even on the form.
                int avail = PhysicalClientWidth() - S(190) - S(213);   // content area = form client minus the rail and the TOC
                int gut = System.Math.Max(S(24), (avail - wrap) / 2);
                gut = System.Math.Min(gut, System.Math.Max(S(24), avail - wrap - S(16)));   // never push the column off the right edge
                if (content.Padding.Left != gut) content.Padding = new Padding(gut, S(14), S(16), S(30));
            }
            content.SizeChanged += (s, e) => CenterContent();
            CenterContent();
            var scrollTimer = new System.Windows.Forms.Timer { Interval = 200 };   // active-follows-scroll (cheap; only runs while the Codex tab is open)
            scrollTimer.Tick += (s, e) => SyncActiveNow();
            content.VisibleChanged += (s, e) => { if (content.Visible) { CenterContent(); scrollTimer.Start(); BeginInvoke(new Action(SyncActiveNow)); } else scrollTimer.Stop(); };
            host.Disposed += (s, e) => scrollTimer.Dispose();

            body.Controls.Add(content); body.Controls.Add(toc);
            host.Controls.Add(body); host.Controls.Add(top);
            return host;
        }
    }
}
