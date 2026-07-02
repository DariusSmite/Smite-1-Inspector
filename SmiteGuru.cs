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

    // EXPERIMENTAL (user opt-in): pull a player's FULL match history from SmiteGuru (smite.guru) for head-to-head
    // "encounters" across years — the Hi-Rez API caps history at ~50 recent games; SmiteGuru has stored it for years.
    // smite.guru is Cloudflare-gated, so a plain HttpClient is blocked; we drive a hidden WebView2 (real Edge engine) to
    // load the player's PUBLIC match page and read its server-rendered data (window.__NUXT__.data[0].matches). Each match
    // carries its FULL 10-player roster (Hi-Rez ids + team), so one player's history answers any A-vs-B. Respectful:
    // persistent cookie profile (skips the CF challenge after the first hit), ~1.2s between page loads, disk cache +
    // incremental refresh (a re-query fetches only new pages). Reads only public stats; nothing is uploaded.
    sealed class SmiteGuru
    {
        public sealed class Player { [System.Text.Json.Serialization.JsonPropertyName("id")] public long Id { get; set; } [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } [System.Text.Json.Serialization.JsonPropertyName("champion")] public int Champion { get; set; } [System.Text.Json.Serialization.JsonPropertyName("team")] public int Team { get; set; } }
        public sealed class Match { [System.Text.Json.Serialization.JsonPropertyName("match_id")] public string MatchId { get; set; } [System.Text.Json.Serialization.JsonPropertyName("queue_id")] public int QueueId { get; set; } [System.Text.Json.Serialization.JsonPropertyName("time")] public string Time { get; set; } [System.Text.Json.Serialization.JsonPropertyName("winning_team")] public int WinningTeam { get; set; } [System.Text.Json.Serialization.JsonPropertyName("duration")] public int Duration { get; set; } [System.Text.Json.Serialization.JsonPropertyName("players")] public List<Player> Players { get; set; } = new(); }
        sealed class Cursor { [System.Text.Json.Serialization.JsonPropertyName("current")] public int Current { get; set; } [System.Text.Json.Serialization.JsonPropertyName("max")] public int Max { get; set; } }
        sealed class Wrapper { [System.Text.Json.Serialization.JsonPropertyName("cursor")] public Cursor Cursor { get; set; } [System.Text.Json.Serialization.JsonPropertyName("data")] public List<Match> Data { get; set; } = new(); }
        sealed class CacheFile { public int CursorMax { get; set; } public int DeepestPage { get; set; } public string FetchedAt { get; set; } = ""; public List<Match> Matches { get; set; } = new(); public Dictionary<int, int> SeasonDone { get; set; } = new(); }
        // GetHistory result: the matches + how deep the scan reached (Deepest of Max reachable pages). Complete == every game
        // smite.guru exposes was pulled (false only when a hyper-active CURRENT season exceeds the ~179-page offset cap).
        public sealed class History { public List<Match> Matches { get; set; } = new(); public int Deepest { get; set; } public int Max { get; set; } public bool Complete { get; set; } }
        // Full match detail from api.smite.guru/v3/matches/pc/<id> — has per-player stats (KDA/damage/gold/build) and survives
        // for ALL ages (Hi-Rez getmatchdetails only retains a few weeks), so it powers the click-a-row scoreboard for old games.
        public sealed class MPlayer
        {
            [System.Text.Json.Serialization.JsonPropertyName("id")] public long Id { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("champion")] public int Champion { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("team")] public int Team { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("level")] public int Level { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("account_level")] public int AccountLevel { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("kills")] public int Kills { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("assists")] public int Assists { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("deaths")] public int Deaths { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("damage")] public int Damage { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("mitigated")] public int Mitigated { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("taken")] public int Taken { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("healing")] public int Healing { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("gold")] public int Gold { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("structure_damage")] public int StructureDamage { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("party")] public int Party { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("build")] public Dictionary<string, int> Build { get; set; } = new();
        }
        public sealed class MDetail
        {
            [System.Text.Json.Serialization.JsonPropertyName("match_id")] public string MatchId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("queue_id")] public int QueueId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("time")] public string Time { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("winning_team")] public int WinningTeam { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("duration")] public int Duration { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("players")] public List<MPlayer> Players { get; set; } = new();
        }

        readonly Form _owner;
        Microsoft.Web.WebView2.WinForms.WebView2 _wv;
        bool _ready;
        readonly SemaphoreSlim _gate = new(1, 1);
        static readonly JsonSerializerOptions JOpt = new() { PropertyNameCaseInsensitive = true };
        Form _host;
        Microsoft.Web.WebView2.Core.CoreWebView2Environment _env;
        string _lastTitle = "";
        bool _apiCleared;          // api.smite.guru Cloudflare cleared this session → later accounts fetch() page 1 directly (no re-navigate)
        int _mockMax, _mockLatency;   // TEST ONLY (SMITE_TEST_MOCK / SMITE_TEST_MOCKLAT): serve synthetic api.smite.guru JSON to time the scan offline
        public string LastDiag => _lastTitle;

        public SmiteGuru(Form owner) { _owner = owner; }

        async Task EnsureReady()
        {
            if (_ready && _wv?.CoreWebView2 != null) return;
            if (_wv == null)
            {
                // Host the WebView in a REAL but off-screen window. A 2x2 hidden control stalls Cloudflare's JS challenge
                // (it throttles rendering/rAF when not actually painted); an off-screen rendered window runs the challenge
                // normally yet stays invisible to the user.
                _host = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600), Size = new Size(1200, 820) };
                _wv = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, TabStop = false };
                _host.Controls.Add(_wv);
                // TEST ONLY (SMITE_TEST_SHOWHOST): bring the host on-screen + topmost so Cloudflare's managed challenge can solve in
                // an automated/headless run (Turnstile needs a focused, painted window). Never set in normal use — the host stays hidden.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMITE_TEST_SHOWHOST"))) { _host.Location = new Point(40, 40); _host.Size = new Size(700, 560); _host.TopMost = true; }
                _host.Show();
                try { if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMITE_TEST_SHOWHOST"))) _host.Activate(); } catch { }
            }
            // persistent profile dir → keep the cf_clearance cookie between page loads + runs (skip the CF challenge).
            // SPEED + robustness: the host window is hidden off-screen, so Chromium's occlusion/background detection would
            // throttle its rendering + timers to a crawl — which stalls Cloudflare's JS challenge AND slows every fetch the
            // page makes. Disable that throttling so the hidden WebView runs JS/fetch at full foreground speed at all times.
            var opts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling");
            _env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(Theme.DataDir, "webview2"), opts);
            await _wv.EnsureCoreWebView2Async(_env);
            try { _wv.CoreWebView2.Settings.AreDevToolsEnabled = false; _wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; _wv.CoreWebView2.Settings.IsStatusBarEnabled = false; } catch { }
            int.TryParse(Environment.GetEnvironmentVariable("SMITE_TEST_MOCK"), out _mockMax);          // >0 → serve synthetic api JSON (offline scan timing)
            int.TryParse(Environment.GetEnvironmentVariable("SMITE_TEST_MOCKLAT"), out _mockLatency);   // per-request artificial latency (ms) to model network round-trips
            // SPEED + courtesy: block everything we don't need. The match data is INLINE in the SSR'd document (window.__NUXT__),
            // so we only need smite.guru's HTML + Cloudflare's challenge — block all third-party hosts (ads/analytics/trackers)
            // and all images/media/fonts/css. Pages then load in a fraction of the time and we stop pulling their ad partners.
            try
            {
                _wv.CoreWebView2.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                _wv.CoreWebView2.WebResourceRequested += async (s, e) =>
                {
                    try
                    {
                        string host = new Uri(e.Request.Uri).Host;
                        // TEST MOCK: answer api.smite.guru from a synthetic fixture so the full scan can be timed offline (no live service).
                        if (_mockMax > 0 && host.Equals("api.smite.guru", StringComparison.OrdinalIgnoreCase))
                        {
                            string body = MockApi(e.Request.Uri);
                            if (body != null)
                            {
                                var d = e.GetDeferral();
                                try { if (_mockLatency > 0) await Task.Delay(_mockLatency); e.Response = _env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), 200, "OK", "Content-Type: text/plain; charset=utf-8\r\nAccess-Control-Allow-Origin: *"); }
                                catch { }
                                finally { d.Complete(); }
                                return;
                            }
                        }
                        bool allowHost = host.EndsWith("smite.guru", StringComparison.OrdinalIgnoreCase) || host.EndsWith("cloudflare.com", StringComparison.OrdinalIgnoreCase);
                        var c = e.ResourceContext;
                        bool heavy = c == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image || c == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Media
                                  || c == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Font || c == Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Stylesheet;
                        if (!allowHost || heavy) e.Response = _env.CreateWebResourceResponse(null, 204, "No Content", "");
                    }
                    catch { }
                };
            }
            catch { }
            _ready = true;
        }

        // TEST ONLY: synthesize the api.smite.guru matches/season JSON for a given request URL so the scan can be timed without the
        // live (currently flaky) service. Models ONE real season (id 12) of _mockMax pages × 20 games — identical work for old/new.
        string MockApi(string uri)
        {
            int qi = uri.IndexOf('?');
            string path = qi >= 0 ? uri.Substring(0, qi) : uri;
            string query = qi >= 0 ? uri.Substring(qi + 1) : "";
            if (path.Contains("/matches/pc/")) return "{\"match_id\":\"mock\",\"queue_id\":451,\"time\":\"2024-01-01 00:00:00\",\"winning_team\":1,\"duration\":1800,\"players\":[]}";
            if (!path.Contains("/matches")) return null;
            // id from /v3/profiles/<id>/matches → distinct rosters/match_ids per account (so accounts don't cross-dedup)
            long pid = 0; int ip = path.IndexOf("/profiles/"); if (ip >= 0) { int a = ip + 10, b = path.IndexOf('/', a); if (b > a) long.TryParse(path.Substring(a, b - a), out pid); }
            int season = 0, page = 1; bool hasSeason = false;
            foreach (var kv in query.Split('&'))
            {
                var p = kv.Split('=');
                if (p.Length != 2) continue;
                if (p[0] == "season" && int.TryParse(p[1], out var sv)) { season = sv; hasSeason = true; }
                else if (p[0] == "page" && int.TryParse(p[1], out var pv)) page = pv;
            }
            // Model NSeasons real seasons (SMITE_TEST_MOCKSEASONS, default 5) of _mockMax pages each — matches a typical multi-year
            // account. OLD pays per-season batch+gap overhead; NEW fetches every page of every season in one pooled burst.
            int.TryParse(Environment.GetEnvironmentVariable("SMITE_TEST_MOCKSEASONS"), out int nSeasons); if (nSeasons <= 0) nSeasons = 5;
            const int CUR = 12, PERPAGE = 20;
            bool active = hasSeason ? (season <= CUR && season > CUR - nSeasons) : true;   // season probe in range, OR global current-season top
            int yr = 2025 - (CUR - (hasSeason ? season : CUR));                            // map season → a plausible year for the date span
            int max = active ? _mockMax : 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"matches\":{\"cursor\":{\"current\":").Append(page).Append(",\"max\":").Append(max).Append("},\"data\":[");
            if (max > 0 && page >= 1 && page <= max)
                for (int i = 0; i < PERPAGE; i++)
                {
                    if (i > 0) sb.Append(',');
                    string mid = "P" + pid + (hasSeason ? ("S" + season) : "G") + "p" + page + "m" + i;
                    sb.Append("{\"match_id\":\"").Append(mid).Append("\",\"queue_id\":451,\"time\":\"").Append(yr).Append("-01-01 00:00:00\",\"winning_team\":1,\"duration\":1800,\"players\":[{\"id\":").Append(pid).Append(",\"name\":\"P").Append(pid).Append("\",\"champion\":1,\"team\":1},{\"id\":99,\"name\":\"Other\",\"champion\":2,\"team\":2}]}");
                }
            sb.Append("]}}");
            return sb.ToString();
        }

        // Run a script with a hard timeout — ExecuteScriptAsync can block for many seconds while CF's challenge runs, which
        // would otherwise wedge the poll loop and defeat its deadline.
        async Task<string> Eval(string script, int timeoutMs)
        {
            try { var t = _wv.CoreWebView2.ExecuteScriptAsync(script); if (await Task.WhenAny(t, Task.Delay(timeoutMs)) == t) return await t; } catch { }
            return null;
        }

        static string CacheFilePath(long id) => Path.Combine(Theme.DataDir, "sguru_" + id + ".json");
        public static bool HasCache(long id) => File.Exists(CacheFilePath(id));

        // ARCHIVER helpers: read a cached player's history off disk (no network) to expand the crawl frontier / queue scoreboards.
        public static List<long> CachedRosterIds(long id)
        {
            var ids = new List<long>();
            try { var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CacheFilePath(id)), JOpt); if (c?.Matches != null) foreach (var m in c.Matches) if (m?.Players != null) foreach (var p in m.Players) if (p.Id > 0) ids.Add(p.Id); } catch { }
            return ids;
        }
        public static List<string> CachedMatchIds(long id)
        {
            var ms = new List<string>();
            try { var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CacheFilePath(id)), JOpt); if (c?.Matches != null) foreach (var m in c.Matches) if (!string.IsNullOrEmpty(m?.MatchId)) ms.Add(m.MatchId); } catch { }
            return ms;
        }
        // ARCHIVER: fetch one match's FULL scoreboard and persist the raw JSON verbatim to sgmatch_<id>.json (atomic), or write a
        // sgmatch_<id>.dead tombstone if smite.guru no longer stores it. Returns true on success/dead (done), false on timeout (distress).
        public static bool HasMatch(string matchId) => File.Exists(Path.Combine(Theme.DataDir, "sgmatch_" + matchId + ".json")) || File.Exists(Path.Combine(Theme.DataDir, "sgmatch_" + matchId + ".dead"));
        public async Task<bool> GetMatchFullToDisk(string matchId, CancellationToken ct)
        {
            string outp = Path.Combine(Theme.DataDir, "sgmatch_" + matchId + ".json");
            string dead = Path.Combine(Theme.DataDir, "sgmatch_" + matchId + ".dead");
            if (File.Exists(outp) || File.Exists(dead)) return true;
            await _gate.WaitAsync(ct);
            try
            {
                await EnsureReady();
                _wv.CoreWebView2.Navigate("https://api.smite.guru/v3/matches/pc/" + matchId);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                string script = "(function(){try{var t=document.body?document.body.innerText:'';if(!t||t.charAt(0)!=='{')return null;var j=JSON.parse(t);if(j&&j.players&&j.match_id)return t;if(j&&j.error)return '__ERR__';return null;}catch(e){return null;}})()";
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(200, ct);
                    var r = await Eval(script, 5000);
                    if (string.IsNullOrEmpty(r) || r == "null") continue;
                    string inner; try { inner = JsonSerializer.Deserialize<string>(r); } catch { continue; }
                    if (inner == "__ERR__") { try { Theme.AtomicWriteText(dead, ""); } catch { } return true; }   // smite.guru has no stored detail → tombstone, never retry
                    if (inner.Length > 2 && inner[0] == '{') { try { Theme.AtomicWriteText(outp, inner); } catch { } return true; }
                }
                return false;   // timeout → caller treats as distress
            }
            finally { _gate.Release(); }
        }

        // CONCURRENT match-detail archive — fetch a BATCH of match ids through an in-page fetch POOL (same-origin; CF cleared by a
        // prior history fetch or a one-off navigate), persisting each raw scoreboard JSON to sgmatch_<id>.json (or a .dead
        // tombstone when smite.guru no longer stores it). ~conc× faster than navigate-per-match — the burst technique applied to
        // scoreboards. Returns the number newly persisted (incl. tombstones). Transient failures are left for the next round.
        public async Task<int> FetchMatchDetailsToDisk(long clearId, IReadOnlyList<string> matchIds, int conc, CancellationToken ct)
        {
            var todo = matchIds.Where(m => !string.IsNullOrEmpty(m) && !HasMatch(m)).Distinct().ToList();
            if (todo.Count == 0) return 0;
            await _gate.WaitAsync(ct);
            try
            {
                if (!_apiCleared) { var w = await NavigateApi(clearId, ct); if (w == null) return 0; _apiCleared = true; }
                string arr = "[" + string.Join(",", todo.Select(m => JsonSerializer.Serialize(m))) + "]";
                string fire =
                    "(function(){var G=(window.__sgGen=(window.__sgGen||0)+1);var IDS=" + arr + ";var C=" + conc + ";var i=0;var out=[];window.__md=null;" +
                    "async function one(id){for(var a=0;a<2;a++){try{var r=await fetch('https://api.smite.guru/v3/matches/pc/'+id,{credentials:'include'});" +
                    "if(!r.ok){await new Promise(function(z){setTimeout(z,300+a*400);});continue;}var t=await r.text();var dead=false;" +
                    "try{var j=JSON.parse(t);if(j&&j.error){dead=true;}else if(!(j&&j.players&&j.match_id)){await new Promise(function(z){setTimeout(z,300);});continue;}}catch(e){await new Promise(function(z){setTimeout(z,300);});continue;}" +
                    "out.push({id:id,dead:dead,body:dead?'':t});return;}catch(e){await new Promise(function(z){setTimeout(z,300+a*400);});}}out.push({id:id,err:1});}" +
                    "async function worker(){while(i<IDS.length&&window.__sgGen===G){var id=IDS[i++];await one(id);}}" +
                    "(async function(){await Promise.all(Array.from({length:C},function(){return worker();}));if(window.__sgGen===G)window.__md=JSON.stringify(out);})();})();true";
                await Eval(fire, 5000);
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    var r = await Eval("window.__md", 12000);
                    if (string.IsNullOrEmpty(r) || r == "null") continue;
                    int saved = 0;
                    try
                    {
                        var inner = JsonSerializer.Deserialize<string>(r);
                        using var doc = JsonDocument.Parse(inner);
                        foreach (var e in doc.RootElement.EnumerateArray())
                        {
                            if (e.TryGetProperty("err", out _)) continue;   // transient → retry a later round
                            string id = e.GetProperty("id").GetString();
                            if (string.IsNullOrEmpty(id)) continue;
                            bool dead = e.TryGetProperty("dead", out var dv) && dv.ValueKind == JsonValueKind.True;
                            if (dead) { try { Theme.AtomicWriteText(Path.Combine(Theme.DataDir, "sgmatch_" + id + ".dead"), ""); saved++; } catch { } }
                            else if (e.TryGetProperty("body", out var bv)) { var body = bv.GetString(); if (!string.IsNullOrEmpty(body)) { try { Theme.AtomicWriteText(Path.Combine(Theme.DataDir, "sgmatch_" + id + ".json"), body); saved++; } catch { } } }
                        }
                    }
                    catch { }
                    return saved;
                }
                return 0;
            }
            finally { _gate.Release(); }
        }

        // Navigate the WebView straight to the JSON API URL (page 1). This (a) clears api.smite.guru's OWN Cloudflare challenge
        // — its cf_clearance is separate from smite.guru's, and WebView2 solves it like any browser — and (b) gives page 1 +
        // cursor.max from the document body. After this returns, fetch()es to api.smite.guru are SAME-ORIGIN + cleared, so the
        // parallel batches just work. Polls until the body is JSON (i.e., past the CF interstitial).
        async Task<Wrapper> NavigateApi(long id, CancellationToken ct)
        {
            await EnsureReady();
            _wv.CoreWebView2.Navigate("https://api.smite.guru/v3/profiles/" + id + "/matches?page=1");
            var deadline = DateTime.UtcNow.AddSeconds(30);   // generous: one-time CF challenge for the api subdomain
            string script = "(function(){try{var t=document.body?document.body.innerText:'';if(!t||t.charAt(0)!=='{')return null;var j=JSON.parse(t);if(j&&j.matches&&j.matches.cursor)return JSON.stringify(j.matches);return null;}catch(e){return null;}})()";
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(120, ct);
                var r = await Eval(script, 5000);
                if (!string.IsNullOrEmpty(r) && r != "null")
                {
                    try { var inner = JsonSerializer.Deserialize<string>(r); return JsonSerializer.Deserialize<Wrapper>(inner, JOpt); } catch { }
                }
            }
            return null;
        }

        // Fetch a BATCH of pages CONCURRENTLY via smite.guru's own JSON API (api.smite.guru/v3/profiles/<id>/matches?page=N
        // — the endpoint their SPA uses). fetch()+r.json() needs no eval (CSP allows connect to api.smite.guru since the site
        // itself calls it), so it runs in WebView2 and parallelizes. Returns (cursorMax, pages-that-returned, matches).
        async Task<(int max, List<int> got, List<Match> matches)> FetchJsonBatch(long id, IReadOnlyList<int> pages, int season, CancellationToken ct)
        {
            string list = string.Join(",", pages);
            string seasonQ = season > 0 ? "season=" + season + "&" : "";   // season=N&page=M paginates WITHIN a season → reaches past the global ~179-page offset cap
            // FIRE the async fetch and stash the result in a global — ExecuteScriptAsync does NOT await a returned Promise
            // (it serializes the unresolved promise → empty), so we kick it off (returns "true") then POLL the global.
            string fire =
                "window.__sgB=null;(async()=>{const ps=[" + list + "];let mx=0;const got=[];const out=[];" +
                "await Promise.all(ps.map(async pg=>{try{" +
                "const r=await fetch('https://api.smite.guru/v3/profiles/" + id + "/matches?" + seasonQ + "page='+pg,{credentials:'include'});" +
                "if(!r.ok)return;const j=await r.json();const m=j&&j.matches;if(!m)return;" +
                "if(m.cursor&&m.cursor.max>mx)mx=m.cursor.max;" +
                // count a page as 'got' only if it returned games OR is a legit empty tail (pg>=cursor.max); a transient empty-200
                // on an interior page is left ungot so it gets retried and never permanently marks a season complete.
                "if(Array.isArray(m.data)){if(m.data.length>0||(m.cursor&&pg>=m.cursor.max))got.push(pg);out.push.apply(out,m.data);}" +
                "}catch(e){}}));window.__sgB=JSON.stringify({max:mx,got:got,data:out});})();true";
            await Eval(fire, 5000);
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct);
                var r = await Eval("window.__sgB", 5000);
                if (string.IsNullOrEmpty(r) || r == "null") continue;
                try
                {
                    var inner = JsonSerializer.Deserialize<string>(r);
                    using var doc = JsonDocument.Parse(inner);
                    int mx = doc.RootElement.GetProperty("max").GetInt32();
                    var got = doc.RootElement.GetProperty("got").EnumerateArray().Select(e => e.GetInt32()).ToList();
                    var matches = JsonSerializer.Deserialize<List<Match>>(doc.RootElement.GetProperty("data").GetRawText(), JOpt) ?? new();
                    return (mx, got, matches);
                }
                catch { return (0, new(), new()); }
            }
            return (0, new(), new());
        }

        // FAST PATH: fetch an ARBITRARY set of (season,page) jobs in ONE in-browser dispatch driven by a concurrency-limited
        // worker POOL (CONC fetches always in flight, no per-batch C# round-trip). Each job self-retries on !ok/throw with
        // backoff, so completeness holds without a separate C# retry storm. C# polls only a tiny scalar progress object
        // (done/total/max/fin) at fine granularity, then reads the full result ONCE when the pool drains. A generation token
        // makes a superseded run's stragglers exit and never clobber the next run's globals. Returns (cursorMax, got-keys "s:p", matches).
        async Task<(int max, HashSet<string> got, List<Match> matches)> FetchAllJobs(long id, IReadOnlyList<(int season, int page)> jobs, int conc, Action<int, int> progress, CancellationToken ct)
        {
            var empty = (0, new HashSet<string>(), new List<Match>());
            if (jobs.Count == 0) return empty;
            string arr = "[" + string.Join(",", jobs.Select(j => "[" + j.season + "," + j.page + "]")) + "]";
            string fire =
                "(function(){var G=(window.__sgGen=(window.__sgGen||0)+1);var ID=" + id + ";var J=" + arr + ";var C=" + conc + ";" +
                "var st={done:0,total:J.length,max:0,fin:false,gen:G};window.__sgP=st;window.__sgR=null;var i=0;var got=[];var data=[];" +
                "async function one(s,p){var u='https://api.smite.guru/v3/profiles/'+ID+'/matches?'+(s>0?('season='+s+'&'):'')+'page='+p;" +
                "for(var a=0;a<3;a++){try{var r=await fetch(u,{credentials:'include'});if(!r.ok){await new Promise(z=>setTimeout(z,200+a*300));continue;}" +
                "var j=await r.json();var m=j&&j.matches;if(!m)return;if(m.cursor&&m.cursor.max>st.max)st.max=m.cursor.max;" +
                "if(Array.isArray(m.data)){if(m.data.length>0||(m.cursor&&p>=m.cursor.max))got.push(s+':'+p);for(var k=0;k<m.data.length;k++)data.push(m.data[k]);}return;" +
                "}catch(e){await new Promise(z=>setTimeout(z,200+a*300));}}}" +
                "async function worker(){while(i<J.length&&window.__sgGen===G){var job=J[i++];await one(job[0],job[1]);st.done++;}}" +
                "(async function(){var ws=[];for(var w=0;w<C;w++)ws.push(worker());await Promise.all(ws);" +
                "if(window.__sgGen===G){window.__sgR=JSON.stringify({max:st.max,got:got,data:data});st.fin=true;}})();" +
                "})();true";
            await Eval(fire, 5000);
            // poll the cheap scalar progress object; never serialize the growing got/data arrays until the pool reports fin.
            string pscript = "(function(){var s=window.__sgP;if(!s)return '';return JSON.stringify({d:s.done,t:s.total,m:s.max,f:s.fin,g:s.gen});})()";
            var deadline = DateTime.UtcNow.AddSeconds(120);
            int lastD = -1;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(70, ct);
                var pr = await Eval(pscript, 4000);
                bool fin = false;
                if (!string.IsNullOrEmpty(pr) && pr != "null")
                {
                    try
                    {
                        var inner = JsonSerializer.Deserialize<string>(pr);
                        using var doc = JsonDocument.Parse(inner);
                        int d = doc.RootElement.GetProperty("d").GetInt32();
                        int t = doc.RootElement.GetProperty("t").GetInt32();
                        fin = doc.RootElement.GetProperty("f").GetBoolean();
                        if (d != lastD) { lastD = d; progress?.Invoke(Math.Max(1, d), Math.Max(1, t)); }
                    }
                    catch { }
                }
                if (!fin) continue;
                var rr = await Eval("window.__sgR", 8000);
                if (string.IsNullOrEmpty(rr) || rr == "null") continue;
                try
                {
                    var inner = JsonSerializer.Deserialize<string>(rr);
                    using var doc = JsonDocument.Parse(inner);
                    int mx = doc.RootElement.GetProperty("max").GetInt32();
                    var got = new HashSet<string>(doc.RootElement.GetProperty("got").EnumerateArray().Select(e => e.GetString()));
                    var matches = JsonSerializer.Deserialize<List<Match>>(doc.RootElement.GetProperty("data").GetRawText(), JOpt) ?? new();
                    return (mx, got, matches);
                }
                catch { return empty; }
            }
            return empty;
        }

        // Ensure api.smite.guru's Cloudflare challenge is cleared THIS SESSION and return page-1 matches + cursor.max. The first
        // account navigates (solves CF once); every later account reuses the cleared origin and just fetch()es page 1 — saving a
        // full navigate + poll cycle per account. If the direct probe comes back blocked (clearance lapsed), re-navigates.
        async Task<(List<Match> page1, int max, bool ok)> EnsureCleared(long id, CancellationToken ct)
        {
            if (_apiCleared)
            {
                var (mx, got, ms) = await FetchAllJobs(id, new[] { (0, 1) }, 1, null, ct);
                if (got.Contains("0:1")) return (ms, mx, true);   // direct same-origin fetch worked → CF still cleared
            }
            var w = await NavigateApi(id, ct);
            if (w == null) return (new List<Match>(), 0, false);
            _apiCleared = true;
            return (w.Data, w.Cursor?.Max ?? 0, true);
        }

        // FULL-history scan via the JSON API in PARALLEL batches — typically the whole history in a few seconds. Navigate once
        // (page 1) to establish Cloudflare clearance + a smite.guru page context, read cursor.max, then concurrently pull every
        // remaining page from the JSON API (retrying any that didn't come back, so it's complete — every game, first→last).
        // Disk-cached + deduped; a repeat compare with no new games returns instantly. Returns History (matches + Deepest/Max).
        // DIAGNOSTIC ONLY: navigate (clear CF) then fetch specific pages, reporting per-page match counts + date range, to
        // tell a hard offset cap (pages return empty) from a transient rate-limit. Used by the SMITE_TEST_DEEPPAGE hook.
        public async Task<string> ProbePages(long id, IReadOnlyList<int> pages, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var w1 = await NavigateApi(id, ct);
                if (w1 == null) return "NavigateApi failed (CF?)";
                int max = w1.Cursor?.Max ?? 0;
                var sb = new System.Text.StringBuilder($"cursorMax={max}; page1={w1.Data.Count} games\n");
                foreach (var pg in pages)
                {
                    var (mx, got, ms) = await FetchJsonBatch(id, new[] { pg }, 0, ct);
                    string range = ms.Count > 0 ? (ms[ms.Count - 1].Time + " .. " + ms[0].Time) : "-";
                    sb.Append($"page {pg}: got={string.Join(",", got)} count={ms.Count} range={range}\n");
                    await Task.Delay(400, ct);
                }
                return sb.ToString();
            }
            finally { _gate.Release(); }
        }

        // DIAGNOSTIC ONLY: clear CF for api.smite.guru (via id's page 1) then fetch an ARBITRARY api url, to test whether some
        // alternate pagination param (cursor/before/offset) reaches past the ~179-page offset cap. Returns a short summary.
        public async Task<string> RawFetch(long clearId, string url, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var w1 = await NavigateApi(clearId, ct);
                if (w1 == null) return "NavigateApi failed (CF?)";
                string u = url.Replace("\\", "");
                string fire =
                    "window.__rf=null;(async()=>{try{const r=await fetch(" + JsonSerializer.Serialize(u) + ",{credentials:'include'});" +
                    "const t=await r.text();let n=-1,first='',last='',cmax=-1;try{const j=JSON.parse(t);const m=j&&j.matches?j.matches:j;" +
                    "if(m&&Array.isArray(m.data)){n=m.data.length;if(n>0){first=m.data[0].time;last=m.data[n-1].time;}}" +
                    "if(m&&m.cursor)cmax=m.cursor.max;}catch(e){}" +
                    "window.__rf=JSON.stringify({status:r.status,ok:r.ok,n:n,first:first,last:last,cmax:cmax,head:t.slice(0,200)});" +
                    "}catch(e){window.__rf=JSON.stringify({err:String(e)});}})();true";
                await Eval(fire, 5000);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    var r = await Eval("window.__rf", 5000);
                    if (string.IsNullOrEmpty(r) || r == "null") continue;
                    try { return JsonSerializer.Deserialize<string>(r); } catch { return r; }
                }
                return "(timeout)";
            }
            finally { _gate.Release(); }
        }

        // RECON (reverse-engineer the PUBLIC API surface): load smite.guru's own SPA, record every api.smite.guru call it makes
        // on load, and download its frontend JS bundles to extract every endpoint/path string they reference — so we can find a
        // bulk/enumeration/leaderboard endpoint that beats one-player-at-a-time snowballing. Reads only public assets.
        public async Task<string> Recon(CancellationToken ct, string extraPath = null)
        {
            await _gate.WaitAsync(ct);
            try
            {
                await EnsureReady();
                _wv.CoreWebView2.Navigate("https://smite.guru/" + (extraPath ?? ""));
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)   // wait for the SPA document to finish past Cloudflare
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(600, ct);
                    var rs = await Eval("(function(){try{return document.readyState;}catch(e){return 'e';}})()", 4000);
                    if (!string.IsNullOrEmpty(rs) && rs.Contains("complete")) break;
                }
                await Task.Delay(3000, ct);   // let the SPA fire its initial XHRs
                string fire =
                    "window.__rc=null;(async function(){try{" +
                    "var R=performance.getEntriesByType('resource').map(function(e){return e.name;});" +
                    "var scripts=[].slice.call(document.scripts).map(function(s){return s.src;}).filter(Boolean);" +
                    "var urls={};R.concat(scripts).forEach(function(u){urls[u]=1;});" +
                    "var apiCalls=R.filter(function(u){return /api\\.smite\\.guru/.test(u);});" +
                    "var js=Object.keys(urls).filter(function(u){return /\\.js(\\?|$)/.test(u)&&/smite\\.guru/.test(u);});" +
                    "var ep={};" +
                    "for(var i=0;i<js.length;i++){try{var t=await (await fetch(js[i])).text();" +
                    "(t.match(/\\/(v\\d+|api)\\/[A-Za-z0-9_\\-\\/:{}.$]+/g)||[]).forEach(function(s){ep[s]=1;});" +
                    "(t.match(/https?:\\/\\/[a-z0-9.\\-]*smite\\.guru[^\"'`\\s)]*/g)||[]).forEach(function(s){ep[s]=1;});" +
                    "}catch(e){}}" +
                    "window.__rc=JSON.stringify({js:js,apiCalls:apiCalls,endpoints:Object.keys(ep).sort(),nuxtKeys:window.__NUXT__?Object.keys(window.__NUXT__):[]});" +
                    "}catch(e){window.__rc=JSON.stringify({err:String(e)});}})();true";
                await Eval(fire, 5000);
                var d2 = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < d2)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(500, ct);
                    var r = await Eval("window.__rc", 8000);
                    if (string.IsNullOrEmpty(r) || r == "null") continue;
                    try { return JsonSerializer.Deserialize<string>(r); } catch { return r; }
                }
                return "(recon timeout)";
            }
            finally { _gate.Release(); }
        }

        // DIAGNOSTIC ONLY: navigate (clear CF) then fetch specific pages, reporting per-page match counts + date range, to
        // tell a hard offset cap (pages return empty) from a transient rate-limit. Used by the SMITE_TEST_DEEPPAGE hook.
        // Probe which SMITE seasons have data for this player (season=N&page=1 → cursor.max>0), one concurrent burst with a
        // per-fetch retry. Seasons are global, so 1..MAX covers everyone; empty seasons return nothing and are skipped.
        async Task<Dictionary<int, int>> DiscoverSeasons(long id, CancellationToken ct)
        {
            const int MAXS = 15;
            string fire =
                "window.__sgS=null;(async()=>{const out={};await Promise.all(Array.from({length:" + (MAXS + 1) + "},(_,i)=>i).map(async s=>{" +   // seasons 0..MAXS (0 = a small misc bucket; smite.guru data starts ~2020 for everyone)
                "for(let a=0;a<2;a++){try{const r=await fetch('https://api.smite.guru/v3/profiles/" + id + "/matches?season='+s+'&page=1',{credentials:'include'});" +
                "if(!r.ok){await new Promise(z=>setTimeout(z,250));continue;}const j=await r.json();const m=j&&j.matches;" +
                "if(m&&m.cursor&&m.cursor.max>0)out[s]=m.cursor.max;break;}catch(e){await new Promise(z=>setTimeout(z,250));}}}));" +
                "window.__sgS=JSON.stringify(out);})();true";
            await Eval(fire, 5000);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(90, ct);
                var r = await Eval("window.__sgS", 5000);
                if (string.IsNullOrEmpty(r) || r == "null") continue;
                try
                {
                    var inner = JsonSerializer.Deserialize<string>(r);
                    using var doc = JsonDocument.Parse(inner);
                    var d = new Dictionary<int, int>();
                    foreach (var p in doc.RootElement.EnumerateObject()) if (int.TryParse(p.Name, out var s)) d[s] = p.Value.GetInt32();
                    return d;
                }
                catch { return new(); }
            }
            return new();
        }

        // FULL-history scan, SEASON BY SEASON. The global ?page=N endpoint 500-errors past ~179 pages (~4,475 games), so a very
        // active player's older matches are unreachable that way. But ?season=N&page=M paginates within each season (each tiny:
        // ≤~40 pages), so iterating seasons reaches a player's COMPLETE history back to 2020 — only a sliver of a hyper-active
        // CURRENT season (its pages past the 179 cap) stays out of reach. Disk-cached per season (old seasons are immutable →
        // fetched once; the current season is re-checked for new games). Dedup by match_id. Returns History (Complete=nothing capped).
        public async Task<History> GetHistory(long id, int maxPages, Action<int, int> progress, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                const int CAP = 179;   // per-season offset cap (page 180+ → HTTP 500)
                const int CONC = 16;   // in-browser worker-pool size (HTTP/2 over Cloudflare multiplexes this over one connection)
                var cache = LoadCache(id);
                cache.SeasonDone ??= new();
                var have = new HashSet<string>(cache.Matches.Where(m => m.MatchId != null).Select(m => m.MatchId));
                var merged = new List<Match>(cache.Matches);
                // clear CF once per session → page-1 + cursor.max; later accounts skip the navigate and fetch() page 1 directly
                progress?.Invoke(1, 1);
                var (page1, globalMaxRaw, ok) = await EnsureCleared(id, ct);
                if (!ok) { merged.Sort((a, b) => string.CompareOrdinal(b.Time ?? "", a.Time ?? "")); return new History { Matches = merged, Deepest = cache.DeepestPage, Max = cache.CursorMax, Complete = false }; }
                int globalMax = globalMaxRaw > 0 ? globalMaxRaw : cache.CursorMax;
                foreach (var m in page1) if (m?.MatchId != null && have.Add(m.MatchId)) merged.Add(m);   // newest games (current season top)
                var seasons = await DiscoverSeasons(id, ct);
                if (seasons.Count == 0)   // season param unavailable for this profile → fall back to global pagination (capped)
                {
                    int limit = Math.Min(globalMax > 0 ? globalMax : 1, maxPages);
                    var jobs = new List<(int, int)>(); for (int p = 2; p <= limit; p++) jobs.Add((0, p));
                    var (mx, got, ms) = await FetchAllJobs(id, jobs, CONC, (d, t) => progress?.Invoke(d, Math.Max(t, 1)), ct);
                    if (mx > 0) globalMax = mx;
                    foreach (var m in ms) if (m?.MatchId != null && have.Add(m.MatchId)) merged.Add(m);
                    var miss = jobs.Where(j => !got.Contains(j.Item1 + ":" + j.Item2)).ToList();   // self-retry inside the pool already covered most; one more cleanup pass for stragglers
                    if (miss.Count > 0) { progress?.Invoke(-1, limit); var (m2, g2, ms2) = await FetchAllJobs(id, miss, CONC, null, ct); foreach (var k in g2) got.Add(k); foreach (var m in ms2) if (m?.MatchId != null && have.Add(m.MatchId)) merged.Add(m); }
                    var gotG = new HashSet<int> { 1 }; foreach (var k in got) { var pp = k.Split(':'); if (pp.Length == 2 && int.TryParse(pp[1], out var pg)) gotG.Add(pg); }
                    int deepG = 1; while (gotG.Contains(deepG + 1)) deepG++;
                    merged.Sort((a, b) => string.CompareOrdinal(b.Time ?? "", a.Time ?? ""));
                    SaveCache(id, new CacheFile { CursorMax = globalMax, DeepestPage = deepG, FetchedAt = DateTime.UtcNow.ToString("o"), Matches = merged, SeasonDone = cache.SeasonDone });
                    return new History { Matches = merged, Deepest = deepG, Max = globalMax, Complete = globalMax > 0 && deepG >= globalMax };
                }
                int curSeason = seasons.Keys.Max();
                int totalPages = seasons.Sum(kv => Math.Min(kv.Value, CAP));   // reachable pages across all seasons (progress denominator)
                int cappedSeasons = 0;
                // Build ONE job list across every season that still needs scanning (newest first), then fetch the whole thing in a
                // single concurrency-pooled burst — no per-season/per-batch C# round-trips. Old fully-scanned seasons are skipped.
                var allJobs = new List<(int, int)>();
                var seasonLimit = new Dictionary<int, int>();
                foreach (var s in seasons.Keys.OrderByDescending(s => s))
                {
                    int cm = seasons[s]; int sLimit = Math.Min(cm, CAP); seasonLimit[s] = sLimit;
                    if (cm > CAP) cappedSeasons++;
                    bool isCurrent = (s == curSeason);
                    if (!isCurrent && cache.SeasonDone.TryGetValue(s, out var doneCm) && doneCm >= cm) continue;   // immutable old season already complete → skip
                    for (int p = 1; p <= sLimit; p++) allJobs.Add((s, p));
                }
                var (mxA, gotA, msA) = await FetchAllJobs(id, allJobs, CONC, (d, t) => progress?.Invoke(Math.Min(d, totalPages), totalPages), ct);
                foreach (var m in msA) if (m?.MatchId != null && have.Add(m.MatchId)) merged.Add(m);
                var missA = allJobs.Where(j => !gotA.Contains(j.Item1 + ":" + j.Item2)).ToList();   // straggler cleanup pass (pool already self-retried each job 3×)
                if (missA.Count > 0) { progress?.Invoke(-1, totalPages); var (mxB, gotB, msB) = await FetchAllJobs(id, missA, CONC, null, ct); foreach (var k in gotB) gotA.Add(k); foreach (var m in msB) if (m?.MatchId != null && have.Add(m.MatchId)) merged.Add(m); }
                // per-season completeness from the merged got-set
                bool anyGap = false;
                foreach (var kv in seasonLimit)
                {
                    int s = kv.Key, sLimit = kv.Value, cm = seasons[s];
                    bool isCurrent = (s == curSeason);
                    if (!isCurrent && cache.SeasonDone.TryGetValue(s, out var dCm) && dCm >= cm) continue;   // not scanned this round (already done)
                    var gp = new HashSet<int>();
                    foreach (var k in gotA) { var pp = k.Split(':'); if (pp.Length == 2 && int.TryParse(pp[0], out var sv) && sv == s && int.TryParse(pp[1], out var pg)) gp.Add(pg); }
                    int deepS = 0; while (gp.Contains(deepS + 1)) deepS++;
                    if (!isCurrent && deepS >= sLimit && cm <= CAP) cache.SeasonDone[s] = cm;   // fully fetched, non-capped, immutable → mark done (never the current season)
                    else if (deepS < sLimit) anyGap = true;                                     // pages genuinely failed after retries → don't claim complete
                }
                merged.Sort((a, b) => string.CompareOrdinal(b.Time ?? "", a.Time ?? ""));
                bool complete = cappedSeasons == 0 && !anyGap;   // full history ONLY if nothing capped AND no season left pages unfetched
                SaveCache(id, new CacheFile { CursorMax = globalMax, DeepestPage = totalPages, FetchedAt = DateTime.UtcNow.ToString("o"), Matches = merged, SeasonDone = cache.SeasonDone });
                return new History { Matches = merged, Deepest = totalPages, Max = totalPages, Complete = complete };
            }
            finally { _gate.Release(); }
        }

        Dictionary<int, string> _champs, _items;
        // same-origin fetch of one api.smite.guru URL → response text (assumes NavigateApi/NavigateMatch already cleared CF).
        async Task<string> FetchOne(string url, CancellationToken ct)
        {
            string fire = "window.__sg1=null;(async()=>{try{const r=await fetch(" + JsonSerializer.Serialize(url) + ",{credentials:'include'});window.__sg1=await r.text();}catch(e){window.__sg1='__ERR__';}})();true";
            await Eval(fire, 5000);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(200, ct);
                var r = await Eval("window.__sg1", 5000);
                if (string.IsNullOrEmpty(r) || r == "null") continue;
                try { var s = JsonSerializer.Deserialize<string>(r); return s == "__ERR__" ? null : s; } catch { return null; }
            }
            return null;
        }

        // id→name maps for gods (champions) and items, fetched once from smite.guru's static endpoints and cached to disk.
        async Task<Dictionary<int, string>> LoadIdNameMap(string which, string url, CancellationToken ct)
        {
            string f = Path.Combine(Theme.DataDir, "sguru_" + which + ".json");
            try { if (File.Exists(f)) { var d = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(f)); if (d != null && d.Count > 0) return d; } } catch { }
            var txt = await FetchOne(url, ct);
            if (string.IsNullOrEmpty(txt)) return null;   // fetch failed/timed out → return null so the caller retries (don't cache an empty map for the session)
            var map = new Dictionary<int, string>();
            try
            {
                using var doc = JsonDocument.Parse(txt);
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.Number && p.Value.TryGetProperty("name", out var nmE))
                        map[idE.GetInt32()] = nmE.GetString();
            }
            catch { return null; }
            if (map.Count == 0) return null;
            try { Theme.AtomicWriteText(f, JsonSerializer.Serialize(map)); } catch { }   // atomic god/item name map write
            return map;
        }

        // Full scoreboard for ONE match (any age) from api.smite.guru/v3/matches/pc/<id>, plus the god/item name maps to render it.
        public async Task<(MDetail match, Dictionary<int, string> gods, Dictionary<int, string> items)> GetMatchFull(string matchId, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                await EnsureReady();
                _wv.CoreWebView2.Navigate("https://api.smite.guru/v3/matches/pc/" + matchId);   // clears CF + body = the match JSON
                MDetail md = null;
                var deadline = DateTime.UtcNow.AddSeconds(15);   // CF clear + fetch; shorter so a missing/old match fails fast
                // return the match JSON when ready, or "__ERR__" the moment smite.guru answers with {"error":...} (old match
                // whose detail it no longer stores) so we bail immediately instead of polling the whole deadline.
                string script = "(function(){try{var t=document.body?document.body.innerText:'';if(!t||t.charAt(0)!=='{')return null;var j=JSON.parse(t);if(j&&j.players&&j.match_id)return t;if(j&&j.error)return '__ERR__';return null;}catch(e){return null;}})()";
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(300, ct);
                    var r = await Eval(script, 5000);
                    if (string.IsNullOrEmpty(r) || r == "null") continue;
                    string inner; try { inner = JsonSerializer.Deserialize<string>(r); } catch { continue; }
                    if (inner == "__ERR__") break;   // smite.guru has no stored detail for this match → fail fast
                    try { md = JsonSerializer.Deserialize<MDetail>(inner, JOpt); if (md != null) break; } catch { }
                }
                if (md == null) return (null, null, null);
                _champs ??= await LoadIdNameMap("champions", "https://api.smite.guru/v3/champions", ct);
                _items ??= await LoadIdNameMap("items", "https://api.smite.guru/v3/items", ct);
                return (md, _champs, _items);
            }
            finally { _gate.Release(); }
        }

        CacheFile LoadCache(long id) { try { var f = CacheFilePath(id); if (File.Exists(f)) { var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(f), JOpt) ?? new(); c.Matches = (c.Matches ?? new()).Where(m => m != null).ToList(); return c; } } catch { } return new(); }
        void SaveCache(long id, CacheFile c) { try { Theme.AtomicWriteText(CacheFilePath(id), JsonSerializer.Serialize(c)); } catch { } }   // atomic: irreplaceable old-season history must survive a kill mid-write
        public void Wipe(long id) { try { var f = CacheFilePath(id); if (File.Exists(f)) File.Delete(f); } catch { } }
        // gated wipe — serialized through _gate so a forced refresh can't race an in-flight GetHistory's SaveCache
        public async Task WipeAsync(long id, CancellationToken ct = default) { await _gate.WaitAsync(ct); try { var f = CacheFilePath(id); if (File.Exists(f)) File.Delete(f); } catch { } finally { _gate.Release(); } }
        // Resolve a batch of player ids → (name, account level, clan tag) from smite.guru's permanent profile cache.
        // /v3/profiles/{id}/matches page-1 carries a `player` object (name/level/team) that is present even for accounts
        // CURRENTLY Hi-Rez-private, as long as smite.guru ever indexed them — this is the id→name bridge GodBoard needs to
        // turn a leaked leaderboard id into a real name. Navigate once to clear CF, then ONE concurrency-limited in-browser
        // fetch pool (a superseded run's stragglers exit via the __gbGen token, like FetchAllJobs). Unknown/never-indexed
        // ids simply don't appear in the result (smite.guru 404s them) → the caller treats them as unresolved.
        public async Task<Dictionary<string, (string name, int level, string clan)>> ResolveProfilesBatch(IReadOnlyList<string> ids, CancellationToken ct)
        {
            var outMap = new Dictionary<string, (string name, int level, string clan)>();
            if (ids == null || ids.Count == 0) return outMap;
            await _gate.WaitAsync(ct);
            try
            {
                await EnsureReady();
                if (!_apiCleared) { var w = await NavigateApi(long.TryParse(ids[0], out var f0) ? f0 : 0, ct); if (w != null) _apiCleared = true; }   // clear api.smite.guru CF once
                string arr = "[" + string.Join(",", ids.Select(i => JsonSerializer.Serialize(i))) + "]";
                string fire =
                    "(function(){var G=(window.__gbGen=(window.__gbGen||0)+1);var IDS=" + arr + ";var C=8;var i=0;" +
                    "var st={done:0,total:IDS.length,fin:false};window.__gbP=st;window.__gbR=null;var out={};" +
                    "async function one(id){var u='https://api.smite.guru/v3/profiles/'+id+'/matches?page=1';" +
                    "for(var a=0;a<3;a++){try{var r=await fetch(u,{credentials:'include'});if(!r.ok){await new Promise(z=>setTimeout(z,150+a*250));continue;}" +
                    "var j=await r.json();var p=j&&j.player;if(p){out[id]={name:p.name||'',level:p.level||0,clan:p.team||''};}return;" +
                    "}catch(e){await new Promise(z=>setTimeout(z,150+a*250));}}}" +
                    "async function worker(){while(i<IDS.length&&window.__gbGen===G){var id=IDS[i++];await one(id);st.done++;}}" +
                    "(async function(){var ws=[];for(var w=0;w<C;w++)ws.push(worker());await Promise.all(ws);" +
                    "if(window.__gbGen===G){window.__gbR=JSON.stringify(out);st.fin=true;}})();})();true";
                await Eval(fire, 5000);
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(150, ct);
                    var pr = await Eval("(function(){var s=window.__gbP;return s?JSON.stringify({f:s.fin}):'';})()", 4000);
                    bool fin = false;
                    if (!string.IsNullOrEmpty(pr) && pr != "null") { try { using var d = JsonDocument.Parse(JsonSerializer.Deserialize<string>(pr)); fin = d.RootElement.GetProperty("f").GetBoolean(); } catch { } }
                    if (!fin) continue;
                    var rr = await Eval("window.__gbR", 8000);
                    if (string.IsNullOrEmpty(rr) || rr == "null") continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(rr));
                        foreach (var p in doc.RootElement.EnumerateObject())
                        {
                            string nm = p.Value.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                            int lv = p.Value.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                            string cl = p.Value.TryGetProperty("clan", out var c) ? (c.GetString() ?? "") : "";
                            outMap[p.Name] = (nm, lv, cl);
                        }
                    }
                    catch { }
                    break;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { _gate.Release(); }
            return outMap;
        }

        // Tear down the WebView2 + host form on app exit (must run on the UI thread, which FormClosed does), so the
        // msedgewebview2.exe child process and the webview2 profile-dir lock are released instead of lingering.
        public void Shutdown()
        {
            try { _wv?.Dispose(); } catch { }
            try { _host?.Dispose(); } catch { }
            try { _gate?.Dispose(); } catch { }
            _wv = null; _host = null; _env = null; _ready = false;
        }
    }
}
