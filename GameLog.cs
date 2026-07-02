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

    // EXPERIMENT (reveal-hidden-names): EXACT reveal from the LOCAL game logs. SMITE 1 (UE3) writes a per-match combat
    // log to Documents\My Games\Smite\BattleGame\Logs\CombatLog_0.log listing EVERY player's real id + name + god + team
    // at spawn — including players the stats API anonymizes ("hidden"), because the client must render them in-match.
    // We only READ that file (no memory reading / no injection → no anti-cheat exposure). The combat log is overwritten
    // each match, so we ingest it (file-watch + on demand) into a small accumulating store, then correlate it to a
    // viewed match by the public players' ids and fill the hidden slots by god+team. Exact truth, for the user's OWN matches.
    static class GameLog
    {
        public static bool Enabled;
        public sealed class Slot { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string GodId { get; set; } = ""; public int Team { get; set; } }
        public sealed class Roster { public List<Slot> Players { get; set; } = new(); public string At { get; set; } = ""; }
        sealed class Persist { public Dictionary<string, string> IdName { get; set; } = new(); public List<Roster> Rosters { get; set; } = new(); public string SkipLogPath { get; set; } = ""; public long SkipLogLen { get; set; } = -1; }

        const int MaxRosters = 400;
        static readonly object _lock = new object();
        static Dictionary<string, string> _idName = new();   // playerid -> name (accumulated; for future use)
        static List<Roster> _rosters = new();
        // After a "nuke", suppress re-ingesting the combat log that's currently on disk (SMITE's last match) until the game
        // writes a NEWER one — otherwise opening that match's scoreboard would re-read the live log and instantly undo the
        // wipe. Stored as the log's UTC write time at nuke; cleared the moment a newer log appears. Persisted across restarts.
        static string _skipLogPath = ""; static long _skipLogLen = -1;   // post-nuke: suppress this exact log file (path+length) until it's replaced/truncated by a new match (NOT keyed on the mutable write-time, which the game bumps with every append)
        static bool _dirty;
        static long _lastIngestTick = -100000;
        static FileSystemWatcher _watch;

        static string LogsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Smite", "BattleGame", "Logs");
        static string CombatLogDefault => Path.Combine(LogsDir, "CombatLog_0.log");
        // SMITE normally writes CombatLog_0.log, but a second client instance or an engine rotation can bump the suffix
        // (CombatLog_1.log, …). Hardcoding _0 would then silently read a STALE file and the exact local reveal — the single
        // strongest reveal mechanism — would go dark with no error. So resolve the NEWEST CombatLog_*.log (skipping rotated
        // "backup" copies) on every read, mirroring how SDPS (antD97) locates the active log.
        static string CurrentLog()
        {
            try
            {
                if (Directory.Exists(LogsDir))
                {
                    var f = new DirectoryInfo(LogsDir).GetFiles("CombatLog_*.log")
                        .Where(x => x.Name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) < 0)
                        .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                    if (f != null) return f.FullName;
                }
            }
            catch { }
            return CombatLogDefault;
        }
        static string StoreFile => Path.Combine(Theme.DataDir, "gamelog.json");
        static string BakFile => StoreFile + ".bak";

        public static int RosterCount { get { lock (_lock) return _rosters.Count; } }

        public static void Init()
        {
            Load();
            try { Ingest(); } catch { }   // fold in whatever match log is currently on disk
            try
            {
                if (Directory.Exists(LogsDir))
                {
                    _watch = new FileSystemWatcher(LogsDir, "CombatLog_*.log") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                    FileSystemEventHandler h = (s, e) => { try { Ingest(); } catch { } };
                    _watch.Changed += h; _watch.Created += h;
                    _watch.Renamed += (s, e) => { try { Ingest(); } catch { } };
                    _watch.Error += (s, e) => { try { _watch.EnableRaisingEvents = false; _watch.EnableRaisingEvents = true; } catch { } };   // buffer overflow → re-arm
                    _watch.EnableRaisingEvents = true;
                }
            }
            catch { }
        }

        public static void Shutdown() { try { _watch?.Dispose(); } catch { } Save(); }

        // Wipe every captured combat-log roster + id→name map (the "start fresh" nuke clears this alongside NameDb). Also
        // suppress the log currently on disk (SMITE's last match) so re-opening that scoreboard can't instantly re-reveal it.
        public static void Clear()
        {
            lock (_lock)
            {
                _idName = new(); _rosters = new();
                try { string f = CurrentLog(); if (File.Exists(f)) { _skipLogPath = f; _skipLogLen = new FileInfo(f).Length; } else { _skipLogPath = ""; _skipLogLen = -1; } } catch { _skipLogPath = ""; _skipLogLen = -1; }
                _dirty = true;
            }
            Save();
        }
        // Settings fail-safe: confirm the combat log is actually present + being read. (found, human-readable detail).
        public static (bool found, string detail) Status()
        {
            try
            {
                string f = CurrentLog();
                if (File.Exists(f))
                    return (true, "✓ Combat log found (" + Path.GetFileName(f) + ", updated " + File.GetLastWriteTime(f).ToString("g") + ") — " + RosterCount.ToString("N0") + " rosters captured");
            }
            catch { }
            return (false, "⚠ No combat log found yet — in-game, open chat and type /combatlog (or press PageUp) so it records the roster");
        }
        // Idempotent re-arm: set up the folder watcher if it isn't already (covers SMITE installed/launched AFTER this app
        // started, or the toggle being switched on later). Safe to call repeatedly — only creates the watcher once.
        public static void EnsureWatching()
        {
            try
            {
                if (_watch == null && Directory.Exists(LogsDir))
                {
                    _watch = new FileSystemWatcher(LogsDir, "CombatLog_*.log") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                    FileSystemEventHandler h = (s, e) => { try { Ingest(); } catch { } };
                    _watch.Changed += h; _watch.Created += h;
                    _watch.Renamed += (s, e) => { try { Ingest(); } catch { } };
                    _watch.Error += (s, e) => { try { _watch.EnableRaisingEvents = false; _watch.EnableRaisingEvents = true; } catch { } };
                    _watch.EnableRaisingEvents = true;
                }
                Ingest(true);   // fold in whatever match log is on disk right now
            }
            catch { }
        }

        static void Load()
        {
            lock (_lock)
            {
                _idName = new(); _rosters = new(); _skipLogPath = ""; _skipLogLen = -1;
                var p = TryRead(StoreFile) ?? TryRead(BakFile);
                if (p != null) { if (p.IdName != null) _idName = p.IdName; if (p.Rosters != null) _rosters = p.Rosters; _skipLogPath = p.SkipLogPath ?? ""; _skipLogLen = p.SkipLogLen; }
            }
        }
        static Persist TryRead(string path) { try { if (File.Exists(path)) return JsonSerializer.Deserialize<Persist>(File.ReadAllText(path)); } catch { } return null; }

        static void Save()
        {
            Persist p;
            lock (_lock)
            {
                if (!_dirty) return;
                if (_rosters.Count > MaxRosters) _rosters.RemoveRange(0, _rosters.Count - MaxRosters);
                // snapshot under the lock; serialize + write OUTSIDE it so a watcher-thread Save never blocks the UI thread's
                // CorrelateMatch on the lock (Slots are immutable once created, so copying the lists is enough).
                p = new Persist { IdName = new Dictionary<string, string>(_idName), Rosters = _rosters.Select(r => new Roster { At = r.At, Players = new List<Slot>(r.Players) }).ToList(), SkipLogPath = _skipLogPath, SkipLogLen = _skipLogLen };
                _dirty = false;
            }
            string tmp = StoreFile + "." + Environment.CurrentManagedThreadId + ".tmp";   // unique per thread → UI + watcher saves don't collide
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(p));
                if (File.Exists(StoreFile)) File.Replace(tmp, StoreFile, BakFile); else File.Move(tmp, StoreFile);
            }
            catch { lock (_lock) _dirty = true; try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }   // failed write → retry next change; no orphan .tmp
        }

        // Read the top of the (possibly still-being-written-by-the-game) combat log without locking the game out.
        static List<string> ReadTop(string path, int max)
        {
            var lines = new List<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The combat log is single-byte (Latin-1/Win-1252), NOT UTF-8 — a lone 0xB1 ('±') etc. proves it; reading it
            // as UTF-8 turns every accented name char into � (U+FFFD). Latin-1 is built-in and decodes 0xA0-0xFF correctly.
            using var sr = new StreamReader(fs, System.Text.Encoding.Latin1);
            string ln; while (lines.Count < max && (ln = sr.ReadLine()) != null) lines.Add(ln);
            return lines;
        }
        // PCET_Spawn lines have a FIXED field order, and player names can contain RAW control bytes / unescaped quotes
        // that a strict JSON parser rejects (the user's own log has a 0x11 byte in a name → System.Text.Json throws and
        // the whole player is lost). So extract the fields by regex instead — tolerant of any bytes inside the name.
        static readonly System.Text.RegularExpressions.Regex SpawnRe = new System.Text.RegularExpressions.Regex(
            "\"playerid\":\"(?<id>\\d+)\",\\s*\"playername\":\"(?<name>.*?)\",\\s*\"godid\":\"(?<god>\\d+)\".*?\"taskforce\":\"(?<tf>\\d+)\"",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);
        static string Clean(string s) => string.Concat(s.Where(c => c >= ' '));   // drop control bytes for a clean display name

        // Parse the current CombatLog_0.log spawn roster and fold it into the store. Idempotent (an already-captured roster
        // — same ids + names — is a no-op, so a match's streaming combat-event writes don't churn the file). Debounced;
        // force=true bypasses the debounce for the on-demand reads the UI does right before correlating a viewed match.
        public static void Ingest(bool force = false)
        {
            lock (_lock) { long n = Environment.TickCount64; if (!force && n - _lastIngestTick < 2000) return; _lastIngestTick = n; }
            string logFile = CurrentLog();   // re-resolve each ingest: the newest CombatLog_* can change between matches
            if (!File.Exists(logFile)) return;
            // post-nuke suppression: ignore the EXACT log file present at nuke time (SMITE's last match) — even as the game keeps
            // APPENDING to it — until that file is REPLACED by a newer log or TRUNCATED (a brand-new match). Keying on path+length
            // (not the write-time, which every append bumps) stops a wipe from being instantly undone by the game's own writes.
            if (_skipLogLen >= 0)
            {
                long curLen; try { curLen = new FileInfo(logFile).Length; } catch { curLen = long.MaxValue; }
                if (string.Equals(logFile, _skipLogPath, StringComparison.OrdinalIgnoreCase) && curLen >= _skipLogLen) return;   // same file, only grown → still the nuked match
                lock (_lock) { _skipLogPath = ""; _skipLogLen = -1; _dirty = true; }   // a different / truncated log appeared → the wipe is safe to lift
            }
            // Read generously (the 10 initial spawns sit at the top, but read past interleaved early events so a pushed-down
            // spawn isn't missed — a single dropped player would fail CorrelateMatch's full-lineup gate and kill the reveal),
            // and stop as soon as a full 10-player roster is captured.
            List<string> lines; try { lines = ReadTop(logFile, 400); } catch { return; }
            var slots = new List<Slot>(); var seen = new HashSet<string>();
            foreach (var raw in lines)
            {
                if (raw.IndexOf("PCET_Spawn", StringComparison.Ordinal) < 0) continue;
                var m = SpawnRe.Match(raw);
                if (!m.Success) continue;
                string id = m.Groups["id"].Value, name = Clean(m.Groups["name"].Value), godId = m.Groups["god"].Value;
                if (string.IsNullOrEmpty(id) || id == "0" || string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(godId) || godId == "0") continue;
                if (!seen.Add(id)) continue;   // first spawn per player (ignore re-spawns)
                slots.Add(new Slot { Id = id, Name = name, GodId = godId, Team = int.TryParse(m.Groups["tf"].Value, out var t) ? t : 0 });
                if (slots.Count >= 10) break;   // full roster captured
            }
            if (slots.Count < 2) return;   // not a real roster
            lock (_lock)
            {
                foreach (var s in slots) _idName[s.Id] = s.Name;
                var byId = slots.ToDictionary(s => s.Id);
                // A roster's identity = its full (id + god + team) line-up, NOT just the id-set: the SAME premade re-drafting
                // is a DIFFERENT match, so it gets its own roster (both coexist; CorrelateMatch picks the right one by lineup,
                // or returns Hidden if two are ambiguous). Re-ingesting the same match only refreshes changed names.
                var existing = _rosters.FirstOrDefault(rr => rr.Players.Count == slots.Count
                    && rr.Players.All(p => byId.TryGetValue(p.Id, out var s) && s.GodId == p.GodId && s.Team == p.Team));
                string at = ""; try { at = File.GetLastWriteTime(logFile).ToString("yyyy-MM-dd HH:mm"); } catch { }
                if (existing == null) { _rosters.Add(new Roster { Players = slots, At = at }); _dirty = true; }
                else if (existing.Players.Any(p => byId.TryGetValue(p.Id, out var s) && s.Name != p.Name))   // same match → refresh self-healed names
                { existing.Players = slots; existing.At = at; _dirty = true; }
            }
            Save();
        }

        // Correlate a viewed match (API slots; id is ""/"0" for hidden players) to a captured roster — by the PUBLIC
        // players' ids — then return a map "godId|team" -> (name, real id) for every slot. Null if no confident match.
        public static Dictionary<string, (string name, string id)> CorrelateMatch(List<(string id, string godId, int team)> slots)
        {
            if (slots == null || slots.Count == 0) return null;
            var pub = new HashSet<string>(slots.Where(s => !string.IsNullOrEmpty(s.id) && s.id != "0").Select(s => s.id));
            if (pub.Count < 2) return null;   // need >=2 public anchors to identify a match (a different match can't contain all the same public players)
            lock (_lock)
            {
                // Collect EVERY roster passing the SAME-MATCH gate (same size, every public id present, full god/team line-up).
                // A premade re-queue can yield two rosters that both contain the same public ids; if >1 passes AND they disagree
                // on any slot's name, we can't tell which match this is → return null (Hidden) rather than a confident WRONG ✔.
                // Identical mappings across passers are fine (same people → same reveal). god/team overlap alone is NOT proof.
                Dictionary<string, (string, string)> map = null;
                foreach (var r in _rosters)
                {
                    if (r.Players.Count != slots.Count) continue;
                    var rids = new HashSet<string>(r.Players.Select(p => p.Id));
                    if (!pub.All(x => rids.Contains(x))) continue;                 // every public player must be present
                    int gt = slots.Count(s => !string.IsNullOrEmpty(s.godId) && r.Players.Any(p => p.GodId == s.godId && p.Team == s.team));
                    if (gt < slots.Count - 1) continue;                            // full line-up aligns
                    var m = new Dictionary<string, (string, string)>();
                    foreach (var p in r.Players) if (!string.IsNullOrEmpty(p.GodId) && p.GodId != "0") m[p.GodId + "|" + p.Team] = (p.Name, p.Id);
                    if (map == null) { map = m; continue; }
                    // A second passing roster must be IDENTICAL (same keys + names) — not just non-conflicting on shared keys.
                    // The 1-slot god/team tolerance means two different matches can differ on a HIDDEN slot under a DIFFERENT
                    // key (no overlap → no conflict), so require full equality or refuse (ambiguous → Hidden).
                    if (m.Count != map.Count) return null;
                    foreach (var kv in m) if (!map.TryGetValue(kv.Key, out var ex) || ex.Item1 != kv.Value.Item1) return null;
                }
                return map;
            }
        }

        // TEST-ONLY (SMITE_TEST_GAMELOG): on the latest captured roster verify (1) POSITIVE recall — blanking 3 slots still
        // reveals them by god+team; (2) NEGATIVE — a different comp (bogus gods) is REJECTED; (3) NEGATIVE — a single public
        // anchor is REJECTED. Proves recall AND cross-match discrimination (the false-reveal guard) on real log data.
        public static string SelfTest()
        {
            List<Slot> r;
            lock (_lock) { if (_rosters.Count == 0) return "GAMELOG SELFTEST: no rosters ingested (combat log missing/off?)"; r = new List<Slot>(_rosters[_rosters.Count - 1].Players); }
            var sb = new System.Text.StringBuilder();
            sb.Append("GAMELOG SELFTEST: rosters=" + RosterCount + ", roster size=" + r.Count + "\n");

            // (1) POSITIVE — blank the last 3 slots (simulate API-hidden), expect all 3 revealed by god+team
            var slots = new List<(string id, string godId, int team)>(); var expect = new List<(string godId, int team, string name)>();
            for (int i = 0; i < r.Count; i++) { var p = r[i]; if (i >= r.Count - 3) { slots.Add(("0", p.GodId, p.Team)); expect.Add((p.GodId, p.Team, p.Name)); } else slots.Add((p.Id, p.GodId, p.Team)); }
            var map = CorrelateMatch(slots); int ok = 0;
            if (map != null) foreach (var h in expect) { map.TryGetValue(h.godId + "|" + h.team, out var got); if (got.name == h.name) ok++; }
            sb.Append("  (1) positive reveal: " + ok + "/" + expect.Count + (map == null ? " (map=null!)" : "") + "  " + (ok == expect.Count ? "OK" : "FAIL") + "\n");

            // (2) NEGATIVE — same player ids but a totally different comp (bogus gods) must be REJECTED
            var bogus = r.Select(p => (p.Id, "999999", p.Team)).ToList();
            sb.Append("  (2) different-comp rejected: " + (CorrelateMatch(bogus) == null ? "OK" : "FAIL (revealed a non-match!)") + "\n");

            // (3) NEGATIVE — full comp but only ONE public anchor (rest hidden) must be REJECTED by the identity gate
            var oneAnchor = new List<(string id, string godId, int team)>();
            for (int i = 0; i < r.Count; i++) { var p = r[i]; oneAnchor.Add((i == 0 ? p.Id : "0", p.GodId, p.Team)); }
            sb.Append("  (3) single-anchor rejected: " + (CorrelateMatch(oneAnchor) == null ? "OK" : "FAIL (too-weak anchor accepted!)") + "\n");
            return sb.ToString();
        }
    }
}
