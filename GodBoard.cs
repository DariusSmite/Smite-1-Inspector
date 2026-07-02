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

    // EXPERIMENT (reveal-hidden-names, 2026-06-25): reveal a privacy-hidden RANKED player via the god-leaderboard id-leak.
    // getgodleaderboard(godId, rankedQueue 451/450/440) is the ONE Hi-Rez endpoint that leaks a hidden player's real
    // player_id — it blanks player_name but NOT player_id (every other endpoint zeroes BOTH). Chained with smite.guru's
    // permanent id→name cache (/v3/profiles/{id}/matches returns the real name/level/clan for any account it EVER indexed,
    // even one that is CURRENTLY Hi-Rez-private), this de-anonymizes a hidden ranked player with no prior sighting. The
    // join from a hidden completed-match SLOT to a leaked board id can't use MMR (the board's player_ranking is a per-god
    // score, NOT the account Rank_Stat — verified: Kokushíbo Rank_Stat_Duel 3438 vs board 92) nor per-god mastery
    // (getgodranks is privacy-blocked for the id), so we match on the only attributes visible on BOTH sides: god (narrows
    // to that god's ~100-deep board), account level, and CLAN (both survive the privacy flag in a completed match; the
    // candidate's clan/level come from smite.guru). A clan-exact + close-level match inside one god's hidden pool is
    // near-unique → safe to assert (the experiment's 99.9%-precision bar). COVERAGE: hidden players who are top-100 on a
    // god in a ranked queue. Casual-only / never-indexed accounts (e.g. match 1393561801's two hidden players) are on no
    // board and in no cache → NOT reachable here; only the local game-log/MCTS path reveals those. Network-heavy → opt-in.
    static class GodBoard
    {
        // ranked-queue NAME (as it appears in a completed getmatchdetails: <Queue>_Tier / Rank_Stat_<Queue>) → god-leaderboard queue id
        public static readonly Dictionary<string, int> RankedQueueId = new(StringComparer.OrdinalIgnoreCase)
        { ["Conquest"] = 451, ["Joust"] = 450, ["Duel"] = 440 };

        public sealed class Slot { public string GodId = ""; public string GodName = ""; public int Tf; public int Level; public string Clan = ""; public int ClanId; public int Mastery; public List<int> Queues = new(); }
        public sealed class Cand { public string Id = ""; public string Name = ""; public string Clan = ""; public int Level; }

        sealed class Prof { public string Name { get; set; } = ""; public string Clan { get; set; } = ""; public int Level { get; set; } public string At { get; set; } = ""; }
        static readonly object _lock = new();
        static Dictionary<string, Prof> _prof = new();   // smite.guru id→profile cache (name/level/clan), persisted across runs
        static string ProfFile => Path.Combine(Theme.DataDir, "godboard.json");
        static bool _loaded;
        public static void Load() { lock (_lock) { if (_loaded) return; _loaded = true; try { if (File.Exists(ProfFile)) _prof = JsonSerializer.Deserialize<Dictionary<string, Prof>>(File.ReadAllText(ProfFile)) ?? new(); } catch { _prof = new(); } } }
        static void SaveProf() { try { Theme.AtomicWriteText(ProfFile, JsonSerializer.Serialize(_prof)); } catch { } }

        sealed class Board { public List<string> HiddenIds = new(); public DateTime At; }
        static readonly Dictionary<string, Board> _boards = new();   // (godId|queue) → leaked hidden ids, short-TTL (the board moves slowly)
        static readonly TimeSpan BoardTtl = TimeSpan.FromMinutes(30);

        // PURE, TESTABLE: pick the slot's identity from the god-board candidates. v1 asserts a name ONLY on a clan-exact,
        // near-unique match (safest; clanless disambiguation on stale smite.guru levels is deferred). Returns (null,null,0) for Hidden.
        public static (string id, string name, int conf) BestMatch(string slotClan, int slotLevel, IReadOnlyList<Cand> cands)
        {
            if (cands == null || cands.Count == 0 || string.IsNullOrWhiteSpace(slotClan)) return (null, null, 0);
            var m = new List<(Cand c, double s)>();
            foreach (var c in cands)
            {
                if (string.IsNullOrWhiteSpace(c.Clan) || !string.Equals(slotClan, c.Clan, StringComparison.OrdinalIgnoreCase)) continue;
                double s = 50;   // clan-exact base
                if (slotLevel > 0 && c.Level > 0)
                {
                    int d = slotLevel - c.Level;   // smite.guru level is last-seen (≤ current) → d≥0 expected; level only rises
                    if (d == 0) s += 30; else if (d >= 1 && d <= 8) s += 18; else if (d >= 9 && d <= 20) s += 6;
                    else if (d < 0 && d >= -3) s -= 4; else s -= 25;   // big gap either way → likely a different account
                }
                m.Add((c, s));
            }
            if (m.Count == 0) return (null, null, 0);
            m.Sort((a, b) => b.s.CompareTo(a.s));
            if (m[0].s < 55) return (null, null, 0);                                   // need clan + a non-terrible level
            if (m.Count > 1 && (m[0].s - m[1].s) < 20) return (null, null, 0);          // two same-clan/same-god candidates at close level → ambiguous → Hidden
            return (m[0].c.Id, m[0].c.Name, (int)Math.Min(93, 72 + m[0].s / 6));
        }

        static async Task<List<string>> PullBoardHidden(string godId, int queueId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(godId) || godId == "0") return new();
            string key = godId + "|" + queueId;
            lock (_lock) { if (_boards.TryGetValue(key, out var b) && (DateTime.UtcNow - b.At) < BoardTtl) return b.HiddenIds; }
            var ids = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(await SmiteApi.Call("getgodleaderboard", godId, queueId.ToString()));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string nm = e.TryGetProperty("player_name", out var n) ? (n.GetString() ?? "") : "";
                        string id = e.TryGetProperty("player_id", out var i) ? (i.ValueKind == JsonValueKind.Number ? i.GetInt64().ToString() : (i.GetString() ?? "")) : "";
                        if (string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(id) && id != "0") ids.Add(id);   // a HIDDEN board entry whose real id leaked
                    }
            }
            catch { }
            lock (_lock) { _boards[key] = new Board { HiddenIds = ids, At = DateTime.UtcNow }; }
            return ids;
        }

        // Resolve hidden RANKED slots → names. resolveProfiles = the smite.guru id→(name,level,clan) batch (cached here).
        // Returns "godId|tf" → (name,conf), consumed by MakeScoreRow. Also NameDb.Learn's every RESOLVED candidate, because
        // a leaked-id→name mapping is GROUND TRUTH regardless of which slot it is (it enriches the fingerprint DB).
        public static async Task<Dictionary<string, (string name, int conf)>> ResolveSlots(
            IReadOnlyList<Slot> slots,
            Func<IReadOnlyList<string>, CancellationToken, Task<Dictionary<string, (string name, int level, string clan)>>> resolveProfiles,
            CancellationToken ct)
        {
            Load();
            var result = new Dictionary<string, (string name, int conf)>();
            if (slots == null || slots.Count == 0) return result;

            // 1) gather candidate ids across each slot's god boards (only the queues the slot is actually ranked in)
            var slotIds = new Dictionary<Slot, List<string>>();
            var need = new HashSet<string>();
            foreach (var s in slots)
            {
                var ids = new List<string>();
                foreach (var q in s.Queues)
                    foreach (var id in await PullBoardHidden(s.GodId, q, ct))
                        if (!ids.Contains(id)) ids.Add(id);
                slotIds[s] = ids;
                lock (_lock) { foreach (var id in ids) if (!_prof.ContainsKey(id)) need.Add(id); }
            }

            // 2) resolve unknown candidate profiles via smite.guru (batched), cache to disk
            if (need.Count > 0 && resolveProfiles != null)
            {
                Dictionary<string, (string name, int level, string clan)> got = null;
                try { got = await resolveProfiles(need.ToList(), ct); } catch { }
                if (got != null)
                    lock (_lock)
                    {
                        foreach (var kv in got)
                            if (!string.IsNullOrEmpty(kv.Value.name))
                                _prof[kv.Key] = new Prof { Name = kv.Value.name, Level = kv.Value.level, Clan = kv.Value.clan ?? "", At = DateTime.Now.ToString("yyyy-MM-dd") };
                        SaveProf();
                    }
            }

            // 3) match each slot, and learn every resolved id→name (ground truth)
            foreach (var s in slots)
            {
                var cands = new List<Cand>();
                foreach (var id in slotIds[s])
                {
                    Prof p; lock (_lock) { _prof.TryGetValue(id, out p); }
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    cands.Add(new Cand { Id = id, Name = p.Name, Clan = p.Clan, Level = p.Level });
                    if (NameDb.Enabled) NameDb.Learn(id, p.Name, 0, 0, p.Clan, p.Level, s.GodName, 0);   // id→name is true regardless of the slot
                }
                var (mid, mname, conf) = BestMatch(s.Clan, s.Level, cands);
                if (!string.IsNullOrEmpty(mname))
                {
                    result[s.GodId + "|" + s.Tf] = (mname, conf);
                    if (NameDb.Enabled) NameDb.Learn(mid, mname, 0, s.ClanId, s.Clan, s.Level, s.GodName, s.Mastery);   // fold THIS slot's full fingerprint (clan id + mastery) onto the matched real id
                }
            }
            return result;
        }
    }
}
