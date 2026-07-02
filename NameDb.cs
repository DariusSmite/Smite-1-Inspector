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

    // EXPERIMENTAL (experiment/reveal-hidden-names): an auto-learned database that maps player fingerprints to
    // real names so privacy-hidden players (playerId=0 / blank name in a completed getmatchdetails) can be
    // revealed. Privacy is account-level, so a private account is anonymized in EVERY completed match — the only
    // place their name leaks is the LIVE roster (getmatchplayerdetails). So we harvest names from every visible
    // player we see (live rosters above all), key them by a fingerprint that survives anonymization
    // (god + that god's mastery + account level + clan + named party-mates), and match completed hidden rows
    // against it. Exact reveals come from matches we captured while live (by match id). Local only; never uploaded.
    static class NameDb
    {
        public class Entry
        {
            public string Id { get; set; } = "";                       // hi-rez player_id
            public string Name { get; set; } = "";
            public int Portal { get; set; }
            public int ClanId { get; set; }
            public string Clan { get; set; } = "";
            public int Level { get; set; }                             // latest account level seen
            public Dictionary<string, int> GodMastery { get; set; } = new();   // god -> latest per-god mastery rank
            public Dictionary<string, string> GodSkin { get; set; } = new();   // god -> latest non-default SkinId used (a bought/mastery skin is a stable, identifying choice)
            public List<string> Companions { get; set; } = new();      // player_ids this player PREMADE with (shared PartyId — strong, immediate)
            public Dictionary<string, int> NeighborCounts { get; set; } = new();   // named SAME-TEAM co-players -> times co-occurred (weak; trusted only after repeats)
            public Dictionary<string, int> RankTier { get; set; } = new();   // queue (Conquest/Joust/Duel) -> ranked tier 1-27 (latest seen). SURVIVES the privacy flag in completed matches.
            public Dictionary<string, int> RankMmr { get; set; } = new();    // queue -> MMR (Rank_Stat, rounded). A near-unique per-account number → the most DISCRIMINATING fingerprint signal available.
            public string PortalUserId { get; set; } = "";               // platform-specific user ID (Steam64 ID for Portal=5, Epic account for 9, etc.) — UNIQUE per player, never changes, survives privacy in MCTS responses
            public string LastSeen { get; set; } = "";
            public int Seen { get; set; }
        }
        public class LiveCap   // exact reveal: the names captured from a specific live match, keyed by team|god
        {
            public Dictionary<string, string> ByGod { get; set; } = new();
            public string At { get; set; } = "";
        }
        class Persist { public List<Entry> Players { get; set; } = new(); public Dictionary<string, LiveCap> Live { get; set; } = new(); }

        static readonly int MaxEntries = MaxEntriesForRam();   // RAM-adaptive cap: lean on 4GB, richer on 16GB+
        const int MaxLive = 20000;
        static readonly object _lock = new object();
        static Dictionary<string, Entry> _byId = new();
        static Dictionary<string, LiveCap> _live = new();
        const int NeighborTrust = 2;       // a same-team co-player becomes a trusted "neighbor" edge after this many co-occurrences
        // inverted blocking indexes (postings of entry ids) so Resolve scores only plausible candidates, never the whole DB
        static Dictionary<string, HashSet<string>> _byCompanion = new();
        static Dictionary<string, HashSet<string>> _byGod = new();
        static Dictionary<int, HashSet<string>> _byClan = new();
        static Dictionary<string, HashSet<string>> _byNeighbor = new();   // trusted same-team co-player -> entries (only edges that cleared NeighborTrust)
        static Dictionary<string, string> _byPortal = new();               // PORTAL_USERID → entry.Id (1:1 — MCTS leaks this for hidden players; unique per account, never changes)
        static bool _dirty;
        static DateTime _lastSave = DateTime.MinValue;

        public static bool Enabled;        // gated by the "reveal hidden players" setting

        static string DbFile => Path.Combine(Theme.DataDir, "namedb.json");
        static string BakFile => DbFile + ".bak";
        static string Today => DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        public static int PlayerCount { get { lock (_lock) return _byId.Count; } }
        public static int LiveCount { get { lock (_lock) return _live.Count; } }
        // Resolve a learned player_id → its known name (for showing a hidden tag's party-mate ids as names). Null if unknown.
        public static string NameById(string id) { if (string.IsNullOrEmpty(id)) return null; lock (_lock) return _byId.TryGetValue(id, out var e) ? e.Name : null; }

        // Record a platform id ↔ account id link WITHOUT a name. Used when getplayeridbyportaluserid recovers a hidden
        // player's real player_id but the account is private + un-indexed (no name anywhere). Persisting the link means
        // that if the id ever gets a name (harvester, a later match, manual tag), the PORTAL_USERID resolves instantly.
        public static void LinkPortal(string id, string portalUserId)
        {
            if (string.IsNullOrEmpty(id) || id == "0" || string.IsNullOrEmpty(portalUserId)) return;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out var e)) { e = new Entry { Id = id, Seen = 0, Name = "" }; _byId[id] = e; }
                if (string.IsNullOrEmpty(e.PortalUserId)) e.PortalUserId = portalUserId;
                _byPortal[portalUserId] = id;
                _dirty = true;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);
        static int MaxEntriesForRam()
        {
            long gb = 8;
            try { if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0) gb = kb / 1024 / 1024; } catch { }
            return gb <= 4 ? 60000 : gb <= 8 ? 120000 : gb <= 16 ? 250000 : 400000;   // ~a few hundred bytes/entry
        }
        // --- inverted-index maintenance ---
        static void Idx(Dictionary<string, HashSet<string>> ix, string key, string id) { if (string.IsNullOrEmpty(key)) return; if (!ix.TryGetValue(key, out var s)) ix[key] = s = new(); s.Add(id); }
        static void IndexEntry(Entry e)
        {
            foreach (var g in e.GodMastery.Keys) Idx(_byGod, g, e.Id);
            if (e.ClanId != 0) { if (!_byClan.TryGetValue(e.ClanId, out var sc)) _byClan[e.ClanId] = sc = new(); sc.Add(e.Id); }
            foreach (var c in e.Companions) Idx(_byCompanion, c, e.Id);
            foreach (var kv in e.NeighborCounts) if (kv.Value >= NeighborTrust) Idx(_byNeighbor, kv.Key, e.Id);   // only trusted edges
            if (!string.IsNullOrEmpty(e.PortalUserId)) _byPortal[e.PortalUserId] = e.Id;
        }
        static void RebuildIndexes() { _byCompanion = new(); _byGod = new(); _byClan = new(); _byNeighbor = new(); _byPortal = new(); foreach (var e in _byId.Values) IndexEntry(e); }
        // Inverse document frequency of a party-mate: a teammate that partners with MANY distinct players (a popular pub
        // or a streamer) carries little identity; a rare 2-3 stack partner pins hard. df = how many entries list them.
        static double Idf(string companionId) { int n = Math.Max(2, _byId.Count); int df = (_byCompanion.TryGetValue(companionId, out var s) ? s.Count : 0) + 1; return Math.Log((double)(n + 1) / df); }
        static double NIdf(string neighborId) { int n = Math.Max(2, _byId.Count); int df = (_byNeighbor.TryGetValue(neighborId, out var s) ? s.Count : 0) + 1; return Math.Log((double)(n + 1) / df); }

        static Persist TryRead(string path)
        {
            try { if (File.Exists(path)) return JsonSerializer.Deserialize<Persist>(File.ReadAllText(path)); } catch { }
            return null;
        }
        public static void Load()
        {
            lock (_lock)
            {
                _byId = new(); _live = new();
                var p = TryRead(DbFile) ?? TryRead(BakFile);   // fall back to the backup if the main file is missing/corrupt
                if (p != null)
                {
                    if (p.Players != null) foreach (var e in p.Players) if (e != null && !string.IsNullOrEmpty(e.Id)) { e.GodMastery ??= new(); e.GodSkin ??= new(); e.Companions ??= new(); e.NeighborCounts ??= new(); e.RankTier ??= new(); e.RankMmr ??= new(); e.PortalUserId ??= ""; _byId[e.Id] = e; }
                    if (p.Live != null) _live = p.Live;
                }
                RebuildIndexes();
            }
        }
        // Atomic save: write a temp file then swap it in (keeping the prior copy as .bak). A crash mid-write can no
        // longer leave a truncated namedb.json that Load silently resets to empty (the old bare WriteAllText bug).
        public static void Save(bool force = false)
        {
            Persist p;
            lock (_lock)
            {
                if (!_dirty && !force) return;
                if (!force && (DateTime.UtcNow - _lastSave).TotalSeconds < 8) return;   // debounce churn
                if (_byId.Count > MaxEntries) Evict(_byId.Count - MaxEntries);
                if (_live.Count > MaxLive)
                    foreach (var k in _live.OrderBy(kv => kv.Value.At).Take(_live.Count - MaxLive).Select(kv => kv.Key).ToList()) _live.Remove(k);
                // DEEP-COPY snapshot under the lock (Entry has mutable nested dicts/lists), so the serialize + atomic write
                // below run OUTSIDE the lock — a concurrent Learn() can't tear an entry mid-serialize, and a background
                // Save no longer freezes the UI thread's Resolve()/Learn() while a large DB serializes + hits disk.
                p = new Persist
                {
                    Players = _byId.Values.Select(e => new Entry { Id = e.Id, Name = e.Name, Portal = e.Portal, ClanId = e.ClanId, Clan = e.Clan, Level = e.Level, GodMastery = new Dictionary<string, int>(e.GodMastery), GodSkin = new Dictionary<string, string>(e.GodSkin), Companions = new List<string>(e.Companions), NeighborCounts = new Dictionary<string, int>(e.NeighborCounts), RankTier = new Dictionary<string, int>(e.RankTier), RankMmr = new Dictionary<string, int>(e.RankMmr), PortalUserId = e.PortalUserId, LastSeen = e.LastSeen, Seen = e.Seen }).ToList(),
                    Live = _live.ToDictionary(kv => kv.Key, kv => new LiveCap { At = kv.Value.At, ByGod = new Dictionary<string, string>(kv.Value.ByGod) }),
                };
                _dirty = false; _lastSave = DateTime.UtcNow;
            }
            string tmp = DbFile + "." + Environment.CurrentManagedThreadId + ".tmp";   // unique per thread → UI + watcher saves don't collide on one .tmp
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(p));
                if (File.Exists(DbFile)) File.Replace(tmp, DbFile, BakFile); else File.Move(tmp, DbFile);
            }
            catch { lock (_lock) _dirty = true; try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }   // failed write → retry next change; don't leave an orphan .tmp
        }
        // Value-aware eviction: never drop a rare PARTY-ANCHOR (someone referenced as a companion); otherwise prefer
        // many sightings / many gods / recency. Sheds one-off pub rows, keeps the identifying anchors.
        static void Evict(int n)
        {
            foreach (var id in _byId.Values.OrderBy(Value).Take(n).Select(e => e.Id).ToList())
            {
                if (_byId.TryGetValue(id, out var e))
                {
                    foreach (var g in e.GodMastery.Keys) if (_byGod.TryGetValue(g, out var s)) s.Remove(id);
                    if (_byClan.TryGetValue(e.ClanId, out var sc)) sc.Remove(id);
                    foreach (var c in e.Companions) if (_byCompanion.TryGetValue(c, out var s2)) s2.Remove(id);
                    foreach (var kv in e.NeighborCounts) if (_byNeighbor.TryGetValue(kv.Key, out var s3)) s3.Remove(id);
                    if (!string.IsNullOrEmpty(e.PortalUserId)) _byPortal.Remove(e.PortalUserId);
                }
                _byId.Remove(id);
            }
        }
        static double Value(Entry e)
        {
            double v = _byCompanion.ContainsKey(e.Id) ? 1e9 : 0;   // anchor → pin, never evict
            v += Math.Log(1 + e.Seen) * 1000 + e.GodMastery.Count * 50;
            if (DateTime.TryParse(e.LastSeen, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) v += (d - new DateTime(2020, 1, 1)).TotalDays;   // recency (invariant: LastSeen is written yyyy-MM-dd)
            return v;
        }

        public static void Clear()
        {
            lock (_lock) { _byId = new(); _live = new(); _byCompanion = new(); _byGod = new(); _byClan = new(); _byNeighbor = new(); _byPortal = new(); _dirty = true; }
            Save(true);
        }

        // Record a VISIBLE player. god/mastery are optional (0/"" when learning from a profile/friend rather than a match row).
        public static void Learn(string id, string name, int portal, int clanId, string clan, int level, string god, int godMastery, IEnumerable<string> companions = null, IEnumerable<string> neighbors = null, string skinId = null, IEnumerable<(string queue, int tier, int mmr)> ranked = null, string portalUserId = null)
        {
            if (string.IsNullOrEmpty(id) || id == "0" || string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out var e)) { e = new Entry { Id = id, Seen = 0 }; _byId[id] = e; }
                e.Name = name;
                if (portal != 0) e.Portal = portal;
                if (!string.IsNullOrEmpty(portalUserId)) { e.PortalUserId = portalUserId; _byPortal[portalUserId] = e.Id; }
                if (clanId != 0) { if (e.ClanId != 0 && e.ClanId != clanId && _byClan.TryGetValue(e.ClanId, out var oldClanSet)) oldClanSet.Remove(e.Id); e.ClanId = clanId; if (!string.IsNullOrEmpty(clan)) e.Clan = clan; }   // purge stale _byClan posting on a clan change
                if (level > e.Level) e.Level = level;
                if (!string.IsNullOrEmpty(god) && godMastery > 0)
                    e.GodMastery[god] = Math.Max(godMastery, e.GodMastery.TryGetValue(god, out var gm) ? gm : 0);
                if (!string.IsNullOrEmpty(god) && !string.IsNullOrEmpty(skinId) && skinId != "0") e.GodSkin[god] = skinId;   // caller passes only NON-default skins (a bought/mastery skin is identifying)
                if (companions != null)
                    foreach (var c in companions) if (!string.IsNullOrEmpty(c) && c != "0" && c != id && !e.Companions.Contains(c)) e.Companions.Add(c);
                if (e.Companions.Count > 60)
                {
                    var keptC = e.Companions.OrderByDescending(Idf).Take(60).ToList();   // keep the rarest (highest-IDF) anchors, shed common pub teammates
                    var keepC = new HashSet<string>(keptC);
                    foreach (var d in e.Companions) if (!keepC.Contains(d) && _byCompanion.TryGetValue(d, out var sc2)) sc2.Remove(e.Id);   // purge stale postings so Idf df stays honest
                    e.Companions = keptC;
                }
                // same-team co-players: tally co-plays (a trusted "neighbor" edge needs >=NeighborTrust repeats — guards against
                // a coincidental one-off rare teammate). Premade party-mates are excluded (already the stronger Companions signal).
                if (neighbors != null)
                    foreach (var nb in neighbors)
                        if (!string.IsNullOrEmpty(nb) && nb != "0" && nb != id && !e.Companions.Contains(nb))
                            e.NeighborCounts[nb] = (e.NeighborCounts.TryGetValue(nb, out var cnt) ? cnt : 0) + 1;
                if (e.NeighborCounts.Count > 80)
                {
                    var keptN = e.NeighborCounts.OrderByDescending(kv => kv.Value).Take(80).ToDictionary(kv => kv.Key, kv => kv.Value);
                    foreach (var kv in e.NeighborCounts) if (!keptN.ContainsKey(kv.Key) && kv.Value >= NeighborTrust && _byNeighbor.TryGetValue(kv.Key, out var sn2)) sn2.Remove(e.Id);   // purge stale trusted-edge postings
                    e.NeighborCounts = keptN;
                }
                // per-queue ranked tier + MMR (only when actually ranked — an unranked queue reports the 1500 placeholder).
                // MMR is non-monotonic (drifts as the player climbs/falls), so keep the LATEST value rather than the max.
                if (ranked != null)
                    foreach (var (q, tr, mmr) in ranked)
                    {
                        if (string.IsNullOrEmpty(q) || tr <= 0) continue;
                        e.RankTier[q] = tr;
                        if (mmr > 0) e.RankMmr[q] = mmr;
                    }
                if (e.LastSeen != Today) { e.LastSeen = Today; e.Seen++; }
                IndexEntry(e);   // keep the blocking indexes current (HashSet.Add is idempotent)
                _dirty = true;
            }
        }

        // Record the exact roster of a LIVE match so the SAME match, once completed (and anonymized), reveals exactly.
        // KEYED BY GodId (numeric) — verified identical across live getmatchplayerdetails and completed getmatchdetails.
        // The god NAME is NOT safe as a key: live exposes it as "GodName" but completed as "Reference_Name" (different
        // fields that can differ in spelling). Pass name="" / null to FORGET a slot (untag). taskForce is the team (1/2).
        public static void LearnLiveSlot(string matchId, int taskForce, string godId, string name)
        {
            if (string.IsNullOrEmpty(matchId) || string.IsNullOrEmpty(godId) || godId == "0") return;
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(name))   // forget this slot (untag)
                {
                    if (_live.TryGetValue(matchId, out var ex)) { ex.ByGod.Remove(taskForce + "|" + godId); _dirty = true; }
                    return;
                }
                if (!_live.TryGetValue(matchId, out var lc)) { lc = new LiveCap { At = Today }; _live[matchId] = lc; }
                lc.At = Today;   // refresh recency so an actively-used capture isn't evicted by the date-ordered MaxLive trim
                lc.ByGod[taskForce + "|" + godId] = name.Trim();
                _dirty = true;
            }
        }
        // Resolve a hidden completed-match slot to a name captured live. Try GodId (robust) first, then the legacy
        // god-NAME key (entries captured before the GodId switch). DIRECT team+god lookup ONLY — no cross-team wildcard:
        // taskForce(live) == TaskForce(completed) for a given match (verified), so a captured slot resolves exactly,
        // while a god mirrored on both teams with only one side tagged can never reveal the untagged twin by mistake.
        // If teams ever disagreed we'd show "Hidden" (safe) rather than a confident wrong name.
        public static string ResolveExact(string matchId, int taskForce, string godId, string godName = null)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(matchId) || !_live.TryGetValue(matchId, out var lc)) return null;
                foreach (var key in new[] { godId, godName })
                {
                    if (string.IsNullOrEmpty(key) || key == "0") continue;
                    if (lc.ByGod.TryGetValue(taskForce + "|" + key, out var n)) return n;
                }
                return null;
            }
        }

        public static (string name, string id) ResolveByPortal(string portalUserId)
        {
            if (string.IsNullOrEmpty(portalUserId)) return (null, null);
            lock (_lock)
            {
                if (_byPortal.TryGetValue(portalUserId, out var eid) && _byId.TryGetValue(eid, out var e))
                    return (e.Name, e.Id);
            }
            return (null, null);
        }

        // Honest, CORROBORATION-GATED reveal. A privacy-hidden record carries no identifier, so a name can only ever be
        // a GUESS. We refuse blind stat-twin guesses: a candidate is only considered if it's corroborated by at least one
        // shared NAMED party-mate, OR by an exact clan match together with the same god at near-identical mastery AND a
        // near-identical account level (very unlikely to collide by chance). Everything else returns (null,0) → "Hidden".
        // This catches privacy-TOGGLERS we saw while public + party-graph attribution; it can never name a permanently
        // private account (it's not in the DB). The caller must present the result as a guess, never as fact.
        public static (string name, string id, int conf) Resolve(int clanId, int level, int mastery, string god, IReadOnlyCollection<string> companions = null, IReadOnlyCollection<string> neighbors = null, IReadOnlyCollection<string> exclude = null, string skinId = null, IReadOnlyDictionary<string, (int tier, int mmr)> slotRank = null)
        {
            lock (_lock)
            {
                // Candidate set from the blocking indexes (lossless vs the corroboration gate): entries sharing a
                // party-mate, OR in the same clan playing this god (statLock), OR sharing a trusted same-team neighbor.
                var cand = new HashSet<string>();
                if (companions != null) foreach (var c in companions) if (_byCompanion.TryGetValue(c, out var s)) cand.UnionWith(s);
                if (neighbors != null) foreach (var n in neighbors) if (_byNeighbor.TryGetValue(n, out var sn)) cand.UnionWith(sn);
                if (clanId != 0 && !string.IsNullOrEmpty(god) && _byClan.TryGetValue(clanId, out var sclan) && _byGod.TryGetValue(god, out var sgod))
                    foreach (var id in sclan) if (sgod.Contains(id)) cand.Add(id);
                var nameTop = new Dictionary<string, double>();   // best score per candidate NAME (for the top-2 margin guard)
                var nameId = new Dictionary<string, string>();
                // The IDF of a given query companion/neighbor is constant across all candidates within this (locked) call, so
                // memoize it once rather than recomputing the log + dictionary lookup for every candidate that shares it.
                Dictionary<string, double> idfC = null, idfN = null;
                if (companions != null) { idfC = new(); foreach (var c in companions) idfC[c] = Idf(c); }
                if (neighbors != null) { idfN = new(); foreach (var n in neighbors) idfN[n] = NIdf(n); }
                foreach (var cid in cand)
                {
                    if (!_byId.TryGetValue(cid, out var e)) continue;
                    if (exclude != null && (exclude.Contains(cid) || exclude.Contains(e.Name))) continue;   // can't be a player already VISIBLE in this same match
                    int overlap = 0; double idfSum = 0;
                    if (companions != null) foreach (var c in companions) if (e.Companions.Contains(c)) { overlap++; idfSum += idfC[c]; }
                    int nbOverlap = 0; double nbIdf = 0;   // shared TRUSTED same-team neighbors (the social-graph anchor)
                    if (neighbors != null) foreach (var n in neighbors) if (e.NeighborCounts.TryGetValue(n, out var nc) && nc >= NeighborTrust) { nbOverlap++; nbIdf += idfN[n]; }
                    bool clanExact = clanId != 0 && e.ClanId == clanId;
                    bool playsGod = !string.IsNullOrEmpty(god) && e.GodMastery.ContainsKey(god);
                    int dmSigned = playsGod ? mastery - e.GodMastery[god] : 0;       // observed − stored; <0 = regression (mastery only rises)
                    int dlSigned = (level > 0 && e.Level > 0) ? level - e.Level : 0; // observed − stored; <0 = regression (level only rises)
                    bool masteryRegress = playsGod && mastery > 0 && dmSigned < -2;
                    bool statLock = clanExact && playsGod && mastery > 0 && Math.Abs(dmSigned) <= 2 && level > 0 && Math.Abs(dlSigned) <= 3;
                    // CORROBORATION GATE: a premade party-mate, OR the stat-lock, OR >=2 trusted shared neighbors (a stable group)
                    if (overlap == 0 && !statLock && nbOverlap < 2) continue;
                    double score = idfSum * 5.0 + nbIdf * 2.0;   // party-mates strong; trusted neighbors weaker
                    if (clanExact) score += 35; else if (clanId != 0 && e.ClanId != 0) score -= 35;   // three-state: missing clan = neutral; a clan MISMATCH (both clanned, different) is strong "different person" evidence
                    if (playsGod) score += Math.Abs(dmSigned) <= 2 ? 30 - Math.Abs(dmSigned) * 5 : (dmSigned > 2 && dmSigned <= 12 ? 12 : 0);
                    if (masteryRegress) score -= 40;           // mastery can't drop → likely a different person
                    // SKIN match (this god): a bought/mastery skin is a stable, identifying choice the API can't strip. The
                    // caller only passes NON-default skins, so any match is meaningful — but popular skins still collide, so
                    // this only BOOSTS an already-corroborated candidate (never opens the gate alone). Capped BELOW the top-2
                    // margin guard (12) so a single shared popular skin can't flip a near-tie the design wants as "Hidden".
                    // Mismatch = neutral (players own many skins).
                    if (!string.IsNullOrEmpty(skinId) && e.GodSkin.TryGetValue(god, out var eskin) && eskin == skinId) score += 10;
                    // RANKED MMR / TIER — survives the privacy flag in completed getmatchdetails. MMR (Rank_Stat) is a
                    // near-unique per-account number, so a tight match is the most discriminating evidence available (more
                    // than level/mastery, which cluster); a large gap on a shared ranked queue is positive evidence of a
                    // DIFFERENT person. Only a queue BOTH sides actually played ranked (tier>0) counts — an unranked queue
                    // reports the 1500 placeholder. A tight MMR match also counts as corroboration (lifts the level cap),
                    // but never opens the gate alone.
                    bool mmrTight = false;
                    if (slotRank != null)
                        foreach (var kv in slotRank)
                        {
                            int stier = kv.Value.tier, smmr = kv.Value.mmr;
                            if (stier <= 0 || !e.RankTier.TryGetValue(kv.Key, out var etier) || etier <= 0) continue;
                            if (stier == etier) score += 5;            // same tier bucket (coarse)
                            if (smmr > 0 && e.RankMmr.TryGetValue(kv.Key, out var emmr) && emmr > 0)
                            {
                                int dmmr = Math.Abs(smmr - emmr);
                                if (dmmr <= 25) { score += 25; mmrTight = true; }   // near-identical → near-unique
                                else if (dmmr <= 75) score += 14;
                                else if (dmmr <= 150) score += 6;
                                else score -= 10;                      // both ranked this queue but far apart → likely different
                            }
                        }
                    if (level > 0 && e.Level > 0)
                    {
                        // SMITE XP-per-level RISES STEEPLY (progression table: ~2k XP/level at L20, ~17k at L100, ~35k at L120,
                        // ~170k at L150, ~490k at L160, higher beyond), so high-level accounts barely move. Plausible forward
                        // growth between two sightings therefore SHRINKS as the level rises: +10 at L30 is routine, +10 at L200
                        // is millions of XP → a different account. So the tolerance is LEVEL-AWARE. Levels never drop, but the
                        // DB keeps the MAX seen, so an OLDER match reads lower → a mild penalty, not a hard reject.
                        int lvlRef = Math.Max(level, e.Level);
                        int nearMax = lvlRef >= 150 ? 1 : lvlRef >= 100 ? 3 : lvlRef >= 50 ? 8 : 15;   // "a normal bit of recent play" for this level band
                        int lvl;
                        if (dlSigned < -3) lvl = -28;                       // clear regression → different / much-older account
                        else if (dlSigned < 0) lvl = -6;                    // small regression (-1..-3) → likely just an older match
                        else if (dlSigned == 0) lvl = 28;                   // EXACT account level
                        else if (dlSigned <= nearMax) lvl = 20;            // within plausible recent growth for this level band
                        else if (dlSigned <= nearMax * 3) lvl = 8;         // a longer gap → moderate
                        else lvl = 0;                                       // implausible jump for this band → no positive (likely different)
                        // level CORROBORATES, never ANCHORS: cap its positive below the soft floor without real non-level evidence,
                        // so a coincidental exact level + a single worthless shared pub-mate can't, by itself, mint a name.
                        // real non-level evidence = a real premade, same clan, the same god at CLOSE mastery (not mere god
                        // ownership — the harvester learns every god a public player ever touched), or trusted neighbours.
                        bool corroborated = idfSum * 5.0 >= 8 || overlap >= 2 || clanExact || (playsGod && Math.Abs(dmSigned) <= 2) || nbOverlap >= 2 || mmrTight;
                        if (lvl > 8 && !corroborated) lvl = 8;
                        score += lvl;
                    }
                    // STALE-ENTRY penalty: an old snapshot is a weaker anchor than a fresh one, so a recently-corroborated
                    // candidate outranks a stale stat-twin whose year-old stats happen to still match. Mild — the gate +
                    // margin guard already block most stale false reveals.
                    if (DateTime.TryParse(e.LastSeen, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ls)) { double months = (DateTime.Now - ls).TotalDays / 30.0; if (months > 8) score -= 12; else if (months > 4) score -= 6; }
                    if (score >= 20 && (!nameTop.TryGetValue(e.Name, out var ns) || score > ns)) { nameTop[e.Name] = score; nameId[e.Name] = e.Id; }   // soft floor + per-name best
                }
                if (nameTop.Count == 0) return (null, null, 0);
                var ranked = nameTop.OrderByDescending(kv => kv.Value).ToList();
                // top-2 MARGIN GUARD: if two DIFFERENT names are near-tied, it's ambiguous → show "Hidden" rather than risk the wrong one
                if (ranked.Count > 1 && ranked[0].Value - ranked[1].Value < 12) return (null, null, 0);
                int conf = (int)Math.Min(95, 30 + ranked[0].Value / 3);
                return (ranked[0].Key, nameId[ranked[0].Key], conf);
            }
        }
    }
}
