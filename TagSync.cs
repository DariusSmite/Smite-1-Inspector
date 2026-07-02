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

    // EXPERIMENTAL (experiment/reveal-hidden-names): crowdsourced hidden-player tags shared across all installs via
    // the project's own GitHub repo as a zero-cost backend. READS pull a snapshot from the raw CDN (no auth). WRITES
    // open a "[tag]" issue via the GitHub API using an obfuscated, issues-only token; a GitHub Action validates +
    // de-dupes + merges into snapshot.json on the `tag-db` branch. All fuzzy matching + vote tallying happens here on
    // the client (reusing the same weighted fingerprint), so the backend stays trivial. Local-only otherwise.
    static class TagSync
    {
        // Anonymous, free, NO-ACCOUNT shared store (jsonblob.com) — nothing here is tied to anyone's identity (no GitHub,
        // no repo, no personal account). The whole tag DB is one JSON document we read (GET) and update (PUT). The blob
        // id is a read+write capability, XOR-obfuscated like the Hi-Rez key so it isn't casually extractable; swap stores
        // by changing this one constant.
        static readonly string _blob = Deob(0x5AC31B7F, "nfsTkqRRhrjIcfsYCfopKV4FJhD5QoZmkwNnPnYNlEYpVrSg");
        static string BlobUrl => "https://jsonblob.com/api/jsonBlob/" + _blob;
        public static bool ShareConfigured => _blob.Length > 20;

        public static bool Enabled;                  // gated by the "community tags" setting
        static readonly HttpClient _http = MakeHttp();
        static string _clientId;
        static DateTime _lastPull = DateTime.MinValue;
        static DateTime _lastSubmit = DateTime.MinValue;
        static readonly List<Shared> _shared = new();
        static readonly object _lock = new object();

        public class Shared { public string name; public long c; public int l; public List<string> g = new(); public List<string> p = new(); public string u; }
        // wire format of the shared blob (lowercase keys keep it compact)
        class TagRec { public string k { get; set; } public string u { get; set; } public string name { get; set; } public long c { get; set; } public int l { get; set; } public List<string> g { get; set; } = new(); public List<string> p { get; set; } = new(); public long t { get; set; } }
        class Snap { public int version { get; set; } = 1; public long updated { get; set; } public int count { get; set; } public List<TagRec> tags { get; set; } = new(); }
        static string Md5(string s) { using var m = System.Security.Cryptography.MD5.Create(); var b = m.ComputeHash(Encoding.UTF8.GetBytes(s)); var sb = new StringBuilder(); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString().Substring(0, 16); }

        static string IdFile => Path.Combine(Theme.DataDir, "sync_id.txt");
        static string CacheFile => Path.Combine(Theme.DataDir, "sharedtags.json");
        public static int SharedCount { get { lock (_lock) return _shared.Count; } }

        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.Add("User-Agent", "Smite1Inspector-TagSync/1.0");
            return h;
        }
        static byte[] Pad(int seed, int n) { var p = new byte[n]; long s = seed & 0x7fffffff; for (int i = 0; i < n; i++) { s = (s * 1103515245 + 12345) & 0x7fffffff; p[i] = (byte)((s >> 16) & 0xFF); } return p; }
        static string Deob(int seed, string b64) { try { var d = Convert.FromBase64String(b64); var p = Pad(seed, d.Length); for (int i = 0; i < d.Length; i++) d[i] ^= p[i]; return Encoding.UTF8.GetString(d); } catch { return ""; } }

        public static void Init()
        {
            try
            {
                if (File.Exists(IdFile)) _clientId = File.ReadAllText(IdFile).Trim();
                if (string.IsNullOrEmpty(_clientId) || _clientId.Length < 8) { _clientId = Guid.NewGuid().ToString("N"); File.WriteAllText(IdFile, _clientId); }
            }
            catch { _clientId = Guid.NewGuid().ToString("N"); }
            try { if (File.Exists(CacheFile)) Parse(File.ReadAllText(CacheFile)); } catch { }
        }

        static void Parse(string json)
        {
            try
            {
                var snap = JsonSerializer.Deserialize<Snap>(json);
                var list = new List<Shared>();
                if (snap?.tags != null)
                    foreach (var t in snap.tags)
                        if (!string.IsNullOrEmpty(t.name))
                            list.Add(new Shared { name = t.name, c = t.c, l = t.l, g = t.g ?? new(), p = t.p ?? new(), u = t.u });
                lock (_lock) { _shared.Clear(); _shared.AddRange(list); }
            }
            catch { }
        }

        public static async Task Pull(bool force = false)
        {
            if (!Enabled) return;
            if (!force && (DateTime.UtcNow - _lastPull).TotalMinutes < 10) return;
            _lastPull = DateTime.UtcNow;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, BlobUrl);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;
                string json = await resp.Content.ReadAsStringAsync();
                Parse(json);
                try { Theme.AtomicWriteText(CacheFile, json); } catch { }   // atomic shared-tags cache write
            }
            catch { }
        }

        // Share a user-confirmed tag: read the current blob, append (de-duped by a per-client key), write it back.
        // Read-merge-write so concurrent submitters don't clobber the whole DB. Low write volume makes races rare.
        public static async Task Submit(string name, long clan, int lvl, IEnumerable<string> gods, IEnumerable<string> comp)
        {
            if (!Enabled || !ShareConfigured || string.IsNullOrWhiteSpace(name)) return;
            if ((DateTime.UtcNow - _lastSubmit).TotalSeconds < 3) return;   // local rate-limit
            _lastSubmit = DateTime.UtcNow;
            try
            {
                var gl = (gods ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(12).OrderBy(x => x).ToList();
                var pl = (comp ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x) && x != "0").Distinct().Take(20).OrderBy(x => x).ToList();
                string key = _clientId + ":" + Md5(name.Trim().ToLowerInvariant() + "|" + clan + "|" + string.Join(",", gl) + "|" + string.Join(",", pl));
                string cur = null;
                using (var g = new HttpRequestMessage(HttpMethod.Get, BlobUrl))
                { g.Headers.TryAddWithoutValidation("Accept", "application/json"); using var gr = await _http.SendAsync(g); if (gr.IsSuccessStatusCode) cur = await gr.Content.ReadAsStringAsync(); }
                Snap snap = null; try { snap = JsonSerializer.Deserialize<Snap>(cur); } catch { }
                snap ??= new Snap(); snap.tags ??= new();
                if (snap.tags.Any(t => t.k == key)) return;            // we already submitted this exact tag
                if (snap.tags.Count >= 500000) return;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                snap.tags.Add(new TagRec { k = key, u = _clientId, name = name.Trim(), c = clan, l = lvl, g = gl, p = pl, t = now });
                snap.version = 1; snap.count = snap.tags.Count; snap.updated = now;
                string outp = JsonSerializer.Serialize(snap);
                using var put = new HttpRequestMessage(HttpMethod.Put, BlobUrl) { Content = new StringContent(outp, Encoding.UTF8, "application/json") };
                put.Headers.TryAddWithoutValidation("Accept", "application/json");
                await _http.SendAsync(put);
                Parse(outp);   // refresh local view immediately
            }
            catch { }
        }

        // Resolve a hidden player against the community snapshot. Returns the name with the most distinct submitters
        // among fuzzy-matching tags, that submitter count (votes), and whether it clears the trust gate.
        public static (string name, int votes, bool confirmed) Resolve(long clan, int lvl, int mastery, string god, IReadOnlyCollection<string> comp)
        {
            lock (_lock)
            {
                if (_shared.Count == 0) return (null, 0, false);
                var byName = new Dictionary<string, HashSet<string>>();
                foreach (var t in _shared)
                {
                    int overlap = 0;
                    if (comp != null) foreach (var c in comp) if (t.p.Contains(c)) overlap++;
                    bool clanSame = clan != 0 && t.c == clan;
                    int dl = Math.Abs(t.l - lvl);
                    if (overlap == 0 && clan != 0 && t.c != 0 && t.c != clan) continue;   // different clan, no party link
                    if (overlap == 0 && dl > 30) continue;                                 // too far on level
                    int score = overlap * 50;
                    if (clanSame) score += 60; else if (clan == 0 && t.c == 0) score += 5; else score -= 50;
                    if (lvl > 0) score += dl <= 6 ? 30 - dl * 3 : dl <= 20 ? 8 : -25;
                    if (!string.IsNullOrEmpty(god) && t.g.Contains(god)) score += 15;
                    if (score < 70) continue;
                    if (!byName.TryGetValue(t.name, out var voters)) byName[t.name] = voters = new HashSet<string>();
                    if (!string.IsNullOrEmpty(t.u)) voters.Add(t.u); else voters.Add(t.name + voters.Count);
                }
                if (byName.Count == 0) return (null, 0, false);
                var best = byName.OrderByDescending(kv => kv.Value.Count).First();
                return (best.Key, best.Value.Count, best.Value.Count >= 2);
            }
        }
    }
}
