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
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Manual scaling everywhere (S helper) so the 4K target stays crisp; no auto layout magic.
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
            Theme.LoadFont();   // bring up Montserrat (installed / downloaded / Segoe UI) before any control exists
            AbilityData.Load();
            SdkData.Load();
            SdkInspect.Load();
            Application.Run(new MainForm());
        }
    }

    // ---------------------------------------------------------------- theme ----
    static class Theme
    {
        // Palette sampled from "SMITE Optimizer": pure black, accent red #C11E1F, white text.
        public static readonly Color Bg       = Color.FromArgb(0, 0, 0);      // pure black
        public static readonly Color Panel    = Color.FromArgb(13, 13, 13);   // near-black chrome
        public static readonly Color Input     = Color.FromArgb(20, 20, 20);
        public static readonly Color Text      = Color.White;
        public static readonly Color Dim       = Color.FromArgb(150, 150, 150);
        public static readonly Color Line      = Color.FromArgb(48, 48, 48);
        public static readonly Color Accent    = Color.FromArgb(193, 30, 31); // #C11E1F
        public static readonly Color AccentHi  = Color.FromArgb(220, 46, 47); // hover/lit red
        public static readonly Color AccentDk  = Color.FromArgb(120, 22, 22); // pressed red
        public static readonly Color Dirty     = Color.FromArgb(48, 18, 18);  // changed-field tint

        // source colours for added values: purple = "Add value", yellow = "SDK Inspector"
        public static readonly Color Purple    = Color.FromArgb(138, 79, 255);
        public static readonly Color PurpleTint = Color.FromArgb(40, 24, 66);
        public static readonly Color Yellow    = Color.FromArgb(214, 170, 40);
        public static readonly Color YellowTint = Color.FromArgb(48, 40, 14);
        public static readonly Color Blue      = Color.FromArgb(46, 134, 222);   // player-tracker accent
        public static readonly Color Green     = Color.FromArgb(60, 180, 90);    // online / in-game status
        public static Color Lighten(Color c, int d = 24) => Color.FromArgb(Math.Min(255, c.R + d), Math.Min(255, c.G + d), Math.Min(255, c.B + d));
        public static Color Darken(Color c, int d = 40) => Color.FromArgb(Math.Max(0, c.R - d), Math.Max(0, c.G - d), Math.Max(0, c.B - d));

        public static FontFamily Family;          // Montserrat or a safe fallback
        public static bool UsingMontserrat;
        static PrivateFontCollection _pfc;
        static readonly Dictionary<string, Font> _cache = new Dictionary<string, Font>();

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);
        const uint FR_PRIVATE = 0x10;

        static readonly HttpClient _http = MakeHttp();
        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.Add("User-Agent", "SmiteGodLab/1.0");
            return h;
        }

        public static string AppDir
        {
            get
            {
                try { var p = Environment.ProcessPath; if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p); }
                catch { }
                return AppContext.BaseDirectory;
            }
        }

        // User-data folder: Documents\Smite Inspector (always writable, unlike the exe folder). Created on demand.
        public static string DataDir
        {
            get
            {
                try
                {
                    // Test/sandbox override (env var) so verification never writes into the user's real data folder.
                    var ov = Environment.GetEnvironmentVariable("SMITE_TEST_DATADIR");
                    if (!string.IsNullOrEmpty(ov)) { Directory.CreateDirectory(ov); return ov; }
                    string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Smite Inspector");
                    Directory.CreateDirectory(d);
                    return d;
                }
                catch { return AppDir; }   // fall back to the exe folder if Documents is unavailable
            }
        }

        // Crash-safe text write: serialize to a sibling temp file, flush it all the way to physical disk, then atomically
        // swap it over the target. A kill/crash/power-loss mid-write can only ever leave the PREVIOUS good file intact (plus a
        // stray .tmp that the next write overwrites) — never a truncated/corrupt file. Used for caches whose loss is permanent
        // now that smite.guru is shutting down (per-player history, scoreboards, name maps, shared tags). No external deps.
        public static void AtomicWriteText(string path, string content)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);   // UTF-8, no BOM — matches File.WriteAllText/ReadAllText defaults
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);   // flush OS buffers to the physical disk BEFORE the rename → the swapped-in file is always complete
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);   // atomic in-place replace on NTFS
            else File.Move(tmp, path);                              // first write: no target to replace
        }

        public static void LoadFont()
        {
            // 1) installed system-wide?
            try
            {
                using (var ic = new InstalledFontCollection())
                    if (ic.Families.Any(f => f.Name.Equals("Montserrat", StringComparison.OrdinalIgnoreCase)))
                    { Family = new FontFamily("Montserrat"); UsingMontserrat = true; return; }
            }
            catch { }

            // 2) load (and if needed download) a private copy next to the exe
            try
            {
                string dir = Path.Combine(AppDir, "assets");
                Directory.CreateDirectory(dir);
                string reg = Path.Combine(dir, "Montserrat-Regular.ttf");
                string bold = Path.Combine(dir, "Montserrat-Bold.ttf");
                Task.WhenAll(   // download both concurrently (halves the worst-case first-launch stall)
                    EnsureFileAsync(reg, "https://github.com/JulietaUla/Montserrat/raw/master/fonts/ttf/Montserrat-Regular.ttf"),
                    EnsureFileAsync(bold, "https://github.com/JulietaUla/Montserrat/raw/master/fonts/ttf/Montserrat-Bold.ttf")).GetAwaiter().GetResult();

                _pfc = new PrivateFontCollection();
                bool any = false;
                if (File.Exists(reg))  { AddFontResourceEx(reg, FR_PRIVATE, IntPtr.Zero);  _pfc.AddFontFile(reg);  any = true; }
                if (File.Exists(bold)) { AddFontResourceEx(bold, FR_PRIVATE, IntPtr.Zero); _pfc.AddFontFile(bold); }
                if (any)
                {
                    var fam = _pfc.Families.FirstOrDefault(f => f.Name.StartsWith("Montserrat", StringComparison.OrdinalIgnoreCase))
                              ?? _pfc.Families.FirstOrDefault();
                    if (fam != null) { Family = fam; UsingMontserrat = true; return; }
                }
            }
            catch { }

            // 3) offline fallback
            try { Family = new FontFamily("Segoe UI"); } catch { Family = FontFamily.GenericSansSerif; }
            UsingMontserrat = false;
        }

        static async Task EnsureFileAsync(string path, string url)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1024) return;
            try
            {
                // short timeout: the shared HttpClient's default is 100s, and this runs (awaited) BEFORE the window shows —
                // a slow/unreachable network must not hang startup. On timeout we just fall back to Segoe UI and retry next launch.
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                byte[] data = await _http.GetByteArrayAsync(url, cts.Token);
                if (data != null && data.Length > 1024) File.WriteAllBytes(path, data);
            }
            catch { }
        }

        // Cached fonts (app-lifetime; never disposed — they are shared across controls and paints).
        public static Font F(float pt, FontStyle st = FontStyle.Regular)
        {
            string k = pt.ToString("0.0") + "|" + (int)st;
            if (_cache.TryGetValue(k, out var f)) return f;
            Font font;
            try { font = new Font(Family, pt, st, GraphicsUnit.Point); }
            catch
            {
                try { font = new Font(Family, pt, FontStyle.Regular, GraphicsUnit.Point); }
                catch { font = new Font("Segoe UI", pt, st); }
            }
            _cache[k] = font;
            return font;
        }

        // Cached monospace font for value boxes (one shared instance, not one-per-row).
        public static Font Mono(float pt)
        {
            string k = "mono|" + pt.ToString("0.0");
            if (_cache.TryGetValue(k, out var f)) return f;
            Font font;
            try { font = new Font("Consolas", pt); }
            catch { try { font = new Font(FontFamily.GenericMonospace, pt); } catch { font = new Font("Segoe UI", pt); } }
            _cache[k] = font;
            return font;
        }

        public static async Task<byte[]> GetBytes(string url)
        {
            try { return await _http.GetByteArrayAsync(url); } catch { return null; }
        }
    }

    class AbilityInfo { public string Slot, Name, Slug; }

    // god base name -> slot ("P","1".."4") -> ability. Sourced from the embedded abilities.json
    // (built by reading the SMITE wiki); icon files live next to the exe at icons\abilities\<slug>.jpg.
    static class AbilityData
    {
        static readonly Dictionary<string, Dictionary<string, AbilityInfo>> _map =
            new Dictionary<string, Dictionary<string, AbilityInfo>>(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            string json = ReadJson();
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var god in doc.RootElement.EnumerateObject())
                {
                    try   // one malformed god/ability entry must not abort the whole loop (dropping all remaining gods)
                    {
                        var slots = new Dictionary<string, AbilityInfo>(StringComparer.OrdinalIgnoreCase);
                        if (god.Value.ValueKind != JsonValueKind.Array) { _map[god.Name] = slots; continue; }
                        foreach (var ab in god.Value.EnumerateArray())
                        {
                            if (!ab.TryGetProperty("slot", out var slotE)) continue;
                            string slot = slotE.GetString();
                            if (string.IsNullOrEmpty(slot)) continue;
                            slots[slot] = new AbilityInfo
                            {
                                Slot = slot,
                                Name = ab.TryGetProperty("name", out var n) ? n.GetString() : null,
                                Slug = ab.TryGetProperty("slug", out var s) ? s.GetString() : null,
                            };
                        }
                        _map[god.Name] = slots;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static AbilityInfo Get(string godBase, string slot)
        {
            if (godBase != null && _map.TryGetValue(godBase, out var slots) && slots.TryGetValue(slot, out var info))
                return info;
            return null;
        }

        static string ReadJson()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("abilities.json", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var r = new StreamReader(s))
                        return r.ReadToEnd();
            }
            catch { }
            try
            {
                string f = Path.Combine(Theme.AppDir, "data", "abilities.json");
                if (File.Exists(f)) return File.ReadAllText(f);
            }
            catch { }
            return null;
        }
    }

    class SdkSection { public string Slot; public Dictionary<string, string> Props = new Dictionary<string, string>(); }

    // SDK-derived per-ini-section data: resolved ability slot + config-property types.
    // Built from the UE3 SDK dump (TgGame_classes.hpp) by _work/sdk_gen.py -> data/sdkdata.json.
    static class SdkData
    {
        static readonly Dictionary<string, SdkSection> _map =
            new Dictionary<string, SdkSection>(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            string json = ReadEmbedded("sdkdata.json") ?? ReadFile("sdkdata.json");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var sec in doc.RootElement.EnumerateObject())
                {
                    var si = new SdkSection();
                    if (sec.Value.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.String)
                        si.Slot = sl.GetString();
                    if (sec.Value.TryGetProperty("props", out var pr) && pr.ValueKind == JsonValueKind.Object)
                        foreach (var p in pr.EnumerateObject())
                            si.Props[p.Name] = p.Value.GetString();
                    _map[sec.Name] = si;
                }
            }
            catch { }
        }

        // .ini sections carry the "TgGame." prefix; sdkdata.json keys do not — normalize before lookup.
        static string Norm(string section)
            => section != null && section.StartsWith("TgGame.", StringComparison.OrdinalIgnoreCase) ? section.Substring(7) : section;

        public static string Slot(string section)
            => section != null && _map.TryGetValue(Norm(section), out var s) ? s.Slot : null;

        public static string TypeOf(string section, string key)
            => section != null && _map.TryGetValue(Norm(section), out var s) && s.Props.TryGetValue(key, out var t) ? t : null;

        public static SdkSection Get(string section)
            => section != null && _map.TryGetValue(Norm(section), out var s) ? s : null;

        static string ReadEmbedded(string suffix)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (res != null)
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var r = new StreamReader(s))
                        return r.ReadToEnd();
            }
            catch { }
            return null;
        }

        static string ReadFile(string name)
        {
            try { string f = Path.Combine(Theme.AppDir, "data", name); if (File.Exists(f)) return File.ReadAllText(f); }
            catch { }
            return null;
        }
    }

    class SdkMember { public string Name, Type, Flags, DeclaredIn; public bool Cfg; }
    class SdkClassRow { public string Sec, Slot, Cat, Cls; public bool Ini; }
    class SdkClassDef { public string Parent; public List<SdkMember> Members = new List<SdkMember>(); }

    // Per-god leaf classes + a shared class map; member lists are composed by walking the inheritance chain,
    // so the inspector shows a god's FULL property surface (own + inherited), not just its own members.
    static class SdkInspect
    {
        static readonly Dictionary<string, List<SdkClassRow>> _gods =
            new Dictionary<string, List<SdkClassRow>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, SdkClassDef> _classes =
            new Dictionary<string, SdkClassDef>(StringComparer.Ordinal);

        public static void Load()
        {
            string json = Read("sdkinspect.json");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                foreach (var god in root.GetProperty("gods").EnumerateObject())
                {
                    var rows = new List<SdkClassRow>();
                    foreach (var cls in god.Value.EnumerateArray())
                        rows.Add(new SdkClassRow
                        {
                            Sec = cls.GetProperty("sec").GetString(),
                            Cat = cls.TryGetProperty("cat", out var ct) ? ct.GetString() : "",
                            Slot = cls.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.String ? sl.GetString() : null,
                            Ini = cls.TryGetProperty("ini", out var iv) && iv.GetInt32() == 1,
                            Cls = cls.GetProperty("cls").GetString(),
                        });
                    _gods[god.Name] = rows;
                }
                foreach (var c in root.GetProperty("classes").EnumerateObject())
                {
                    var def = new SdkClassDef { Parent = c.Value.GetProperty("p").GetString() };
                    foreach (var m in c.Value.GetProperty("m").EnumerateArray())
                        def.Members.Add(new SdkMember
                        {
                            Name = m[0].GetString(), Type = m[1].GetString(),
                            Flags = m[2].GetString(), Cfg = m[3].GetInt32() == 1, DeclaredIn = c.Name,
                        });
                    _classes[c.Name] = def;
                }
            }
            catch { }
        }

        public static List<SdkClassRow> Get(string godBase)
            => godBase != null && _gods.TryGetValue(godBase, out var r) ? r : null;

        // Full member list for a class: own first, then inherited up the chain (deduped by name).
        public static List<SdkMember> ChainMembers(string cls)
        {
            var list = new List<SdkMember>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenCls = new HashSet<string>(StringComparer.Ordinal);
            while (cls != null && _classes.TryGetValue(cls, out var def) && seenCls.Add(cls))
            {
                foreach (var m in def.Members)
                    if (seenNames.Add(m.Name)) list.Add(m);
                cls = def.Parent;
            }
            return list;
        }

        static string Read(string suffix)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (res != null)
                    using (var s = asm.GetManifestResourceStream(res))
                    using (var rd = new StreamReader(s))
                        return rd.ReadToEnd();
            }
            catch { }
            try { string f = Path.Combine(Theme.AppDir, "data", suffix); if (File.Exists(f)) return File.ReadAllText(f); }
            catch { }
            return null;
        }
    }

    // Minimal client for the official Hi-Rez SMITE 1 API (api.smitegame.com). Read-only player stats.
    // Credentials are user-supplied and obtained officially; an api.txt next to the exe ("devId,authKey")
    // overrides the embedded pair (note: embedded keys are extractable from a shared exe).
    static class SmiteApi
    {
        const string Base = "https://api.smitegame.com/smiteapi.svc";
        // The default dev id / auth key are stored XOR-obfuscated (LCG keystream + base64) and reassembled at
        // runtime, so the raw key never appears in source — this defeats secret scanners and casual scraping.
        // It is NOT real secrecy: a running client must rebuild the key to sign requests, so a determined user
        // can still recover it. Use your OWN Hi-Rez dev key by dropping an api.txt ("devId,authKey") next to the
        // exe or in Documents\Smite Inspector (handled below) — that override is the supported way.
        static string _dev = Deob(0x6F2A91C3, "aVJvLg==");
        static string _auth = Deob(0x13C7E5A9, "icrHqW2WNJZ" + "C4VLrZrSlSx" + "npX866biqya" + "gWMaP40Fww=");
        static readonly HttpClient _http = MakeHttp();
        static string _sid;
        static DateTime _sidUtc = DateTime.MinValue;

        // Free, local-only tally of how many requests THIS app has made today (resets at local midnight). Costs no
        // extra API calls — it just counts the ones we already make — so the Friend List poller can throttle itself
        // before it nears the key's daily request cap. (It can't see calls from other tools sharing the same key.)
        static int _reqCount;
        static DateTime _reqDay = DateTime.MinValue;
        static readonly object _reqLock = new object();   // the harvester (thread-pool) + UI poller both count concurrently
        // Roll the day on READ as well as on count, so a poller that throttled itself to silence still un-sticks at
        // midnight (the reset can't depend on an outbound call, or hitting the cap would freeze it permanently).
        static void RollDay() { var today = DateTime.Now.Date; if (today != _reqDay) { _reqDay = today; _reqCount = 0; } }   // caller holds _reqLock
        public static int RequestsToday { get { lock (_reqLock) { RollDay(); return _reqCount; } } }
        static void CountRequest() { lock (_reqLock) { RollDay(); _reqCount++; } }

        static HttpClient MakeHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.Add("User-Agent", "Smite1Inspector/1.0");
            // optional override so the key needn't live in a shared binary (Documents\Smite Inspector first, then the exe folder)
            try
            {
                string f = Path.Combine(Theme.DataDir, "api.txt");
                if (!File.Exists(f)) f = Path.Combine(Theme.AppDir, "api.txt");
                if (File.Exists(f))
                {
                    var parts = File.ReadAllText(f).Trim().Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) { _dev = parts[0].Trim(); _auth = parts[1].Trim(); }
                }
            }
            catch { }
            return h;
        }

        // Reassemble an obfuscated default credential: base64 → XOR against an LCG keystream seeded by `seed`.
        static byte[] Pad(int seed, int n)
        {
            var p = new byte[n];
            long s = seed & 0x7fffffff;
            for (int i = 0; i < n; i++) { s = (s * 1103515245 + 12345) & 0x7fffffff; p[i] = (byte)((s >> 16) & 0xFF); }
            return p;
        }
        static string Deob(int seed, string b64)
        {
            // Never throw from here: this runs in a static field initializer, so a malformed blob would surface as a
            // TypeInitializationException on first use and brick every API call — even when a valid api.txt override
            // exists. Degrade to "" instead; MakeHttp()'s api.txt read still applies and signing fails cleanly.
            try
            {
                var d = Convert.FromBase64String(b64);
                var p = Pad(seed, d.Length);
                for (int i = 0; i < d.Length; i++) d[i] ^= p[i];
                return Encoding.UTF8.GetString(d);
            }
            catch { return ""; }
        }

        static string Md5(string s)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(32);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        static string Ts() => DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        static readonly System.Threading.SemaphoreSlim _sessGate = new System.Threading.SemaphoreSlim(1, 1);
        static async Task<string> EnsureSession()
        {
            if (_sid != null && (DateTime.UtcNow - _sidUtc).TotalMinutes < 13) return _sid;
            // serialize creation so a burst of cold callers (harvester + UI poller at startup) doesn't spawn N sessions
            // and burn the daily session cap (thundering herd). Re-check inside the gate — the first caller fills _sid.
            await _sessGate.WaitAsync();
            try
            {
                if (_sid != null && (DateTime.UtcNow - _sidUtc).TotalMinutes < 13) return _sid;
                string t = Ts();
                string url = Base + "/createsessionJson/" + _dev + "/" + Md5(_dev + "createsession" + _auth + t) + "/" + t;
                CountRequest();
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
                if (doc.RootElement.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String && sid.GetString().Length > 0)
                {
                    _sid = sid.GetString(); _sidUtc = DateTime.UtcNow; return _sid;
                }
                string msg = doc.RootElement.TryGetProperty("ret_msg", out var rm) ? rm.GetString() : "no session";
                throw new Exception("SMITE API session failed: " + msg);
            }
            finally { _sessGate.Release(); }
        }

        // Detects the intermittent "concatenation blob" the API returns under heavy concurrent load: a single
        // row whose fields are space-joined across all players. player ids never contain spaces, so two digit
        // runs separated by whitespace inside a *Id field is an unambiguous blob signature.
        static bool LooksLikeBlob(string raw)
            => !string.IsNullOrEmpty(raw)
               && System.Text.RegularExpressions.Regex.IsMatch(raw, "\"(playerId|ActivePlayerId|Match)\"\\s*:\\s*\"?\\d+\\s+\\d+");

        // Calls a data method and returns the raw JSON string. args are appended (URL-escaped) to the path.
        // Retries on the SAME session when the response comes back as the concatenation blob (a plain retry
        // usually clears it) — deliberately not refreshing the session, so we don't burn the daily session cap.
        public static async Task<string> Call(string method, params string[] args)
        {
            string sid = await EnsureSession();
            string raw = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string t = Ts();
                string url = Base + "/" + method + "Json/" + _dev + "/" + Md5(_dev + method + _auth + t) + "/" + sid + "/" + t;
                foreach (var a in args) url += "/" + Uri.EscapeDataString(a);
                CountRequest();
                raw = await _http.GetStringAsync(url);
                if (!LooksLikeBlob(raw)) return raw;
                await Task.Delay(300);
            }
            return raw;   // give back the last (still-blobbed) response; callers tolerate a bad parse
        }
    }

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
        }
        static void RebuildIndexes() { _byCompanion = new(); _byGod = new(); _byClan = new(); _byNeighbor = new(); foreach (var e in _byId.Values) IndexEntry(e); }
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
                    if (p.Players != null) foreach (var e in p.Players) if (e != null && !string.IsNullOrEmpty(e.Id)) { e.GodMastery ??= new(); e.GodSkin ??= new(); e.Companions ??= new(); e.NeighborCounts ??= new(); e.RankTier ??= new(); e.RankMmr ??= new(); _byId[e.Id] = e; }
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
                    Players = _byId.Values.Select(e => new Entry { Id = e.Id, Name = e.Name, Portal = e.Portal, ClanId = e.ClanId, Clan = e.Clan, Level = e.Level, GodMastery = new Dictionary<string, int>(e.GodMastery), GodSkin = new Dictionary<string, string>(e.GodSkin), Companions = new List<string>(e.Companions), NeighborCounts = new Dictionary<string, int>(e.NeighborCounts), RankTier = new Dictionary<string, int>(e.RankTier), RankMmr = new Dictionary<string, int>(e.RankMmr), LastSeen = e.LastSeen, Seen = e.Seen }).ToList(),
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
            lock (_lock) { _byId = new(); _live = new(); _byCompanion = new(); _byGod = new(); _byClan = new(); _byNeighbor = new(); _dirty = true; }
            Save(true);
        }

        // Record a VISIBLE player. god/mastery are optional (0/"" when learning from a profile/friend rather than a match row).
        public static void Learn(string id, string name, int portal, int clanId, string clan, int level, string god, int godMastery, IEnumerable<string> companions = null, IEnumerable<string> neighbors = null, string skinId = null, IEnumerable<(string queue, int tier, int mmr)> ranked = null)
        {
            if (string.IsNullOrEmpty(id) || id == "0" || string.IsNullOrEmpty(name)) return;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out var e)) { e = new Entry { Id = id, Seen = 0 }; _byId[id] = e; }
                e.Name = name;
                if (portal != 0) e.Portal = portal;
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

    class Param
    {
        public string Key, Value, Original, Comment, Prefix, Section;
        public int LineIndex;
        public bool IsNew;   // added from the SDK list; gets inserted into the .ini on Apply
        public int Source;   // 0 = original ini, 1 = "Add value" (purple), 2 = SDK Inspector (yellow)
    }

    class GodFile
    {
        public string FileName, Base, Name, Text, Path;
        public bool NonGod;
        public int ParamCount;
    }

    // A named group of account names for the Encounters tab — e.g. all the smurfs of one person (persisted to enc_presets.json).
    class EncPreset { public string Name { get; set; } = ""; public List<string> Accounts { get; set; } = new(); }
    class EncPresetFile { public List<EncPreset> Presets { get; set; } = new(); }

    // A saved favorite player (persisted to favorites.json next to the exe).
    class FavPlayer
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public int Portal { get; set; }
        public string Note { get; set; } = "";   // Friend List: free-text comment shown in the preview panel
    }

    // A user-assigned nickname for a privacy-hidden player, keyed by the fingerprint the privacy flag leaves
    // on a match row (clan + account level + total mastery). Matched fuzzily since level/mastery grow over time.
    class HiddenTag
    {
        public int ClanId { get; set; }
        public string Clan { get; set; } = "";
        public int Level { get; set; }
        public int Mastery { get; set; }
        public string Nick { get; set; } = "";
        public string Note { get; set; } = "";
        // accumulated sighting signals that make re-recognition robust (the "strong algorithm")
        public List<string> Companions { get; set; } = new();   // player_ids of NAMED players this hidden player has partied with
        public List<string> Gods { get; set; } = new();         // gods seen on this hidden player
        public int Seen { get; set; }                            // number of times matched (confidence)
        public string LastSeen { get; set; } = "";              // ISO-ish timestamp of the last sighting
        public string Tagged { get; set; } = "";                // date this tag was first created (for "sort by date tagged")
    }

    // User preferences, persisted to settings.json next to the exe.
    class AppSettings
    {
        public int StartupTab { get; set; }   // 0 = God Inspector, 1 = Player Tracker
        public int TimeFormat { get; set; }   // 0 = system default, 1 = 12-hour, 2 = 24-hour
        public bool ShowFriendUptime { get; set; }   // Friend List: show how long online friends have been logged in
        public bool CheckUpdates { get; set; } = true;   // check GitHub for a newer release at startup (default on)
        public bool AutoUpdate { get; set; }             // download + install new versions without asking (default off)
        public string SkippedVersion { get; set; } = "";  // a version the user said "no" to → don't re-prompt for it
        public bool BetaChannel { get; set; }            // opt in to pre-release (beta) builds when checking for updates
        public string AppliedTag { get; set; } = "";     // full tag of the last update applied in-app (so iterative betas of the same numeric version are still offered)
        public bool RevealHidden { get; set; }   // EXPERIMENT: auto-reveal privacy-hidden players from the learned name DB
        public bool Harvest { get; set; }        // EXPERIMENT: run the background name harvester to grow the DB at scale
        public bool RankedReveal { get; set; }   // EXPERIMENT (2026-06-25): de-anon hidden RANKED players via the god-leaderboard id-leak → smite.guru name (network-heavy, opt-in)
        public bool CommunityTags { get; set; }  // EXPERIMENT: share + use crowdsourced hidden-player tags (TagSync)
        public bool LogReveal { get; set; } = true;   // EXPERIMENT: EXACT reveal of hidden players from the local game logs (GameLog) — default on
        public string MyProfileId { get; set; } = "";    // "My profile" tab: the user's own pinned account
        public string MyProfileName { get; set; } = "";
        public int MyProfilePortal { get; set; }
    }

    // One entry in the Codex table-of-contents tree (a section H2 or an indented sub-section H3).
    sealed class TocNode
    {
        public string Title;
        public Control Anchor;        // the header Label in the doc flow → the ScrollControlIntoView jump target
        public bool IsSection;        // true = top-level section, false = sub-section
        public TocNode Parent;        // null for sections
        public TocRow Row;            // the rendered sidebar row (back-ref)
        public bool Expanded = true;  // sections start expanded
        public readonly List<TocNode> Children = new();
    }
    // A single owner-drawn TOC row (chevron + label + accent bar painted in one Paint; no child controls).
    sealed class TocRow : Panel
    {
        public bool Hovered;
        public TocRow() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    // Flat checkbox: white square box with a black check mark when ticked (sharp, on-theme).
    class FlatCheck : CheckBox
    {
        public int BoxSize = 15;
        public FlatCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        public override Size GetPreferredSize(Size proposed)
        {
            var ts = TextRenderer.MeasureText(Text, Font);
            return new Size(BoxSize + BoxSize / 2 + ts.Width + 4, Math.Max(BoxSize, ts.Height));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int bs = BoxSize;
            var box = new Rectangle(0, (Height - bs) / 2, bs, bs);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, box);   // white box
            if (Checked)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.Black, Math.Max(2f, bs / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.Left + bs * 0.22f, box.Top + bs * 0.52f),
                        new PointF(box.Left + bs * 0.42f, box.Top + bs * 0.72f),
                        new PointF(box.Left + bs * 0.78f, box.Top + bs * 0.28f),
                    });   // black ✓
                g.SmoothingMode = SmoothingMode.None;
            }
            var tr = new Rectangle(box.Right + bs / 2, 0, Width - box.Right - bs / 2, Height);
            TextRenderer.DrawText(g, Text, Font, tr, ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // One row in a PlayerList (a search hit, a favorite, a friend, or a section header).
    class PlayerRow
    {
        public string Name = "", Id = "";
        public int Portal;
        public bool Priv, Deletable, Savable;                    // Deletable = trash glyph; Savable = ☆ add-to-favorites glyph
        public bool Header;                                      // a clickable section divider (Name = caption)
        public bool Collapsed;                                   // header collapse state (▶ vs ▼)
        public string Plat = "";                                 // short platform code, e.g. "STEAM"
        public Color PlatCol = Color.FromArgb(70, 70, 70);       // platform brand colour
        public string Status = "";                               // Friend List status text (e.g. "In Game"), drawn with a dot
        public Color StatusCol = Color.FromArgb(110, 110, 110);
        public string Extra = "";                                // secondary right-aligned text (e.g. last-login on the Friend List)
        public string Key = "";                                  // stable key for headers (collapse tracking)
        public DateTime LastLogin = DateTime.MinValue;           // for Friend List "last seen" sort
        public int StatusSort = 9;                               // for Friend List status sort (0 = in game … higher = offline)
        public string Avatar = "";                               // in-game avatar/icon URL (getplayer Avatar_URL) for the preview panel
        // Friend List live-poller scheduling (runtime only — never serialized to friendlist.json):
        public int Tier = 1;                                     // refresh priority: 0 god-select · 1 online/lobby · 2 in-game · 3 offline (backs off by days idle) · 4 unknown/error
        public DateTime NextDueUtc = DateTime.MinValue;          // when getplayerstatus is next eligible (MinValue = due now)
        public DateTime NextDetailUtc = DateTime.MinValue;       // when the slow getplayer (name/avatar/last-login) is next eligible
        public bool Polling;                                     // in-flight guard so a slow await spanning ticks can't double-schedule a row
        public int ErrBackoff;                                   // consecutive getplayerstatus failures → exponential backoff
        public static PlayerRow Section(string caption, string key = "") => new PlayerRow { Header = true, Name = caption, Key = key };
    }

    // Owner-drawn list used for search results, favorites and friends: a coloured platform
    // chip + the player name (+ "(private)") + an optional trash button (favorites only).
    class PlayerList : ListBox
    {
        readonly List<PlayerRow> _rows = new List<PlayerRow>();
        // shared row-background brushes (fixed colors) — cached so DrawRow doesn't allocate a SolidBrush per row per paint
        static readonly SolidBrush _brSel = new SolidBrush(Color.FromArgb(50, 50, 60)), _brHov = new SolidBrush(Color.FromArgb(34, 34, 42)),
            _brNorm = new SolidBrush(Color.FromArgb(20, 20, 20)), _brAccent = new SolidBrush(Color.FromArgb(193, 30, 31)), _brHdrBg = new SolidBrush(Color.FromArgb(13, 13, 13));
        Font _glyph, _chip, _hdr;
        int _hoverAction = -1;   // index of the row whose action glyph (trash/☆) the cursor is over → highlight it
        int _hoverRow = -1;      // index of the row the cursor is over (whole-row hover highlight)
        Bitmap _buf;             // off-screen double-buffer (WM_PAINT) — kills owner-draw flicker on live re-sorts

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore; public int fIncUpdate; [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        public event Action<PlayerRow> Activated;
        public event Action<PlayerRow> Deleted;
        public event Action<PlayerRow> Saved;          // ☆ clicked on a Savable row (add to favorites)
        public event Action<PlayerRow> HeaderClicked;  // a section header clicked (toggle collapse)

        public PlayerList()
        {
            // OwnerDrawFixed is load-bearing even though we paint in WM_PAINT (not WM_DRAWITEM): it makes ItemHeight
            // effective, which IndexFromPoint / GetItemRectangle / TopIndex all key off for hit-testing and scrolling.
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.FixedSingle;
            IntegralHeight = false;
            BackColor = Color.FromArgb(20, 20, 20);
            ForeColor = Color.White;
        }
        int Sc(int v) => v * DeviceDpi / 96;
        int TrashW => Sc(34);
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = Sc(30); }

        public IReadOnlyList<PlayerRow> Rows => _rows;
        public bool AutoSelectFirst = true;   // pickers default-select row 0 (keyboard nav); the Friend List opts out so no row looks "selected"
        public void SetRows(IEnumerable<PlayerRow> rows)
        {
            if (IsDisposed) return;   // a queued LiveSort BeginInvoke can fire during teardown — don't touch a dead handle
            // Preserve the selection BY IDENTITY across a re-sort, and update Items IN PLACE when the count is
            // unchanged (e.g. a live status re-sort) — a blanket Items.Clear()+re-add makes the native ListBox
            // blank then refill (the bulk of the refresh flicker).
            var prevSel = (SelectedIndex >= 0 && SelectedIndex < _rows.Count) ? _rows[SelectedIndex] : null;
            _rows.Clear(); _rows.AddRange(rows);
            BeginUpdate();
            if (Items.Count == _rows.Count)
                for (int i = 0; i < _rows.Count; i++) Items[i] = _rows[i].Name ?? "";
            else { Items.Clear(); foreach (var r in _rows) Items.Add(r.Name ?? ""); }   // ListBox.Items.Add(null) throws
            EndUpdate();
            if (_rows.Count == 0) { _hoverAction = _hoverRow = -1; Invalidate(); return; }
            int ns = prevSel != null ? _rows.IndexOf(prevSel) : -1;
            SelectedIndex = ns >= 0 ? ns : (AutoSelectFirst ? 0 : -1);          // don't fabricate a selection where there was none
            RefreshHover();   // rows moved under a possibly-stationary cursor → re-hit-test which glyph is hovered
            Invalidate();
        }
        // Repaint just one row (its status/name changed) instead of the whole control — flicker-free incremental update.
        public void UpdateRow(PlayerRow r)
        {
            if (!IsHandleCreated) return;
            int i = _rows.IndexOf(r);
            if (i < 0) return;
            var rc = GetItemRectangle(i);
            if (rc.IntersectsWith(ClientRectangle)) Invalidate(rc);
        }

        // Draws one row into g at bounds. Called from the double-buffered WM_PAINT (PaintBuffered), not WM_DRAWITEM.
        void DrawRow(Graphics g, int index, Rectangle bounds)
        {
            var r = _rows[index];

            if (r.Header)   // clickable section divider: ▼/▶ collapse arrow + accent caption
            {
                g.FillRectangle(_brHdrBg, bounds);
                if (_hdr == null) _hdr = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
                var arrow = new Rectangle(bounds.Left + Sc(6), bounds.Top, Sc(16), bounds.Height);
                TextRenderer.DrawText(g, r.Collapsed ? "▶" : "▼", Font, arrow, Color.FromArgb(193, 30, 31), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                var hrect = new Rectangle(bounds.Left + Sc(24), bounds.Top, bounds.Width - Sc(26), bounds.Height);
                TextRenderer.DrawText(g, r.Name, _hdr, hrect, Color.FromArgb(193, 30, 31), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            bool sel = index == SelectedIndex;
            bool hov = index == _hoverRow && !sel;   // whole-row hover highlight
            g.FillRectangle(sel ? _brSel : hov ? _brHov : _brNorm, bounds);
            if (sel) g.FillRectangle(_brAccent, bounds.Left, bounds.Top, Sc(3), bounds.Height);   // red accent bar = selected
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_chip == null) _chip = new Font(Font.FontFamily, Math.Max(6f, Font.Size - 1.5f), FontStyle.Bold);
            int pad = Sc(6);
            var chip = new Rectangle(bounds.Left + pad, bounds.Top + Sc(5), Sc(58), bounds.Height - Sc(10));
            using (var cb = new SolidBrush(r.PlatCol)) g.FillRectangle(cb, chip);
            TextRenderer.DrawText(g, r.Plat, _chip, chip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int nameX = chip.Right + Sc(10);
            int rEdge = bounds.Right;
            bool hasAction = r.Deletable || r.Savable;
            int rightLimit = rEdge - (hasAction ? TrashW : Sc(6));

            if (!string.IsNullOrEmpty(r.Status))   // Friend List: status dot + text, right-aligned
            {
                var sz = TextRenderer.MeasureText(r.Status, Font);
                int stX = rightLimit - sz.Width - Sc(4);
                int dotX = stX - Sc(15), dotY = bounds.Top + bounds.Height / 2 - Sc(4);
                using (var db = new SolidBrush(r.StatusCol)) g.FillEllipse(db, dotX, dotY, Sc(9), Sc(9));
                TextRenderer.DrawText(g, r.Status, Font, new Rectangle(stX, bounds.Top, sz.Width + Sc(6), bounds.Height), r.StatusCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                rightLimit = dotX - Sc(8);
            }
            if (!string.IsNullOrEmpty(r.Extra))   // e.g. last-login, dim, to the left of the status
            {
                var ez = TextRenderer.MeasureText(r.Extra, _chip);
                int exX = rightLimit - ez.Width - Sc(4);
                TextRenderer.DrawText(g, r.Extra, _chip, new Rectangle(exX, bounds.Top, ez.Width + Sc(6), bounds.Height), Color.FromArgb(120, 120, 120), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                rightLimit = exX - Sc(8);
            }

            var nameRect = new Rectangle(nameX, bounds.Top, rightLimit - nameX, bounds.Height);
            string nm = r.Name + (r.Priv ? "   (private)" : "");
            TextRenderer.DrawText(g, nm, Font, nameRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (hasAction)
            {
                if (_glyph == null) { try { _glyph = new Font("Segoe MDL2 Assets", Font.Size); } catch { _glyph = Font; } }
                // trash glyph drawn inline below (char-code form). original:"✕" : "";   // Segoe MDL2 trash, else a ✕
                var tr = new Rectangle(rEdge - TrashW, bounds.Top, TrashW, bounds.Height);
                bool hot = index == _hoverAction;   // cursor is over this row's action glyph → highlight it
                string glyph; Color gc;
                var hotRect = new Rectangle(tr.Left + Sc(2), tr.Top + Sc(4), tr.Width - Sc(4), tr.Height - Sc(8));
                if (r.Deletable)
                {
                    glyph = _glyph == Font ? "X" : ((char)0xE74D).ToString();                                  // trash
                    if (hot) RoundRect(g, hotRect, Sc(6), Color.FromArgb(110, 210, 70, 70), Color.FromArgb(200, 225, 95, 95));
                    gc = hot ? Color.FromArgb(255, 150, 150) : Color.FromArgb(205, 90, 90);
                }
                else
                {
                    glyph = _glyph == Font ? "+" : ((char)0xE734).ToString();                                  // star = add to favorites
                    if (hot) RoundRect(g, hotRect, Sc(6), Color.FromArgb(80, 230, 190, 60), Color.FromArgb(190, 235, 200, 80));
                    gc = hot ? Color.FromArgb(255, 215, 90) : Color.FromArgb(214, 170, 40);
                }
                TextRenderer.DrawText(g, glyph, _glyph, tr, gc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexFromPoint(e.Location);
            if (i < 0 || i >= _rows.Count) { base.OnMouseDown(e); return; }
            var r = _rows[i];
            if (r.Header) { HeaderClicked?.Invoke(r); return; }   // toggle collapse
            SelectedIndex = i; _hoverRow = i;
            Invalidate(); Update();   // show the selection immediately — don't wait for the next mouse-move
            if ((r.Deletable || r.Savable) && e.X >= ClientSize.Width - TrashW)   // action glyph at the right edge
            { if (r.Deletable) Deleted?.Invoke(r); else Saved?.Invoke(r); return; }
            base.OnMouseDown(e);
            if (!string.IsNullOrEmpty(r.Id) && r.Id != "0") Activated?.Invoke(r);                   // hidden/no-id rows aren't loadable
        }

        // The row index whose action glyph (right edge) the point falls on, else -1.
        int ActionIndexAt(Point p)
        {
            int i = IndexFromPoint(p);
            if (i < 0 || i >= _rows.Count || _rows[i].Header) return -1;
            if (!(_rows[i].Deletable || _rows[i].Savable)) return -1;
            return p.X >= ClientSize.Width - TrashW ? i : -1;
        }
        // The non-header row index under the point, else -1 (for whole-row hover highlight).
        int RowAt(Point p)
        {
            int i = IndexFromPoint(p);
            return (i >= 0 && i < _rows.Count && !_rows[i].Header) ? i : -1;
        }
        bool Loadable(int i) => i >= 0 && i < _rows.Count && !string.IsNullOrEmpty(_rows[i].Id) && _rows[i].Id != "0";
        void SetHover(int row, int action)
        {
            if (row == _hoverRow && action == _hoverAction) return;
            _hoverRow = row; _hoverAction = action;
            Invalidate();   // buffered paint → a full repaint is flicker-free
        }
        // Re-evaluate hover from the CURRENT cursor (after a re-sort moves rows under a stationary cursor).
        public void RefreshHover()
        {
            int row = -1, act = -1;
            if (IsHandleCreated) { var p = PointToClient(Cursor.Position); if (ClientRectangle.Contains(p)) { row = RowAt(p); act = ActionIndexAt(p); } }
            SetHover(row, act);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int act = ActionIndexAt(e.Location), row = RowAt(e.Location);
            Cursor = (act >= 0 || Loadable(row)) ? Cursors.Hand : Cursors.Default;
            SetHover(row, act);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            SetHover(-1, -1);
        }

        static void RoundRect(Graphics g, Rectangle r, int rad, Color fill, Color border)
        {
            using var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, rad, rad, 180, 90);
            p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            p.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
            p.CloseFigure();
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (border.A > 0) using (var pen = new Pen(border)) g.DrawPath(pen, p);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A, WM_KEYDOWN = 0x0100;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }     // we paint the whole client in WM_PAINT
            if (m.Msg == WM_PAINT && PaintBuffered()) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);   // PaintBuffered returns false only if BeginPaint failed → let base validate (no repaint loop)
            // The native ListBox scrolls by bit-blitting existing pixels and invalidating only the exposed strip; that
            // WM_PAINT is low-priority and gets deferred while wheel/drag input keeps coming, so scrolling looks frozen
            // until input stops. Force the exposed strip to paint NOW.
            if (m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL || m.Msg == WM_KEYDOWN) Update();
        }
        // Double-buffered repaint: draw the visible rows to an off-screen bitmap, then blit (the DC is clipped to the
        // update region, so a scroll only repaints the exposed strip from the buffer — the native bit-blit already
        // shifted the rest correctly). This kills owner-draw flicker (a native OwnerDraw ListBox paints each item
        // straight to the screen DC, so a live re-sort visibly redraws row-by-row).
        bool PaintBuffered()
        {
            var hdc = BeginPaint(Handle, out var ps);
            if (hdc == IntPtr.Zero) return false;   // BeginPaint failed (GDI exhausted / dying window) — don't validate; let base try
            try
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w > 0 && h > 0)
                {
                    if (_buf == null || _buf.Width != w || _buf.Height != h) { _buf?.Dispose(); _buf = new Bitmap(w, h); }
                    using (var g = Graphics.FromImage(_buf))
                    {
                        g.Clear(BackColor);
                        int top = Math.Max(0, TopIndex), ih = Math.Max(1, ItemHeight);
                        for (int i = top; i < _rows.Count; i++)
                        {
                            int y = (i - top) * ih;
                            if (y >= h) break;
                            try { DrawRow(g, i, new Rectangle(0, y, w, ih)); } catch { }   // one bad row must not drop the whole frame
                        }
                    }
                    using (var screen = Graphics.FromHdc(hdc)) screen.DrawImageUnscaled(_buf, 0, 0);
                }
            }
            catch { }
            finally { EndPaint(Handle, ref ps); }
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (SelectedIndex >= 0 && SelectedIndex < _rows.Count)
            {
                var r = _rows[SelectedIndex];
                if (!r.Header)
                {
                    if (e.KeyCode == Keys.Enter && !string.IsNullOrEmpty(r.Id) && r.Id != "0") { e.SuppressKeyPress = true; Activated?.Invoke(r); return; }
                    if (e.KeyCode == Keys.Delete && r.Deletable) { e.SuppressKeyPress = true; Deleted?.Invoke(r); return; }
                }
            }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (_glyph != null && _glyph != Font) _glyph.Dispose(); _chip?.Dispose(); _hdr?.Dispose(); _buf?.Dispose(); }
            base.Dispose(disposing);
        }
    }

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

    // ===================== Whispers: standalone (game-closed) MCTS chat =====================
    // One persisted whisper conversation with a player.
    // St: "" normal · "queued" (sent before the engine finished logging in — still cancellable) · "cancelled".
    sealed class WMsg  { public long T { get; set; } public string Dir { get; set; } = "in"; public string Text { get; set; } = ""; public string St { get; set; } = ""; }
    // Pin = sticks to the top of the list. Hidden = soft-deleted (removed from the list but history kept; reopening restores it).
    sealed class WConv { public string Key { get; set; } = ""; public string Display { get; set; } = ""; public string Id { get; set; } = ""; public long Last { get; set; } public List<WMsg> Msgs { get; set; } = new(); public bool Pin { get; set; } public bool Hidden { get; set; } }
    // A flicker-free panel (double-buffered) — used for the conversation list + rows so in-place updates don't repaint-flash.
    sealed class BufPanel : Panel { public BufPanel() { DoubleBuffered = true; } }

    // Spawns + manages the headless Probe5 MCTS engine and relays messages through the WHISPER_DIR file pair.
    sealed class WhisperEngine
    {
        readonly string _exe, _dir, _relay;
        System.Diagnostics.Process _proc;
        System.Threading.Thread _worker;
        volatile bool _run;
        long _inPos;
        readonly object _outLock = new object();
        readonly Queue<string> _outQ = new Queue<string>();
        public event Action<string> Status;          // "connecting" | "connected" | "stopped"
        public event Action<string, string> Inbound; // (sender, text)
        public event Action<string, bool> Presence;  // (player name, online) — from REQUEST_PLAYER_INFO responses
        long _presPos;
        public string State { get; private set; } = "stopped";

        // Login method: "steam" (default — uses the Steam SMITE ticket, so Steam shows the game running) or
        // "hirez" (Hi-Rez username/password — no Steam, no "playing" status, and skips the EOS/Steam startup waits).
        string _loginMode = "steam", _stdUser = "", _stdPass = "";
        public void SetLogin(string mode, string user, string pass)
        {
            _loginMode = (mode == "hirez") ? "hirez" : "steam";
            _stdUser = user ?? ""; _stdPass = pass ?? "";
        }
        public string LoginMode { get { return _loginMode; } }

        public WhisperEngine(string exe, string relayDir)
        {
            _exe = exe; _dir = Path.GetDirectoryName(exe); _relay = relayDir;
            // Backstop: if the app exits without FormClosing (rare graceful paths), still kill the child engine.
            try { AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Stop(); } catch { } }; } catch { }
        }
        public bool Running { get { try { return _proc != null && !_proc.HasExited; } catch { return false; } } }

        // A crash or force-kill of a previous app run leaves Probe5.exe orphaned — and several orphans all log in as the
        // same account, so the chat server CLOSE_CONNECTIONs the duplicates and whisper delivery silently dies. Probe5 is
        // our private engine (unique name), so clearing every instance before we spawn a fresh one is safe.
        static void KillStaleEngines()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Probe5"))
                { try { p.Kill(); p.WaitForExit(1500); } catch { } finally { try { p.Dispose(); } catch { } } }
            }
            catch { }
        }

        public void Start()
        {
            if (Running) return;
            KillStaleEngines();
            try { Directory.CreateDirectory(_relay); } catch { }
            try { File.Delete(Path.Combine(_relay, "whisper_out.txt")); } catch { }
            try { File.Delete(Path.Combine(_relay, "whisper_in.txt")); } catch { }
            try { File.Delete(Path.Combine(_relay, "presence.tsv")); } catch { }
            _presPos = 0;
            _inPos = 0;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _exe,
                Arguments = "-pid=017 -steam -anon -seekfreeloadingpcconsole 5",
                WorkingDirectory = _dir,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var e = psi.EnvironmentVariables;
            e["ACFLAG"] = "1"; e["SENDID"] = "0"; e["CLIENTTYPE"] = "1";
            e["GAMETICKET"] = "0"; e["NORECON"] = "0"; e["PUMPPATCH"] = "0"; e["POLLFL"] = "1";
            e["MESSENGER"] = "1"; e["CHATCAP"] = "1"; e["FULLCONFIGS"] = "1"; e["DIAGFIELDS"] = "0"; e["STORELOG"] = "0";
            e["CHATDIAG"] = "0";
            e["KEEPSECS"] = "86400"; e["WHISPER_DIR"] = _relay; e["WHISPER_TO"] = "";
            if (_loginMode == "hirez")
            {
                // Hi-Rez username/password: no Steam ticket + no in-process EOS (so Steam shows nothing and connect is faster).
                e["STD"] = "1"; e["STDUSER"] = _stdUser; e["STDPASS"] = _stdPass;
                e["LOGINFIX"] = "0"; e["EOSINPROC"] = "0";
            }
            else
            {
                e["STD"] = "0"; e["LOGINFIX"] = "1"; e["EOSINPROC"] = "1";
            }
            // VERHEX / SMITEBIN intentionally unset -> Probe5 auto-detects the install + version
            SetState("connecting");
            try
            {
                _proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (s, a) => OnLine(a.Data);
                _proc.ErrorDataReceived += (s, a) => OnLine(a.Data);
                _proc.Exited += (s, a) => SetState("stopped");
                _proc.Start(); _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine();
            }
            catch { SetState("stopped"); return; }
            _run = true;
            _worker = new System.Threading.Thread(Worker) { IsBackground = true };
            _worker.Start();
        }

        // Live ring buffer of the engine's stdout/stderr, captured as it streams (probe5_out.txt is only written on
        // exit, so this is the freshest engine log for diagnostics / log export).
        readonly System.Collections.Generic.List<string> _log = new System.Collections.Generic.List<string>();
        readonly object _logLock = new object();
        public string RelayDir { get { return _relay; } }
        public string[] RecentLog() { lock (_logLock) { return _log.ToArray(); } }
        void OnLine(string line)
        {
            if (line == null) return;
            lock (_logLock) { _log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + line); if (_log.Count > 5000) _log.RemoveRange(0, _log.Count - 5000); }
            if (line.Contains("LOGGED IN & READY") || line.Contains("loggedOn=True")) SetState("connected");
            else if (line.Contains("FATAL") || line.Contains("EXCEPTION:")) SetState("stopped");
        }
        void SetState(string s) { if (State == s) return; State = s; try { Status?.Invoke(s); } catch { } }

        void Worker()
        {
            string inbox = Path.Combine(_relay, "whisper_in.txt");
            string outbox = Path.Combine(_relay, "whisper_out.txt");
            string presFile = Path.Combine(_relay, "presence.tsv");
            while (_run)
            {
                try
                {
                    // Hold _outLock across the whole exists-check + write + dequeue so Cancel() can't interleave
                    // (it deletes whisper_out.txt under the same lock) — otherwise a cancelled message could still go out.
                    lock (_outLock)
                    {
                        if (_outQ.Count > 0 && !File.Exists(outbox))
                        {
                            File.WriteAllText(outbox, _outQ.Peek(), new UTF8Encoding(false));
                            _outQ.Dequeue();
                        }
                    }
                }
                catch { }
                try
                {
                    if (File.Exists(inbox))
                        using (var fs = new FileStream(inbox, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (fs.Length < _inPos) _inPos = 0;
                            if (fs.Length > _inPos)
                            {
                                SetState("connected");   // any inbound traffic (whisper or send-confirmation) proves we're logged in
                                fs.Seek(_inPos, SeekOrigin.Begin);
                                using (var sr = new StreamReader(fs, Encoding.UTF8))
                                {
                                    string ln;
                                    while ((ln = sr.ReadLine()) != null)
                                    {
                                        if (ln.Trim().Length == 0) continue;
                                        var parts = ln.Split(new[] { '\t' }, 3);
                                        string sender = parts.Length >= 3 ? parts[1] : "";
                                        string text = parts.Length >= 3 ? parts[2] : ln;
                                        if (text.Length > 0) try { Inbound?.Invoke(sender, text); } catch { }
                                    }
                                    _inPos = fs.Position;
                                }
                            }
                        }
                }
                catch { }
                try
                {
                    if (File.Exists(presFile))
                        using (var fs = new FileStream(presFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (fs.Length < _presPos) _presPos = 0;
                            if (fs.Length > _presPos)
                            {
                                SetState("connected");   // a presence reply also proves the backend is talking to us
                                fs.Seek(_presPos, SeekOrigin.Begin);
                                using (var sr = new StreamReader(fs, Encoding.UTF8))
                                {
                                    string ln;
                                    while ((ln = sr.ReadLine()) != null)
                                    {
                                        var parts = ln.Split('\t');   // id \t name \t 780flag (0=online,1=offline)
                                        if (parts.Length >= 3 && parts[1].Length > 0)
                                            try { Presence?.Invoke(parts[1], parts[2].Trim() == "0"); } catch { }
                                    }
                                    _presPos = fs.Position;
                                }
                            }
                        }
                }
                catch { }
                System.Threading.Thread.Sleep(150);
            }
        }
        public void Query(System.Collections.Generic.IEnumerable<string> names)   // REQUEST_PLAYER_INFO for each (one per line)
        {
            try
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (var n in names) if (!string.IsNullOrWhiteSpace(n) && !lines.Contains(n.Trim())) lines.Add(n.Trim());
                if (lines.Count == 0) return;
                File.WriteAllText(Path.Combine(_relay, "query_out.txt"), string.Join("\n", lines), new UTF8Encoding(false));
            }
            catch { }
        }

        public void Send(string to, string msg)
        {
            if (string.IsNullOrWhiteSpace(to) || string.IsNullOrEmpty(msg)) return;
            lock (_outLock) { _outQ.Enqueue(to + "|" + msg); }
        }
        // Retract a queued send before it goes out. Works while it's still in the in-memory queue, or sitting in
        // whisper_out.txt un-consumed (Probe5 only reads it once logged in, so it's safe to delete while connecting).
        // Returns false if it already left for the server (can't unsend).
        public bool Cancel(string to, string msg)
        {
            string payload = to + "|" + msg;
            string outbox = Path.Combine(_relay, "whisper_out.txt");
            // Everything under _outLock so the Worker's exists-check+write+dequeue can't interleave with our delete.
            lock (_outLock)
            {
                bool removed = false;
                if (_outQ.Count > 0)
                {
                    var keep = new Queue<string>();
                    while (_outQ.Count > 0)
                    {
                        var it = _outQ.Dequeue();
                        if (!removed && it == payload) { removed = true; continue; }
                        keep.Enqueue(it);
                    }
                    while (keep.Count > 0) _outQ.Enqueue(keep.Dequeue());
                }
                if (!removed && State != "connected")
                {
                    try { if (File.Exists(outbox) && File.ReadAllText(outbox).Trim() == payload) { File.Delete(outbox); removed = true; } }
                    catch { }
                }
                return removed;
            }
        }
        public void Stop()
        {
            _run = false;
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            SetState("stopped");
        }
    }

    class MainForm : Form
    {
        // Clearly non-god files (engine / systems / modes / maps / items): hidden unless "Show all entities".
        static readonly HashSet<string> NonGods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "engine","game","input","ui","systemsettings","lightmass","gametips","datatracker",
            "deathzones","deployable","depthzy","dynamicconquestwall","effectmanager","environmentprop",
            "gamemodifiers","monster","obelisk","pawn","playerrepinfo","projectile","set","skinrefs",
            "slash","tgstaticmeshactor_ownable","trebuchet","itemstoreitems","firegiant",
            "conquest","conquest8r10","conquesty11","conquesty1s4","domination","ch15",
            "bancroftsclaw","fightersmask","manticoresspike"
        };

        static readonly Dictionary<string, string> Overrides = new Dictionary<string, string>
        {
            {"KingArthur","King Arthur"},{"ChangE","Chang'e"},{"ErlangShen","Erlang Shen"},
            {"IxChel","Ix Chel"},{"MamanBrigitte","Maman Brigitte"},{"MorganLeFay","Morgan Le Fay"},
            {"Nuwa","Nu Wa"},{"NutGod","Nut"},{"TheMorrigan","The Morrigan"},{"YuHuang","Yu Huang"},
            {"BakeKujira","Bake-Kujira"},{"BancroftsClaw","Bancroft's Claw"},
            {"FightersMask","Fighter's Mask"},{"ManticoresSpike","Manticore's Spikes"}
        };

        float scale = 1f;
        int S(int px) => (int)Math.Round(px * scale);

        string folderPath;
        List<GodFile> gods = new List<GodFile>();
        GodFile current;
        bool suppressGodSel;   // re-entrancy guard while we programmatically revert/repopulate godBox selection
        List<Param> prms = new List<Param>();
        Dictionary<string, string> defaults = new Dictionary<string, string>();   // "SectionKey" -> pristine value
        readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Image> abilityIconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Image> godListCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);   // full roster icons by API name
        readonly Dictionary<string, Image> itemIconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Image> logoCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);   // platform/game logos (circle-clipped) by key+size
        readonly List<FavPlayer> favorites = new List<FavPlayer>();   // persisted to favorites.json next to exe
        readonly List<HiddenTag> hiddenTags = new List<HiddenTag>();  // user nicknames for privacy-hidden players (hiddentags.json)
        string curPid = "", curName = "";                             // currently loaded tracker player
        int curPortal;
        string curLiveMatch = "";                                     // live match id when the player is in a game (clickable status chip)
        bool trackBusy;                                               // serializes tracker loads/dialogs (no concurrent awaits)

        Button openBtn, rescanBtn, applyBtn, reloadBtn, restoreBtn, addBtn, inspectBtn;
        Button[] navBtns;                            // left rail: 0 God Inspector, 1 Player Tracker, 2 Friend List, 3 Settings (tracker Track/Saved/Favorites/Friends are sub-tabs inside the view)
        int navIdx;
        Panel settingsHost, friendListHost, codexHost, whispersHost;   // Settings / Friend List / Codex / Whispers tab content
        Action _wsOnShow;                            // entering the Whispers tab: ensure the engine is started
        Action _wsAutoStart;                          // app startup: connect the engine in the background IF that option is on
        Action<string, string> _openWhisper;         // open/create a conversation with (player name, player id) — id may be "" for typed names
        Action _codexJumpReveal;   // set by BuildCodexPanel; scrolls the Codex to the hidden-player-reveal section (Settings link)
        SmiteGuru _sguru;          // lazily-created SmiteGuru fetcher (Encounters tab); holds the hidden WebView2
        System.Threading.CancellationTokenSource _archiveCts;   // SMITE_ARCHIVE bulk crawl → canceled on FormClosed
        Action _flShow;                              // entering the Friend List tab: seed once, else resume the live poller
        Action _flPause;                             // leaving the Friend List tab: pause the live poller
        Button friendAddBtn;                         // ＋ add-current-player-to-Friend-List toggle (tracker)
        readonly AppSettings settings = new AppSettings();
        readonly List<FavPlayer> friendList = new List<FavPlayer>();   // user buddy list w/ live status (friendlist.json)
        readonly List<FavPlayer> recents = new List<FavPlayer>();   // auto recent-lookups ("Saved"), recents.json
        Action<int> _trkSubTab;                      // selects a PRIMARY tracker tab (0 My profile, 1 Track, 2 Favorites, 3 Recent Profiles, 4 Custom Hidden Tags)
        Action<int> _trkSubTab2;                     // selects a SECONDARY tab (0 Overview, 1 Masteries, 2 Matches, 3 Achievements, 4 Friend List, 5 Encounters) — used by the test/screenshot hook
        Action<string> _trkEncCompare;               // fills the Encounters box + runs a compare (test/screenshot hook)
        Action _trkPlayerLoaded;                      // called when a player finishes loading → reveal the secondary (Overview/Achievements/Friends) strip
        Func<string, string, Task> _trkLoadPlayer;   // load a player into the tracker by (id, name) — used by the Friend List
        Action _trkResetSecondary;                    // reset the player-scoped sub-tab to Overview BEFORE navigating (so opening a profile never restores a stale Friends view that locks the loader)
        Label folderLbl, statusLbl, nameLbl, fileLbl;
        Panel headPanel, headIcon, bottomBar, trackerHost;
        TableLayoutPanel root, table;
        TextBox searchBox, trackerBox;
        CheckBox showAllChk, showHelpChk;
        ListBox godBox;
        PlayerList trackSuggest;                     // search results / favorites / friends overlay
        Button favSaveBtn;                           // ★ save-current-player toggle
        SplitContainer split;
        ListView trackGodLv, trackMatchLv;
        ImageList trackGodImgs;                       // shared god icons for the two tracker lists
        readonly Dictionary<string, int> godImgIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly ToolTip tip = new ToolTip();

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        struct RECT { public int Left, Top, Right, Bottom; }

        // Freeze a control's painting while we rebuild its content (e.g. refilling a RichTextBox), so the user never sees
        // it repaint line-by-line or scroll through the whole history. Pair Suspend/Resume; Resume repaints once.
        [DllImport("user32.dll")] static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        const int WM_SETREDRAW = 0x000B;
        static void SuspendDrawing(Control c) { if (c != null && c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, 0, 0); }
        static void ResumeDrawing(Control c) { if (c != null && c.IsHandleCreated) { SendMessage(c.Handle, WM_SETREDRAW, 1, 0); c.Invalidate(); } }

        // ---- DPAPI (Windows per-user encryption) — used to remember the Hi-Rez password without storing it in plaintext.
        [StructLayout(LayoutKind.Sequential)] struct DATA_BLOB { public int cbData; public IntPtr pbData; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr hMem);
        static string DpapiProtect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var inB = new DATA_BLOB(); var outB = new DATA_BLOB();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                inB.cbData = data.Length; inB.pbData = Marshal.AllocHGlobal(data.Length); Marshal.Copy(data, 0, inB.pbData, data.Length);
                if (!CryptProtectData(ref inB, "SmiteInspector", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outB)) return "";
                byte[] o = new byte[outB.cbData]; Marshal.Copy(outB.pbData, o, 0, outB.cbData);
                return Convert.ToBase64String(o);
            }
            catch { return ""; }
            finally { if (inB.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inB.pbData); if (outB.pbData != IntPtr.Zero) LocalFree(outB.pbData); }
        }
        static string DpapiUnprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            var inB = new DATA_BLOB(); var outB = new DATA_BLOB();
            try
            {
                byte[] data = Convert.FromBase64String(b64);
                inB.cbData = data.Length; inB.pbData = Marshal.AllocHGlobal(data.Length); Marshal.Copy(data, 0, inB.pbData, data.Length);
                if (!CryptUnprotectData(ref inB, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outB)) return "";
                byte[] o = new byte[outB.cbData]; Marshal.Copy(outB.pbData, o, 0, outB.cbData);
                return Encoding.UTF8.GetString(o);
            }
            catch { return ""; }
            finally { if (inB.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inB.pbData); if (outB.pbData != IntPtr.Zero) LocalFree(outB.pbData); }
        }
        // True physical client width of the form. Managed ClientSize inflates on child controls (and the form) at this
        // app's mixed DPI, so layout math that needs real pixels must read it from Win32 GetClientRect on the top-level form.
        int PhysicalClientWidth() { return GetClientRect(Handle, out var r) ? r.Right - r.Left : ClientSize.Width; }

        public MainForm()
        {
            try { using (var g = CreateGraphics()) scale = g.DpiX / 96f; } catch { scale = 1f; }

            Text = "Smite 1 Inspector";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(9.5f);
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1300), S(720));
            MinimumSize = new Size(S(900), S(560));

            BuildUi();
            TryAutoLoad();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark native title bar (Win10 2004+ uses attr 20; older builds 19).
            try { int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); }
            catch { }
        }

        void BuildUi()
        {
            MigrateData();    // move any data written next to the exe by an earlier build into Documents\Smite Inspector
            LoadSettings();   // before BuildSettingsPanel so the radios reflect the saved values, and before the startup-tab pick
            LoadFriendList(); // before BuildFriendListPanel so its first refresh sees the saved roster
            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(54)));   // top bar
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(2)));    // red accent strip
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // body
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(48)));   // bottom bar

            // ---- top bar ----
            var top = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };

            var leftFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(S(14), S(11), S(8), S(8)) };
            openBtn   = MkBtn("Open Config folder…", 168, true); openBtn.Margin = new Padding(0, 0, S(6), 0);
            rescanBtn = MkBtn("Rescan", 80, false);
            inspectBtn = MkBtn("SDK Inspector", 128, false, Theme.Yellow, Color.FromArgb(28, 22, 0)); inspectBtn.Enabled = false;
            leftFlow.Controls.Add(openBtn);
            leftFlow.Controls.Add(rescanBtn);
            leftFlow.Controls.Add(inspectBtn);

            var rightFlow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Theme.Panel, Padding = new Padding(0, S(11), S(12), S(8)) };
            showAllChk  = MkChk("Show all entities", false);
            showHelpChk = MkChk("Show help", true);
            rightFlow.Controls.Add(showAllChk);
            rightFlow.Controls.Add(showHelpChk);

            top.Controls.Add(leftFlow);
            top.Controls.Add(rightFlow);

            var strip = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Accent };

            // ---- body split ----
            split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Line, SplitterWidth = S(3), FixedPanel = FixedPanel.Panel1, Panel1MinSize = S(190) };
            split.Panel1.BackColor = Theme.Panel;
            split.Panel2.BackColor = Theme.Bg;

            // left sidebar: search (top), list (fill), folder path (bottom)
            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Panel };
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));

            searchBox = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10f) };
            try { searchBox.PlaceholderText = "Filter gods…"; } catch { }
            var searchHost = WrapInput(searchBox, 0);
            searchHost.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            searchHost.Margin = new Padding(S(8), S(8), S(8), S(2));

            godBox = new ListBox
            {
                Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(38)
            };
            godBox.DrawItem += GodBox_DrawItem;

            folderLbl = new Label { Dock = DockStyle.Fill, Text = "No folder loaded", ForeColor = Theme.Dim, Font = Theme.F(8f), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, S(6), 0) };

            side.Controls.Add(searchHost, 0, 0);
            side.Controls.Add(godBox, 0, 1);
            side.Controls.Add(folderLbl, 0, 2);
            split.Panel1.Controls.Add(side);

            // right: god header (icon + name) then the param table
            headPanel = new Panel { Dock = DockStyle.Top, Height = S(64), BackColor = Theme.Bg };
            headIcon = new Panel { Bounds = new Rectangle(S(12), S(9), S(46), S(46)), BackColor = Theme.Bg };
            headIcon.Paint += HeadIcon_Paint;
            nameLbl = new Label { AutoSize = false, Location = new Point(S(70), S(8)), Size = new Size(S(560), S(28)), ForeColor = Theme.Text, Font = Theme.F(15f, FontStyle.Bold), Text = "" };
            fileLbl = new Label { AutoSize = false, Location = new Point(S(72), S(37)), Size = new Size(S(560), S(18)), ForeColor = Theme.Dim, Font = Theme.F(8.5f), Text = "" };
            headPanel.Controls.Add(headIcon);
            headPanel.Controls.Add(nameLbl);
            headPanel.Controls.Add(fileLbl);
            headPanel.Resize += (s, e) => { nameLbl.Width = headPanel.Width - nameLbl.Left - S(12); fileLbl.Width = headPanel.Width - fileLbl.Left - S(12); };

            table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(S(12), S(2), S(12), S(14)) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(196)));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(40)));

            split.Panel2.Controls.Add(table);
            split.Panel2.Controls.Add(headPanel);

            // ---- bottom bar (Inspector mode only) ----
            bottomBar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            statusLbl = new Label { Dock = DockStyle.Fill, UseMnemonic = false, ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleRight, Font = Theme.F(9f), Padding = new Padding(0, 0, S(14), 0), Text = "Pick your SMITE Config folder to start." };
            var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Panel, Padding = new Padding(S(10), S(9), 0, S(8)) };
            applyBtn   = MkBtn("Apply changes", 132, true);  applyBtn.Enabled = false;
            reloadBtn  = MkBtn("Reload file", 100, false);   reloadBtn.Enabled = false;
            restoreBtn = MkBtn("Restore defaults", 138, false); restoreBtn.Enabled = false;
            addBtn     = MkBtn("＋ Add value", 110, false, Theme.Purple, Color.White); addBtn.Enabled = false;
            btnFlow.Controls.Add(applyBtn);
            btnFlow.Controls.Add(reloadBtn);
            btnFlow.Controls.Add(restoreBtn);
            btnFlow.Controls.Add(addBtn);
            bottomBar.Controls.Add(statusLbl);
            bottomBar.Controls.Add(btnFlow);

            // ---- body: God Inspector split + Player Tracker + Friend List + Settings views, toggled by the rail ----
            var bodyHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            trackerHost = BuildTrackerPanel();
            trackerHost.Dock = DockStyle.Fill; trackerHost.Visible = false;
            friendListHost = BuildFriendListPanel();
            friendListHost.Dock = DockStyle.Fill; friendListHost.Visible = false;
            settingsHost = BuildSettingsPanel();
            settingsHost.Dock = DockStyle.Fill; settingsHost.Visible = false;
            codexHost = BuildCodexPanel();
            codexHost.Dock = DockStyle.Fill; codexHost.Visible = false;
            whispersHost = BuildWhispersPanel();
            whispersHost.Dock = DockStyle.Fill; whispersHost.Visible = false;
            bodyHost.Controls.Add(whispersHost);
            bodyHost.Controls.Add(codexHost);
            bodyHost.Controls.Add(settingsHost);
            bodyHost.Controls.Add(friendListHost);
            bodyHost.Controls.Add(trackerHost);
            bodyHost.Controls.Add(split);

            root.Controls.Add(top, 0, 0);
            root.Controls.Add(strip, 0, 1);
            root.Controls.Add(bodyHost, 0, 2);
            root.Controls.Add(bottomBar, 0, 3);

            // ---- left navigation rail: God Inspector / Player Tracker (Track·Saved·Favorites·Friends) / Settings ----
            var sideRail = new Panel { Dock = DockStyle.Left, Width = S(190), BackColor = Theme.Panel };
            var railLine = new Panel { Dock = DockStyle.Right, Width = S(1), BackColor = Theme.Line };
            var brandWrap = new Panel { Dock = DockStyle.Top, Height = S(68), BackColor = Theme.Panel };
            brandWrap.Controls.Add(new Label { Text = "SMITE 1", AutoSize = true, ForeColor = Theme.Text, Font = Theme.F(15f, FontStyle.Bold), Location = new Point(S(16), S(14)) });
            brandWrap.Controls.Add(new Label { Text = "INSPECTOR", AutoSize = true, ForeColor = Theme.Accent, Font = Theme.F(11f, FontStyle.Bold), Location = new Point(S(16), S(40)) });
            var navFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(S(8), S(4), S(8), S(8)) };
            // navBtns indexed by MODE: 0 God Inspector, 1 Player Tracker, 2 Friend List, 3 Settings, 4 Codex.
            // Shown in a custom order: Player Tracker on top, then Friend List, then God Inspector, then Codex, then Settings.
            navBtns = new[] { MkNav("God Inspector"), MkNav("Player Tracker"), MkNav("Friend List"), MkNav("Settings"), MkNav("Codex"), MkNav("Whispers") };
            foreach (var k in new[] { 1, 2, 5, 0, 4, 3 }) navFlow.Controls.Add(navBtns[k]);
            for (int i = 0; i < navBtns.Length; i++) { int k = i; navBtns[i].Click += (s, e) => SelectNav(k); }
            sideRail.Controls.Add(navFlow); sideRail.Controls.Add(brandWrap); sideRail.Controls.Add(railLine);

            Controls.Add(root);       // fill (added first → behind)
            Controls.Add(sideRail);   // docks left

            // events
            openBtn.Click   += (s, e) => OpenFolder();
            rescanBtn.Click += (s, e) => { if (folderPath != null && ConfirmDiscardEdits()) Scan(); };
            inspectBtn.Click += (s, e) => ShowSdkInspector();
            applyBtn.Click  += (s, e) => Apply();
            reloadBtn.Click += (s, e) => ReloadFile();
            restoreBtn.Click += (s, e) => RestoreDefaults();
            addBtn.Click += (s, e) => AddTunable();
            searchBox.TextChanged      += (s, e) => RenderList();
            showAllChk.CheckedChanged  += (s, e) => RenderList();
            showHelpChk.CheckedChanged += (s, e) => { if (current != null) RenderRows(); };
            godBox.SelectedIndexChanged += (s, e) =>
            {
                if (suppressGodSel) return;
                if (godBox.SelectedItem is GodFile g)
                {
                    if (g == current) return;   // re-select of the same god (e.g. after filtering) → nothing to discard
                    if (!ConfirmDiscardEdits()) { suppressGodSel = true; try { godBox.SelectedItem = current; } catch { } finally { suppressGodSel = false; } return; }
                    LoadGod(g);
                }
            };

            SelectNav(settings.StartupTab == 1 ? 1 : 0);   // honor the "open on startup" preference (settings already loaded)
        }

        int curMode = -1;
        // Rail indices = modes: 0 God Inspector, 1 Player Tracker, 2 Friend List, 3 Settings.
        void SelectNav(int idx)
        {
            navIdx = idx;
            if (navBtns != null) for (int i = 0; i < navBtns.Length; i++) StyleNav(navBtns[i], i == idx);
            if (idx == 0) { SwitchMode(0); return; }
            if (idx == 2) { SwitchMode(2); _flShow?.Invoke(); return; }
            if (idx == 3) { SwitchMode(3); return; }
            if (idx == 4) { SwitchMode(4); return; }
            if (idx == 5) { SwitchMode(5); _wsOnShow?.Invoke(); return; }
            bool wasTracker = curMode == 1;
            SwitchMode(1);
            if (!wasTracker) _trkSubTab?.Invoke(1);   // entering the tracker → default to the Track tab (index 1; 0 is My profile)
        }

        // mode visibility: 0 = God Inspector, 1 = Player Tracker, 2 = Friend List, 3 = Settings
        void SwitchMode(int mode)
        {
            curMode = mode;
            if (mode != 2) _flPause?.Invoke();   // stop the Friend List live poller whenever another tab is showing (zero FL calls while hidden)
            bool insp = mode == 0, trk = mode == 1, fl = mode == 2, set = mode == 3, cod = mode == 4, wsp = mode == 5;
            split.Visible = insp;
            trackerHost.Visible = trk;
            if (friendListHost != null) friendListHost.Visible = fl;
            if (settingsHost != null) settingsHost.Visible = set;
            if (codexHost != null) codexHost.Visible = cod;
            if (whispersHost != null) whispersHost.Visible = wsp;
            bottomBar.Visible = insp;
            try { root.RowStyles[3].Height = insp ? S(48) : 0; } catch { }   // bottom bar (inspector only)
            try { root.RowStyles[0].Height = insp ? S(54) : 0; } catch { }   // top toolbar (inspector only)
            try { root.RowStyles[1].Height = insp ? S(2) : 0; } catch { }    // red accent strip lives under the toolbar; hide it when the toolbar is gone
            openBtn.Visible = rescanBtn.Visible = inspectBtn.Visible = insp;
            showHelpChk.Visible = showAllChk.Visible = insp;
            if (trk) trackerBox?.Focus();
        }

        Button MkNav(string text)
        {
            var b = new Button { Text = "    " + text, Width = S(170), Height = S(40), FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                Font = Theme.F(10.5f, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Margin = new Padding(0, S(2), 0, 0) };
            b.FlatAppearance.BorderSize = 0;
            var bar = new Panel { Dock = DockStyle.Left, Width = S(3), BackColor = Theme.Accent, Visible = false };   // active accent bar
            b.Controls.Add(bar); b.Tag = bar;
            return b;
        }

        void StyleNav(Button b, bool active)
        {
            b.BackColor = active ? Color.FromArgb(40, 40, 46) : Theme.Panel;
            b.ForeColor = active ? Color.White : Theme.Dim;
            b.FlatAppearance.MouseOverBackColor = active ? Color.FromArgb(46, 46, 52) : Color.FromArgb(26, 26, 30);
            if (b.Tag is Panel bar) bar.Visible = active;
        }

        // Horizontal segmented sub-tab (Track / Saved / Favorites / Friends) at the top of the tracker view.
        Button MkSubTab(string text)
        {
            int w = Math.Max(S(96), TextRenderer.MeasureText(text, Theme.F(10f, FontStyle.Bold)).Width + S(34));   // fit the label (e.g. "Achievements")
            var b = new Button { Text = text, Width = w, Height = S(40), FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                Font = Theme.F(10f, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Margin = new Padding(0, 0, S(2), 0) };
            b.FlatAppearance.BorderSize = 0;
            var ul = new Panel { Dock = DockStyle.Bottom, Height = S(3), BackColor = Theme.Accent, Visible = false };   // active underline
            b.Controls.Add(ul); b.Tag = ul;
            return b;
        }
        void StyleSubTab(Button b, bool active)
        {
            b.BackColor = active ? Color.FromArgb(34, 34, 40) : Theme.Panel;
            b.ForeColor = active ? Color.White : Theme.Dim;
            b.FlatAppearance.MouseOverBackColor = active ? Color.FromArgb(40, 40, 46) : Color.FromArgb(26, 26, 30);
            if (b.Tag is Panel ul) ul.Visible = active;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { split.SplitterDistance = S(252); } catch { }
            // Dark-mode the native scrollbars on the scrollable controls (Win10/11).
            try { SetWindowTheme(godBox.Handle, "DarkMode_Explorer", null); } catch { }
            try { SetWindowTheme(table.Handle, "DarkMode_Explorer", null); } catch { }
            try { SetWindowTheme(trackGodLv.Handle, "DarkMode_Explorer", null); } catch { }
            try { SetWindowTheme(trackMatchLv.Handle, "DarkMode_Explorer", null); } catch { }
            try { SetWindowTheme(trackSuggest.Handle, "DarkMode_Explorer", null); } catch { }
            CleanupOldExe();   // clear the renamed-aside exe a previous update left behind
            BeginInvoke(new Action(() => { try { _wsAutoStart?.Invoke(); } catch { } }));   // optional: pre-connect the Whispers engine
            if (settings.CheckUpdates && !_updateChecked) { _updateChecked = true; BeginInvoke(new Action(async () => await CheckForUpdate(false))); }
            // Test hook (no-op in production): open a scoreboard directly so verification can screenshot the reveal.
            var tmid = Environment.GetEnvironmentVariable("SMITE_TEST_MATCHID");
            if (!string.IsNullOrEmpty(tmid)) BeginInvoke(new Action(async () => { try { await ShowMatchDetails(tmid); } catch { } }));
            var tprim = Environment.GetEnvironmentVariable("SMITE_TEST_PRIMARY");
            if (!string.IsNullOrEmpty(tprim) && int.TryParse(tprim, out var tpi)) BeginInvoke(new Action(() => { try { SelectNav(1); _trkSubTab?.Invoke(tpi); } catch { } }));
            var tnav = Environment.GetEnvironmentVariable("SMITE_TEST_NAV");
            if (!string.IsNullOrEmpty(tnav) && int.TryParse(tnav, out var tni)) BeginInvoke(new Action(() => { try { SelectNav(tni); } catch { } }));
            // SMITE_TEST_SECONDARY=N: open the tracker → My profile (auto-loads the pinned player), then after the async load
            // settles, select secondary tab N (3 = Achievements). Screenshot/verification only.
            var tsec = Environment.GetEnvironmentVariable("SMITE_TEST_SECONDARY");
            if (!string.IsNullOrEmpty(tsec) && int.TryParse(tsec, out var tsi))
                BeginInvoke(new Action(() => { try { SelectNav(1); _trkSubTab?.Invoke(0); var tt = new System.Windows.Forms.Timer { Interval = 6000 }; tt.Tick += (s, e) => { tt.Stop(); tt.Dispose(); try { _trkSubTab2?.Invoke(tsi); var vs = Environment.GetEnvironmentVariable("SMITE_TEST_ENCVS"); if (tsi == 5 && !string.IsNullOrEmpty(vs)) _trkEncCompare?.Invoke(vs); } catch { } }; tt.Start(); } catch { } }));
            // SMITE_TEST_SGMATCH="<matchId>": verify the SmiteGuru scoreboard fetch end-to-end (parse + god/item name maps).
            var tsm = Environment.GetEnvironmentVariable("SMITE_TEST_SGMATCH");
            if (!string.IsNullOrEmpty(tsm))
                BeginInvoke(new Action(async () =>
                {
                    string outp = Path.Combine(Theme.DataDir, "sgmatch_test.txt");
                    try
                    {
                        File.WriteAllText(outp, "started");
                        _sguru ??= new SmiteGuru(this);
                        var (md, gods, items) = await _sguru.GetMatchFull(tsm, System.Threading.CancellationToken.None);
                        if (md == null) { File.WriteAllText(outp, "match null (not loaded)"); return; }
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"match {md.MatchId} q{md.QueueId} win-team {md.WinningTeam} dur {md.Duration} {md.Time}  gods:{gods?.Count} items:{items?.Count}");
                        foreach (var p in md.Players.OrderBy(p => p.Team).ThenByDescending(p => p.Kills))
                        {
                            string gn = gods != null && gods.TryGetValue(p.Champion, out var n) ? n : ("god" + p.Champion);
                            string itms = p.Build != null ? string.Join("/", p.Build.OrderBy(kv => kv.Key).Select(kv => items != null && items.TryGetValue(kv.Value, out var inm) ? inm : ("?" + kv.Value))) : "";
                            sb.AppendLine($"T{p.Team} {(string.IsNullOrWhiteSpace(p.Name) ? "<hidden>" : p.Name),-18} {gn,-14} {p.Kills}/{p.Deaths}/{p.Assists} dmg {p.Damage} gold {p.Gold} | {itms}");
                        }
                        File.WriteAllText(outp, sb.ToString());
                    }
                    catch (Exception ex) { try { File.WriteAllText(outp, "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } }
                }));
            // SMITE_TEST_GBLIVE: end-to-end LIVE test of the god-board reveal chain (getgodleaderboard id-leak →
            // SmiteGuru.ResolveProfilesBatch name → GodBoard.BestMatch clan+level). Synthetic slot = Maman Brigitte (god
            // 4301), Duel board (440), clan "Team Rival", lvl 160 → should reveal CaptainTwig (id 834980, that clan/level).
            var tgbl = Environment.GetEnvironmentVariable("SMITE_TEST_GBLIVE");
            if (!string.IsNullOrEmpty(tgbl))
                BeginInvoke(new Action(async () =>
                {
                    string outp = Path.Combine(Theme.DataDir, "gblive_test.txt");
                    try
                    {
                        File.WriteAllText(outp, "started");
                        NameDb.Enabled = true; GodBoard.Load();
                        _sguru ??= new SmiteGuru(this);
                        var slot = new GodBoard.Slot { GodId = "4301", GodName = "Maman Brigitte", Tf = 1, Level = 160, Clan = "Team Rival", ClanId = 0, Mastery = 0, Queues = new List<int> { 440 } };
                        var map = await GodBoard.ResolveSlots(new[] { slot }, (ids, c) => _sguru.ResolveProfilesBatch(ids, c), System.Threading.CancellationToken.None);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("slot: Maman Brigitte / Duel(440) / clan 'Team Rival' / lvl 160  (expect ✦ CaptainTwig)");
                        if (map != null && map.Count > 0) foreach (var kv in map) sb.AppendLine("  REVEAL " + kv.Key + " -> '" + kv.Value.name + "' conf " + kv.Value.conf);
                        else sb.AppendLine("  (no reveal — board/CF/clan-match miss)");
                        File.WriteAllText(outp, sb.ToString());
                    }
                    catch (Exception ex) { try { File.WriteAllText(outp, "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } }
                }));
            // SMITE_TEST_RAWFETCH="clearId|url": test alternate pagination params past the offset cap. Diagnostic only.
            var trf = Environment.GetEnvironmentVariable("SMITE_TEST_RAWFETCH");
            if (!string.IsNullOrEmpty(trf))
                BeginInvoke(new Action(async () =>
                {
                    string outp = Path.Combine(Theme.DataDir, "rawfetch_test.txt");
                    try
                    {
                        File.WriteAllText(outp, "started");
                        var pr = trf.Split(new[] { '|' }, 2); long cid = long.Parse(pr[0]);
                        var urls = pr[1].Split('\n').Where(u => u.Trim().Length > 0).ToList();
                        _sguru ??= new SmiteGuru(this);
                        var sb = new System.Text.StringBuilder();
                        foreach (var u in urls) { sb.AppendLine("URL: " + u.Trim()); sb.AppendLine(await _sguru.RawFetch(cid, u.Trim(), System.Threading.CancellationToken.None)); sb.AppendLine(); File.WriteAllText(outp, sb.ToString()); }
                    }
                    catch (Exception ex) { try { File.WriteAllText(outp, "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } }
                }));
            // SMITE_TEST_RECON[=path]: load smite.guru's SPA, dump every api call it makes + every endpoint string in its JS bundles → recon.txt.
            var trc = Environment.GetEnvironmentVariable("SMITE_TEST_RECON");
            if (!string.IsNullOrEmpty(trc))
                BeginInvoke(new Action(async () =>
                {
                    string outp = Path.Combine(Theme.DataDir, "recon.txt");
                    string path = (trc == "1" || trc.Equals("root", StringComparison.OrdinalIgnoreCase)) ? null : trc;   // "1"/"root" → homepage; else treat as a sub-path
                    try { File.WriteAllText(outp, "started"); _sguru ??= new SmiteGuru(this); var res = await _sguru.Recon(System.Threading.CancellationToken.None, path); File.WriteAllText(outp, res); }
                    catch (Exception ex) { try { File.WriteAllText(outp, "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } }
                }));
            // SMITE_TEST_DEEPPAGE="id:p1,p2,p3": probe whether deep pages are reachable (hard cap vs rate-limit). Diagnostic only.
            var tdp = Environment.GetEnvironmentVariable("SMITE_TEST_DEEPPAGE");
            if (!string.IsNullOrEmpty(tdp))
                BeginInvoke(new Action(async () =>
                {
                    string outp = Path.Combine(Theme.DataDir, "deeppage_test.txt");
                    try
                    {
                        File.WriteAllText(outp, "started");
                        var pr = tdp.Split(':'); long id = long.Parse(pr[0]);
                        var pages = pr[1].Split(',').Select(int.Parse).ToList();
                        _sguru ??= new SmiteGuru(this);
                        var res = await _sguru.ProbePages(id, pages, System.Threading.CancellationToken.None);
                        File.WriteAllText(outp, res);
                    }
                    catch (Exception ex) { try { File.WriteAllText(outp, "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } }
                }));
            // SMITE_TEST_SGURU="aId:bId": prove the SmiteGuru WebView2 fetch end-to-end — pull a few pages of player aId and
            // count encounters with bId, writing the result to sguru_test.txt. Verification only.
            var tsg = Environment.GetEnvironmentVariable("SMITE_TEST_SGURU");
            if (!string.IsNullOrEmpty(tsg))
                BeginInvoke(new Action(async () =>
                {
                    string sgOut = Path.Combine(Theme.DataDir, "sguru_test.txt");
                    try
                    {
                        File.WriteAllText(sgOut, "step: hook started");
                        var pr = tsg.Split(':'); long aId = long.Parse(pr[0]); long bId = pr.Length > 1 ? long.Parse(pr[1]) : 0;
                        string bNameSearch = pr.Length > 2 ? pr[2] : null;
                        _sguru ??= new SmiteGuru(this);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var histA = await _sguru.GetHistory(aId, 400, (p, m) => { try { File.WriteAllText(sgOut, $"step: A page {p}/{m} ({sw.Elapsed.TotalSeconds:0}s)"); } catch { } }, System.Threading.CancellationToken.None);
                        var matches = histA.Matches;
                        int enc = 0, allied = 0, against = 0; string firstD = null, lastD = null;
                        foreach (var mt in matches) { var a = mt.Players.FirstOrDefault(p => p.Id == aId); var b = mt.Players.FirstOrDefault(p => p.Id == bId); if (a != null && b != null) { enc++; if (a.Team == b.Team) allied++; else against++; lastD ??= mt.Time; firstD = mt.Time; } }
                        // scan span + per-year histogram (matches are newest-first → [0]=newest, last=oldest)
                        string newest = matches.Count > 0 ? matches[0].Time : "(none)";
                        string oldest = matches.Count > 0 ? matches[matches.Count - 1].Time : "(none)";
                        var byYear = matches.Where(mm => !string.IsNullOrEmpty(mm.Time) && mm.Time.Length >= 4).GroupBy(mm => mm.Time.Substring(0, 4)).OrderBy(gr => gr.Key).Select(gr => gr.Key + ":" + gr.Count());
                        // hidden-slot analysis (privacy-flagged players appear as id==0/empty name) — tests "was B private back then?"
                        int hidAll = matches.Count(mm => mm.Players != null && mm.Players.Any(p => p.Id == 0));
                        var m2024 = matches.Where(mm => (mm.Time ?? "").StartsWith("2024")).ToList();
                        int hid2024 = m2024.Count(mm => mm.Players != null && mm.Players.Any(p => p.Id == 0));
                        string r2024 = m2024.Count > 0 ? (m2024[m2024.Count - 1].Time + " .. " + m2024[0].Time) : "(none)";
                        // NAME-based search across A's whole history (catches an id mismatch the id-match would miss)
                        string nameHits = "(no name given)";
                        if (!string.IsNullOrEmpty(bNameSearch))
                        {
                            var rows = matches.Where(mm => mm.Players != null && mm.Players.Any(p => !string.IsNullOrWhiteSpace(p.Name) && string.Equals(p.Name.Trim(), bNameSearch, StringComparison.OrdinalIgnoreCase))).ToList();
                            var idsUnder = rows.SelectMany(mm => mm.Players).Where(p => !string.IsNullOrWhiteSpace(p.Name) && string.Equals(p.Name.Trim(), bNameSearch, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).Distinct().ToList();
                            nameHits = $"{rows.Count} games contain name '{bNameSearch}'; ids under that name: [{string.Join(",", idsUnder)}]; dates: {string.Join(", ", rows.Take(6).Select(mm => mm.Time))}";
                        }
                        string sample = matches.Count > 0 ? string.Join(", ", matches[0].Players.Select(p => p.Id + "/" + p.Name + "/T" + p.Team)) : "(none)";
                        // B's OWN full history → depth (how far back smite.guru has B) + every distinct name
                        string bNames = "(bId=0, skipped)";
                        if (bId > 0)
                        {
                            var bh = (await _sguru.GetHistory(bId, 400, (p, m) => { try { File.WriteAllText(sgOut, $"step: B page {p}/{m}"); } catch { } }, System.Threading.CancellationToken.None));
                            var bm = bh.Matches;
                            var distinct = bm.Where(mm => mm.Players != null).SelectMany(mm => mm.Players).Where(p => p.Id == bId && !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            int aInB = bm.Count(mm => mm.Players != null && mm.Players.Any(p => p.Id == aId));
                            string bOldest = bm.Count > 0 ? bm[bm.Count - 1].Time : "(none)";
                            string bNewest = bm.Count > 0 ? bm[0].Time : "(none)";
                            bNames = $"{bm.Count} games (cursorMax {bh.Max}, deepest {bh.Deepest}, complete {bh.Complete}), span {bOldest} -> {bNewest}; A({aId}) appears in {aInB} of B's games; names: " + (distinct.Count > 0 ? string.Join(", ", distinct) : "(none)");
                        }
                        File.WriteAllText(sgOut, $"A({aId}) matches: {matches.Count}  (cursorMax {histA.Max}, deepest {histA.Deepest}, complete {histA.Complete})\nelapsed: {sw.Elapsed.TotalSeconds:0.000}s\nA scan span: {oldest}  ->  {newest}\nA games per year: {string.Join("  ", byYear)}\nhidden-slot games: {hidAll} of {matches.Count} total; 2024: {hid2024} of {m2024.Count} 2024-games (2024 range {r2024})\nencounters {aId} vs {bId} (by id): {enc}  (allied {allied}, against {against})\nencounter dates: first {firstD}  last {lastD}\nname-search in A history: {nameHits}\nlast page title: {_sguru.LastDiag}\npage1 match0 roster: {sample}\nB own-history: {bNames}");
                    }
                    catch (Exception ex) { try { File.WriteAllText(sgOut, "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } }
                }));
            // SMITE_ARCHIVE=1: bulk-archive smite.guru into Theme.DataDir before it shuts down. Snowball BFS over the social graph
            // from seeds (favorites/recents/own profile + already-cached players + SMITE_ARCHIVE_SEED ids), saving each player's
            // full history, then (phase 2) every reachable match's full scoreboard. Resumable via archive_crawl.json, gentle,
            // cancelable on close. Point SMITE_TEST_DATADIR at the archive folder (e.g. E:\Claude\Data).
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMITE_ARCHIVE")))
                BeginInvoke(new Action(async () => { try { await ArchiveCrawl(); } catch (OperationCanceledException) { } catch (Exception ex) { try { File.WriteAllText(Path.Combine(Theme.DataDir, "archive_status.txt"), "FATAL " + ex.GetType().Name + ": " + ex.Message); } catch { } } }));
            // Self-tests that WRITE may run ONLY against the throwaway test DataDir — never the user's real Documents\Smite
            // Inspector (these seed fake players/tags and must never pollute real data or the community store).
            bool testDir = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMITE_TEST_DATADIR"));
            var tgl = Environment.GetEnvironmentVariable("SMITE_TEST_GAMELOG");
            if (testDir && !string.IsNullOrEmpty(tgl)) { try { File.WriteAllText(Path.Combine(Theme.DataDir, "gamelog_selftest.txt"), GameLog.SelfTest()); } catch { } }
            // SMITE_TEST_GODBOARD: unit-test GodBoard.BestMatch (the slot↔leaked-id join). Pure, no network. Verifies the
            // safe-by-design decisions: clan-exact+close-level reveals; same-clan/same-god near-ties stay Hidden; a clan
            // MISMATCH or a clanless slot stays Hidden (v1); a large level gap is rejected even with a clan match.
            var tgb = Environment.GetEnvironmentVariable("SMITE_TEST_GODBOARD");
            if (testDir && !string.IsNullOrEmpty(tgb)) { try {
                Func<string, int, GodBoard.Cand> C = (id, lv) => new GodBoard.Cand { Id = id, Name = "P" + id, Clan = "FTOA", Level = lv };
                // 1) clan-exact, unique, exact level → reveal
                var r1 = GodBoard.BestMatch("FTOA", 150, new[] { C("1", 150), new GodBoard.Cand { Id = "2", Name = "P2", Clan = "Other", Level = 150 } });
                // 2) two same-clan candidates at the SAME level → ambiguous → Hidden
                var r2 = GodBoard.BestMatch("FTOA", 150, new[] { C("1", 150), C("2", 150) });
                // 3) clanless slot → Hidden (v1 asserts only on a clan)
                var r3 = GodBoard.BestMatch("", 150, new[] { new GodBoard.Cand { Id = "1", Name = "P1", Clan = "", Level = 150 } });
                // 4) clan MISMATCH (no candidate in the slot's clan) → Hidden
                var r4 = GodBoard.BestMatch("FTOA", 150, new[] { new GodBoard.Cand { Id = "1", Name = "P1", Clan = "Other", Level = 150 } });
                // 5) clan-exact but a huge level gap (smite.guru saw lvl 60, slot is 150) → rejected (different account)
                var r5 = GodBoard.BestMatch("FTOA", 150, new[] { C("1", 60) });
                // 6) clan-exact, smite.guru level slightly stale (lower) → still reveals
                var r6 = GodBoard.BestMatch("FTOA", 150, new[] { C("1", 146), new GodBoard.Cand { Id = "2", Name = "P2", Clan = "Other", Level = 150 } });
                File.WriteAllText(Path.Combine(Theme.DataDir, "godboard_selftest.txt"),
                    "clan-exact-unique: '" + (r1.name ?? "<none>") + "' conf" + r1.conf + "  (expect P1)\n" +
                    "same-clan-tie:     '" + (r2.name ?? "<none>") + "'  (expect <none>)\n" +
                    "clanless:          '" + (r3.name ?? "<none>") + "'  (expect <none>)\n" +
                    "clan-mismatch:     '" + (r4.name ?? "<none>") + "'  (expect <none>)\n" +
                    "level-gap:         '" + (r5.name ?? "<none>") + "'  (expect <none>)\n" +
                    "stale-level-ok:    '" + (r6.name ?? "<none>") + "'  (expect P1)\n");
            } catch (Exception ex) { try { File.WriteAllText(Path.Combine(Theme.DataDir, "godboard_selftest.txt"), "ERR " + ex.Message); } catch { } } }
            var tex = Environment.GetEnvironmentVariable("SMITE_TEST_EXCLUDE");
            if (testDir && !string.IsNullOrEmpty(tex)) { try {
                NameDb.Enabled = true; NameDb.Learn("90001", "ExclAlice", 0, 100, "X", 200, "Thor", 50);
                var a = NameDb.Resolve(100, 200, 50, "Thor");
                var b = NameDb.Resolve(100, 200, 50, "Thor", null, null, new[] { "ExclAlice" });
                var c = NameDb.Resolve(100, 200, 50, "Thor", null, null, new[] { "90001" });
                // game-log-learn -> fingerprint recognition (the Nonkas case): learn a player with a premade, then a
                // DIFFERENT match with the SAME party should recognise them by fingerprint.
                NameDb.Learn("90002", "Nonkas", 0, 0, "", 198, "Thor", 130, new[] { "mateA", "mateB", "mateC" }, null);
                NameDb.Learn("90004", "WrongLvl", 0, 0, "", 250, "Thor", 130, new[] { "mateA", "mateB", "mateC" }, null);   // SAME party, different level → must lose to the exact-level match
                var d = NameDb.Resolve(0, 198, 130, "Thor", new[] { "mateA", "mateB", "mateC" }, null);
                // tag-healing: a degenerate LIVE tag (no companions, mastery=1) can't match → heal it with completed data → matches.
                SetHiddenTag(0, "", 198, 1, "HealMe", null, "Thor");
                var hBefore = MatchHidden(0, 198, 130, new[] { "pm1", "pm2", "pm3" }, "Thor");
                var ht = hiddenTags.FirstOrDefault(h => h.Nick == "HealMe");
                if (ht != null) UpdateSighting(ht, "", 198, 130, new[] { "pm1", "pm2", "pm3" }, "Thor");
                var hAfter = MatchHidden(0, 198, 130, new[] { "pm1", "pm2", "pm3" }, "Thor");
                // false-positive guard: a LONE candidate with only ONE common (low-IDF) shared mate + a coincidental EXACT
                // level, no clan, no god match → level must not anchor it over the soft floor → expect Hidden.
                for (int i = 0; i < 6; i++) NameDb.Learn("dum" + i, "Dummy" + i, 0, 0, "", 100, "Anubis", 50, new[] { "commonMate" }, null);
                NameDb.Learn("90005", "WeakLone", 0, 0, "", 300, "Loki", 80, new[] { "commonMate" }, null);
                var f = NameDb.Resolve(0, 300, 80, "Zeus", new[] { "commonMate" }, null);
                // level-band awareness: at HIGH level a big forward jump is implausible (XP/level is huge) → must lose to the exact-level match.
                NameDb.Learn("90006", "ExactHi", 0, 0, "", 200, "Thor", 130, new[] { "hmA", "hmB", "hmC" }, null);
                NameDb.Learn("90007", "JumpHi", 0, 0, "", 185, "Thor", 130, new[] { "hmA", "hmB", "hmC" }, null);   // observed 200 → +15 at L200 = implausible
                var g = NameDb.Resolve(0, 200, 130, "Thor", new[] { "hmA", "hmB", "hmC" }, null);
                // skin signal: a matching NON-default skin boosts confidence (same party, same god, same skin → higher conf).
                NameDb.Learn("90008", "SkinGuy", 0, 0, "", 200, "Thor", 130, new[] { "skMate1", "skMate2" }, null, "55555");
                var h2 = NameDb.Resolve(0, 200, 130, "Thor", new[] { "skMate1", "skMate2" }, null, null, "55555");
                var h2b = NameDb.Resolve(0, 200, 130, "Thor", new[] { "skMate1", "skMate2" }, null, null, null);
                // MMR/tier signal: two same-party candidates that would TIE (→Hidden) are split by ranked MMR proximity.
                NameDb.Learn("90010", "MmrMatch", 0, 0, "", 200, "Zeus", 130, new[] { "mmrMate1", "mmrMate2" }, null, null, new[] { ("Conquest", 11, 1600) });
                NameDb.Learn("90011", "MmrFar", 0, 0, "", 200, "Zeus", 130, new[] { "mmrMate1", "mmrMate2" }, null, null, new[] { ("Conquest", 11, 2900) });
                var slotRank = new Dictionary<string, (int tier, int mmr)> { ["Conquest"] = (11, 1605) };
                var m1 = NameDb.Resolve(0, 200, 130, "Zeus", new[] { "mmrMate1", "mmrMate2" }, null, null, null, slotRank);   // MMR splits the tie → close one wins
                var m2 = NameDb.Resolve(0, 200, 130, "Zeus", new[] { "mmrMate1", "mmrMate2" }, null);                         // no MMR → tie → Hidden
                File.WriteAllText(Path.Combine(Theme.DataDir, "exclude_test.txt"),
                    "no-exclude:   '" + (a.name ?? "<none>") + "'  (expect ExclAlice)\nexclude-name: '" + (b.name ?? "<none>") + "'  (expect <none>)\nexclude-id:   '" + (c.name ?? "<none>") + "'  (expect <none>)\nlearned-recognized: '" + (d.name ?? "<none>") + "'  (expect Nonkas)\nheal-before:  '" + (hBefore?.Nick ?? "<none>") + "'  (expect <none>)\nheal-after:   '" + (hAfter?.Nick ?? "<none>") + "'  (expect HealMe)\nweak-lone:    '" + (f.name ?? "<none>") + "'  (expect <none>)\nlevel-band:   '" + (g.name ?? "<none>") + "'  (expect ExactHi)\nskin-boost:   '" + (h2.name ?? "<none>") + "' conf+" + (h2.conf - h2b.conf) + "  (expect SkinGuy, conf+ > 0)\nmmr-pick:     '" + (m1.name ?? "<none>") + "'  (expect MmrMatch)\nmmr-tie:      '" + (m2.name ?? "<none>") + "'  (expect <none>)\n");
            } catch (Exception ex) { try { File.WriteAllText(Path.Combine(Theme.DataDir, "exclude_test.txt"), "ERR " + ex.Message); } catch { } } }
            // SMITE_TEST_MATCHER: characterization harness — seed a deterministic synthetic corpus, run a large battery of
            // Resolve() queries, dump "(name|conf)" per query. Snapshot before/after a matcher change → the diff must be empty
            // (golden master), which is the safe way to refactor/optimize the matcher without altering any reveal decision.
            var tmh = Environment.GetEnvironmentVariable("SMITE_TEST_MATCHER");
            if (testDir && !string.IsNullOrEmpty(tmh)) { try { File.WriteAllText(Path.Combine(Theme.DataDir, "matcher_harness.txt"), RunMatcherHarness()); } catch (Exception ex) { try { File.WriteAllText(Path.Combine(Theme.DataDir, "matcher_harness.txt"), "ERR " + ex.GetType().Name + ": " + ex.Message); } catch { } } }
        }

        // ===== smite.guru bulk archiver (SMITE_ARCHIVE) ================================================================
        // Snowball BFS over the social graph: fetch each player full history (saves sguru_<id>.json), pull the 10-player
        // rosters out, enqueue everyone new one hop deeper, repeat — then Phase 2 fetches every reachable match scoreboard
        // (sgmatch_<id>.json). Resumable (archive_crawl.json journal, atomic), gentle (single-flight via SmiteGuru._gate +
        // delay + distress backoff), cancelable on close. BFS-by-depth means the user own sphere is captured first; we will
        // NOT finish a full mirror of a multi-million-player DB, but whatever we grab is the most relevant slice.
        sealed class ArcNode { public long Id { get; set; } public int Depth { get; set; } }
        sealed class ArcState
        {
            public int Version { get; set; } = 1;
            public string Phase { get; set; } = "Histories";   // Histories -> Scoreboards -> Done
            public List<ArcNode> Frontier { get; set; } = new();
            public HashSet<long> Visited { get; set; } = new();
            public int MaxDepth { get; set; } = 2;   // bounds frontier growth; raise via SMITE_ARCHIVE_MAXDEPTH for more breadth
            public int Players { get; set; }
            public int Scoreboards { get; set; }
            public string StartedAt { get; set; } = "";
        }

        // Seeds: explicit SMITE_ARCHIVE_SEED list, the user own profile + in-memory favorites/recents, and every player ALREADY
        // cached in the archive dir (so a re-run keeps snowballing from where it left off even if the journal was lost).
        List<long> SeedArchiveIds(string dir)
        {
            var ids = new List<long>();
            void Add(string s) { if (long.TryParse((s ?? "").Trim(), out var v) && v > 0) ids.Add(v); }
            var env = Environment.GetEnvironmentVariable("SMITE_ARCHIVE_SEED");
            if (!string.IsNullOrEmpty(env)) foreach (var s in env.Split(',', ';')) Add(s);
            Add(settings?.MyProfileId);
            try { foreach (var f in favorites) Add(f?.Id); } catch { }
            try { foreach (var r in recents) Add(r?.Id); } catch { }
            try { foreach (var fn in Directory.GetFiles(dir, "sguru_*.json")) { var n = Path.GetFileNameWithoutExtension(fn); if (n.Length > 6) Add(n.Substring(6)); } } catch { }
            return ids.Distinct().ToList();
        }

        async Task ArchiveCrawl()
        {
            string dir = Theme.DataDir;
            string jp = Path.Combine(dir, "archive_crawl.json");
            string statusFile = Path.Combine(dir, "archive_status.txt");
            _archiveCts ??= new System.Threading.CancellationTokenSource();
            var ct = _archiveCts.Token;
            _sguru ??= new SmiteGuru(this);

            ArcState st;
            try { st = File.Exists(jp) ? (JsonSerializer.Deserialize<ArcState>(File.ReadAllText(jp)) ?? new ArcState()) : new ArcState(); }
            catch { st = new ArcState(); }
            if (st.Version != 1) st = new ArcState();
            if (string.IsNullOrEmpty(st.StartedAt)) st.StartedAt = DateTime.UtcNow.ToString("o");
            if (int.TryParse(Environment.GetEnvironmentVariable("SMITE_ARCHIVE_MAXDEPTH"), out var mdEnv) && mdEnv > 0) st.MaxDepth = mdEnv;

            var seen = new HashSet<long>(st.Visited);
            foreach (var n in st.Frontier) seen.Add(n.Id);
            foreach (var id in SeedArchiveIds(dir)) if (id > 0 && seen.Add(id)) st.Frontier.Add(new ArcNode { Id = id, Depth = 0 });

            void Save() { try { Theme.AtomicWriteText(jp, JsonSerializer.Serialize(st)); } catch { } }
            void Status(string s) { try { File.WriteAllText(statusFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\nphase=" + st.Phase + " players=" + st.Visited.Count + " frontier=" + st.Frontier.Count + " scoreboards=" + st.Scoreboards + " (started " + st.StartedAt + ")"); } catch { } }

            var rnd = new Random();
            int interDelay = 1500;
            if (int.TryParse(Environment.GetEnvironmentVariable("SMITE_ARCHIVE_DELAYMS"), out var dEnv) && dEnv >= 0) interDelay = dEnv;

            // ---- Phase 1: histories (priority BFS by depth → user sphere first) ----
            try
            {
            int zeroStreak = 0, sinceSave = 0;
            while (st.Phase == "Histories" && st.Frontier.Count > 0 && !ct.IsCancellationRequested)
            {
                int bi = 0; for (int i = 1; i < st.Frontier.Count; i++) if (st.Frontier[i].Depth < st.Frontier[bi].Depth) bi = i;
                var node = st.Frontier[bi]; st.Frontier.RemoveAt(bi);
                if (st.Visited.Contains(node.Id)) continue;

                List<long> roster;
                if (SmiteGuru.HasCache(node.Id))
                {
                    roster = SmiteGuru.CachedRosterIds(node.Id);   // already archived → expand the frontier without touching the network
                }
                else
                {
                    Status("fetching history for " + node.Id + " (depth " + node.Depth + ")");
                    var h = await _sguru.GetHistory(node.Id, 400, null, ct);
                    if (h.Matches.Count == 0 && !h.Complete)
                    {
                        st.Frontier.Insert(0, node);   // origin likely unreachable (504) → requeue, back off, bail after a few so a re-run resumes
                        if (++zeroStreak >= 4) { Status("paused — smite.guru origin unreachable; re-run to resume"); Save(); return; }
                        await Task.Delay(8000, ct); continue;
                    }
                    zeroStreak = 0;
                    roster = new List<long>();
                    foreach (var m in h.Matches) if (m?.Players != null) foreach (var p in m.Players) if (p.Id > 0) roster.Add(p.Id);
                    await Task.Delay(interDelay + rnd.Next(0, 750), ct);   // gentle pacing only after a real network fetch
                }
                st.Visited.Add(node.Id); st.Players = st.Visited.Count;
                if (node.Depth + 1 <= st.MaxDepth)
                    foreach (var rid in roster) if (rid > 0 && seen.Add(rid)) st.Frontier.Add(new ArcNode { Id = rid, Depth = node.Depth + 1 });
                if (++sinceSave >= 25) { Save(); sinceSave = 0; }   // debounce: cached histories self-heal the frontier on re-seed, so losing a few frontier entries is harmless
                Status("archived " + node.Id);
            }
            if (st.Phase == "Histories" && st.Frontier.Count == 0 && !ct.IsCancellationRequested) { st.Phase = "Scoreboards"; Save(); }

            // ---- Phase 2: scoreboards — the rich data (10 players + full stats per match), fetched in CONCURRENT batches ----
            if (st.Phase == "Scoreboards" && !ct.IsCancellationRequested)
            {
                int conc = 16; if (int.TryParse(Environment.GetEnvironmentVariable("SMITE_ARCHIVE_SBCONC"), out var cEnv) && cEnv > 0) conc = cEnv;
                long clearId = st.Visited.Count > 0 ? st.Visited.First() : 0;
                int sbZero = 0;
                foreach (var pid in st.Visited.ToList())
                {
                    if (ct.IsCancellationRequested) break;
                    var mids = SmiteGuru.CachedMatchIds(pid).Distinct().Where(m => !SmiteGuru.HasMatch(m)).ToList();
                    for (int k = 0; k < mids.Count && !ct.IsCancellationRequested; k += conc)
                    {
                        var batch = mids.Skip(k).Take(conc).ToList();
                        Status("scoreboards x" + batch.Count + " (player " + pid + ")");
                        int saved = await _sguru.FetchMatchDetailsToDisk(clearId, batch, conc, ct);
                        if (saved == 0) { if (++sbZero >= 4) { Status("paused (scoreboards) — origin unreachable; re-run to resume"); Save(); return; } await Task.Delay(8000, ct); continue; }
                        sbZero = 0; st.Scoreboards += saved; Save();
                        await Task.Delay(interDelay + rnd.Next(0, 500), ct);
                    }
                }
                if (!ct.IsCancellationRequested) { st.Phase = "Done"; Save(); Status("DONE"); }
            }
            }
            finally { Save(); }   // always persist the journal on exit (cancel / app close / error) so a re-run resumes cleanly
        }

        // Deterministic matcher characterization harness (SMITE_TEST_MATCHER). No RNG/time-of-day dependence beyond Today (all
        // entries share it), so two runs on the same day are byte-identical — diff a snapshot before vs after a matcher edit.
        string RunMatcherHarness()
        {
            string[] G = { "Thor", "Zeus", "Loki", "Ymir", "Ra", "Anubis", "Kali", "Sol" };
            const int N = 50;
            int ClanOf(int i) => (i % 6 == 0) ? 0 : 1000 + (i % 5);
            int LvlOf(int i) => 30 + (i * 7) % 121;
            int MastOf(int i) => 1 + (i * 17) % 200;
            string GodOf(int i) => G[i % G.Length];
            string SkinOf(int i) => (i % 4 == 0) ? null : "SK" + (i % 6);
            string[] CompsOf(int i) => new[] { "pop" + (i % 3), "rare" + i, "rare" + ((i * 7) % N) };   // 1 popular (low-IDF) + 2 rarer
            string[] NbOf(int i) => new[] { "nb" + (i % 4), "nb" + ((i * 5) % 20) };
            Dictionary<string, (int tier, int mmr)> RankOf(int i) => (i % 3 == 0) ? new Dictionary<string, (int tier, int mmr)> { ["Conquest"] = (5 + (i % 15), 1000 + (i * 53) % 2500) } : null;

            NameDb.Enabled = true;
            NameDb.Clear();
            for (int i = 0; i < N; i++)
            {
                var rk = RankOf(i);
                IEnumerable<(string queue, int tier, int mmr)> ranked = rk?.Select(kv => (kv.Key, kv.Value.tier, kv.Value.mmr));
                NameDb.Learn("H" + i, "P" + i.ToString("00"), 0, ClanOf(i), ClanOf(i) == 0 ? "" : "C" + (i % 5), LvlOf(i), GodOf(i), MastOf(i), CompsOf(i), NbOf(i), SkinOf(i), ranked);
            }
            // identical-fingerprint pair → exercises the tie path (should resolve to nobody)
            NameDb.Learn("TWA", "TwinA", 0, 0, "", 88, "Ra", 120, new[] { "tw1", "tw2", "tw3" }, null);
            NameDb.Learn("TWB", "TwinB", 0, 0, "", 88, "Ra", 120, new[] { "tw1", "tw2", "tw3" }, null);

            var lines = new List<string>();
            void Q(string key, int clan, int lvl, int mast, string god, string[] comp, string[] nb, string[] excl, string skin, IReadOnlyDictionary<string, (int tier, int mmr)> sr)
            {
                var r = NameDb.Resolve(clan, lvl, mast, god, comp, nb, excl, skin, sr);
                lines.Add(key + " => " + (r.name ?? "-") + "|" + r.conf);
            }
            for (int i = 0; i < N; i++)
            {
                int c = ClanOf(i), l = LvlOf(i), m = MastOf(i); string g = GodOf(i), sk = SkinOf(i);
                string[] comp = CompsOf(i), nb = NbOf(i); var sr = RankOf(i); string k = "P" + i.ToString("00");
                Q(k + ".exact", c, l, m, g, comp, nb, null, sk, sr);
                Q(k + ".lvl+1", c, l + 1, m, g, comp, nb, null, sk, sr);
                Q(k + ".lvl-5", c, l - 5, m, g, comp, nb, null, sk, sr);
                Q(k + ".lvl+15", c, l + 15, m, g, comp, nb, null, sk, sr);
                Q(k + ".noComp", c, l, m, g, null, nb, null, sk, sr);
                Q(k + ".partComp", c, l, m, g, comp.Take(2).ToArray(), nb, null, sk, sr);
                Q(k + ".swapComp", c, l, m, g, CompsOf((i + 7) % N), nb, null, sk, sr);
                Q(k + ".noSkin", c, l, m, g, comp, nb, null, null, sr);
                Q(k + ".wrongGod", c, l, m, GodOf((i + 1) % G.Length), comp, nb, null, sk, sr);
                Q(k + ".noMmr", c, l, m, g, comp, nb, null, sk, null);
                Q(k + ".exclSelf", c, l, m, g, comp, nb, new[] { "P" + i.ToString("00") }, sk, sr);
            }
            Q("ZZ.twinTie", 0, 88, 120, "Ra", new[] { "tw1", "tw2", "tw3" }, null, null, null, null);
            lines.Sort(StringComparer.Ordinal);
            return "MATCHER HARNESS  corpus=" + (N + 2) + " queries=" + lines.Count + "\n" + string.Join("\n", lines);
        }

        // --- control factories -------------------------------------------------
        // bg!=null -> solid coloured button (e.g. purple/yellow); accent -> red; else dark with red hover border.
        Button MkBtn(string text, int w, bool accent, Color? bg = null, Color? fg = null)
        {
            bool solid = bg.HasValue || accent;
            Color back = bg ?? (accent ? Theme.Accent : Theme.Input);
            var b = new Button
            {
                Text = text, Width = S(w), Height = S(30), FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, S(6), 0), UseVisualStyleBackColor = false,
                BackColor = solid ? back : Theme.Input,
                ForeColor = solid ? (fg ?? Color.White) : Theme.Text,
                Font = Theme.F(9.5f, solid ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 1;
            if (solid)
            {
                b.FlatAppearance.BorderColor = back;
                b.FlatAppearance.MouseOverBackColor = Theme.Lighten(back);
                b.FlatAppearance.MouseDownBackColor = Theme.Darken(back);
            }
            else
            {
                b.FlatAppearance.BorderColor = Theme.Line;
                b.FlatAppearance.MouseOverBackColor = Theme.Input;
                b.FlatAppearance.MouseDownBackColor = Theme.AccentDk;
                b.MouseEnter += (s, e) => b.FlatAppearance.BorderColor = Theme.Accent;
                b.MouseLeave += (s, e) => b.FlatAppearance.BorderColor = Theme.Line;
            }
            return b;
        }

        CheckBox MkChk(string text, bool ch)
            => new FlatCheck { Text = text, Checked = ch, AutoSize = true, ForeColor = Theme.Dim, BackColor = Theme.Panel, Font = Theme.F(9f), BoxSize = S(15), Margin = new Padding(S(12), S(6), 0, 0) };

        // A 1px panel that turns red while the hosted textbox has focus -> sharp red focus border.
        // Host height is pinned to the textbox's natural height so there is no gray gap below it.
        Panel WrapInput(TextBox tb, int width)
        {
            tb.Dock = DockStyle.Fill;
            var host = new Panel { BackColor = Theme.Line, Padding = new Padding(1), Height = tb.PreferredHeight + 2 };
            if (width > 0) host.Width = width;
            tb.Enter += (s, e) => host.BackColor = Theme.Accent;
            tb.Leave += (s, e) => host.BackColor = Theme.Line;
            host.Controls.Add(tb);
            return host;
        }

        // --- scanning / listing ------------------------------------------------
        static string DefaultConfigPath()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Smite", "BattleGame", "Config");

        void TryAutoLoad()
        {
            try { string def = DefaultConfigPath(); if (Directory.Exists(def)) { folderPath = def; Scan(); } }
            catch { }
        }

        void OpenFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select your SMITE BattleGame\\Config folder";
                string def = folderPath ?? DefaultConfigPath();
                if (Directory.Exists(def)) dlg.SelectedPath = def;
                if (dlg.ShowDialog(this) == DialogResult.OK) { folderPath = dlg.SelectedPath; Scan(); }
            }
        }

        void Scan()
        {
            gods.Clear();
            current = null;
            prms = new List<Param>();
            table.Controls.Clear();
            SetHeader(null);
            applyBtn.Enabled = false;
            reloadBtn.Enabled = false;

            try
            {
                foreach (string file in Directory.GetFiles(folderPath, "Battle*.ini"))
                {
                    string name = Path.GetFileName(file);
                    var m = Regex.Match(name, @"^Battle(.+)\.ini$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string text;
                    try { text = File.ReadAllText(file); } catch { continue; }
                    if (!IsEntity(text)) continue;
                    string bse = m.Groups[1].Value;
                    int pcount = Parse(text).Count;
                    gods.Add(new GodFile
                    {
                        FileName = name, Base = bse, Name = Prettify(bse), Text = text, Path = file,
                        NonGod = NonGods.Contains(bse), ParamCount = pcount
                    });
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not read folder:\n" + ex.Message, "Smite 1 Inspector"); return; }

            gods = gods.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            folderLbl.Text = folderPath;
            LoadGodIcons();
            RenderList();
            if (godBox.Items.Count > 0) godBox.SelectedIndex = 0;   // populate the right panel on load
        }

        void RenderList()
        {
            string q = searchBox.Text.Trim();
            bool all = showAllChk.Checked;
            godBox.BeginUpdate();
            godBox.Items.Clear();
            foreach (var g in gods)
            {
                if (g.NonGod && !all) continue;
                if (q.Length > 0 &&
                    g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    g.Base.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                godBox.Items.Add(g);
            }
            godBox.EndUpdate();
            statusLbl.Text = godBox.Items.Count + " gods listed";
        }

        void GodBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= godBox.Items.Count) return;
            var g = (GodFile)godBox.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = e.Bounds;

            using (var bg = new SolidBrush(sel ? Theme.Accent : Theme.Input))
                e.Graphics.FillRectangle(bg, r);
            if (sel)
                using (var bar = new SolidBrush(Color.White))
                    e.Graphics.FillRectangle(bar, r.Left, r.Top, S(3), r.Height);

            int pad = S(4);
            int sz = r.Height - pad * 2;
            var iconRect = new Rectangle(r.Left + S(8), r.Top + pad, sz, sz);
            DrawGodIcon(e.Graphics, iconRect, g, sel);

            int tx = iconRect.Right + S(9);
            var textRect = new Rectangle(tx, r.Top, r.Right - tx - S(8), r.Height);
            Color fg = sel ? Color.White : Theme.Text;
            TextRenderer.DrawText(e.Graphics, g.Name, Theme.F(10f, FontStyle.Bold), textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string tag = (g.NonGod ? "□ " : "") + g.ParamCount;
            TextRenderer.DrawText(e.Graphics, tag, Theme.F(8f), textRect, sel ? Color.White : Theme.Dim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        void DrawGodIcon(Graphics gr, Rectangle r, GodFile g, bool sel)
        {
            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Image img = null;
            if (g != null) iconCache.TryGetValue(g.Base, out img);
            if (img != null)
            {
                gr.DrawImage(img, r);
            }
            else
            {
                using (var b = new SolidBrush(Theme.Panel)) gr.FillRectangle(b, r);
                if (g != null)
                    TextRenderer.DrawText(gr, Initials(g.Name), Theme.F(9f, FontStyle.Bold), r,
                        sel ? Color.White : Theme.Dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            using (var pen = new Pen(sel ? Color.White : Theme.Accent, 1))
                gr.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        static string Initials(string name)
        {
            var parts = name.Split(new[] { ' ', '-', '\'' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            string s = char.ToUpperInvariant(parts[0][0]).ToString();
            if (parts.Length > 1) s += char.ToUpperInvariant(parts[1][0]);
            return s;
        }

        // --- god header --------------------------------------------------------
        void SetHeader(GodFile g)
        {
            nameLbl.Text = g != null ? g.Name : "";
            fileLbl.Text = g != null ? g.FileName : "";
            headIcon.Invalidate();
        }

        void HeadIcon_Paint(object sender, PaintEventArgs e)
        {
            if (current == null) return;
            DrawGodIcon(e.Graphics, new Rectangle(0, 0, headIcon.Width, headIcon.Height), current, false);
        }

        // --- god rows ----------------------------------------------------------
        void LoadGod(GodFile g)
        {
            current = g;
            prms = Parse(g.Text);
            LoadDefaults(g);
            SetHeader(g);
            reloadBtn.Enabled = true;
            restoreBtn.Enabled = defaults.Count > 0;
            addBtn.Enabled = AvailableTunables().Count > 0;
            inspectBtn.Enabled = SdkInspect.Get(g.Base) != null;
            RenderRows();
        }

        static string DKey(Param p) => (p.Section ?? "") + "" + p.Key;

        // Snapshot each god's values the first time it's seen (defaults\<Base>.json), so edits can be
        // reverted to the pristine originals even after saving. The SDK has no default *values*.
        void LoadDefaults(GodFile g)
        {
            defaults = new Dictionary<string, string>();
            try
            {
                string dir = Path.Combine(Theme.DataDir, "defaults");
                string f = Path.Combine(dir, g.Base + ".json");
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var kv in doc.RootElement.EnumerateObject())
                        defaults[kv.Name] = kv.Value.GetString();
                }
                else
                {
                    foreach (var p in prms) defaults[DKey(p)] = p.Original;
                    Directory.CreateDirectory(dir);
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                    {
                        w.WriteStartObject();
                        foreach (var kv in defaults) w.WriteString(kv.Key, kv.Value);
                        w.WriteEndObject();
                    }
                    File.WriteAllBytes(f, ms.ToArray());
                }
            }
            catch { }
        }

        string DefaultOf(Param p) => defaults.TryGetValue(DKey(p), out var v) ? v : null;

        void RenderRows()
        {
            table.SuspendLayout();
            // dispose the previous render's controls (Controls.Clear() only un-parents them) and drop their
            // stale tooltip associations, so handles/fonts/tooltip entries don't accumulate per god switch.
            tip.RemoveAll();
            var oldRows = table.Controls.Cast<Control>().ToArray();
            table.Controls.Clear();
            foreach (var c in oldRows) c.Dispose();
            table.RowStyles.Clear();
            table.RowCount = 0;
            bool help = showHelpChk.Checked;

            // Bucket every parameter into an ability slot (Passive / A1..A4) or a named kit / general group,
            // then lay them out in canonical order: Passive, A1, A2, A3, A4, then kit pieces / general (file order).
            var groups = new List<AbilityGroup>();
            var byKey = new Dictionary<string, AbilityGroup>();
            int namedSeq = 0;
            foreach (var p in prms)
            {
                var c = ClassifyAbility(p);
                if (!byKey.TryGetValue(c.Key, out var g))
                {
                    g = new AbilityGroup
                    {
                        Key = c.Key, Label = c.Label, Badge = c.Badge,
                        Ability = c.Ability, Ult = c.Ult,
                        Order = c.Ability ? c.Slot : 100 + (namedSeq++)
                    };
                    if (c.Ability)
                    {
                        g.SlotKey = c.Slot == 0 ? "P" : c.Slot.ToString();
                        var info = current != null ? AbilityData.Get(current.Base, g.SlotKey) : null;
                        if (info != null) { g.Name = info.Name; g.Slug = info.Slug; }
                    }
                    byKey[c.Key] = g;
                    groups.Add(g);
                }
                g.Items.Add(p);
            }
            groups = groups.OrderBy(g => g.Order).ToList();   // OrderBy is stable -> file order kept within equal Order

            foreach (var g in groups)
            {
                var hdr = BuildAbilityHeader(g);
                hdr.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                AddSpan(hdr);
                foreach (var p in g.Items) AddParamRow(p, help);
            }

            table.ResumeLayout();
            UpdateApply();
        }

        void AddParamRow(Param p, bool help)
        {
            var cp = p;
            string typeKey = Regex.Replace(p.Key, @"\[\d+\]$", "");
            string sdkType = SdkData.TypeOf(p.Section, typeKey);
            string def = DefaultOf(p);
            string kind = EditorKind(sdkType, p.Value);   // "bool" | "num" | "text"

            // added values are tinted by source: purple = "Add value", yellow = "SDK Inspector"
            bool isNew = cp.IsNew;
            Color baseBg = isNew ? (cp.Source == 2 ? Theme.YellowTint : Theme.PurpleTint) : Theme.Input;
            Color tagColor = cp.Source == 2 ? Theme.Yellow : Theme.Purple;

            var keyLbl = new Label { Text = p.Key, AutoSize = true, ForeColor = isNew ? tagColor : Theme.Text, Font = Theme.F(9.5f, isNew ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(S(10), S(8), S(6), S(2)) };
            string ktip = p.Comment ?? "";
            if (!string.IsNullOrEmpty(p.Section)) ktip = (ktip.Length > 0 ? ktip + "\n\n" : "") + "[" + p.Section + "]";
            if (sdkType != null) ktip += "\n\ntype: " + sdkType;
            if (def != null) ktip += (sdkType == null ? "\n\n" : "  ·  ") + "default: " + def;
            if (ktip.Length > 0) tip.SetToolTip(keyLbl, ktip);

            Control editor;
            Action revert;    // ⟲ revert the unsaved edit back to the value as loaded

            if (kind == "bool")
            {
                bool numeric = p.Value.Trim() == "0" || p.Value.Trim() == "1";
                var tg = new Button { Width = S(150), Height = S(26), FlatStyle = FlatStyle.Flat, Font = Theme.F(9f, FontStyle.Bold), Margin = new Padding(S(2), S(5), S(2), S(2)), Cursor = Cursors.Hand, Anchor = AnchorStyles.Left | AnchorStyles.Top };
                tg.FlatAppearance.BorderSize = 1;
                Func<bool> isOn = () => { var v = cp.Value.Trim().ToLowerInvariant(); return v == "true" || v == "1" || v == "yes"; };
                Action paint = () =>
                {
                    bool on = isOn();
                    tg.Text = on ? "ON" : "OFF";
                    tg.BackColor = on ? (isNew ? tagColor : Theme.Accent) : baseBg;
                    tg.ForeColor = on ? Color.White : (isNew ? tagColor : Theme.Dim);
                    tg.FlatAppearance.BorderColor = isNew ? tagColor : ((cp.Value != cp.Original) ? Theme.AccentHi : (on ? Theme.Accent : Theme.Line));
                };
                tg.Click += (s, e) =>
                {
                    bool on = isOn();
                    cp.Value = numeric ? (on ? "0" : "1") : (on ? "false" : "true");
                    paint(); UpdateApply();
                };
                revert = () => { cp.Value = cp.Original; paint(); UpdateApply(); };
                paint();
                editor = tg;
            }
            else
            {
                bool numeric = kind == "num";
                var box = new TextBox { Text = p.Value, BorderStyle = BorderStyle.None, BackColor = baseBg, ForeColor = Theme.Text, Font = Theme.Mono(10f), TextAlign = HorizontalAlignment.Right };
                var host = WrapInput(box, numeric ? S(108) : S(150));
                host.Anchor = AnchorStyles.Left | AnchorStyles.Top;
                host.Margin = new Padding(S(2), S(5), 0, S(2));
                if (isNew) host.BackColor = tagColor;   // 1px source-coloured border
                var cbx = box;
                Action paint = () =>
                {
                    bool bad = numeric && !TryNum(cbx.Text, out _);
                    cbx.BackColor = isNew ? baseBg : ((cp.Value != cp.Original) ? Theme.Dirty : Theme.Input);
                    cbx.ForeColor = bad ? Theme.AccentHi : Theme.Text;
                };
                box.TextChanged += (s, e) => { cp.Value = cbx.Text; paint(); UpdateApply(); };
                revert = () => { cbx.Text = cp.Original; };

                if (numeric)
                {
                    var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.Top };
                    flow.Controls.Add(host);
                    flow.Controls.Add(MkStep("−", () => Nudge(cbx, -1)));
                    flow.Controls.Add(MkStep("+", () => Nudge(cbx, +1)));
                    editor = flow;
                }
                else editor = host;
            }

            var rst = new Button { Text = "⟲", Width = S(34), Height = S(26), FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Dim, Font = Theme.F(10f), Margin = new Padding(S(2), S(5), S(2), S(2)), Cursor = Cursors.Hand };
            rst.FlatAppearance.BorderSize = 1;
            rst.FlatAppearance.BorderColor = Theme.Line;
            rst.FlatAppearance.MouseOverBackColor = Theme.Input;
            rst.MouseEnter += (s, e) => { rst.FlatAppearance.BorderColor = Theme.Accent; rst.ForeColor = Theme.Accent; };
            rst.MouseLeave += (s, e) => { rst.FlatAppearance.BorderColor = Theme.Line; rst.ForeColor = Theme.Dim; };
            rst.Click += (s, e) => revert();
            tip.SetToolTip(rst, "Revert to " + cp.Original + (def != null && def != cp.Original ? "   (default: " + def + ")" : ""));

            int r = table.RowCount;
            table.RowCount = r + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(keyLbl, 0, r);
            table.Controls.Add(editor, 1, r);
            table.Controls.Add(rst, 2, r);

            // show the help line when "Show help" is on, OR always for added rows (so the source tag shows)
            if ((help && !string.IsNullOrEmpty(p.Comment)) || isNew)
            {
                string badge = BadgeText(p.Prefix);
                string tail = sdkType != null ? "   ·   " + sdkType : "";
                if (def != null) tail += "   ·   default " + def;
                string txt = (isNew ? (cp.Source == 2 ? "✦ ADDED via SDK Inspector — " : "✦ ADDED via Add value — ") : "")
                             + (badge.Length > 0 ? badge + "   " : "") + (p.Comment ?? "") + tail;
                var cl = new Label { Text = txt, AutoSize = true, ForeColor = isNew ? tagColor : Theme.Dim, Font = Theme.F(8.5f), Margin = new Padding(S(14), 0, S(2), S(9)) };
                AddSpan(cl);
            }
        }

        static string EditorKind(string sdkType, string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (sdkType == "bool" || v == "true" || v == "false") return "bool";
            if (sdkType == "float" || sdkType == "int") return "num";
            if (sdkType == "vector") return "text";
            if (sdkType == null && TryNum(value, out _) && !value.TrimStart().StartsWith("(")) return "num";
            return "text";
        }

        static bool TryNum(string s, out double d)
        {
            d = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.EndsWith("f") || t.EndsWith("F")) t = t.Substring(0, t.Length - 1);   // ini floats like 25.f
            return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d);
        }

        Button MkStep(string text, Action onClick)
        {
            var b = new Button { Text = text, Width = S(28), Height = S(26), FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(11f, FontStyle.Bold), Margin = new Padding(S(2), S(5), 0, S(2)), Cursor = Cursors.Hand, TabStop = false };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Line;
            b.FlatAppearance.MouseOverBackColor = Theme.Input;
            b.MouseEnter += (s, e) => b.FlatAppearance.BorderColor = Theme.Accent;
            b.MouseLeave += (s, e) => b.FlatAppearance.BorderColor = Theme.Line;
            b.Click += (s, e) => onClick();
            return b;
        }

        // Step a numeric textbox by a magnitude-aware increment, preserving integer-ness.
        static void Nudge(TextBox box, int dir)
        {
            string t = box.Text.Trim();
            if (!TryNum(t, out double v)) return;
            bool fSuffix = t.EndsWith("f") || t.EndsWith("F");   // ini floats like "25.f" / "0.2f"
            bool isInt = !t.Contains('.');                       // ini ints have no decimal point
            double a = Math.Abs(v);
            double step = a >= 1000 ? 50 : a >= 100 ? 10 : a >= 10 ? 1 : a >= 1 ? 0.5 : 0.05;
            if (isInt && step < 1) step = 1;
            double nv = v + dir * step;
            string text = isInt ? ((long)Math.Round(nv)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : nv.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            box.Text = (fSuffix && !isInt) ? text + "f" : text;
        }

        void AddSpan(Control c)
        {
            int r = table.RowCount;
            table.RowCount = r + 1;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(c, 0, r);
            table.SetColumnSpan(c, 3);
        }

        // ---- ability grouping ----
        class AbilityGroup
        {
            public string Key, Label, Badge, SlotKey, Name, Slug;
            public bool Ability, Ult;
            public int Order;
            public List<Param> Items = new List<Param>();
        }

        // Decide which ability a parameter belongs to. Primary signal is the .ini section suffix
        // (_Psv, _A01.._A04, incl. _A04_Sub / _A03_V3 / TgDeployable_..._A04); fall back to an explicit
        // A0N tag or the word "passive" in the key/comment for values that live in the base pawn section.
        (string Key, string Label, string Badge, int Slot, bool Ability, bool Ult) ClassifyAbility(Param p)
        {
            string sec = p.Section ?? "";
            // 1) authoritative slot from the SDK class hierarchy (resolves named/variant sections)
            string slot = SdkData.Slot(sec);
            // 2) fallbacks: improved section-name regex (A0N/A0Na/B0N), then an A0N tag or "passive" in the comment
            if (slot == null)
            {
                var m = Regex.Match(sec, @"_[AB]0?([1-4])(?![0-9])");
                if (m.Success) slot = m.Groups[1].Value;
                else
                {
                    var m2 = Regex.Match(p.Key + " " + (p.Comment ?? ""), @"\bA0?([1-4])\b", RegexOptions.IgnoreCase);
                    if (m2.Success) slot = m2.Groups[1].Value;
                    else if (Regex.IsMatch(sec, @"_Psv|Passive|_PSV", RegexOptions.IgnoreCase)
                             || Regex.IsMatch(p.Key, @"passive", RegexOptions.IgnoreCase)
                             || Regex.IsMatch(p.Comment ?? "", @"\bpassive\b", RegexOptions.IgnoreCase))
                        slot = "P";
                }
            }
            if (slot != null)
            {
                if (slot == "P") return ("PSV", "PASSIVE", "P", 0, true, false);
                if (int.TryParse(slot, out int n))   // malformed slot must not crash god loading
                {
                    if (n == 4) return ("A4", "ULTIMATE", "4", 4, true, true);
                    return ("A" + n, "ABILITY " + n, n.ToString(), n, true, false);
                }
            }

            string suffix = PrettifySuffix(DeviceSuffix(sec));
            if (suffix.Length == 0) return ("GEN", "GENERAL", "G", 0, false, false);
            string up = suffix.ToUpperInvariant();
            return ("N:" + up, up, up.Substring(0, 1), 0, false, false);
        }

        string DeviceSuffix(string sec)
        {
            string s = Regex.Replace(sec ?? "", @"^TgGame\.Tg\w+?_", "");   // strip "TgGame.TgDevice_" / "TgPawn_" / "TgDeployable_"
            string b = current?.Base ?? "";
            if (b.Length > 0 && s.StartsWith(b, StringComparison.OrdinalIgnoreCase)) s = s.Substring(b.Length);
            s = Regex.Replace(s, @"^_*V\d+", "");   // drop a version token like V3
            return s.TrimStart('_');
        }

        static string PrettifySuffix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("_", " ");
            s = Regex.Replace(s, @"([a-z0-9])([A-Z])", "$1 $2");   // BigCat -> Big Cat
            return s.Trim();
        }

        // A flat, sharp ability header: the real ability icon + name when known, otherwise a
        // red letter/number badge (ultimate is filled). Kit pieces / general get a muted gray badge.
        Panel BuildAbilityHeader(AbilityGroup g)
        {
            var panel = new Panel { Height = S(40), Margin = new Padding(S(2), S(16), S(2), S(6)), BackColor = Theme.Bg };
            panel.Resize += (s, e) => panel.Invalidate();
            panel.Paint += (s, e) =>
            {
                var gr = e.Graphics;
                gr.SmoothingMode = SmoothingMode.None;
                int sz = S(30);
                var rect = new Rectangle(S(2), (panel.Height - sz) / 2, sz, sz);

                Image abImg = g.Ability ? AbilityIcon(g.Slug) : null;
                if (abImg != null)
                {
                    gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gr.DrawImage(abImg, rect);
                    using (var pen = new Pen(Theme.Accent, 1)) gr.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                }
                else
                {
                    Color border = g.Ability ? Theme.Accent : Theme.Line;
                    using (var b = new SolidBrush(g.Ult ? Theme.Accent : Theme.Panel)) gr.FillRectangle(b, rect);
                    using (var pen = new Pen(border, 2)) gr.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                    Color glyph = g.Ult ? Color.White : (g.Ability ? Theme.Accent : Theme.Dim);
                    TextRenderer.DrawText(gr, g.Badge, Theme.F(12f, FontStyle.Bold), rect, glyph,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }

                // right side: slot tag (PASSIVE / ABILITY 1 / ULTIMATE) + value count
                string cnt = g.Items.Count + (g.Items.Count == 1 ? " value" : " values");
                bool named = g.Ability && !string.IsNullOrEmpty(g.Name);
                string right = named ? (g.Label + "   ·   " + cnt) : cnt;
                var rf = Theme.F(8.5f);
                int rw = TextRenderer.MeasureText(gr, right, rf).Width + S(6);
                TextRenderer.DrawText(gr, right, rf, new Rectangle(0, 0, panel.Width - S(6), panel.Height), Theme.Dim,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                // main label: ability name when known, else the generic slot label
                string main = named ? g.Name : g.Label;
                int lx = rect.Right + S(12);
                var lr = new Rectangle(lx, 0, Math.Max(S(40), panel.Width - lx - rw - S(14)), panel.Height);
                TextRenderer.DrawText(gr, main, Theme.F(12f, FontStyle.Bold), lr, g.Ability ? Theme.Text : Theme.Dim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                using (var pen = new Pen(Theme.Line, 1)) gr.DrawLine(pen, rect.Left, panel.Height - 1, panel.Width - S(6), panel.Height - 1);
            };
            return panel;
        }

        static string BadgeText(string prefix)
        {
            switch (prefix)
            {
                case "r": return "[REPLICATED · server→client]";
                case "s": return "[SERVER]";
                case "c": return "[CLIENT · likely applies in solo]";
                case "m": return "[MEMBER]";
                default:  return "";
            }
        }

        void UpdateApply()
        {
            int dirty = prms.Count(p => p.IsNew || p.Value != p.Original);
            applyBtn.Enabled = dirty > 0;
            statusLbl.Text = dirty > 0
                ? (dirty + " unsaved change" + (dirty > 1 ? "s" : ""))
                : (current != null ? current.FileName : "");
        }

        void Apply()
        {
            if (current == null) return;
            var changed = prms.Where(p => !p.IsNew && p.Value != p.Original).ToList();
            var added = prms.Where(p => p.IsNew).ToList();
            if (changed.Count == 0 && added.Count == 0) return;
            // ';' starts an inline comment in the config format with no escape, so a value containing it would be silently
            // truncated on the next reload (and leave a stray comment in the file). Block the save with a clear message.
            var badSemi = changed.Concat(added).Where(p => p.Value != null && p.Value.Contains(';')).ToList();
            if (badSemi.Count > 0) { MessageBox.Show(this, "These values contain ';', which the game config uses to start a comment and can't be saved safely:\n\n  " + string.Join("\n  ", badSemi.Select(p => p.Key + " = " + p.Value)), "Smite 1 Inspector"); return; }
            try
            {
                var enc = new UTF8Encoding(false);
                var lines = new List<string>(current.Text.Split('\n'));
                foreach (var p in changed)
                    if (p.LineIndex >= 0 && p.LineIndex < lines.Count)
                        lines[p.LineIndex] = SetLineValue(lines[p.LineIndex], p.Value);

                bool crlf = current.Text.Contains("\r\n");   // robust: don't rely on line 0 alone
                string cr = crlf ? "\r" : "";
                foreach (var p in added)
                {
                    string newLine = p.Key + "=" + p.Value + " ; added in Smite 1 Inspector (overrides game default)" + cr;
                    int hdr = FindSectionHeader(lines, p.Section);
                    if (hdr >= 0) lines.Insert(hdr + 1, newLine);
                    else
                    {
                        string body = p.Section.StartsWith("TgGame.", StringComparison.OrdinalIgnoreCase) ? p.Section : "TgGame." + p.Section;
                        lines.Add("" + cr); lines.Add("[" + body + "]" + cr); lines.Add(newLine);
                    }
                }

                string newText = string.Join("\n", lines);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string bak = Path.Combine(folderPath, current.FileName + "." + stamp + ".bak");
                File.WriteAllText(bak, current.Text, enc);
                // ATOMIC write of the user's game config: temp + File.Replace so a crash mid-write can never leave a
                // half-written/corrupt config (the timestamped .bak above is the extra safety net).
                string ctmp = current.Path + ".tmp";
                File.WriteAllText(ctmp, newText, enc);
                if (File.Exists(current.Path)) File.Replace(ctmp, current.Path, null); else File.Move(ctmp, current.Path);

                int total = changed.Count + added.Count;
                current.Text = newText;
                LoadGod(current);   // re-parse so inserted keys get a real LineIndex and IsNew clears
                statusLbl.Text = "Saved " + total + " change" + (total > 1 ? "s" : "")
                    + (added.Count > 0 ? " (" + added.Count + " added)" : "") + "  ·  backup: " + Path.GetFileName(bak);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed:\n" + ex.Message, "Smite 1 Inspector");
            }
        }

        static int FindSectionHeader(List<string> lines, string section)
        {
            // section may or may not already carry the "TgGame." prefix
            string body = section.StartsWith("TgGame.", StringComparison.OrdinalIgnoreCase) ? section : "TgGame." + section;
            string target = "[" + body + "]";
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Trim() == target) return i;
            return -1;
        }

        // True if it's OK to discard the current in-memory God-Inspector edits (none pending, or the user confirmed).
        bool ConfirmDiscardEdits()
        {
            if (current == null || prms == null) return true;
            int dirty = prms.Count(p => p.IsNew || p.Value != p.Original);
            if (dirty == 0) return true;
            return MessageBox.Show(this, dirty + " unsaved change" + (dirty > 1 ? "s" : "") + " to " + current.FileName + " will be lost.\n\nSwitch anyway?",
                "Discard changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }
        void ReloadFile()
        {
            if (current == null) return;
            int dirty = prms.Count(p => p.IsNew || p.Value != p.Original);
            if (dirty > 0 && MessageBox.Show(this,
                    dirty + " unsaved change" + (dirty > 1 ? "s" : "") + " will be lost.\n\nReload " + current.FileName + " from disk anyway?",
                    "Reload file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try { current.Text = File.ReadAllText(current.Path); LoadGod(current); statusLbl.Text = "Reloaded from disk"; }
            catch (Exception ex) { MessageBox.Show(this, "Reload failed:\n" + ex.Message, "Smite 1 Inspector"); }
        }

        void RestoreDefaults()
        {
            if (current == null || defaults.Count == 0) return;
            int n = prms.Count(p => DefaultOf(p) != null && p.Value != DefaultOf(p));
            if (n == 0) { statusLbl.Text = "Already at default values."; return; }
            if (MessageBox.Show(this,
                    "Reset every value for " + current.Name + " to its original (first-seen) value?\n\n" +
                    n + " field" + (n > 1 ? "s" : "") + " will change. Nothing is saved until you click Apply changes.",
                    "Restore defaults", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            foreach (var p in prms) { var d = DefaultOf(p); if (d != null) p.Value = d; }
            RenderRows();
            statusLbl.Text = "Restored defaults for " + current.Name + " — review and Apply to save.";
        }

        class Tun
        {
            public string Section, Key, Type;
            public override string ToString() => Key + "   (" + Type + ")   ·   " + Section;
        }

        // SDK CPF_Config properties for the current god's sections that aren't already in its .ini.
        List<Tun> AvailableTunables()
        {
            var res = new List<Tun>();
            if (current == null) return res;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // match AddParamToGod's case-insensitive dedup
            var sections = new List<string>();
            foreach (var p in prms)
            {
                have.Add(p.Section + "" + Regex.Replace(p.Key, @"\[\d+\]$", ""));
                if (!sections.Contains(p.Section)) sections.Add(p.Section);
            }
            foreach (var sec in sections)
            {
                var si = SdkData.Get(sec);
                if (si == null) continue;
                foreach (var kv in si.Props)
                {
                    string key = kv.Key, type = kv.Value;
                    if (type != "float" && type != "int" && type != "bool" && type != "vector") continue;
                    if (key.StartsWith("s_fLiveSpectate")) continue;   // generic engine noise
                    if (have.Contains(sec + "" + key)) continue;
                    res.Add(new Tun { Section = sec, Key = key, Type = type });
                }
            }
            return res.OrderBy(t => t.Section, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string TypeDefaultValue(string type)
        {
            switch (type)
            {
                case "bool": return "false";
                case "int": case "byte": case "enum": return "0";
                case "vector": return "(X = 0.0, Y = 0.0, Z = 0.0)";
                default: return "0.0";
            }
        }

        // Add a brand-new tunable to the current god (source 1 = Add value/purple, 2 = SDK Inspector/yellow).
        // Returns false if it's already present. Section may be bare or "TgGame."-prefixed.
        bool AddParamToGod(string section, string key, string value, int source)
        {
            string sec = section.StartsWith("TgGame.", StringComparison.OrdinalIgnoreCase) ? section : "TgGame." + section;
            string baseKey = Regex.Replace(key, @"\[\d+\]$", "");
            if (prms.Any(p => string.Equals(p.Section, sec, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(Regex.Replace(p.Key, @"\[\d+\]$", ""), baseKey, StringComparison.OrdinalIgnoreCase)))
                return false;
            var pm = Regex.Match(key, @"^([a-zA-Z]+)_");
            prms.Add(new Param
            {
                Key = key, Value = value, Original = value, IsNew = true, Source = source,
                Comment = source == 2 ? "added from SDK Inspector (overrides game default)" : "added (overrides game default)",
                Section = sec, Prefix = pm.Success ? pm.Groups[1].Value.ToLowerInvariant() : "", LineIndex = -1
            });
            return true;
        }

        void AddTunable()
        {
            var avail = AvailableTunables();
            if (avail.Count == 0) { statusLbl.Text = "No additional SDK tunables for this god."; return; }

            using (var dlg = new Form())
            {
                dlg.Text = "Add tunable  —  " + current.Name;
                dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(S(520), S(446));

                var warn = new Label
                {
                    Dock = DockStyle.Top, Height = S(82), ForeColor = Theme.AccentHi, BackColor = Theme.Bg,
                    Font = Theme.F(8.5f), Padding = new Padding(S(12), S(10), S(12), S(2)),
                    Text = "⚠  This writes a new key to the god's .ini, overriding the game's hidden default. " +
                           "The SDK has no default value, so set a sensible one. A timestamped .bak is made on Apply."
                };
                var lst = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.F(9.5f), IntegralHeight = false };
                foreach (var t in avail) lst.Items.Add(t);

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(78), BackColor = Theme.Panel };
                var vlbl = new Label { Text = "Value:", AutoSize = true, ForeColor = Theme.Dim, Location = new Point(S(12), S(14)) };
                var vbox = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Mono(10f) };
                var vhost = WrapInput(vbox, S(220)); vhost.Location = new Point(S(70), S(10));
                var ok = MkBtn("Add", 86, false, Theme.Purple, Color.White); ok.Location = new Point(S(310), S(10));
                var cancel = MkBtn("Cancel", 86, false); cancel.Location = new Point(S(404), S(10));
                lst.SelectedIndexChanged += (s, e) => { if (lst.SelectedItem is Tun t) vbox.Text = TypeDefaultValue(t.Type); };
                bottom.Controls.Add(vlbl); bottom.Controls.Add(vhost); bottom.Controls.Add(ok); bottom.Controls.Add(cancel);

                dlg.Controls.Add(lst); dlg.Controls.Add(bottom); dlg.Controls.Add(warn);
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
                if (avail.Count > 0) lst.SelectedIndex = 0;
                try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }

                if (dlg.ShowDialog(this) != DialogResult.OK || !(lst.SelectedItem is Tun pick)) return;
                if (!AddParamToGod(pick.Section, pick.Key, vbox.Text, 1))
                {
                    statusLbl.Text = pick.Key + " is already in this god's .ini.";
                    return;
                }
                RenderRows();
                addBtn.Enabled = AvailableTunables().Count > 0;
                statusLbl.Text = "Added " + pick.Key + " — set its value and click Apply changes.";
            }
        }

        static string SlotLabel(string slot)
        {
            switch (slot)
            {
                case "P": return "Passive";
                case "1": return "Ability 1";
                case "2": return "Ability 2";
                case "3": return "Ability 3";
                case "4": return "Ultimate";
                default: return null;
            }
        }

        // Read-only listing of every SDK data member for the god's classes, with flags and an
        // editable marker (CPF_Config = loads from the .ini; everything else is runtime/internal).
        void ShowSdkInspector()
        {
            if (current == null) return;
            var rows = SdkInspect.Get(current.Base);
            if (rows == null) { statusLbl.Text = "No SDK data for this god."; return; }

            using (var dlg = new Form())
            {
                dlg.Text = "SDK Inspector  —  " + current.Name;
                dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(S(940), S(660));
                dlg.MinimumSize = new Size(S(620), S(420));

                var top = new Panel { Dock = DockStyle.Top, Height = S(76), BackColor = Theme.Panel };
                var legend = new Label
                {
                    Dock = DockStyle.Top, Height = S(40), ForeColor = Theme.Dim, BackColor = Theme.Panel,
                    Font = Theme.F(8.5f), Padding = new Padding(S(12), S(8), S(12), S(2)),
                    Text = "✓ = CPF_Config — the game loads it from the .ini, so it's editable here.  " +
                           "Everything else (Net = live match state, no flag = internal) is read-only and can't be set via .ini."
                };
                var srch = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10f) };
                try { srch.PlaceholderText = "Filter properties…"; } catch { }
                var srchHost = WrapInput(srch, S(260)); srchHost.Location = new Point(S(12), S(44));
                var chk = MkChk("Editable (CPF_Config) only", false); chk.Location = new Point(S(300), S(46)); chk.BackColor = Theme.Panel;
                var inh = MkChk("Show inherited", true); inh.Location = new Point(S(548), S(46)); inh.BackColor = Theme.Panel;
                var count = new Label { AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Location = new Point(S(712), S(48)), Text = "" };
                top.Controls.Add(srchHost); top.Controls.Add(chk); top.Controls.Add(inh); top.Controls.Add(count); top.Controls.Add(legend);

                var lv = new ListView
                {
                    Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                    BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
                    Font = Theme.F(9.5f), ShowGroups = true, HideSelection = true
                };
                lv.Columns.Add("Property", S(280));
                lv.Columns.Add("Type", S(72));
                lv.Columns.Add("Flags", S(230));
                lv.Columns.Add("Ini", S(58));
                lv.Columns.Add("Declared in", S(224));

                // compose each leaf class's full inheritance chain once (own + inherited)
                var composed = rows.Select(r => new KeyValuePair<SdkClassRow, List<SdkMember>>(r, SdkInspect.ChainMembers(r.Cls))).ToList();
                Func<string, string> shortCls = c => (c != null && c.Length > 1 && (c[0] == 'A' || c[0] == 'U') && char.IsUpper(c[1])) ? c.Substring(1) : c;

                Action rebuild = () =>
                {
                    string q = srch.Text.Trim();
                    bool cfgOnly = chk.Checked;
                    bool showInh = inh.Checked;
                    lv.BeginUpdate();
                    lv.Items.Clear(); lv.Groups.Clear();
                    int shownCls = 0, shownMem = 0, editable = 0;
                    foreach (var kv in composed)
                    {
                        var row = kv.Key;
                        bool secMatch = q.Length == 0 || row.Sec.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                        || (row.Cat != null && row.Cat.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                        var matched = kv.Value.Where(m =>
                            (showInh || m.DeclaredIn == row.Cls) &&
                            (!cfgOnly || m.Cfg) &&
                            (secMatch || m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                        if (matched.Count == 0 && !(secMatch && !cfgOnly && q.Length > 0)) continue;

                        string sl = SlotLabel(row.Slot);
                        string head = "[" + row.Sec + "]   ·   " + row.Cat
                                      + (sl != null ? "   ·   " + sl : "")
                                      + (row.Ini ? "   ·   in your .ini" : "   ·   SDK only");
                        var grp = new ListViewGroup(head);
                        lv.Groups.Add(grp);
                        shownCls++;
                        if (matched.Count == 0)
                        {
                            var ph = new ListViewItem(new[] { "(no matching members)", "", "", "", "" }) { Group = grp };
                            ph.UseItemStyleForSubItems = true; ph.ForeColor = Theme.Line;
                            lv.Items.Add(ph);
                        }
                        else foreach (var m in matched)
                        {
                            bool own = m.DeclaredIn == row.Cls;
                            var it = new ListViewItem(new[] { m.Name, m.Type, m.Flags, m.Cfg ? "✓ yes" : "—", own ? "" : shortCls(m.DeclaredIn) }) { Group = grp };
                            it.UseItemStyleForSubItems = true;
                            it.ForeColor = m.Cfg ? Theme.Text : Theme.Dim;
                            it.Tag = new[] { row.Sec, m.Name, m.Type, m.Cfg ? "1" : "0" };
                            lv.Items.Add(it);
                            shownMem++; if (m.Cfg) editable++;
                        }
                    }
                    lv.EndUpdate();
                    count.Text = shownCls + " classes  ·  " + shownMem + " properties  ·  " + editable + " editable";
                };

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(52), BackColor = Theme.Panel };
                int addedCount = 0;
                var addSel = MkBtn("Add selected to god", 188, false, Theme.Yellow, Color.FromArgb(28, 22, 0));
                addSel.Location = new Point(S(12), S(11)); addSel.Enabled = false;
                var hint = new Label { AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Location = new Point(S(212), S(18)), Text = "Pick an editable (✓) property to add it to this god's .ini (highlighted yellow)." };
                var close = MkBtn("Close", 90, true); close.Location = new Point(dlg.ClientSize.Width - S(104), S(11));
                close.Anchor = AnchorStyles.Right | AnchorStyles.Top; close.DialogResult = DialogResult.OK;
                bottom.Controls.Add(addSel); bottom.Controls.Add(hint); bottom.Controls.Add(close);

                Func<string[]> sel = () => (lv.SelectedItems.Count == 1 ? lv.SelectedItems[0].Tag as string[] : null);
                bool Addable(string[] t)
                {
                    if (t == null || t[3] != "1") return false;                 // must be CPF_Config
                    if (t[2] == "ref") return false;                            // object refs aren't ini values
                    if (t[1].StartsWith("s_fLiveSpectate")) return false;       // generic engine noise (matches AvailableTunables)
                    string sec = "TgGame." + t[0]; string bk = Regex.Replace(t[1], @"\[\d+\]$", "");
                    return !prms.Any(p => string.Equals(p.Section, sec, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(Regex.Replace(p.Key, @"\[\d+\]$", ""), bk, StringComparison.OrdinalIgnoreCase));
                }
                lv.SelectedIndexChanged += (s, e) => addSel.Enabled = Addable(sel());
                addSel.Click += (s, e) =>
                {
                    var t = sel(); if (!Addable(t)) return;
                    if (AddParamToGod(t[0], t[1], TypeDefaultValue(t[2]), 2)) { addedCount++; addSel.Enabled = false; hint.Text = addedCount + " added — set values & Apply after closing."; }
                };

                dlg.Controls.Add(lv); dlg.Controls.Add(bottom); dlg.Controls.Add(top);
                dlg.AcceptButton = close;
                srch.TextChanged += (s, e) => rebuild();
                chk.CheckedChanged += (s, e) => rebuild();
                inh.CheckedChanged += (s, e) => rebuild();
                dlg.Shown += (s, e) => { try { SetWindowTheme(lv.Handle, "DarkMode_Explorer", null); } catch { } rebuild(); };
                try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }
                dlg.ShowDialog(this);

                if (addedCount > 0)
                {
                    RenderRows();
                    addBtn.Enabled = AvailableTunables().Count > 0;
                    statusLbl.Text = "Added " + addedCount + " value" + (addedCount > 1 ? "s" : "") + " from SDK Inspector — set values and Apply.";
                }
            }
        }

        // --- SMITE API player tracker ------------------------------------------
        static string GS(JsonElement e, string p)
            => e.TryGetProperty(p, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : "";
        static int GI(JsonElement e, string p)
        {
            if (!e.TryGetProperty(p, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt32(out var n)) return n;                                            // plain int
                if (v.TryGetInt64(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue); // huge counts
                if (v.TryGetDouble(out var d)) return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue); // 30.0-style tokens
            }
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (int.TryParse(s, out var n2)) return n2;
                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
                    return (int)Math.Clamp(Math.Round(d2), int.MinValue, int.MaxValue);
            }
            return 0;
        }
        // Per-queue ranked tier + MMR from a completed getmatchdetails row. Conquest/Joust/Duel _Tier + Rank_Stat_* all
        // SURVIVE the privacy flag (probed) — so they're a fingerprint for hidden players, not just public ones.
        static readonly string[] RankedQueues = { "Conquest", "Joust", "Duel" };
        static IEnumerable<(string queue, int tier, int mmr)> RankedFromRow(JsonElement r)
        { foreach (var q in RankedQueues) yield return (q, GI(r, q + "_Tier"), GI(r, "Rank_Stat_" + q)); }
        // The hidden SLOT's ranked dict for Resolve — only queues actually ranked (tier>0; unranked reports the 1500 default).
        static IReadOnlyDictionary<string, (int tier, int mmr)> SlotRankFromRow(JsonElement r)
        {
            Dictionary<string, (int, int)> d = null;
            foreach (var q in RankedQueues) { int t = GI(r, q + "_Tier"); if (t > 0) (d ??= new())[q] = (t, GI(r, "Rank_Stat_" + q)); }
            return d;
        }

        // When the user manually names a hidden player, recover that account's REAL player_id via the getplayeridbyname
        // NAME→id leak (verified 2026-06-25: this endpoint returns the real id + portal + privacy_flag even for privacy=y
        // accounts, while getplayer masks the same id to 0). A manual ★ tag on a hidden slot otherwise has NO id (the
        // completed match zeroes it), so it can only re-match by fuzzy fingerprint; anchoring the learned name by the real
        // id makes the SAME account recognised by id in other matches. GUARD: only anchor if the named account is itself
        // PRIVATE (privacy_flag=y) — a public account would not be hidden in the scoreboard, so a privacy=n hit means the
        // typed name belongs to a different (public) player and must not be id-anchored to this hidden slot. Best-effort.
        async Task ConfirmHiddenNameAsync(string nick, int clanId, string clan, int acct, int mast, string god, List<string> companions, List<string> neighbors)
        {
            try
            {
                if (!NameDb.Enabled || string.IsNullOrWhiteSpace(nick)) return;
                var raw = await SmiteApi.Call("getplayeridbyname", nick.Trim());
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return;
                var e0 = doc.RootElement[0];
                string id = e0.TryGetProperty("player_id", out var pe) ? (pe.ValueKind == JsonValueKind.Number ? pe.GetInt64().ToString() : (pe.GetString() ?? "")) : "";
                string priv = e0.TryGetProperty("privacy_flag", out var fe) ? (fe.GetString() ?? "") : "";
                int portal = 0; if (e0.TryGetProperty("portal_id", out var po)) { if (po.ValueKind == JsonValueKind.Number) portal = po.GetInt32(); else int.TryParse(po.GetString(), out portal); }
                if (string.IsNullOrEmpty(id) || id == "0") return;
                if (!string.Equals(priv, "y", StringComparison.OrdinalIgnoreCase)) return;   // anchor ONLY a private account (a hidden slot's real account is private)
                NameDb.Learn(id, nick.Trim(), portal, clanId, clan, acct, god, mast, companions, neighbors);   // id-anchored → recognised by real id in other matches
                NameDb.Save(true);
            }
            catch { }
        }
        // A getplayer row is privacy-flagged when the API tags ret_msg with "Privacy" OR strips every name
        // (a console account still has Name set, so both-blank only happens for a hidden profile).
        static bool IsPrivateRow(JsonElement p)
            => GS(p, "ret_msg").IndexOf("Privacy", StringComparison.OrdinalIgnoreCase) >= 0
               || (string.IsNullOrEmpty(GS(p, "Name")) && string.IsNullOrEmpty(GS(p, "hz_player_name")));
        static string TierName(int t)
        {
            string[] names = { "", "Bronze V", "Bronze IV", "Bronze III", "Bronze II", "Bronze I",
                "Silver V", "Silver IV", "Silver III", "Silver II", "Silver I",
                "Gold V", "Gold IV", "Gold III", "Gold II", "Gold I",
                "Platinum V", "Platinum IV", "Platinum III", "Platinum II", "Platinum I",
                "Diamond V", "Diamond IV", "Diamond III", "Diamond II", "Diamond I",
                "Masters I", "Grandmaster" };
            return (t >= 1 && t < names.Length) ? names[t] : "";
        }

        RadioButton MkRadio(string text, int x, int y) =>
            new RadioButton { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Font = Theme.F(9.5f), Cursor = Cursors.Hand };

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

            H3("Known issues (beta)");
            P("Whispers is the newest and most experimental feature and ships as a beta. Known rough edges: the first connection after launch can be slow or, rarely, fail and need a reconnect (reopen the tab); on a flaky network the engine may drop and re-establish the chat link, briefly showing \"connecting\"; presence can lag a few seconds behind reality; and because delivery is inferred rather than confirmed by the server, a message shown as sent may in rare cases not have been routed. If something misbehaves, use Export Logs on the Whispers tab and share the zip. It never includes your password or your saved conversation history, but the diagnostic logs can contain recent whisper text and your Hi-Rez username — so only share it with someone you trust to help.");

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
                if (lblCounts != null) lblCounts.Text = "Learned names: " + NameDb.PlayerCount.ToString("N0") + "   ·   live rosters cached: " + NameDb.LiveCount.ToString("N0");
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

        static readonly HttpClient _imgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        readonly Dictionary<string, Image> _avatarCache = new Dictionary<string, Image>();
        // Download (and cache) a player's in-game avatar image from its Avatar_URL. Returns null on empty/failure.
        async Task<Image> LoadAvatar(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_avatarCache.TryGetValue(url, out var cached)) { _avatarCache.Remove(url); _avatarCache[url] = cached; return cached; }   // LRU touch (so an on-screen avatar is never the eviction victim); includes negatively-cached nulls
            Image img = null;
            try
            {
                var bytes = await _imgHttp.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                img = new Bitmap(tmp);   // copy so the stream can be disposed
            }
            catch { img = null; }
            // Cache success AND failure (null) so a broken URL isn't re-downloaded on every preview click. Bounded LRU: at the
            // cap, dispose+evict the oldest (least-recently-used) entry so every Bitmap is cache-owned exactly once and freed
            // deterministically (no GDI handle leak past the cap). The LRU touch above keeps the on-screen avatar from eviction.
            if (_avatarCache.Count >= 256)
            {
                var oldest = _avatarCache.Keys.First();
                if (_avatarCache.TryGetValue(oldest, out var ev)) ev?.Dispose();
                _avatarCache.Remove(oldest);
            }
            _avatarCache[url] = img;
            return img;
        }

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
                bool hasQ = qstatus.TryGetValue(key, out var q);
                bool hasS = lastSeen.TryGetValue(key, out var seen);
                bool qFresh = hasQ && (DateTime.Now - q.when).TotalSeconds < 60;   // live backend status (polled every 5s)
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

            // ---- engine wiring (events arrive off-thread -> marshal to UI) ----
            void Ui(Action a) { try { if (IsHandleCreated) BeginInvoke(a); } catch { } }
            if (engine != null)
            {
                engine.Status += st => Ui(() => { SetStatus(st); if (st == "connected") FlushQueued(); else RefreshConnLabel(); });
                engine.Inbound += (sender, text) => Ui(() =>
                {
                    if (!string.IsNullOrEmpty(sender) && sender[0] == (char)1)   // delivery confirmation, not a message
                    {
                        // NOTE: the server echoes ACCEPTED messages even for OFFLINE recipients (it queues them),
                        // so this confirms acceptance — NOT that the recipient is online. Do not infer presence.
                        for (int i = pending.Count - 1; i >= 0; i--) if (pending[i].text == text) { pending.RemoveAt(i); break; }
                        // The server accepted it, so it definitely LEFT — clear any lingering "queued" marker on it
                        // (e.g. a reconnect meant the "connected" state event never fired to flush it).
                        bool cleared = false;
                        foreach (var kv in convs) { foreach (var m in kv.Value.Msgs) if (m.Dir == "out" && m.St == "queued" && m.Text == text) { m.St = ""; cleared = true; break; } if (cleared) break; }
                        if (cleared) { SaveConvs(); if (activeKey != null) RenderThread(activeKey); RenderConvList(true); RefreshConnLabel(); }
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
                    string snip = p.text.Length > 40 ? p.text.Substring(0, 39) + "…" : p.text;
                    // No echo within the window ⇒ not accepted. Blame offline if we know they're offline, else spam.
                    if (convs.ContainsKey(p.key))
                    {
                        bool offline = qstatus.TryGetValue(p.key, out var q) && (DateTime.Now - q.when).TotalSeconds < 90 && q.code == 0;
                        string reason = _gameOpen
                            ? "your Steam SMITE is open — close it; the messenger can't share that account's chat session."
                            : offline
                            ? "they're offline — it wasn't delivered."
                            : "SMITE's spam/repetition filter rejected it — try rephrasing.";
                        AddMsg(p.key, "sys", "\"" + snip + "\" " + reason);
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
                    sb.AppendLine("[Running processes]");
                    sb.AppendLine("  Smite.exe:  " + ProcInfo("Smite"));
                    sb.AppendLine("  Probe5.exe: " + ProcInfo("Probe5"));
                    sb.AppendLine("  steam:      " + ProcInfo("steam"));
                    sb.AppendLine("[Conversations]");
                    sb.AppendLine("  total=" + convs.Count + "  visible=" + visible + "   (counts only — message history is NOT exported)");
                    sb.AppendLine("=====================================================");
                    sb.AppendLine("Included: report.txt, engine-live.log, relay/* (probe5_out.txt, chatcap.log, loginfix.log,");
                    sb.AppendLine("  eosinproc.log, presence.tsv, idname.tsv, myparams.txt, whisper_in/out.txt ...), login.json.");
                    sb.AppendLine("Privacy: relay logs can contain whisper text + your Hi-Rez username. They do NOT contain your");
                    sb.AppendLine("  password. Your conversation history (conversations.json) is NOT included.");

                    using (var zip = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
                    {
                        AddText(zip, "report.txt", sb.ToString());
                        if (engine != null) AddText(zip, "engine-live.log", string.Join("\r\n", engine.RecentLog()));
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
                    if (modeChanged) { try { if (engine != null && engine.Running) engine.Stop(); } catch { } SetStatus("stopped"); }
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
            presTimer.Tick += (s, e) => { bool g = CheckGameOpen(); if (g != _gameOpen) { _gameOpen = g; RefreshConnLabel(); } if (root.Visible) QueryAllPresence(); };
            presTimer.Start();
            this.Activated += (s, e) => { if (root.Visible) QueryAllPresence(); };
            loginBtn.Click += (s, e) => OpenLoginSettings();
            logsBtn.Click += (s, e) => ExportLogs();

            // exposed hooks
            _wsOnShow = () => { _gameOpen = CheckGameOpen(); SetStatus(engine == null ? "stopped" : engine.State); if (engine != null && !engine.Running && LoginReady()) TryStartEngine(); RenderConvList(); QueryAllPresence(); };
            _wsAutoStart = () => { _gameOpen = CheckGameOpen(); if (loginAuto && engine != null && !engine.Running && LoginReady()) TryStartEngine(); SetStatus(engine == null ? "stopped" : engine.State); };
            _openWhisper = (name, id) => StartConvFromName(name, id);
            this.FormClosing += (s, e) => { try { engine?.Stop(); } catch { } };

            RenderConvList();
            return root;
        }

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

        Panel BuildTrackerPanel()
        {
            LoadFavs(); LoadHiddenTags(); LoadRecents();
            NameDb.Load(); NameDb.Enabled = settings.RevealHidden;   // experiment/reveal-hidden-names
            GodBoard.Load();   // god-leaderboard id-leak → smite.guru name cache (experiment 2026-06-25)
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

        // --- god / ability icons (precharged: embedded in the exe; an icons\ folder next to the exe may override) ---
        static string IconsDir => Path.Combine(Theme.AppDir, "icons");

        void LoadGodIcons()
        {
            foreach (var g in gods)
            {
                if (iconCache.ContainsKey(g.Base)) continue;
                var img = LoadIconFile(Path.Combine(IconsDir, g.Base + ".jpg")) ?? EmbeddedThumb("gicon." + g.Base + ".jpg");
                if (img != null) iconCache[g.Base] = img;
            }
        }

        // Ability icon: disk override at icons\abilities\<slug>.jpg, else the embedded aicon.<slug>.jpg (null cached).
        Image AbilityIcon(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return null;
            if (abilityIconCache.TryGetValue(slug, out var img)) return img;
            img = LoadIconFile(Path.Combine(IconsDir, "abilities", slug + ".jpg")) ?? EmbeddedThumb("aicon." + slug + ".jpg");
            abilityIconCache[slug] = img;
            return img;
        }

        // Normalize an API god/item name to the icon key: lowercase, keep only a-z0-9.
        // Must match the Norm() used by _work/gen_api_icons.ps1. Handles underscore god names ("Ne_Zha").
        static string NormName(string s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

        // Full-roster god icon by API display name (e.g. "Ix Chel", "Ne_Zha"); disk override then embedded gx.<norm>.jpg.
        Image GodListIcon(string apiName)
        {
            string k = NormName(apiName); if (k.Length == 0) return null;
            if (godListCache.TryGetValue(k, out var img)) return img;
            img = LoadIconFile(Path.Combine(IconsDir, "godlist", k + ".jpg")) ?? EmbeddedThumb("gx." + k + ".jpg");
            godListCache[k] = img;
            return img;
        }

        // Item icon by API item name (e.g. "Warrior Tabi"); disk override then embedded itm.<norm>.jpg.
        Image ItemIcon(string itemName)
        {
            string k = NormName(itemName); if (k.Length == 0) return null;
            if (itemIconCache.TryGetValue(k, out var img)) return img;
            img = LoadIconFile(Path.Combine(IconsDir, "items", k + ".jpg")) ?? EmbeddedThumb("itm." + k + ".jpg");
            itemIconCache[k] = img;
            return img;
        }

        Image LoadIconFile(string file)
        {
            try { if (File.Exists(file)) return LoadThumbBytes(File.ReadAllBytes(file)); } catch { }
            return null;
        }

        Image EmbeddedThumb(string res)
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return LoadThumbBytes(ms.ToArray());
            }
            catch { return null; }
        }

        // A platform/game logo (smite/steam/xbox/epic/switch) rendered as a circle-clipped px×px badge.
        // Circle clip hides the JPG backgrounds (steam's white corners, smite's blue square) cleanly.
        Image PlatformLogo(string key, int px)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string ck = key + "@" + px;
            if (logoCache.TryGetValue(ck, out var cached)) return cached;
            Image result = null;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.StartsWith("logo." + key + ".", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                {
                    using var s = asm.GetManifestResourceStream(res);
                    using var src = Image.FromStream(s);
                    var bmp = new Bitmap(px, px);
                    try
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
                            {
                                clip.AddEllipse(0, 0, px - 1, px - 1);
                                g.SetClip(clip);
                                g.DrawImage(src, 0, 0, px, px);   // logos are square; fill the circle
                            }
                        }
                        result = bmp;
                    }
                    catch { bmp.Dispose(); throw; }   // don't leak the partial bitmap if drawing fails
                }
            }
            catch { result = null; }
            logoCache[ck] = result;
            return result;
        }

        // SMITE region string -> ISO flag code (baked-in flag.<code>.png). null -> no flag, region shows as text only.
        static string FlagCodeForRegion(string region)
        {
            if (string.IsNullOrEmpty(region)) return null;
            string r = region.ToLowerInvariant();
            if (r.Contains("brazil") || r.Contains("brasil")) return "br";
            if (r.Contains("latin")) return "mx";
            if (r.Contains("europe")) return "eu";
            if (r.Contains("north america") || r.Contains("americas")) return "us";
            if (r.Contains("australia") || r.Contains("oceania")) return "au";
            if (r.Contains("asia") || r.Contains("singapore")) return "sg";
            if (r.Contains("china")) return "cn";
            if (r.Contains("russia")) return "ru";
            return null;
        }

        // Rectangular region flag scaled to a target HEIGHT (aspect-preserved) with a faint frame. Cached; null if unknown/missing.
        Image RegionFlag(string region, int h)
        {
            string code = FlagCodeForRegion(region);
            if (code == null) return null;
            string ck = "flag:" + code + "@" + h;
            if (logoCache.TryGetValue(ck, out var cached)) return cached;
            Image result = null;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.StartsWith("flag." + code + ".", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                {
                    using var s = asm.GetManifestResourceStream(res);
                    using var src = Image.FromStream(s);
                    int w = Math.Max(1, (int)Math.Round(h * src.Width / (double)src.Height));
                    var bmp = new Bitmap(w, h);
                    try
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, w, h);
                            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255))) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
                        }
                        result = bmp;
                    }
                    catch { bmp.Dispose(); throw; }   // don't leak the partial bitmap if drawing fails
                }
            }
            catch { result = null; }
            logoCache[ck] = result;
            return result;
        }

        // Ranked tier (1-27 from the API) -> emblem resource key. Divisions I-V within a tier share an emblem.
        static string RankKeyForTier(int t)
        {
            if (t >= 27) return "grandmaster";
            if (t >= 26) return "masters";
            if (t >= 21) return "diamond";
            if (t >= 16) return "platinum";
            if (t >= 11) return "gold";
            if (t >= 6) return "silver";
            if (t >= 1) return "bronze";
            return null;
        }

        // Square ranked-tier emblem (transparent art) at px size. Cached; null if tier invalid/missing.
        Image RankEmblem(int tier, int px)
        {
            string key = RankKeyForTier(tier);
            if (key == null) return null;
            string ck = "rank:" + key + "@" + px;
            if (logoCache.TryGetValue(ck, out var cached)) return cached;
            Image result = null;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.StartsWith("rank." + key + ".", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                {
                    using var s = asm.GetManifestResourceStream(res);
                    using var src = Image.FromStream(s);
                    var bmp = new Bitmap(px, px);
                    try
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, px, px);
                        }
                        result = bmp;
                    }
                    catch { bmp.Dispose(); throw; }   // don't leak the partial bitmap if drawing fails
                }
            }
            catch { result = null; }
            logoCache[ck] = result;
            return result;
        }

        Image LoadThumbBytes(byte[] bytes)
        {
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var src = Image.FromStream(ms))
                {
                    var bmp = new Bitmap(S(64), S(64));
                    try
                    {
                        using (var gg = Graphics.FromImage(bmp))
                        {
                            gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            gg.DrawImage(src, 0, 0, S(64), S(64));
                        }
                        return bmp;   // independent of the stream
                    }
                    catch { bmp.Dispose(); throw; }   // don't leak the partial bitmap
                }
            }
            catch { return null; }
        }

        // --- favorites + platform mapping --------------------------------------
        static string FavFile => Path.Combine(Theme.DataDir, "favorites.json");
        void LoadFavs()
        {
            favorites.Clear();
            foreach (var f in ReadPlayerList(FavFile)) if (f != null && !string.IsNullOrEmpty(f.Id) && f.Id != "0") favorites.Add(f);
        }
        // Write user data with a one-level safety backup: if the existing file has content and we're about to
        // replace it (especially with a much smaller/empty payload), copy it to <file>.bak first so an accidental
        // wipe is recoverable. Loaders fall back to .bak when the main file is missing/unparseable.
        static void SaveJson(string path, object data)
        {
            string tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";   // unique per thread so concurrent saves don't collide on one .tmp
            try
            {
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                // ATOMIC: temp + File.Replace. Keep .bak ONLY when the current on-disk content is non-trivial AND differs —
                // so clearing a list (writing "[]") then re-adding can't push the EMPTY file into .bak and lose the prior good
                // copy. Otherwise replace atomically WITHOUT touching .bak (null backup arg). Protects the user's tags etc.
                File.WriteAllText(tmp, json);
                if (!File.Exists(path)) { File.Move(tmp, path); return; }
                string cur = null; try { cur = File.ReadAllText(path); } catch { }
                bool keepBak = cur != null && cur.Trim().Length > 2 && cur.Trim() != json.Trim();
                File.Replace(tmp, path, keepBak ? path + ".bak" : null);
            }
            catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }   // don't leave an orphan .tmp behind on a failed write
        }
        // Read a JSON list. The main file wins even when it's a valid EMPTY array (so an intentional Clear sticks);
        // only when the main file is missing or unparseable do we recover from the .bak backup.
        static List<FavPlayer> ReadPlayerList(string path)
        {
            try { if (File.Exists(path)) { var l = JsonSerializer.Deserialize<List<FavPlayer>>(File.ReadAllText(path)); if (l != null) return l; } }
            catch { }   // main corrupt → fall through to backup
            try { if (File.Exists(path + ".bak")) { var b = JsonSerializer.Deserialize<List<FavPlayer>>(File.ReadAllText(path + ".bak")); if (b != null) return b; } }
            catch { }
            return new List<FavPlayer>();
        }
        void SaveFavs() => SaveJson(FavFile, favorites);
        bool IsFav(string id) => !string.IsNullOrEmpty(id) && favorites.Any(f => f.Id == id);
        void RemoveFav(string id) => favorites.RemoveAll(f => f.Id == id);

        // --- friend list (buddy list with live status) -------------------------
        static string FriendListFile => Path.Combine(Theme.DataDir, "friendlist.json");
        void LoadFriendList()
        {
            friendList.Clear();
            foreach (var f in ReadPlayerList(FriendListFile)) if (f != null && !string.IsNullOrEmpty(f.Id) && f.Id != "0") friendList.Add(f);
        }
        void SaveFriendList()
        {
            SaveJson(FriendListFile, friendList);
        }
        bool IsFriendListed(string id) => !string.IsNullOrEmpty(id) && friendList.Any(f => f.Id == id);
        void RemoveFriendList(string id) { friendList.RemoveAll(f => f.Id == id); SaveFriendList(); }
        // getplayerstatus code -> (label, colour). status_string is preferred for the label when present.
        static (string text, Color col) StatusInfo(int code, string ss)
        {
            Color c = code == 3 ? Color.FromArgb(60, 180, 90)
                    : (code == 4 || code == 1) ? Color.FromArgb(46, 134, 222)
                    : code == 2 ? Color.FromArgb(214, 170, 40)
                    : Color.FromArgb(120, 120, 120);
            string t = !string.IsNullOrWhiteSpace(ss) ? ss
                     : code == 3 ? "In Game" : code == 4 ? "Online" : code == 1 ? "In Lobby" : code == 2 ? "God Select" : "Offline";
            return (t, c);
        }

        // --- recent lookups ("Saved"): auto-kept, most-recent first, capped ---
        static string RecentsFile => Path.Combine(Theme.DataDir, "recents.json");
        void LoadRecents()
        {
            recents.Clear();
            foreach (var f in ReadPlayerList(RecentsFile)) if (f != null && !string.IsNullOrEmpty(f.Id) && f.Id != "0") recents.Add(f);
        }
        void SaveRecents()
        {
            SaveJson(RecentsFile, recents);
        }
        void AddRecent(string name, string id, int portal)
        {
            if (string.IsNullOrEmpty(id) || id == "0") return;
            recents.RemoveAll(f => f.Id == id);
            recents.Insert(0, new FavPlayer { Name = string.IsNullOrWhiteSpace(name) ? id : name, Id = id, Portal = portal });
            if (recents.Count > 30) recents.RemoveRange(30, recents.Count - 30);
            SaveRecents();
        }
        void RemoveRecent(string id) { recents.RemoveAll(f => f.Id == id); SaveRecents(); }

        // --- settings (settings.json) ------------------------------------------
        static string SettingsFile => Path.Combine(Theme.DataDir, "settings.json");
        // One-time move of data an earlier build wrote next to the exe → Documents\Smite Inspector.
        void MigrateData()
        {
            try
            {
                string app = Theme.AppDir, data = Theme.DataDir;
                if (string.Equals(app, data, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var fn in new[] { "favorites.json", "recents.json", "hiddentags.json", "settings.json", "friendlist.json" })
                {
                    try { string src = Path.Combine(app, fn), dst = Path.Combine(data, fn); if (File.Exists(src) && !File.Exists(dst)) File.Move(src, dst); } catch { }
                }
                try { string s = Path.Combine(app, "defaults"), d = Path.Combine(data, "defaults"); if (Directory.Exists(s) && !Directory.Exists(d)) Directory.Move(s, d); } catch { }
            }
            catch { }
        }

        void LoadSettings()
        {
            AppSettings s = null;
            foreach (var pth in new[] { SettingsFile, SettingsFile + ".bak" })   // main file wins; fall back to .bak if missing/corrupt
            { try { if (File.Exists(pth)) { s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(pth)); if (s != null) break; } } catch { } }
            try
            {
                if (s != null) { settings.StartupTab = s.StartupTab; settings.TimeFormat = s.TimeFormat; settings.ShowFriendUptime = s.ShowFriendUptime; settings.CheckUpdates = s.CheckUpdates; settings.AutoUpdate = s.AutoUpdate; settings.SkippedVersion = s.SkippedVersion ?? ""; settings.BetaChannel = s.BetaChannel; settings.AppliedTag = s.AppliedTag ?? ""; settings.RevealHidden = s.RevealHidden; settings.Harvest = s.Harvest; settings.CommunityTags = s.CommunityTags; settings.LogReveal = s.LogReveal; settings.MyProfileId = s.MyProfileId ?? ""; settings.MyProfileName = s.MyProfileName ?? ""; settings.MyProfilePortal = s.MyProfilePortal; }
            }
            catch { }
        }
        void SaveSettings()
        {
            SaveJson(SettingsFile, settings);
        }

        // --- auto-update (checks the GitHub releases of this repo) ---
        // Derived from the assembly version (set by csproj <Version>) so it can NEVER desync from the release tag again —
        // a hardcoded const here previously stayed at "1.0.0" and made the updater re-prompt forever after updating.
        public static readonly string AppVersion = AppVersionFromAssembly();
        static string AppVersionFromAssembly()
        {
            try { var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; if (v != null && (v.Major + v.Minor + v.Build) > 0) return v.Major + "." + v.Minor + "." + v.Build; } catch { }
            return "1.2.1";
        }
        const string ReleasesApi = "https://api.github.com/repos/DariusSmite/Smite-1-Inspector/releases/latest";
        const string ReleasesListApi = "https://api.github.com/repos/DariusSmite/Smite-1-Inspector/releases?per_page=10";   // beta channel: includes pre-releases (newest first)
        const string ReleasesPage = "https://github.com/DariusSmite/Smite-1-Inspector/releases/latest";
        bool _updateChecked;   // startup check runs once per launch

        // True when this build was placed by the installer (so updates must go through the installer, not an exe-swap into
        // a read-only Program Files folder). Detected by the Inno uninstaller sitting next to us, or a Program Files path.
        static bool IsInstalled()
        {
            try
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                if (string.IsNullOrEmpty(dir)) return false;
                if (File.Exists(Path.Combine(dir, "unins000.exe"))) return true;
                foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                { string pf = Environment.GetFolderPath(sf); if (!string.IsNullOrEmpty(pf) && dir.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return true; }
            }
            catch { }
            return false;
        }

        static Version ParseVer(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0);
            s = s.Trim().TrimStart('v', 'V');
            int sp = s.IndexOfAny(new[] { ' ', '-' }); if (sp > 0) s = s.Substring(0, sp);
            return Version.TryParse(s, out var v) ? v : new Version(0, 0);
        }

        // Detached, fire-and-forget delete that runs AFTER this app exits (a short ping delay lets file locks release),
        // so it can remove the running exe or the data folder. Best-effort: whatever is still locked is simply left behind.
        static void ScheduleDetachedDelete(string path, bool isDir)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                path = path.TrimEnd('\\');   // a trailing backslash would escape the closing quote in the cmd line
                string inner = isDir ? "rd /s /q \"" + path + "\"" : "del /f /q \"" + path + "\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & " + inner)
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        // Settings → Uninstall. Confirms, optionally erases the Documents\Smite Inspector data folder, then either runs the
        // Inno uninstaller (installed build) or self-deletes the exe (portable build). The whisper engine is stopped first
        // so its relay files unlock. Destructive — only ever reached by an explicit click + confirmations.
        void UninstallApp()
        {
            bool installed = IsInstalled();
            string exe = Environment.ProcessPath ?? "";
            string dir = Path.GetDirectoryName(exe) ?? "";
            string unins = Path.Combine(dir, "unins000.exe");
            bool haveUninstaller = installed && File.Exists(unins);
            // Normalize once so the path we safety-check is exactly the path we delete (no trailing-slash quoting hazard).
            string dataDir; try { dataDir = Path.GetFullPath(Theme.DataDir).TrimEnd('\\'); } catch { dataDir = Theme.DataDir; }

            string intro = installed
                ? "Uninstall Smite 1 Inspector?\n\nThis closes the app and removes it from your PC."
                : "This is the portable version, so there is nothing to formally uninstall.\n\nYou can close the app and (optionally) delete its saved data now; afterwards just delete SmiteInspector.exe.";
            if (MessageBox.Show(this, intro, "Uninstall", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            // Installed in Program Files but the uninstaller is missing/damaged: never self-delete the program exe — that
            // would orphan the install. Send the user to Windows' own uninstaller and leave everything (incl. data) intact.
            if (installed && !haveUninstaller)
            {
                MessageBox.Show(this, "The uninstaller couldn't be found next to the app. Please uninstall Smite 1 Inspector from Windows Settings → Apps.\n\n(Your saved data was left untouched.)", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var dataChoice = MessageBox.Show(this,
                "Also delete your saved data?\n\nThis permanently removes your conversations, hidden-player tags, friend list, notes and settings stored in:\n" + dataDir + "\n\nYes — delete my data        No — keep my data        Cancel — stop",
                "Delete saved data?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (dataChoice == DialogResult.Cancel) return;
            bool clearData = dataChoice == DialogResult.Yes;

            // Safety: only wipe the data folder when it is NOT the app/exe directory (the rare Documents-unavailable
            // fallback) — recursively deleting the program folder would nuke the install out from under the uninstaller.
            bool dataIsAppDir;
            try { dataIsAppDir = string.Equals(dataDir, Path.GetFullPath(Theme.AppDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { dataIsAppDir = true; }   // can't prove it's safe → don't recursively delete

            // Stop the background whisper engine(s) so the relay files release before any delete.
            try { foreach (var p in System.Diagnostics.Process.GetProcessesByName("Probe5")) { try { p.Kill(); } catch { } } } catch { }

            // Start the removal FIRST. Only once it is under way do we schedule the optional data wipe, so a cancelled/failed
            // uninstaller launch (e.g. the user declines UAC) never erases data and then bails.
            if (haveUninstaller)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(unins) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't start the uninstaller: " + ex.Message + "\n\nYou can uninstall from Windows Settings → Apps instead.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            else if (!string.IsNullOrEmpty(exe))
            {
                ScheduleDetachedDelete(exe, false);   // portable: remove the exe after we exit
            }
            if (clearData && !dataIsAppDir) ScheduleDetachedDelete(dataDir, true);
            Application.Exit();
        }

        // Checks GitHub for a newer release. userInitiated (Settings button) always prompts and reports "up to date";
        // the startup check stays quiet unless there's an update the user hasn't already declined.
        async Task CheckForUpdate(bool userInitiated)
        {
            try
            {
                string tag = null, setupUrl = null, bareUrl = null; long setupSize = 0, bareSize = 0; bool prerelease = false;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                {
                    http.DefaultRequestHeaders.Add("User-Agent", "Smite1Inspector");
                    // Stable users read /releases/latest (GitHub omits pre-releases there). Beta users read the full
                    // release list (newest first) and take the most recent entry, which may be a pre-release build.
                    string api = settings.BetaChannel ? ReleasesListApi : ReleasesApi;
                    using var doc = JsonDocument.Parse(await http.GetStringAsync(api));
                    JsonElement rel;
                    if (settings.BetaChannel)
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                        { if (userInitiated) MessageBox.Show(this, "Couldn't read the latest release.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        rel = doc.RootElement[0];
                    }
                    else rel = doc.RootElement;
                    if (rel.TryGetProperty("tag_name", out var t)) tag = t.GetString();
                    if (rel.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) prerelease = true;
                    if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        foreach (var a in assets.EnumerateArray())
                        {
                            string nm = GS(a, "name");
                            if (!nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            long asz = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                            if (nm.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0) { if (setupUrl == null) { setupUrl = GS(a, "browser_download_url"); setupSize = asz; } }
                            else { if (bareUrl == null) { bareUrl = GS(a, "browser_download_url"); bareSize = asz; } }
                        }
                }
                // An installed build (Program Files / has an uninstaller) must update via the installer (in-place upgrade
                // that also refreshes the engine + shortcuts); a portable build swaps the bare exe. Prefer the matching
                // asset, fall back to the other so a release with only one of them still updates everyone.
                bool installed = IsInstalled();
                string assetUrl; long assetSize; bool isInstaller;
                if (installed) { if (setupUrl != null) { assetUrl = setupUrl; assetSize = setupSize; isInstaller = true; } else { assetUrl = bareUrl; assetSize = bareSize; isInstaller = false; } }
                else { if (bareUrl != null) { assetUrl = bareUrl; assetSize = bareSize; isInstaller = false; } else { assetUrl = setupUrl; assetSize = setupSize; isInstaller = true; } }
                if (string.IsNullOrEmpty(tag)) { if (userInitiated) MessageBox.Show(this, "Couldn't read the latest release.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                // Newer by version, OR (beta channel) a different-tagged beta of the SAME numeric version than the one we
                // last applied in-app — ParseVer strips the "-betaN" suffix, so iterative betas would otherwise be skipped.
                bool isNewer = ParseVer(tag) > ParseVer(AppVersion)
                    || (settings.BetaChannel && ParseVer(tag) == ParseVer(AppVersion)
                        && !string.IsNullOrEmpty(settings.AppliedTag) && tag != settings.AppliedTag);
                if (!isNewer)
                { if (userInitiated) MessageBox.Show(this, "You're on the latest version (v" + AppVersion + ").", "Up to date", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!userInitiated && settings.SkippedVersion == tag) return;   // already declined this version at startup
                if (string.IsNullOrEmpty(assetUrl))
                { if (userInitiated) MessageBox.Show(this, tag + " is available, but no exe was attached. Get it from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (settings.AutoUpdate && !userInitiated) { await ApplyUpdate(assetUrl, assetSize, tag, isInstaller); return; }
                string sizeTxt = assetSize > 0 ? "  (download ~" + (assetSize / 1048576) + " MB)" : "";
                string betaTxt = prerelease ? "  [BETA]" : "";
                var r = MessageBox.Show(this, "A new version is available: " + tag + betaTxt + "\nYou have v" + AppVersion + "." + sizeTxt + "\n\nUpdate now?",
                    "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { settings.SkippedVersion = tag; SaveSettings(); return; }   // remember the "no"
                await ApplyUpdate(assetUrl, assetSize, tag, isInstaller);
            }
            catch (Exception ex) { if (userInitiated) MessageBox.Show(this, "Update check failed: " + ex.Message, "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // Downloads the update (with a progress dialog), then either runs the new INSTALLER (in-place upgrade — works for
        // installed users in Program Files, and updates the whisper engine + shortcuts too) or, for a bare-exe asset,
        // swaps the running exe in place (portable build).
        async Task ApplyUpdate(string url, long size, string tag, bool isInstaller)
        {
            string exe = Environment.ProcessPath, dir = Path.GetDirectoryName(exe ?? "");
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Auto-update only works on the packaged app. Download " + tag + " from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            // Installer → TEMP; portable update → next to the exe.
            string dlPath = isInstaller
                ? Path.Combine(Path.GetTempPath(), "SmiteInspector-Setup-" + (tag ?? "new").TrimStart('v', 'V') + ".exe")
                : Path.Combine(dir, "SmiteInspector.update.exe");
            bool ok = false;
            using (var dlg = new Form { Text = "Updating", BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.F(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ControlBox = false, ClientSize = new Size(S(430), S(96)) })
            {
                dlg.Controls.Add(new Label { Location = new Point(S(16), S(14)), AutoSize = true, ForeColor = Theme.Dim, Text = "Downloading " + tag + "…" });
                var bar = new ProgressBar { Location = new Point(S(16), S(44)), Size = new Size(S(398), S(22)), Style = ProgressBarStyle.Continuous, Maximum = 100 };
                dlg.Controls.Add(bar);
                try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }
                var prog = new Progress<int>(p => bar.Value = Math.Min(100, Math.Max(0, p)));
                dlg.Shown += async (s, e) => { try { ok = await DownloadFile(url, dlPath, prog); } catch { ok = false; } dlg.Close(); };
                dlg.ShowDialog(this);
            }
            // reject a truncated/incomplete download before applying it
            if (ok && size > 0) { try { ok = new FileInfo(dlPath).Length == size; } catch { ok = false; } }
            if (!ok) { try { File.Delete(dlPath); } catch { } MessageBox.Show(this, "Download failed or was incomplete. You can update manually from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!string.IsNullOrEmpty(tag)) { settings.AppliedTag = tag; SaveSettings(); }   // remember the exact tag we applied (iterative-beta tracking)

            if (isInstaller)
            {
                // Run the installer silently. With CloseApplications=yes it closes this app, upgrades in place (app + engine
                // + shortcuts), then relaunches it. UseShellExecute lets it elevate (one UAC prompt). If the user cancels
                // UAC, Process.Start throws — keep the app running and tell them.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true }); }
                catch (Exception ex)
                { MessageBox.Show(this, "Update was cancelled or couldn't start (" + ex.Message + ").\n\nYou can get " + tag + " from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (settings.SkippedVersion == tag) { settings.SkippedVersion = ""; SaveSettings(); }
                Application.Exit();   // release the exe so the elevated installer can replace it; it relaunches us on finish
                return;
            }

            // ---- portable build: swap the running exe by renaming it aside (a running exe can be renamed, not overwritten) ----
            try
            {
                string bak = Path.Combine(dir, "SmiteInspector.old.exe");
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                File.Move(exe, bak);
                try { File.Move(dlPath, exe); }
                catch { try { if (!File.Exists(exe) && File.Exists(bak)) File.Move(bak, exe); } catch { } throw; }   // restore on failure so we're never left with NO exe
            }
            catch (Exception ex)
            {
                try { File.Delete(dlPath); } catch { }
                MessageBox.Show(this, "Couldn't replace the app (is it in a read-only folder?): " + ex.Message + "\n\nUpdate manually from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (settings.SkippedVersion == tag) { settings.SkippedVersion = ""; SaveSettings(); }
            if (MessageBox.Show(this, tag + " installed. Restart now to use it?", "Update ready", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true }); } catch { } Application.Exit(); }
        }

        async Task<bool> DownloadFile(string url, string dest, IProgress<int> progress)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "Smite1Inspector");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, read = 0;
            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920]; int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            { await dst.WriteAsync(buf, 0, n); read += n; if (total > 0) progress?.Report((int)(read * 100 / total)); }
            return true;
        }
        // Remove the renamed-aside previous exe left by a successful update.
        void CleanupOldExe()
        {
            try { var bak = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "SmiteInspector.old.exe"); if (File.Exists(bak)) File.Delete(bak); } catch { }
        }
        // Time-of-day per the preferred format (used for the "Updated …" stamp).
        string FmtNow()
        {
            var now = DateTime.Now;
            return settings.TimeFormat == 1 ? now.ToString("h:mm:ss tt") : settings.TimeFormat == 2 ? now.ToString("HH:mm:ss") : now.ToString("T");
        }
        // Reformat an API date string ("M/d/yyyy h:mm:ss tt") to the preferred format; passthrough if unparseable.
        string FmtApiDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            if (!DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt)) return s;
            return settings.TimeFormat == 1 ? dt.ToString("M/d/yyyy h:mm tt") : settings.TimeFormat == 2 ? dt.ToString("M/d/yyyy HH:mm") : dt.ToString("g");
        }
        // Parse an API date; returns DateTime.MinValue if unparseable.
        static DateTime ParseApiDate(string s)
            => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt) ? dt : DateTime.MinValue;
        // Short relative "last seen" string for a parsed date.
        static string RelTime(DateTime dt)
        {
            if (dt == DateTime.MinValue) return "";
            var span = DateTime.Now - dt;
            if (span.TotalSeconds < 90) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + "m ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + "h ago";
            if (span.TotalDays < 30) return (int)span.TotalDays + "d ago";
            if (span.TotalDays < 365) return (int)(span.TotalDays / 30) + "mo ago";
            return (int)(span.TotalDays / 365) + "y ago";
        }

        // --- hidden-player nicknames (fingerprint = clan + level + total mastery) -----
        static string HiddenFile => Path.Combine(Theme.DataDir, "hiddentags.json");
        void LoadHiddenTags()
        {
            hiddenTags.Clear();
            List<HiddenTag> list = null;
            foreach (var pth in new[] { HiddenFile, HiddenFile + ".bak" })   // main wins; recover the user's tags from .bak if the main file is missing/corrupt
            { try { if (File.Exists(pth)) { list = JsonSerializer.Deserialize<List<HiddenTag>>(File.ReadAllText(pth)); if (list != null) break; } } catch { } }
            if (list != null) foreach (var t in list) if (t != null && !string.IsNullOrWhiteSpace(t.Nick)) hiddenTags.Add(t);
        }
        void SaveHiddenTags()
        {
            SaveJson(HiddenFile, hiddenTags);
        }
        // Weighted multi-signal match for a hidden player. Inputs are everything the API leaves on a privacy-flagged
        // row: clan id, account level, total mastery, the gods seen, and the player_ids of the NAMED players in their
        // party (the strongest signal — a hidden player keeps running with the same friends). Returns the best tag over
        // a confidence threshold, or null. Score design:
        //   same clan id            +100   |  both clanless                +30  |  clan mismatch  -55  (clan change is possible if companions agree)
        //   level/mastery in ±8/±6  +up to 40 (closer = higher)          |  in loose ±25/±15  +6  |  beyond      -35
        //   each shared companion   +60    (2 shared party-mates can outweigh a clan change)
        //   god previously seen     +12
        // Threshold 60: same-clan always clears it (anchor); a clan change needs ≥2 shared companions to re-link.
        // How reliably the heuristic can re-recognise this tagged hidden player (0–99). More sightings + cross-evidence
        // (shared party-mates, gods seen) = higher; a lone first tag is modest.
        int HiddenConfidence(HiddenTag t)
            => Math.Min(99, 25 + t.Seen * 18 + Math.Min(t.Companions?.Count ?? 0, 4) * 8 + Math.Min(t.Gods?.Count ?? 0, 3) * 4);

        HiddenTag MatchHidden(int clanId, int level, int mastery, IReadOnlyCollection<string> companions = null, string god = null)
        {
            HiddenTag best = null; int bestScore = int.MinValue;
            foreach (var t in hiddenTags)
            {
                int dl = Math.Abs(t.Level - level), dm = Math.Abs(t.Mastery - mastery);
                bool statsClose = dl <= 8 && dm <= 6, statsLoose = dl <= 20 && dm <= 15;
                int overlap = 0;
                if (companions != null && t.Companions != null)
                    foreach (var c in companions) if (!string.IsNullOrEmpty(c) && t.Companions.Contains(c)) overlap++;
                // hard gate: without shared party-mates, level/mastery must be within the loose window — otherwise it's
                // a DIFFERENT person (even in the same clan). Companion evidence lifts the gate (they may have leveled a lot).
                if (overlap == 0 && !statsLoose) continue;
                int score = 0;
                if (clanId != 0 && t.ClanId == clanId) score += 100;        // same clan: strong anchor
                else if (clanId == 0 && t.ClanId == 0) score += 30;          // both clanless: weak anchor (needs close stats or a god to clear 60)
                else score -= 55;                                           // clan mismatch (changed clan, or one side clanless)
                if (statsClose) score += 40 - (dl * 2 + dm * 2); else if (statsLoose) score += 6; else score -= 20;
                if (statsClose && dl == 0) score += 12;   // EXACT account level bonus — only reinforces an already-close fingerprint (never leaks into the loose band)
                score += overlap * 60;
                if (!string.IsNullOrEmpty(god) && t.Gods != null && t.Gods.Contains(god)) score += 12;
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return bestScore >= 60 ? best : null;
        }
        // Add/update/remove a nickname (empty nick removes). Seeds the sighting signals from the first tag.
        void SetHiddenTag(int clanId, string clan, int level, int mastery, string nick, IReadOnlyCollection<string> companions = null, string god = null)
        {
            var existing = MatchHidden(clanId, level, mastery, companions, god);
            if (string.IsNullOrWhiteSpace(nick))   // empty nick = remove
            {
                if (existing != null) hiddenTags.Remove(existing);
                SaveHiddenTags();
                return;
            }
            if (existing != null)   // rename in place: keep Seen/companions/gods and fold this sighting's evidence in
            {
                existing.Nick = nick.Trim();
                UpdateSighting(existing, clan, level, mastery, companions, god);   // also saves
                _ = TagSync.Submit(nick.Trim(), clanId, level, new[] { god }, companions);   // share with the community
                return;
            }
            var t = new HiddenTag { ClanId = clanId, Clan = clan, Level = level, Mastery = mastery, Nick = nick.Trim(), Seen = 1, LastSeen = DateTime.Now.ToString("yyyy-MM-dd"), Tagged = DateTime.Now.ToString("yyyy-MM-dd") };
            if (companions != null) foreach (var c in companions) if (!string.IsNullOrEmpty(c) && !t.Companions.Contains(c)) t.Companions.Add(c);
            if (!string.IsNullOrEmpty(god) && !t.Gods.Contains(god)) t.Gods.Add(god);
            hiddenTags.Add(t);
            SaveHiddenTags();
            _ = TagSync.Submit(nick.Trim(), clanId, level, new[] { god }, companions);   // share with the community
        }
        // On every confident sighting, fold the new evidence back into the tag so it tracks the player as they evolve:
        // advance level/mastery FORWARD (players only gain), refresh the clan, and accumulate companions + gods (capped).
        void UpdateSighting(HiddenTag tag, string clan, int level, int mastery, IReadOnlyCollection<string> companions, string god)
        {
            if (tag == null) return;
            tag.Companions ??= new(); tag.Gods ??= new();
            bool changed = false;
            if (level > tag.Level) { tag.Level = level; changed = true; }
            if (mastery > tag.Mastery) { tag.Mastery = mastery; changed = true; }
            if (!string.IsNullOrEmpty(clan) && clan != tag.Clan) { tag.Clan = clan; changed = true; }
            if (companions != null) foreach (var c in companions) if (!string.IsNullOrEmpty(c) && !tag.Companions.Contains(c)) { tag.Companions.Add(c); changed = true; }
            if (tag.Companions.Count > 40) { tag.Companions.RemoveRange(0, tag.Companions.Count - 40); changed = true; }   // keep the most recent
            if (!string.IsNullOrEmpty(god) && !tag.Gods.Contains(god)) { tag.Gods.Add(god); changed = true; }
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (tag.LastSeen != today) { tag.LastSeen = today; tag.Seen++; changed = true; }
            if (changed) SaveHiddenTags();
        }

        // Small dark modal text prompt (WinForms has no built-in InputBox). Returns null on cancel.
        string PromptText(string title, string subtitle, string initial)
        {
            using (var dlg = new Form())
            {
                dlg.Text = title; dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                dlg.StartPosition = FormStartPosition.CenterParent; dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false; dlg.ClientSize = new Size(S(460), S(150));
                var lbl = new Label { Location = new Point(S(14), S(12)), Size = new Size(S(432), S(48)), ForeColor = Theme.Dim, Font = Theme.F(8.5f), Text = subtitle };
                var tb = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f), Text = initial ?? "" };
                var host = WrapInput(tb, S(432)); host.Location = new Point(S(14), S(66));
                var ok = MkBtn("Save", 90, false, Theme.Blue, Color.White); ok.Location = new Point(S(266), S(106));
                var cancel = MkBtn("Cancel", 90, false); cancel.Location = new Point(S(360), S(106));
                ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                dlg.Controls.Add(lbl); dlg.Controls.Add(host); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
                try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }
                tb.Select();
                return dlg.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        // portal_id -> short platform code + brand colour (search / favorites / friends chips).
        static (string code, Color col) PlatformChip(int portal)
        {
            switch (portal)
            {
                case 5: return ("STEAM", Color.FromArgb(27, 40, 56));
                case 9: return ("PS", Color.FromArgb(0, 55, 145));
                case 10: return ("XBOX", Color.FromArgb(16, 124, 16));
                case 22: return ("SWITCH", Color.FromArgb(214, 20, 30));
                case 25: return ("DISCORD", Color.FromArgb(88, 101, 242));
                case 28: return ("EPIC", Color.FromArgb(50, 50, 50));
                case 1: case 4: return ("PC", Color.FromArgb(90, 96, 106));
                default: return ("?", Color.FromArgb(70, 70, 70));
            }
        }
        // Embedded logo key for a portal id (null = no logo, fall back to the coloured text chip).
        static string LogoKeyForPortal(int portal)
        {
            switch (portal) { case 5: return "steam"; case 10: return "xbox"; case 22: return "switch"; case 28: return "epic"; default: return null; }
        }
        // getplayer returns a string Platform ("Steam"/"PSN"/"XboxLive"/"Nintendo"/"Epic"/"HiRez"); map to a portal id.
        static int PortalFromName(string p)
        {
            if (string.IsNullOrEmpty(p)) return 1;
            p = p.ToLowerInvariant();
            if (p.Contains("steam")) return 5;
            if (p.Contains("playstation") || p == "psn" || p.StartsWith("ps")) return 9;
            if (p.Contains("xbox")) return 10;
            if (p.Contains("nintendo") || p.Contains("switch")) return 22;
            if (p.Contains("discord")) return 25;
            if (p.Contains("epic")) return 28;
            return 1;
        }

        // --- experiment/reveal-hidden-names: background name harvester ---------
        // Scrapes match rosters across queues to learn PUBLIC players' names + fingerprints "at scale". (Privacy-flagged
        // players are anonymized EVERYWHERE — live getmatchplayerdetails and completed getmatchdetails alike — so this
        // never captures a hidden name; it enlarges the pool a hidden appearance can later be fingerprint-matched against.)
        // Self-throttles well under the daily request cap.
        System.Threading.CancellationTokenSource _harvestCts;
        static readonly int[] HarvestQueues = { 426, 451, 504, 448, 450, 503, 440, 502, 445, 435, 459, 466 };
        void StartHarvester()
        {
            if (_harvestCts != null) return;
            _harvestCts = new System.Threading.CancellationTokenSource();
            var ct = _harvestCts.Token;
            _ = Task.Run(() => HarvestLoop(ct));
        }
        void StopHarvester()
        {
            try { _harvestCts?.Cancel(); } catch { }
            _harvestCts = null;
        }
        async Task HarvestLoop(System.Threading.CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (SmiteApi.RequestsToday > 285000) { await Task.Delay(TimeSpan.FromMinutes(10), ct); continue; }
                    var now = DateTime.UtcNow;
                    // Collect every recent match id (blob-proof: ignore active_flag, just gather Match tokens). The
                    // newest ids are the most likely to still be live; getmatchplayerdetails returns [] for finished
                    // ones, so probing them is self-selecting for live matches.
                    var seen = new HashSet<long>();
                    foreach (var q in HarvestQueues)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            using var qd = JsonDocument.Parse(await SmiteApi.Call("getmatchidsbyqueue", q.ToString(), now.ToString("yyyyMMdd"), now.ToString("HH")));
                            if (qd.RootElement.ValueKind == JsonValueKind.Array)
                                foreach (var row in qd.RootElement.EnumerateArray())
                                    foreach (var tok in GS(row, "Match").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                        if (long.TryParse(tok, out var lv) && lv > 0) seen.Add(lv);
                        }
                        catch { }
                        await Task.Delay(120, ct);
                    }
                    var ids = seen.OrderByDescending(v => v).Take(120).Select(v => v.ToString()).ToList();
                    foreach (var mid in ids)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (SmiteApi.RequestsToday > 292000) break;
                        try
                        {
                            // Scrape COMPLETED match details: only these carry PartyId (the party graph) + portal ids, which
                            // power corroborated reveals. Learn every visible player with their named party-mates as companions.
                            using var md = JsonDocument.Parse(await SmiteApi.Call("getmatchdetails", mid));
                            if (md.RootElement.ValueKind == JsonValueKind.Array && md.RootElement.GetArrayLength() > 1)
                            {
                                var rows = md.RootElement.EnumerateArray().ToList();
                                var partyNamed = new Dictionary<int, List<string>>();   // named ids per PartyId (premade — strong)
                                var teamNamed = new Dictionary<int, List<string>>();    // named ids per TaskForce (same-team — weak)
                                foreach (var p in rows)
                                {
                                    string id2 = GS(p, "playerId"); if (string.IsNullOrEmpty(id2) || id2 == "0") continue;
                                    int pp = GI(p, "PartyId");
                                    if (pp != 0) { if (!partyNamed.TryGetValue(pp, out var l)) partyNamed[pp] = l = new List<string>(); l.Add(id2); }
                                    int tf = GI(p, "TaskForce");
                                    if (tf != 0) { if (!teamNamed.TryGetValue(tf, out var tl)) teamNamed[tf] = tl = new List<string>(); tl.Add(id2); }
                                }
                                foreach (var p in rows)
                                {
                                    string ppid = GS(p, "playerId"); if (string.IsNullOrEmpty(ppid) || ppid == "0") continue;
                                    string nm = GS(p, "playerName"); if (string.IsNullOrEmpty(nm)) nm = GS(p, "hz_player_name");
                                    if (string.IsNullOrEmpty(nm)) continue;
                                    string god = GS(p, "Reference_Name");
                                    string hps = GS(p, "Skin"); string hrSkin = (!string.IsNullOrEmpty(hps) && !hps.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) && GS(p, "SkinId") != "0") ? GS(p, "SkinId") : null;
                                    int pp = GI(p, "PartyId"), tf = GI(p, "TaskForce");
                                    List<string> comp = (pp != 0 && partyNamed.TryGetValue(pp, out var l)) ? l.Where(x => x != ppid).ToList() : null;
                                    List<string> nbrs = (tf != 0 && teamNamed.TryGetValue(tf, out var tl)) ? tl.Where(x => x != ppid).ToList() : null;
                                    NameDb.Learn(ppid, nm, 0, GI(p, "TeamId"), GS(p, "Team_Name"), GI(p, "Account_Level"), god, GI(p, "Mastery_Level"), comp, nbrs, hrSkin, RankedFromRow(p));
                                }
                            }
                        }
                        catch { }
                        await Task.Delay(80, ct);
                    }
                    NameDb.Save(true);
                }
                catch (OperationCanceledException) { break; }
                catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { break; }
            }
        }

        // --- match scoreboard from SmiteGuru -----------------------------------
        // Hi-Rez's getmatchdetails only keeps matches for a few weeks; SmiteGuru keeps them for years. So Encounters rows
        // (which are historical) open THIS scoreboard, built from api.smite.guru/v3/matches/pc/<id> (full per-player stats).
        static string SgKfmt(int n) => n >= 1000 ? (n / 1000.0).ToString("0.0") + "k" : n.ToString();
        async Task ShowSguruMatch(string matchId)
        {
            if (string.IsNullOrEmpty(matchId)) return;
            _sguru ??= new SmiteGuru(this);
            SmiteGuru.MDetail md = null; Dictionary<int, string> gods = null, items = null;
            var prev = Cursor.Current;
            try { Cursor.Current = Cursors.WaitCursor; (md, gods, items) = await _sguru.GetMatchFull(matchId, System.Threading.CancellationToken.None); }
            catch (Exception ex) { Cursor.Current = prev; MessageBox.Show(this, "Couldn't load this match from SmiteGuru: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            finally { Cursor.Current = prev; }
            if (md == null || md.Players == null || md.Players.Count == 0) { MessageBox.Show(this, "No scoreboard available for this match — smite.guru keeps the match in its history list but no longer stores the full per-player detail for it (older matches age out). Nothing the app can recover.", "Match too old", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            string Qn(int q) => q switch { 426 => "Conquest", 451 => "Ranked Conquest", 459 => "Conquest", 435 => "Arena", 448 => "Joust", 450 => "Ranked Joust", 440 => "Ranked Duel", 445 => "Assault", 466 => "Clash", 10189 => "Slash", 504 => "Slash", _ => "Queue " + q };
            string God(int id) => gods != null && gods.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : ("God " + id);
            string dateS = DateTime.TryParse(md.Time, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt.ToString("yyyy-MM-dd") : (md.Time ?? "");

            using (var dlg = new Form())
            using (var tip = new ToolTip())
            {
                dlg.Text = "Match " + matchId + "  —  " + Qn(md.QueueId) + "  ·  " + md.Duration + " min  ·  " + dateS;
                dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                dlg.StartPosition = FormStartPosition.CenterParent; dlg.FormBorderStyle = FormBorderStyle.Sizable; dlg.MinimizeBox = false;
                dlg.ClientSize = new Size(S(748), S(560));
                var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(S(14), S(10), S(14), S(14)) };
                dlg.Controls.Add(root);
                // party highlight: players sharing a non-zero party id were premade together
                var partyColors = new Dictionary<int, Color>();
                var palette = new[] { Color.FromArgb(86, 156, 214), Color.FromArgb(224, 162, 80), Color.FromArgb(150, 122, 224), Color.FromArgb(86, 196, 142), Color.FromArgb(224, 120, 168), Color.FromArgb(120, 200, 210) };
                foreach (var grp in md.Players.GroupBy(p => p.Party).Where(g => g.Key != 0 && g.Count() > 1)) partyColors[grp.Key] = palette[partyColors.Count % palette.Length];
                // column-header strip
                var head = new Panel { Size = new Size(S(716), S(18)), Margin = new Padding(0, 0, 0, S(2)), BackColor = Theme.Bg };
                head.Paint += (s, e) =>
                {
                    var g = e.Graphics;
                    void Cap(string t, int x, int w2) => TextRenderer.DrawText(g, t, Theme.F(7.5f, FontStyle.Bold), new Rectangle(S(x), 0, S(w2), S(18)), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    Cap("PLAYER", 44, 180); Cap("K / D / A", 232, 96); Cap("DMG", 336, 70); Cap("MIT", 414, 64); Cap("GOLD", 486, 70);
                };
                root.Controls.Add(head);
                foreach (var team in new[] { 1, 2 })
                {
                    var tp = md.Players.Where(p => p.Team == team).OrderByDescending(p => p.Kills).ToList();
                    if (tp.Count == 0) continue;
                    bool won = md.WinningTeam == team;
                    root.Controls.Add(new Label { AutoSize = true, UseMnemonic = false, Font = Theme.F(11f, FontStyle.Bold), ForeColor = won ? Theme.Green : Theme.Accent, Margin = new Padding(S(2), S(10), 0, S(4)), Text = (team == 1 ? "Order" : "Chaos") + "   ·   " + (won ? "VICTORY" : "DEFEAT") + "   ·   " + tp.Sum(p => p.Kills) + "/" + tp.Sum(p => p.Deaths) + "/" + tp.Sum(p => p.Assists) });
                    foreach (var p in tp) root.Controls.Add(MakeSguruScoreRow(p, God, items, partyColors, tip));
                }
                dlg.ShowDialog(this);
            }
        }

        Panel MakeSguruScoreRow(SmiteGuru.MPlayer p, Func<int, string> God, Dictionary<int, string> items, Dictionary<int, Color> partyColors, ToolTip tip)
        {
            var row = new Panel { Size = new Size(S(716), S(36)), Margin = new Padding(0, 0, 0, S(4)), BackColor = Theme.Panel };
            string godName = God(p.Champion);
            bool hidden = p.Id <= 0 || string.IsNullOrWhiteSpace(p.Name);
            var gi = GodListIcon(godName);
            var pc = partyColors.TryGetValue(p.Party, out var c) ? c : (Color?)null;
            // items tooltip (resolved names, by slot)
            if (items != null && p.Build != null && p.Build.Count > 0)
            {
                var names = p.Build.OrderBy(kv => kv.Key).Select(kv => items.TryGetValue(kv.Value, out var nm) ? nm : null).Where(n => !string.IsNullOrEmpty(n)).ToList();
                if (names.Count > 0) tip.SetToolTip(row, "Build: " + string.Join(", ", names));
            }
            row.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (pc.HasValue) using (var b = new SolidBrush(pc.Value)) g.FillRectangle(b, 0, 0, S(3), row.Height);
                if (gi != null) g.DrawImage(gi, new Rectangle(S(8), S(5), S(26), S(26)));
                TextRenderer.DrawText(g, hidden ? "Hidden" : p.Name, Theme.F(9.5f, hidden ? FontStyle.Italic : FontStyle.Bold), new Rectangle(S(44), S(3), S(184), S(17)), hidden ? Theme.Dim : Theme.Text, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, godName + "   ·   Lv " + p.Level, Theme.F(8f), new Rectangle(S(44), S(19), S(184), S(14)), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, p.Kills + " / " + p.Deaths + " / " + p.Assists, Theme.F(10f, FontStyle.Bold), new Rectangle(S(232), 0, S(96), row.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, SgKfmt(p.Damage), Theme.F(9.5f), new Rectangle(S(336), 0, S(70), row.Height), Theme.Blue, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, SgKfmt(p.Mitigated), Theme.F(9.5f), new Rectangle(S(414), 0, S(64), row.Height), Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, SgKfmt(p.Gold), Theme.F(9.5f), new Rectangle(S(486), 0, S(70), row.Height), Theme.Yellow, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            return row;
        }

        // --- match scoreboard (getmatchdetails) --------------------------------
        async Task ShowMatchDetails(string matchId)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(await SmiteApi.Call("getmatchdetails", matchId)); }
            catch (Exception ex) { MessageBox.Show(this, "Match details failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                { MessageBox.Show(this, "No details available for this match (it may be too old).", "SMITE"); return; }

                var players = doc.RootElement.EnumerateArray().ToList();
                var first = players[0];
                int winTf = GI(first, "Winning_TaskForce");
                // party highlight: players sharing a non-zero PartyId are premade together → give each such party a colour
                var partyColors = new Dictionary<int, Color>();
                var partyPalette = new[] { Color.FromArgb(86, 156, 214), Color.FromArgb(224, 162, 80), Color.FromArgb(150, 122, 224), Color.FromArgb(86, 196, 142), Color.FromArgb(224, 120, 168), Color.FromArgb(120, 200, 210) };
                foreach (var grp in players.GroupBy(pl => GI(pl, "PartyId")).Where(grp => grp.Key != 0 && grp.Count() > 1))
                    partyColors[grp.Key] = partyPalette[partyColors.Count % partyPalette.Length];
                // hidden-player matcher signal: PartyId -> player_ids of the NAMED players in that party (a hidden
                // player's "companions" = the named friends they're premade with — the strongest re-recognition cue).
                var partyNamed = new Dictionary<int, List<string>>();
                foreach (var pl in players)
                {
                    int pp = GI(pl, "PartyId"); if (pp == 0) continue;
                    string pn = GS(pl, "playerName"); if (string.IsNullOrEmpty(pn)) pn = GS(pl, "hz_player_name");
                    string pidv = GS(pl, "playerId");
                    if (!string.IsNullOrEmpty(pn) && !string.IsNullOrEmpty(pidv) && pidv != "0")
                    { if (!partyNamed.TryGetValue(pp, out var l)) partyNamed[pp] = l = new List<string>(); l.Add(pidv); }
                }
                // same-team social graph: the NAMED players on each TaskForce (the hidden node's neighborhood, survives PartyId stripping)
                var teamNamed = new Dictionary<int, List<string>>();
                foreach (var pl in players)
                {
                    int tf2 = GI(pl, "TaskForce"); if (tf2 == 0) continue;
                    string pidv = GS(pl, "playerId");
                    if (!string.IsNullOrEmpty(pidv) && pidv != "0")
                    { if (!teamNamed.TryGetValue(tf2, out var tl)) teamNamed[tf2] = tl = new List<string>(); tl.Add(pidv); }
                }
                string queue = GS(first, "name"); if (string.IsNullOrEmpty(queue)) queue = GS(first, "Queue");
                int minutes = GI(first, "Minutes");

                // EXACT reveal from the local game logs: correlate this match to a captured combat-log roster (by the
                // public players' ids) → map "godId|team" -> real name, used to fill the hidden slots in MakeScoreRow.
                Dictionary<string, (string name, string id)> logMap = null;
                if (GameLog.Enabled)
                {
                    GameLog.Ingest(true);   // force: fold in the newest combat log NOW (bypass the watcher's debounce)
                    logMap = GameLog.CorrelateMatch(players.Select(pl => (GS(pl, "playerId"), GS(pl, "GodId"), GI(pl, "TaskForce"))).ToList());
                }
                // The names + ids ALREADY visible in this match — a hidden slot can never be one of them, so the fingerprint
                // guesser must exclude them (else it suggests a player who is sitting right there in the same scoreboard).
                var present = new HashSet<string>();
                foreach (var pl in players)
                {
                    string pn = GS(pl, "playerName"); if (string.IsNullOrEmpty(pn)) pn = GS(pl, "hz_player_name");
                    if (!string.IsNullOrEmpty(pn)) present.Add(pn);
                    string pidp = GS(pl, "playerId"); if (!string.IsNullOrEmpty(pidp) && pidp != "0") present.Add(pidp);
                }
                // An EXACT (game-log) reveal can't be a player PUBLIC elsewhere in this match → drop such entries; then add the
                // (sanitized) exact-revealed names to `present` so the fingerprint guesser can't ≈-suggest a name already shown ✔.
                if (logMap != null)
                {
                    foreach (var k in logMap.Where(kv => present.Contains(kv.Value.name)).Select(kv => kv.Key).ToList()) logMap.Remove(k);
                    foreach (var v in logMap.Values) present.Add(v.name);
                }
                // AMBIGUITY GUARD: if the fingerprint would assign the SAME name to two hidden slots (e.g. a hidden duo whose
                // PartyId is stripped → identical inputs), it can't tell which is which → exclude that name so BOTH show Hidden.
                if (NameDb.Enabled)
                {
                    var gc = new Dictionary<string, int>();
                    foreach (var pl in players)
                    {
                        string nm2 = GS(pl, "playerName"); if (string.IsNullOrEmpty(nm2)) nm2 = GS(pl, "hz_player_name");
                        if (!string.IsNullOrEmpty(nm2)) continue;   // hidden slots only
                        if (logMap != null && logMap.ContainsKey(GS(pl, "GodId") + "|" + GI(pl, "TaskForce"))) continue;   // this slot will show its EXACT ✔ name, not a fingerprint guess → don't pollute the dup tally
                        var comp = partyNamed.TryGetValue(GI(pl, "PartyId"), out var cc) ? cc : null;
                        var nbr = teamNamed.TryGetValue(GI(pl, "TaskForce"), out var nn) ? nn : null;
                        string ps = GS(pl, "Skin"); string prSkin = (!string.IsNullOrEmpty(ps) && !ps.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) && GS(pl, "SkinId") != "0") ? GS(pl, "SkinId") : null;
                        var rv = NameDb.Resolve(GI(pl, "TeamId"), GI(pl, "Account_Level"), GI(pl, "Mastery_Level"), GS(pl, "Reference_Name"), comp, nbr, present, prSkin, SlotRankFromRow(pl));
                        if (!string.IsNullOrEmpty(rv.name)) gc[rv.name] = (gc.TryGetValue(rv.name, out var k2) ? k2 : 0) + 1;
                    }
                    foreach (var dup in gc.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToList()) present.Add(dup);
                }

                // GOD-BOARD reveal (experiment 2026-06-25): de-anonymize hidden RANKED players via the god-leaderboard
                // id-leak → smite.guru name. Only for hidden slots that are actually ranked (a <Queue>_Tier>0 survives the
                // privacy flag), and only when opted in (it pulls leaderboards + drives the smite.guru WebView2). Produces
                // "godId|tf" → (name,conf), consumed by MakeScoreRow exactly like the local game-log map.
                Dictionary<string, (string name, int conf)> gbMap = null;
                if (settings.RankedReveal && NameDb.Enabled)
                {
                    var gbSlots = new List<GodBoard.Slot>();
                    foreach (var pl in players)
                    {
                        string nmh = GS(pl, "playerName"); if (string.IsNullOrEmpty(nmh)) nmh = GS(pl, "hz_player_name");
                        if (!string.IsNullOrEmpty(nmh)) continue;   // hidden slots only
                        if (logMap != null && logMap.ContainsKey(GS(pl, "GodId") + "|" + GI(pl, "TaskForce"))) continue;   // already shown by the exact game-log reveal
                        var qs = new List<int>();
                        foreach (var kv in GodBoard.RankedQueueId) if (GI(pl, kv.Key + "_Tier") > 0) qs.Add(kv.Value);
                        if (qs.Count == 0) continue;   // not ranked → on no god board
                        gbSlots.Add(new GodBoard.Slot { GodId = GS(pl, "GodId"), GodName = GS(pl, "Reference_Name"), Tf = GI(pl, "TaskForce"), Level = GI(pl, "Account_Level"), Clan = GS(pl, "Team_Name"), ClanId = GI(pl, "TeamId"), Mastery = GI(pl, "Mastery_Level"), Queues = qs });
                    }
                    if (gbSlots.Count > 0)
                    {
                        try
                        {
                            _sguru ??= new SmiteGuru(this);
                            gbMap = await GodBoard.ResolveSlots(gbSlots, (idlist, c) => _sguru.ResolveProfilesBatch(idlist, c), CancellationToken.None);
                        }
                        catch { }
                        if (gbMap != null && gbMap.Count > 0)
                        {
                            // a god-board reveal can't be a name already PUBLIC in this match; then add revealed names so the
                            // fingerprint guesser can't ≈-suggest a name already shown ✦ on another row.
                            foreach (var k in gbMap.Where(kv => present.Contains(kv.Value.name)).Select(kv => kv.Key).ToList()) gbMap.Remove(k);
                            foreach (var v in gbMap.Values) present.Add(v.name);
                        }
                    }
                }

                try
                {
                using (var dlg = new Form())
                using (var dtip = new ToolTip())   // scoped to the dialog → all item tooltips drop when it closes
                {
                    dlg.Text = "Match " + matchId + "  —  " + queue + "  ·  " + minutes + " min";
                    dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.FormBorderStyle = FormBorderStyle.Sizable; dlg.MinimizeBox = false;
                    dlg.ClientSize = new Size(S(960), S(640));
                    var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(S(10)) };
                    dlg.Controls.Add(body);

                    int y = S(6), rowW = S(906);
                    foreach (int tf in new[] { 1, 2 })
                    {
                        var team = players.Where(pl => GI(pl, "TaskForce") == tf).ToList();
                        if (team.Count == 0) continue;
                        int kills = team.Sum(pl => GI(pl, "Kills_Player"));
                        bool won = tf == winTf;
                        body.Controls.Add(new Label
                        {
                            Location = new Point(S(4), y), Size = new Size(rowW, S(28)), Font = Theme.F(11f, FontStyle.Bold), ForeColor = Color.White,
                            BackColor = won ? Color.FromArgb(28, 70, 40) : Color.FromArgb(74, 28, 30), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, 0, 0),
                            Text = "TEAM " + tf + "    " + (won ? "VICTORY" : "DEFEAT") + "    ·    " + kills + " kills"
                        });
                        y += S(34);
                        foreach (var pl in team) { body.Controls.Add(MakeScoreRow(pl, y, rowW, dtip, partyColors.TryGetValue(GI(pl, "PartyId"), out var pc) ? pc : Color.Empty, partyNamed.TryGetValue(GI(pl, "PartyId"), out var comp) ? comp : null, matchId, teamNamed.TryGetValue(GI(pl, "TaskForce"), out var nbr) ? nbr : null, logMap, present, gbMap)); y += S(46); }
                        y += S(10);
                    }
                    try { int on = 1; if (DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(dlg.Handle, 19, ref on, 4); } catch { }
                    dlg.ShowDialog(this);
                    if (NameDb.Enabled) NameDb.Save();   // persist names harvested from this scoreboard
                }
                }
                catch (Exception ex) { MessageBox.Show(this, "Match details failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        Control MakeScoreRow(JsonElement pl, int y, int rowW, ToolTip dtip, Color partyCol = default, List<string> companions = null, string matchId = null, List<string> neighbors = null, Dictionary<string, (string name, string id)> logMap = null, HashSet<string> present = null, Dictionary<string, (string name, int conf)> gbMap = null)
        {
            var row = new Panel { Location = new Point(S(4), y), Size = new Size(rowW, S(42)), BackColor = Theme.Panel };
            if (partyCol != Color.Empty)   // premade-party accent bar on the left
            {
                row.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(S(4), S(42)), BackColor = partyCol });
                dtip.SetToolTip(row, "Premade party");
            }
            row.Controls.Add(new PictureBox { Location = new Point(S(6), S(5)), Size = new Size(S(32), S(32)), SizeMode = PictureBoxSizeMode.Zoom, Image = GodListIcon(GS(pl, "Reference_Name")) });
            string god = GS(pl, "Reference_Name");
            string skinNm = GS(pl, "Skin");   // a NON-default skin (not "Standard …") is a stable, identifying choice that survives the privacy flag
            string rareSkin = (!string.IsNullOrEmpty(skinNm) && !skinNm.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) && GS(pl, "SkinId") != "0") ? GS(pl, "SkinId") : null;
            string nm = GS(pl, "playerName"); if (string.IsNullOrEmpty(nm)) nm = GS(pl, "hz_player_name");
            bool priv = string.IsNullOrEmpty(nm);   // privacy flag strips the name + every id; clan/level/mastery survive
            string clan = GS(pl, "Team_Name");
            int clanId = GI(pl, "TeamId"), acct = GI(pl, "Account_Level"), mast = GI(pl, "Mastery_Level");
            string kda = GI(pl, "Kills_Player") + "/" + GI(pl, "Deaths") + "/" + GI(pl, "Assists");
            string pid = GS(pl, "playerId"); int tf = GI(pl, "TaskForce");
            // EXACT reveal from the local game logs (strongest source): this slot's real name from the captured combat-log roster.
            string logName = null;
            if (GameLog.Enabled && priv && logMap != null && logMap.TryGetValue(GS(pl, "GodId") + "|" + tf, out var lr)) logName = lr.name;
            Color logCol = Color.FromArgb(120, 210, 140);   // green = exact, like a confirmed reveal
            // experiment/reveal-hidden-names: harvest every visible player into the name DB, and reveal hidden ones from it.
            var nbrSelf = neighbors?.Where(x => x != pid).ToList();
            if (NameDb.Enabled && !priv && !string.IsNullOrEmpty(pid) && pid != "0")
                NameDb.Learn(pid, nm, 0, clanId, clan, acct, god, mast, companions, nbrSelf, rareSkin, RankedFromRow(pl));
            // A game-log reveal is GROUND TRUTH → also teach the fingerprint DB (keyed by the real id from the log) with
            // this match's fingerprint (party-mates + level + mastery + god), so the SAME hidden ACCOUNT is recognised by
            // fingerprint in OTHER matches the user wasn't in — where the game log can't fire. (This is how a player the
            // game log named in one match gets flagged in the next, e.g. via the shared premade.)
            if (NameDb.Enabled && priv && !string.IsNullOrEmpty(logName)
                && logMap != null && logMap.TryGetValue(GS(pl, "GodId") + "|" + tf, out var lrn) && !string.IsNullOrEmpty(lrn.id) && lrn.id != "0")
                NameDb.Learn(lrn.id, logName, 0, clanId, clan, acct, god, mast, companions, nbrSelf, rareSkin, RankedFromRow(pl));   // a game-log-revealed hidden player's MMR/tier survive privacy → learn them as a fingerprint
            string revName = null; int revConf = 0; bool revExact = false;
            if (NameDb.Enabled && priv)
            {
                revName = NameDb.ResolveExact(matchId, tf, GS(pl, "GodId"), god);   // GodId primary; Reference_Name = legacy fallback
                if (!string.IsNullOrEmpty(revName)) { revExact = true; revConf = 100; }
                else { var rv = NameDb.Resolve(clanId, acct, mast, god, companions, neighbors, present, rareSkin, SlotRankFromRow(pl)); revName = rv.name; revConf = rv.conf; }
            }
            // GOD-BOARD reveal (god-leaderboard id-leak → smite.guru name): a near-exact id→name resolution for THIS hidden
            // ranked slot, pre-computed in ShowMatchDetails. Ranks above the fuzzy ≈ fingerprint, below the user's ★ tag.
            string gbName = null; int gbConf = 0;
            if (NameDb.Enabled && priv && gbMap != null && gbMap.TryGetValue(GS(pl, "GodId") + "|" + tf, out var gbv)) { gbName = gbv.name; gbConf = gbv.conf; }
            Color gbCol = Color.FromArgb(120, 205, 170);   // teal-green = a leaderboard-sourced reveal
            string cName = null; int cVotes = 0; bool cConf = false;
            if (TagSync.Enabled && priv)
            {
                var cr = TagSync.Resolve(clanId, acct, mast, god, companions); cName = cr.name; cVotes = cr.votes; cConf = cr.confirmed;
                if (present != null && !string.IsNullOrEmpty(cName) && present.Contains(cName)) { cName = null; cConf = false; cVotes = 0; }   // not someone already in this match
            }
            // HEAL a user ★ tag from a COMPLETED reveal. A tag made in a LIVE game has a degenerate fingerprint (the live API
            // gives no clan and a per-god "mastery", so MatchHidden can't re-find them in other matches). When an EXACT source
            // (game log ✔ / live-capture ✔) confirms this slot's name here, fold THIS completed match's good fingerprint
            // (party-mates + real account mastery + level + god) into the tag so it recognises the account everywhere.
            if (priv)
            {
                string exactName = !string.IsNullOrEmpty(logName) ? logName : (revExact ? revName : null);
                if (!string.IsNullOrEmpty(exactName))
                {
                    var fp = MatchHidden(clanId, acct, mast, companions, god);   // heal ONLY a tag that ALSO fingerprint-matches this slot (same player),
                    var heal = (fp != null && fp.Nick == exactName) ? fp : null;   // not a coincidentally same-named tag for a DIFFERENT player
                    if (heal != null) UpdateSighting(heal, clan, acct, mast, companions, god);
                }
            }
            Color revCol = revExact ? Color.FromArgb(120, 210, 140) : Color.FromArgb(110, 200, 210);
            Color comCol = Color.FromArgb(186, 156, 232);   // community tag = violet
            // Name line + sub line, both repainted by PaintName():
            //  - hidden + saved tag  -> "God — ★ Nick [clan]" (blue)
            //  - hidden, no tag      -> "God — Private [clan] (click to name)" (gold)
            //  - PUBLIC (name wins) but a tag matches -> real name (blue) + sub starts "tagged ★ Nick" so you learn who it was
            var nameLbl = new Label { Location = new Point(S(46), S(3)), Size = new Size(S(282), S(18)), Font = Theme.F(9.5f, FontStyle.Bold), AutoEllipsis = true };
            var subLbl = new Label { Location = new Point(S(46), S(22)), Size = new Size(S(282), S(16)), Font = Theme.F(8.5f), ForeColor = Theme.Dim, AutoEllipsis = true };
            void PaintName()
            {
                var tag = MatchHidden(clanId, acct, mast, companions, god);
                if (priv && tag != null && present != null && present.Contains(tag.Nick)) tag = null;   // a hidden slot can't be a player already shown in this match
                if (!priv)
                {
                    nameLbl.ForeColor = tag != null ? Theme.Blue : Theme.Text;
                    nameLbl.Text = nm;   // god is shown by the icon — drop the name prefix so the player name reads clearly
                    string gp = "KDA " + kda + "   ·   Lv " + GI(pl, "Final_Match_Level") + "   ·   " + GI(pl, "Gold_Earned").ToString("N0") + "g   ·   " + GI(pl, "Damage_Player").ToString("N0") + " dmg";
                    subLbl.ForeColor = tag != null ? Theme.Blue : Theme.Dim;
                    subLbl.Text = tag != null ? "tagged ★ " + tag.Nick + "   ·   " + gp : gp;
                    return;
                }
                // EXACT sources (game log ✔ / live-capture ✔) are GROUND TRUTH for this slot and SUPERSEDE the fuzzy ★ tag:
                // MatchHidden is only a clan+level+mastery+party heuristic and can mis-match a tag to a slot the log proves is
                // someone else — so an exact reveal outranks ★, and we must NOT fold this slot into a fuzzy tag when an exact
                // source is present (that would poison the tag with a different player's fingerprint). The by-nick heal above
                // already folds completed data into the tag whose Nick == the exact name (the agreement case).
                bool hasExact = !string.IsNullOrEmpty(logName) || revExact;
                if (tag != null && !hasExact) UpdateSighting(tag, clan, acct, mast, companions, god);   // fold only when no exact source supersedes this slot
                string tail = clan.Length > 0 ? "  ·  [" + clan + "]" : "";
                // Priority: EXACT ✔ (ground truth) > your ★ tag > community-confirmed ⚑ > local fingerprint ≈ > community-unconfirmed ⚑ > Hidden
                if (!string.IsNullOrEmpty(logName))
                { nameLbl.ForeColor = logCol; nameLbl.Text = "✔ " + logName + tail; }
                else if (revExact)
                { nameLbl.ForeColor = revCol; nameLbl.Text = "✔ " + revName + tail; }
                else if (tag != null)
                { nameLbl.ForeColor = Theme.Blue; nameLbl.Text = "★ " + tag.Nick + tail; }
                else if (!string.IsNullOrEmpty(gbName))
                { nameLbl.ForeColor = gbCol; nameLbl.Text = "✦ " + gbName + tail; }
                else if (cConf)
                { nameLbl.ForeColor = comCol; nameLbl.Text = "⚑ " + cName + "?" + tail; }
                else if (!string.IsNullOrEmpty(revName))
                { nameLbl.ForeColor = revCol; nameLbl.Text = "≈ " + revName + "?" + tail; }
                else if (!string.IsNullOrEmpty(cName))
                { nameLbl.ForeColor = comCol; nameLbl.Text = "⚑ " + cName + "?" + tail; }
                else
                { nameLbl.ForeColor = Theme.Yellow; nameLbl.Text = "Hidden" + tail + "   (click to name)"; }
                string note; Color noteCol = Theme.Dim;
                if (!string.IsNullOrEmpty(logName)) { note = "   ·   from your match log"; noteCol = logCol; }
                else if (revExact) { note = "   ·   matched live capture"; noteCol = revCol; }
                else if (tag != null) note = !string.IsNullOrEmpty(revName) ? "   ·   maybe " + revName : (!string.IsNullOrEmpty(gbName) ? "   ·   maybe " + gbName : (!string.IsNullOrEmpty(cName) ? "   ·   community: " + cName : ""));
                else if (!string.IsNullOrEmpty(gbName)) { note = "   ·   ranked leaderboard · " + gbConf + "%"; noteCol = gbCol; }
                else if (cConf) { note = "   ·   community · " + cVotes + " taggers"; noteCol = comCol; }
                else if (!string.IsNullOrEmpty(revName)) { note = "   ·   possible · " + revConf + "% (guess)"; noteCol = revCol; }
                else if (!string.IsNullOrEmpty(cName)) { note = "   ·   community · unconfirmed"; noteCol = comCol; }
                else note = "";
                string conf = !hasExact && tag != null && tag.Seen > 1 ? "   ·   seen " + tag.Seen + "×" : "";
                subLbl.ForeColor = noteCol;
                subLbl.Text = "Acct Lv " + acct + "   ·   Mastery " + mast + "   ·   " + kda + conf + note;
            }
            PaintName();
            row.Controls.Add(nameLbl); row.Controls.Add(subLbl);
            // Clickable to set/edit/clear the nickname when the row is private, OR when a public row matches a tag (cleanup).
            if (priv || MatchHidden(clanId, acct, mast, companions, god) != null)
            {
                EventHandler tagIt = (s, e) =>
                {
                    var cur = MatchHidden(clanId, acct, mast, companions, god);
                    // ACTIVE LEARNING: if the algorithm guessed a name (✔ exact / ≈ / ⚑), pre-fill it so one Save CONFIRMS it
                    // into a ground-truth user tag (★, which outranks every guess afterwards). Otherwise the box is blank to name fresh.
                    string suggested = cur?.Nick ?? (!string.IsNullOrEmpty(logName) ? logName : (!string.IsNullOrEmpty(gbName) ? gbName : (string.IsNullOrEmpty(revName) ? "" : revName)));
                    bool isGuess = cur == null && (!string.IsNullOrEmpty(revName) || !string.IsNullOrEmpty(gbName));
                    string head = priv ? (isGuess ? "Confirm or correct this player" : "Name this hidden player") : "Edit the tag for " + nm;
                    int compCount = companions?.Count ?? 0;
                    string matchNote = isGuess ? "suggested name pre-filled — Save to confirm, edit to correct, or clear to dismiss"
                        : (compCount > 0 ? "matched by clan + level + mastery + " + compCount + " party-mate" + (compCount == 1 ? "" : "s") : "matched by clan + level + mastery");
                    string nick = PromptText(head,
                        god + (clan.Length > 0 ? "  ·  [" + clan + "]" : "") + "   ·   Acct Lv " + acct + "   ·   Mastery " + mast + "\r\n(" + matchNote + ")",
                        suggested);
                    if (nick == null) return;
                    SetHiddenTag(clanId, clan, acct, mast, nick, companions, god);
                    // Keep the exact per-match slot in sync from the completed view too: naming a hidden row PINS the
                    // matchId+team+GodId slot, clearing it FORGETS the slot — otherwise a tag captured live couldn't be
                    // removed here (ResolveExact would keep showing ✔). Only hidden rows have an exact slot.
                    if (priv && !string.IsNullOrEmpty(matchId)) { NameDb.LearnLiveSlot(matchId, tf, GS(pl, "GodId"), nick); NameDb.Save(true); }
                    // recover the named account's REAL player_id via the getplayeridbyname leak → id-anchor the tag (best-effort, background)
                    if (priv && !string.IsNullOrEmpty(nick)) _ = ConfirmHiddenNameAsync(nick, clanId, clan, acct, mast, god, companions, nbrSelf);
                    PaintName();
                };
                row.Cursor = nameLbl.Cursor = subLbl.Cursor = Cursors.Hand;
                row.Click += tagIt; nameLbl.Click += tagIt; subLbl.Click += tagIt;
                dtip.SetToolTip(nameLbl, priv ? "Click to set a custom nickname for this hidden player" : "You tagged this player while hidden — click to edit/clear the tag");
            }
            int ix = S(336);
            for (int i = 1; i <= 6; i++)
            {
                string it = GS(pl, "Item_Purch_" + i);
                var pb = new PictureBox { Location = new Point(ix, S(8)), Size = new Size(S(27), S(27)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Input };
                if (!string.IsNullOrEmpty(it)) { pb.Image = ItemIcon(it); dtip.SetToolTip(pb, it); }
                row.Controls.Add(pb); ix += S(31);
            }
            ix += S(10);
            for (int i = 1; i <= 2; i++)
            {
                string ac = GS(pl, "Item_Active_" + i);
                var pb = new PictureBox { Location = new Point(ix, S(8)), Size = new Size(S(27), S(27)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Input };
                if (!string.IsNullOrEmpty(ac)) { pb.Image = ItemIcon(ac); dtip.SetToolTip(pb, ac); }
                row.Controls.Add(pb); ix += S(31);
            }
            return row;
        }

        // --- LIVE match roster (getmatchplayerdetails) -------------------------
        // Live matches expose player names that a COMPLETED scoreboard anonymizes (pid=0/blank). This is how
        // "hidden" players get revealed — only a hard-private profile stays masked even live.
        static string QName(int q)
        {
            switch (q)
            {
                case 426: return "Conquest"; case 451: case 504: return "Ranked Conquest";
                case 435: return "Arena"; case 448: return "Joust"; case 450: case 503: return "Ranked Joust";
                case 440: case 502: return "Ranked Duel"; case 445: return "Assault"; case 466: return "Clash";
                case 459: return "Slash"; case 433: return "Domination"; case 434: return "MOTD";
                default: return q > 0 ? "Queue " + q : "Match";
            }
        }

        // Open the live-match roster for a player IF they're in a game right now (used by the friend preview panel).
        async Task ViewLiveGame(string id)
        {
            try
            {
                using var sdoc = JsonDocument.Parse(await SmiteApi.Call("getplayerstatus", id));
                if (sdoc.RootElement.ValueKind == JsonValueKind.Array && sdoc.RootElement.GetArrayLength() > 0)
                {
                    var st = sdoc.RootElement[0]; string m = GS(st, "Match");
                    if (GI(st, "status") == 3 && !string.IsNullOrEmpty(m) && m != "0") { await ShowLiveMatch(m); return; }
                }
                MessageBox.Show(this, "This player isn't in a live game right now.", "SMITE");
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't load the live game: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        async Task ShowLiveMatch(string matchId)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(await SmiteApi.Call("getmatchplayerdetails", matchId)); }
            catch (Exception ex) { MessageBox.Show(this, "Live match failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                { MessageBox.Show(this, "This game is no longer live (the live roster is only available while the match is in progress).", "SMITE"); return; }
                var players = doc.RootElement.EnumerateArray().ToList();
                string qs = GS(players[0], "Queue");
                string qn = int.TryParse(qs, out var qid) ? QName(qid) : (string.IsNullOrEmpty(qs) ? "Live Match" : qs);
                try
                {
                    using (var dlg = new Form())
                    {
                        dlg.Text = "● LIVE  —  " + qn + "   ·   match " + matchId;
                        dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                        dlg.StartPosition = FormStartPosition.CenterParent;
                        dlg.FormBorderStyle = FormBorderStyle.Sizable; dlg.MinimizeBox = false;
                        dlg.ClientSize = new Size(S(760), S(560));
                        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(S(10)) };
                        dlg.Controls.Add(body);
                        // EXACT reveal from the local game logs — names exist in the combat log from match load, so even a
                        // live match (where the API blanks hidden players) reveals everyone. Correlate by the public ids.
                        Dictionary<string, (string name, string id)> logMap = null;
                        if (GameLog.Enabled)
                        {
                            GameLog.Ingest(true);   // force: bypass the watcher debounce so the live roster is current
                            logMap = GameLog.CorrelateMatch(players.Select(pl => (GS(pl, "playerId"), GS(pl, "GodId"), GI(pl, "taskForce"))).ToList());
                        }
                        var present = new HashSet<string>();   // names/ids already visible → a hidden slot can't be one of them
                        foreach (var pl in players) { string pn = GS(pl, "playerName"); if (!string.IsNullOrEmpty(pn)) present.Add(pn); string pidp = GS(pl, "playerId"); if (!string.IsNullOrEmpty(pidp) && pidp != "0") present.Add(pidp); }
                        int y = S(6), rowW = S(716);
                        foreach (int tf in new[] { 1, 2 })
                        {
                            var team = players.Where(pl => GI(pl, "taskForce") == tf).ToList();
                            if (team.Count == 0) continue;
                            body.Controls.Add(new Label
                            {
                                Location = new Point(S(4), y), Size = new Size(rowW, S(28)), Font = Theme.F(11f, FontStyle.Bold), ForeColor = Color.White,
                                BackColor = tf == 1 ? Color.FromArgb(28, 52, 74) : Color.FromArgb(74, 40, 28), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, 0, 0),
                                Text = "TEAM " + tf + "    ·    " + team.Count + " players"
                            });
                            y += S(34);
                            foreach (var pl in team) { body.Controls.Add(MakeLiveRow(pl, y, rowW, matchId, logMap, present)); y += S(44); }
                            y += S(10);
                        }
                        try { int on = 1; if (DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(dlg.Handle, 19, ref on, 4); } catch { }
                        if (NameDb.Enabled) NameDb.Save();   // persist names captured from this live roster
                        dlg.ShowDialog(this);
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, "Live match failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        Control MakeLiveRow(JsonElement pl, int y, int rowW, string matchId = null, Dictionary<string, (string name, string id)> logMap = null, HashSet<string> present = null)
        {
            var row = new Panel { Location = new Point(S(4), y), Size = new Size(rowW, S(40)), BackColor = Theme.Panel };
            string god = GS(pl, "GodName");
            row.Controls.Add(new PictureBox { Location = new Point(S(6), S(4)), Size = new Size(S(32), S(32)), SizeMode = PictureBoxSizeMode.Zoom, Image = GodListIcon(god) });
            string nm = GS(pl, "playerName"); bool priv = string.IsNullOrEmpty(nm);
            // EXACT reveal from the local game logs for a hidden live slot (by godId+team from the captured combat-log roster).
            string logName = null;
            if (GameLog.Enabled && priv && logMap != null && logMap.TryGetValue(GS(pl, "GodId") + "|" + GI(pl, "taskForce"), out var lr)) logName = lr.name;
            int clanId = GI(pl, "TeamId"), acct = GI(pl, "Account_Level"), mast = GI(pl, "Mastery_Level"), tier = GI(pl, "Tier");
            string clan = GS(pl, "Team_Name");
            // Learn each PUBLIC live player (hidden ones are anonymized here too, so they're skipped). LearnLiveSlot also
            // stashes god→name so a player who is public now but toggles private before the match ends can still resolve.
            if (NameDb.Enabled && !priv)
            {
                NameDb.LearnLiveSlot(matchId, GI(pl, "taskForce"), GS(pl, "GodId"), nm);   // exact reveal once this match completes (keyed by GodId)
                string lpid = GS(pl, "playerId");
                if (!string.IsNullOrEmpty(lpid) && lpid != "0") NameDb.Learn(lpid, nm, GI(pl, "portal_id"), clanId, clan, acct, god, mast);
            }
            string tail = clan.Length > 0 ? "  ·  [" + clan + "]" : "";
            var nameLbl = new Label { Location = new Point(S(46), S(3)), Size = new Size(S(560), S(18)), Font = Theme.F(9.5f, FontStyle.Bold), AutoEllipsis = true };
            var subLbl = new Label { Location = new Point(S(46), S(21)), Size = new Size(S(620), S(16)), Font = Theme.F(8.5f), ForeColor = Theme.Dim };
            void Paint()
            {
                var tag = priv ? MatchHidden(clanId, acct, mast, null, god) : null;
                if (tag != null && present != null && present.Contains(tag.Nick)) tag = null;   // not someone already shown in this match
                // EXACT game-log ✔ is ground truth → outranks the fuzzy ★ tag (which can mis-match a different player).
                nameLbl.ForeColor = !priv ? Theme.Text : !string.IsNullOrEmpty(logName) ? Color.FromArgb(120, 210, 140) : tag != null ? Theme.Blue : Theme.Yellow;
                nameLbl.Text = !priv ? nm : !string.IsNullOrEmpty(logName) ? "✔ " + logName + tail : tag != null ? "★ " + tag.Nick + tail : "Private profile" + tail + "   (click to name)";
                string conf = priv && string.IsNullOrEmpty(logName) && tag != null && tag.Seen > 1 ? "   ·   seen " + tag.Seen + "×" : "";
                subLbl.Text = "Mastery " + mast + "   ·   Account Lv " + acct + "   ·   " + (tier > 0 ? "Ranked: " + TierName(tier) + " (" + GI(pl, "tierWins") + "-" + GI(pl, "tierLosses") + ")" : "Unranked") + conf;
            }
            Paint();
            row.Controls.Add(nameLbl); row.Controls.Add(subLbl);
            if (priv)   // hidden in a LIVE game → let the user name them (weaker fingerprint than a finished scoreboard, but it's the user's own tag)
            {
                EventHandler tagIt = (s, e) =>
                {
                    var cur = MatchHidden(clanId, acct, mast, null, god);
                    string nick = PromptText("Name this hidden player",
                        god + tail + "   ·   Acct Lv " + acct + "   ·   Mastery " + mast + "\r\n(matched by " + (clan.Length > 0 ? "clan + " : "") + "level + mastery; clear the box to remove)",
                        cur?.Nick ?? "");
                    if (nick == null) return;
                    SetHiddenTag(clanId, clan, acct, mast, nick, null, god);
                    // EXACT reveal for THIS match once it completes: live carries no clan and its Mastery_Level differs
                    // from the completed value, so the fingerprint tag alone can't re-match the finished scoreboard. Pin
                    // the exact slot by matchId + team + GodId (empty nick forgets it), and Save now — the roster Save
                    // already ran before this click, so the new tag would otherwise sit only in memory.
                    NameDb.LearnLiveSlot(matchId, GI(pl, "taskForce"), GS(pl, "GodId"), nick);
                    NameDb.Save(true);   // FORCE: the roster Save ran <8s ago, so a debounced Save() here would be a no-op and lose the tag
                    Paint();
                };
                row.Cursor = nameLbl.Cursor = subLbl.Cursor = Cursors.Hand;
                row.Click += tagIt; nameLbl.Click += tagIt; subLbl.Click += tagIt;
            }
            return row;
        }

        // --- per-queue stats for one god (getqueuestats) -----------------------
        async Task ShowGodQueues(string playerId, string playerName, string godName, string godId)
        {
            var probe = new (int id, string name)[]
            {
                (426, "Conquest"), (451, "Ranked Conquest"), (435, "Arena"), (448, "Joust 3v3"),
                (450, "Ranked Joust"), (440, "Ranked Duel"), (445, "Assault"), (466, "Clash"), (459, "Slash")
            };
            var rows = new List<(string q, int w, int l, int k, int d, int a, int m)>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var (qid, qname) in probe)
                {
                    try
                    {
                        using var qdoc = JsonDocument.Parse(await SmiteApi.Call("getqueuestats", playerId, qid.ToString()));
                        if (qdoc.RootElement.ValueKind != JsonValueKind.Array) continue;
                        foreach (var r in qdoc.RootElement.EnumerateArray())
                        {
                            if (GS(r, "GodId") != godId) continue;
                            string q = GS(r, "Queue"); if (string.IsNullOrEmpty(q)) q = qname;
                            rows.Add((q, GI(r, "Wins"), GI(r, "Losses"), GI(r, "Kills"), GI(r, "Deaths"), GI(r, "Assists"), GI(r, "Matches")));
                        }
                    }
                    catch { }
                }
            }
            finally { Cursor = Cursors.Default; }

            try
            {
                using (var dlg = new Form())
                {
                    dlg.Text = godName + " — by queue  ·  " + playerName;
                    dlg.BackColor = Theme.Bg; dlg.ForeColor = Theme.Text; dlg.Font = Theme.F(9.5f);
                    dlg.StartPosition = FormStartPosition.CenterParent; dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dlg.MinimizeBox = false; dlg.MaximizeBox = false; dlg.ClientSize = new Size(S(560), S(360));
                    var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.F(9.5f), HideSelection = true };
                    lv.Columns.Add("Queue", S(190)); lv.Columns.Add("W", S(54)); lv.Columns.Add("L", S(54)); lv.Columns.Add("Win%", S(66)); lv.Columns.Add("KDA", S(70)); lv.Columns.Add("Matches", S(84));
                    foreach (var r in rows.OrderByDescending(r => r.m))
                    {
                        int tot = r.w + r.l; string wp = tot > 0 ? (r.w * 100 / tot) + "%" : "-";
                        double kda = r.d > 0 ? (double)(r.k + r.a) / r.d : (r.k + r.a);
                        lv.Items.Add(new ListViewItem(new[] { r.q, r.w.ToString(), r.l.ToString(), wp, kda.ToString("0.00"), r.m.ToString() }));
                    }
                    if (rows.Count == 0) lv.Items.Add(new ListViewItem(new[] { "(no normal/ranked queue data for this god)", "", "", "", "", "" }));
                    var hdr = new Label { Dock = DockStyle.Top, Height = S(30), Text = "  " + godName + " — performance by queue", Font = Theme.F(10f, FontStyle.Bold), ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Panel };
                    dlg.Controls.Add(lv); dlg.Controls.Add(hdr);
                    try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }
                    try { SetWindowTheme(lv.Handle, "DarkMode_Explorer", null); } catch { }
                    dlg.ShowDialog(this);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Queue stats failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // --- ini helpers -------------------------------------------------------
        static bool IsEntity(string text) => Regex.IsMatch(text, @"\[TgGame\.(TgPawn|TgDevice)_");

        static string Prettify(string b)
        {
            if (Overrides.TryGetValue(b, out var o)) return o;
            string s = Regex.Replace(b, @"([a-z0-9])([A-Z])", "$1 $2");
            s = Regex.Replace(s, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
            return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : b;
        }

        static List<Param> Parse(string text)
        {
            var list = new List<Param>();
            string[] lines = text.Split('\n');
            string section = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                var sm = Regex.Match(line, @"^\s*\[(.+?)\]\s*$");
                if (sm.Success) { section = sm.Groups[1].Value; continue; }
                if (section == null) continue;
                if (Regex.IsMatch(section, @"^IniVersion$", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(section, @"^Configuration$", RegexOptions.IgnoreCase)) continue;
                int semi = line.IndexOf(';');
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (semi >= 0 && semi < eq) continue;     // whole line is a comment
                string key = line.Substring(0, eq).Trim();
                if (key.Length == 0) continue;
                string rest = line.Substring(eq + 1);
                int c = rest.IndexOf(';');
                string value = (c >= 0 ? rest.Substring(0, c) : rest).Trim();
                string comment = c >= 0 ? rest.Substring(c + 1).Trim() : "";
                var pm = Regex.Match(key, @"^([a-zA-Z]+)_");
                string prefix = pm.Success ? pm.Groups[1].Value.ToLowerInvariant() : "";
                list.Add(new Param { Key = key, Value = value, Original = value, Comment = comment, Prefix = prefix, Section = section, LineIndex = i });
            }
            return list;
        }

        // Replaces only the value on a line, preserving the key, spacing, any inline comment, and CRLF.
        static string SetLineValue(string raw, string newVal)
        {
            string cr = raw.EndsWith("\r") ? "\r" : "";
            string line = cr.Length > 0 ? raw.Substring(0, raw.Length - 1) : raw;
            int eq = line.IndexOf('=');
            if (eq < 0) return raw;
            string left = line.Substring(0, eq + 1);
            string rest = line.Substring(eq + 1);
            string lead = Regex.Match(rest, @"^(\s*)").Groups[1].Value;
            int c = rest.IndexOf(';');
            if (c >= 0) return left + lead + newVal + " " + rest.Substring(c) + cr;
            return left + lead + newVal + cr;
        }
    }
}
