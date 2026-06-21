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
                EnsureFile(reg, "https://github.com/JulietaUla/Montserrat/raw/master/fonts/ttf/Montserrat-Regular.ttf");
                EnsureFile(bold, "https://github.com/JulietaUla/Montserrat/raw/master/fonts/ttf/Montserrat-Bold.ttf");

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

        static void EnsureFile(string path, string url)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1024) return;
            try
            {
                byte[] data = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
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
                    var slots = new Dictionary<string, AbilityInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ab in god.Value.EnumerateArray())
                    {
                        string slot = ab.GetProperty("slot").GetString();
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
        // Roll the day on READ as well as on count, so a poller that throttled itself to silence still un-sticks at
        // midnight (the reset can't depend on an outbound call, or hitting the cap would freeze it permanently).
        static void RollDay() { var today = DateTime.Now.Date; if (today != _reqDay) { _reqDay = today; _reqCount = 0; } }
        public static int RequestsToday { get { RollDay(); return _reqCount; } }
        static void CountRequest() { RollDay(); _reqCount++; }

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

        static async Task<string> EnsureSession()
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

        // Calls a data method and returns the raw JSON string. args are appended (URL-escaped) to the path.
        public static async Task<string> Call(string method, params string[] args)
        {
            string sid = await EnsureSession();
            string t = Ts();
            string url = Base + "/" + method + "Json/" + _dev + "/" + Md5(_dev + method + _auth + t) + "/" + sid + "/" + t;
            foreach (var a in args) url += "/" + Uri.EscapeDataString(a);
            CountRequest();
            return await _http.GetStringAsync(url);
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

    // A saved favorite player (persisted to favorites.json next to the exe).
    class FavPlayer
    {
        public string Name { get; set; } = "";
        public string Id { get; set; } = "";
        public int Portal { get; set; }
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
    }

    // User preferences, persisted to settings.json next to the exe.
    class AppSettings
    {
        public int StartupTab { get; set; }   // 0 = God Inspector, 1 = Player Tracker
        public int TimeFormat { get; set; }   // 0 = system default, 1 = 12-hour, 2 = 24-hour
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
                using (var hb = new SolidBrush(Color.FromArgb(13, 13, 13))) g.FillRectangle(hb, bounds);
                if (_hdr == null) _hdr = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
                var arrow = new Rectangle(bounds.Left + Sc(6), bounds.Top, Sc(16), bounds.Height);
                TextRenderer.DrawText(g, r.Collapsed ? "▶" : "▼", Font, arrow, Color.FromArgb(193, 30, 31), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                var hrect = new Rectangle(bounds.Left + Sc(24), bounds.Top, bounds.Width - Sc(26), bounds.Height);
                TextRenderer.DrawText(g, r.Name, _hdr, hrect, Color.FromArgb(193, 30, 31), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            bool sel = index == SelectedIndex;
            bool hov = index == _hoverRow && !sel;   // whole-row hover highlight
            Color bgc = sel ? Color.FromArgb(50, 50, 60) : hov ? Color.FromArgb(34, 34, 42) : Color.FromArgb(20, 20, 20);
            using (var bg = new SolidBrush(bgc)) g.FillRectangle(bg, bounds);
            if (sel) using (var ab = new SolidBrush(Color.FromArgb(193, 30, 31))) g.FillRectangle(ab, bounds.Left, bounds.Top, Sc(3), bounds.Height);   // red accent bar = selected
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
        Panel settingsHost, friendListHost;          // Settings tab / Friend List tab content
        Action _flShow;                              // entering the Friend List tab: seed once, else resume the live poller
        Action _flPause;                             // leaving the Friend List tab: pause the live poller
        Button friendAddBtn;                         // ＋ add-current-player-to-Friend-List toggle (tracker)
        readonly AppSettings settings = new AppSettings();
        readonly List<FavPlayer> friendList = new List<FavPlayer>();   // user buddy list w/ live status (friendlist.json)
        readonly List<FavPlayer> recents = new List<FavPlayer>();   // auto recent-lookups ("Saved"), recents.json
        Action<int> _trkSubTab;                      // selects a PRIMARY tracker tab (0 Track, 1 Favorites, 2 Recent)
        Action _trkPlayerLoaded;                      // called when a player finishes loading → reveal the secondary (Overview/Achievements/Friends) strip
        Func<string, string, Task> _trkLoadPlayer;   // load a player into the tracker by (id, name) — used by the Friend List
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
            statusLbl = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleRight, Font = Theme.F(9f), Padding = new Padding(0, 0, S(14), 0), Text = "Pick your SMITE Config folder to start." };
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
            // 0 God Inspector, 1 Player Tracker, 2 Friend List, 3 Settings (the tracker's Track/Saved/Favorites/Friends are sub-tabs inside the view)
            navBtns = new[] { MkNav("God Inspector"), MkNav("Player Tracker"), MkNav("Friend List"), MkNav("Settings") };
            foreach (var b in navBtns) navFlow.Controls.Add(b);
            for (int i = 0; i < navBtns.Length; i++) { int k = i; navBtns[i].Click += (s, e) => SelectNav(k); }
            sideRail.Controls.Add(navFlow); sideRail.Controls.Add(brandWrap); sideRail.Controls.Add(railLine);

            Controls.Add(root);       // fill (added first → behind)
            Controls.Add(sideRail);   // docks left

            // events
            openBtn.Click   += (s, e) => OpenFolder();
            rescanBtn.Click += (s, e) => { if (folderPath != null) Scan(); };
            inspectBtn.Click += (s, e) => ShowSdkInspector();
            applyBtn.Click  += (s, e) => Apply();
            reloadBtn.Click += (s, e) => ReloadFile();
            restoreBtn.Click += (s, e) => RestoreDefaults();
            addBtn.Click += (s, e) => AddTunable();
            searchBox.TextChanged      += (s, e) => RenderList();
            showAllChk.CheckedChanged  += (s, e) => RenderList();
            showHelpChk.CheckedChanged += (s, e) => { if (current != null) RenderRows(); };
            godBox.SelectedIndexChanged += (s, e) => { if (godBox.SelectedItem is GodFile g) LoadGod(g); };

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
            bool wasTracker = curMode == 1;
            SwitchMode(1);
            if (!wasTracker) _trkSubTab?.Invoke(0);   // entering the tracker → default to the Track sub-tab
        }

        // mode visibility: 0 = God Inspector, 1 = Player Tracker, 2 = Friend List, 3 = Settings
        void SwitchMode(int mode)
        {
            curMode = mode;
            if (mode != 2) _flPause?.Invoke();   // stop the Friend List live poller whenever another tab is showing (zero FL calls while hidden)
            bool insp = mode == 0, trk = mode == 1, fl = mode == 2, set = mode == 3;
            split.Visible = insp;
            trackerHost.Visible = trk;
            if (friendListHost != null) friendListHost.Visible = fl;
            if (settingsHost != null) settingsHost.Visible = set;
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
            string s = char.ToUpper(parts[0][0]).ToString();
            if (parts.Length > 1) s += char.ToUpper(parts[1][0]);
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
                int n = int.Parse(slot);
                if (n == 4) return ("A4", "ULTIMATE", "4", 4, true, true);
                return ("A" + n, "ABILITY " + n, n.ToString(), n, true, false);
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
                File.WriteAllText(current.Path, newText, enc);

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

            Add(Lbl("More options coming soon.  ·  Data folder: " + Theme.DataDir, Theme.Dim, 8.5f, y)); y += S(24);
            return host;
        }

        static readonly HttpClient _imgHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        readonly Dictionary<string, Image> _avatarCache = new Dictionary<string, Image>();
        // Download (and cache) a player's in-game avatar image from its Avatar_URL. Returns null on empty/failure.
        async Task<Image> LoadAvatar(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_avatarCache.TryGetValue(url, out var cached)) return cached;   // includes negatively-cached nulls
            Image img = null;
            try
            {
                var bytes = await _imgHttp.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                img = new Bitmap(tmp);   // copy so the stream can be disposed
            }
            catch { img = null; }
            // Cache success AND failure (null) so a broken URL isn't re-downloaded on every preview click. Light cap so a
            // very long session can't grow the cache without bound (entries aren't disposed, so just stop adding past it).
            if (_avatarCache.Count < 256) _avatarCache[url] = img;
            return img;
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
            var dOpen = MkBtn("Open profile  →", 156, false, Theme.Blue, Color.White); dOpen.Location = new Point(S(22), S(160));
            var dViewGame = MkBtn("● View current game", 184, false, Theme.Input, Theme.Green); dViewGame.Location = new Point(S(22), S(198));
            var dHint = new Label { Location = new Point(S(22), S(26)), AutoSize = true, Font = Theme.F(10f), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = "Click a friend to preview their profile." };
            detail.Controls.Add(dAvatar); detail.Controls.Add(dName); detail.Controls.Add(dSub); detail.Controls.Add(dSeen); detail.Controls.Add(dPrompt); detail.Controls.Add(dOpen); detail.Controls.Add(dViewGame); detail.Controls.Add(dHint);

            string detailId = null;
            void HideDetail() { detailId = null; dImg = null; dAvatar.Visible = dName.Visible = dSub.Visible = dSeen.Visible = dPrompt.Visible = dOpen.Visible = dViewGame.Visible = false; dHint.Visible = true; }
            async void ShowDetail(PlayerRow r)   // async void → wrap the whole body so a stray throw can't crash the message loop
            {
                try
                {
                    detailId = r.Id;
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
                    dAvatar.Visible = dName.Visible = dSub.Visible = dSeen.Visible = dPrompt.Visible = dOpen.Visible = dViewGame.Visible = true;
                    dImg = null; dAvatar.Invalidate();
                    var img = await LoadAvatar(r.Avatar);
                    if (detailId == r.Id) { dImg = img; dAvatar.Invalidate(); }   // ignore if the user clicked a different friend meanwhile
                }
                catch { }
            }
            dOpen.Click += async (s, e) => { if (dOpen.Tag is PlayerRow rr) { SelectNav(1); if (_trkLoadPlayer != null) await _trkLoadPlayer(rr.Id, rr.Name); } };
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
                if (flBusy) { flAgain = true; return; }
                if (friendList.Count == 0) { flRows.Clear(); flist.SetRows(new List<PlayerRow>()); hint.ForeColor = Theme.Dim; hint.Text = "No friends yet — add players from the Player Tracker (＋ Friend List)."; return; }
                flBusy = true;
                try
                {
                    flRows.Clear(); flRows.AddRange(friendList.Select(Row));
                    int progDone = 0, progTotal = flRows.Count; SetProgress(0, progTotal);
                    ApplySort();
                    hint.ForeColor = Theme.Dim; hint.Text = "Checking statuses…";
                    bool nameChanged = false;
                    bool fetchDetails = friendList.Count <= 100;   // getplayer per friend (name + last login); skip for huge lists to spare the rate limit
                    using var sem = new SemaphoreSlim(FlConcurrency);
                    await Task.WhenAll(flRows.Select(async row =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            int code = await PullStatus(row);
                            row.ErrBackoff = 0;
                            if (fetchDetails) { if (await PullPlayer(row)) nameChanged = true; row.NextDetailUtc = DateTime.UtcNow.AddMinutes(30); }
                            row.Extra = code == 0 ? RelTime(row.LastLogin) : "";   // show "last seen" only for offline friends
                        }
                        catch { row.ErrBackoff++; row.Tier = 4; if (string.IsNullOrEmpty(row.Status) || row.Status == "…") { row.Status = "?"; row.StatusCol = Theme.Dim; } }
                        finally { sem.Release(); }
                        flist.UpdateRow(row);   // repaint just this row (no whole-list flash) …
                        LiveSort();             // … and re-order live as statuses land (throttled; A-Z is a no-op)
                        progDone++; SetProgress(progDone, progTotal);   // "12/58" in the red box (continuation is on the UI thread)
                    }));
                    if (nameChanged) SaveFriendList();
                    ApplySort();
                    flLastPoll = DateTime.Now;
                    SetFlHint();
                    // hand off to the live poller: every row is due now (the bucket spreads them out) and gets its tier cadence
                    flSeeded = true;
                    var seedNow = DateTime.UtcNow;
                    // every row was just polled by this pass — schedule the next check per tier cadence (not immediately,
                    // which would re-scan the whole list a second time right after the seed).
                    foreach (var r in flRows) { r.NextDueUtc = seedNow.AddSeconds(TierInterval(r.Tier) + Jitter(TierInterval(r.Tier))); if (r.NextDetailUtc == DateTime.MinValue) r.NextDetailUtc = seedNow.AddMinutes(30); }
                    if (curMode == 2 && !flPoll.Enabled) flPoll.Start();
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
                    foreach (var r in flRows) if (!r.Header && !string.IsNullOrEmpty(r.Extra) && r.LastLogin != DateTime.MinValue)
                    { var ex = RelTime(r.LastLogin); if (ex != r.Extra) { r.Extra = ex; flist.UpdateRow(r); } }
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
                                    row.Extra = code == 0 ? RelTime(row.LastLogin) : "";
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
                finally { flTicking = false; }
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
            this.FormClosed += (s, e) => { flPoll.Stop(); flPoll.Dispose(); };
            return host;
        }

        Panel BuildTrackerPanel()
        {
            LoadFavs(); LoadHiddenTags(); LoadRecents();
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            {
                // --- primary tab strip (Track / Favorites / Recent) ---
                var subBar = new Panel { Dock = DockStyle.Top, Height = S(42), BackColor = Theme.Panel };
                var subBarLine = new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line };
                var subFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(S(14), S(2), 0, 0) };
                var primaryTabs = new[] { MkSubTab("Track"), MkSubTab("Favorites"), MkSubTab("Recent"), MkSubTab("Hidden") };
                foreach (var t in primaryTabs) subFlow.Controls.Add(t);
                subBar.Controls.Add(subFlow); subBar.Controls.Add(subBarLine);

                // --- secondary (player-context) sub-tab strip (Overview / Achievements / Friends) — only while a player is loaded ---
                var subBar2 = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Theme.Bg, Visible = false };
                var subBar2Line = new Panel { Dock = DockStyle.Bottom, Height = S(1), BackColor = Theme.Line };
                var subFlow2 = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(S(28), S(0), 0, 0) };
                var secondaryTabs = new[] { MkSubTab("Overview"), MkSubTab("Masteries"), MkSubTab("Matches"), MkSubTab("Achievements"), MkSubTab("Friends") };
                foreach (var t in secondaryTabs) { t.Height = S(36); subFlow2.Controls.Add(t); }
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
                var lastFriends = new List<PlayerRow>();   // the FRIENDS section of the currently-shown friends list (for "add all")
                var friendCats = new List<(string key, string cap, List<PlayerRow> list)>();   // collapsible friend sections
                var collapsedFriendSecs = new HashSet<string>();
                int friendsHiddenOpaque = 0;
                // Status line: its own full-width strip docked just under the search bar. A fixed Location on the search
                // row would land off-screen at high DPI (the row is already full to the window edge), so it lives here.
                var hint = new Label { Dock = DockStyle.Top, Height = S(22), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(14), 0, S(14), 0), ForeColor = Theme.Dim, Font = Theme.F(8.5f), BackColor = Theme.Panel, Text = "Live data from the official Hi-Rez SMITE API." };
                top.Controls.Add(lbl); top.Controls.Add(bhost); top.Controls.Add(track); top.Controls.Add(favSaveBtn); top.Controls.Add(friendAddBtn); top.Controls.Add(addAllFriendsBtn);

                // --- overview card ---
                var card = new Panel { Dock = DockStyle.Top, Height = S(180), BackColor = Theme.Bg };
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
                var statusLbl = new Label { AutoSize = false, Location = new Point(S(640), S(14)), Size = new Size(S(160), S(24)), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = Theme.F(9f, FontStyle.Bold), Visible = false };
                var statsLbl = new Label { AutoSize = false, Location = new Point(S(14), S(46)), Size = new Size(S(920), S(92)), ForeColor = Theme.Dim, Font = Theme.F(9.5f), Text = "" };
                // Achievements sub-tab — a dedicated FULL-AREA view (its own body panel, card hidden).
                var achStats = new List<(string label, int value)>();
                string achWho = "";
                var achTitleFont = Theme.F(11.5f, FontStyle.Bold);
                var achValFont = Theme.F(15.5f, FontStyle.Bold);
                var achLblFont = Theme.F(9f);
                var achPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, Padding = new Padding(S(18), S(12), S(12), S(12)) };
                achPanel.Paint += (s, e) =>
                {
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    TextRenderer.DrawText(g, "ACHIEVEMENTS" + (string.IsNullOrEmpty(achWho) ? "" : "   —   " + achWho), achTitleFont, new Point(S(18), S(10)), Theme.Accent, TextFormatFlags.NoPrefix);
                    if (achStats.Count == 0)
                    { TextRenderer.DrawText(g, "Load a player on the Track tab to see their achievements.", achLblFont, new Point(S(18), S(44)), Theme.Dim, TextFormatFlags.NoPrefix); return; }
                    int cols = 4, top = S(48), cellW = S(180), cellH = S(58);   // fixed grid (body width reads inflated under mixed DPI; 4 cols must fit a maximized window)
                    for (int i = 0; i < achStats.Count; i++)
                    {
                        int cx = S(18) + (i % cols) * cellW, cy = top + (i / cols) * cellH;
                        TextRenderer.DrawText(g, achStats[i].value.ToString("N0"), achValFont, new Point(cx, cy), Theme.Yellow, TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, achStats[i].label, achLblFont, new Point(cx, cy + S(26)), Theme.Dim, TextFormatFlags.NoPrefix);
                    }
                };
                // Owner-drawn list (search results / favorites / recents / friends). It lives in a FIXED-WIDTH column —
                // a Dock=Fill owner-draw list reads an inflated width under mixed DPI and draws its right-aligned glyphs
                // (trash / ☆ / status) off-screen; a fixed width keeps them in view (same fix as the rail Friend List).
                var plist = new PlayerList { Dock = DockStyle.Fill, Font = Theme.F(10.5f) };
                var listCol = new Panel { Dock = DockStyle.Left, Width = S(740), BackColor = Theme.Bg, Visible = false, Padding = new Padding(S(14), S(8), 0, S(8)) };
                listCol.Controls.Add(plist);
                var hiddenHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, AutoScroll = true };   // "Hidden" primary tab: nicknamed-hidden-player cards
                card.Controls.Add(namePanel); card.Controls.Add(statusLbl); card.Controls.Add(statsLbl);
                card.Resize += (s, e) => { statsLbl.Width = card.Width - S(28); statusLbl.Left = card.Width - S(180); namePanel.Width = Math.Max(S(200), card.Width - S(200)); namePanel.Invalidate(); };

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
                body.Controls.Add(listCol); body.Controls.Add(hiddenHost); body.Controls.Add(achPanel); body.Controls.Add(matchFullHost); body.Controls.Add(godFullHost); body.Controls.Add(splitHost);
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
                    (st == 1 ? (Control)godFullHost : st == 2 ? matchFullHost : st == 3 ? achPanel : st == 4 ? listCol : st == 5 ? hiddenHost : splitHost).BringToFront();
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
                    achStats.Clear(); achWho = ""; achPanel.Invalidate();
                    subBar2.Visible = false;   // no player → hide the player-context sub-tabs
                    statsLbl.Text = ""; statusLbl.Visible = false; statusLbl.Cursor = Cursors.Default;
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
                void ShowAchievements()
                {
                    addAllFriendsBtn.Visible = false; ShowStage(3);
                    hint.ForeColor = Theme.Dim; hint.Text = achStats.Count > 0 ? "Career achievements for " + achWho + "." : "Load a player on the Track tab first.";
                }

                // Renders the full profile for a resolved player. `key` (player name or numeric id) is used for the sub-calls.
                async Task ShowPlayer(JsonElement p, string key)
                {
                    int wins = GI(p, "Wins"), losses = GI(p, "Losses"), tot = wins + losses;
                    string wr = tot > 0 ? ("  (" + (wins * 100 / tot) + "% win)") : "";
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
                    var sb = new StringBuilder();
                    sb.AppendLine("Level " + GI(p, "Level") + "    ·    Mastery " + GI(p, "MasteryLevel") + "    ·    " + GS(p, "Region") + "    ·    " + GS(p, "Platform") + (clan.Length > 0 ? "    ·    Clan: " + clan : ""));
                    sb.AppendLine(wins + " W / " + losses + " L" + wr + "    ·    " + GI(p, "Total_Worshippers").ToString("N0") + " worshippers    ·    " + GI(p, "HoursPlayed").ToString("N0") + " hrs    ·    " + GI(p, "Total_Achievements") + " achievements");
                    string ranks = "";
                    foreach (var rk in new[] { "RankedConquest", "RankedJoust", "RankedDuel" })
                        if (p.TryGetProperty(rk, out var ro) && ro.ValueKind == JsonValueKind.Object)
                        { int tier = GI(ro, "Tier"); if (tier > 0) ranks += (ranks.Length > 0 ? "    ·    " : "") + rk.Replace("Ranked", "") + ": " + TierName(tier) + " (" + GI(ro, "Wins") + "-" + GI(ro, "Losses") + ")"; }
                    sb.AppendLine(ranks.Length > 0 ? "Ranked — " + ranks : "Ranked — none this season");
                    string cr = FmtApiDate(GS(p, "Created_Datetime")), ll = FmtApiDate(GS(p, "Last_Login_Datetime"));
                    var dparts = new List<string>();
                    if (cr.Length > 0) dparts.Add("Created " + cr);
                    if (ll.Length > 0) dparts.Add("Last login " + ll);
                    if (dparts.Count > 0) sb.AppendLine(string.Join("    ·    ", dparts));
                    string msg = GS(p, "Personal_Status_Message");
                    if (!string.IsNullOrWhiteSpace(msg)) sb.AppendLine("“" + msg + "”");
                    statsLbl.Text = sb.ToString();

                    // status chip
                    try
                    {
                        using var sdoc = JsonDocument.Parse(await SmiteApi.Call("getplayerstatus", key));
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
                            tip.SetToolTip(statusLbl, inGame ? "Click to view the live match roster (reveals player names)" : null);
                            statusLbl.Visible = true;
                        }
                    }
                    catch { }

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
                                // multi-kills
                                achStats.Add(("Pentakills", GI(a, "PentaKills")));
                                achStats.Add(("Quadrakills", GI(a, "QuadraKills")));
                                achStats.Add(("Triplekills", GI(a, "TripleKills")));
                                achStats.Add(("Doublekills", GI(a, "DoubleKills")));
                                // sprees
                                achStats.Add(("First Bloods", GI(a, "FirstBloods")));
                                achStats.Add(("Killing Sprees", GI(a, "KillingSpree")));
                                achStats.Add(("Rampages", GI(a, "RampageSpree")));
                                achStats.Add(("Shutdowns", GI(a, "ShutdownSpree")));
                                achStats.Add(("Divine Sprees", GI(a, "DivineSpree")));
                                achStats.Add(("Godlike Sprees", GI(a, "GodLikeSpree")));
                                achStats.Add(("Immortal Sprees", GI(a, "ImmortalSpree")));
                                achStats.Add(("Unstoppable", GI(a, "UnstoppableSpree")));
                                // combat
                                achStats.Add(("Player Kills", GI(a, "PlayerKills")));
                                achStats.Add(("Assists", GI(a, "AssistedKills")));
                                achStats.Add(("Deaths", GI(a, "Deaths")));
                                // objectives
                                achStats.Add(("Tower Kills", GI(a, "TowerKills")));
                                achStats.Add(("Phoenix Kills", GI(a, "PhoenixKills")));
                                achStats.Add(("Gold Furies", GI(a, "GoldFuryKills")));
                                achStats.Add(("Fire Giants", GI(a, "FireGiantKills")));
                                achStats.Add(("Siege Juggernauts", GI(a, "SiegeJuggernautKills")));
                                achStats.Add(("Wild Juggernauts", GI(a, "WildJuggernautKills")));
                                // farm
                                achStats.Add(("Minion Kills", GI(a, "MinionKills")));
                                achStats.Add(("Camps Cleared", GI(a, "CampsCleared")));
                                if (achPanel.Visible) achPanel.Invalidate();
                            }
                        }
                    }
                    catch { }

                    hint.ForeColor = Theme.Dim; hint.Text = "Updated " + FmtNow() + ".";
                }

                // Loads a player by a known key (numeric id from a search pick, or an exact name).
                async Task LoadKey(string key, string display)
                {
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
                    StylePrimary(0);   // searching belongs to the Track tab
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
                plist.Activated += async r => { if (trackBusy) return; listCol.Visible = false; StylePrimary(0); box.Text = r.Name.StartsWith("(hidden") ? "" : r.Name; await Guarded(() => LoadKey(r.Id, r.Name)); };   // loading a row shows a Track profile
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
                _trkLoadPlayer = (id, name) => Guarded(async () => { box.Text = name; StylePrimary(0); await LoadKey(id, name); });
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
                    var d3 = new Label { Location = new Point(S(16), S(84)), AutoSize = true, Font = Theme.F(8.5f), ForeColor = Theme.Dim, BackColor = Theme.Panel, Text = "Seen " + t.Seen + "×" + (string.IsNullOrEmpty(t.LastSeen) ? "" : "    ·    last " + t.LastSeen) + comp + "    ·    click to rename / clear" };
                    EventHandler editIt = (s, e) =>
                    {
                        string n = PromptText("Rename hidden player “" + t.Nick + "”", "Edit the nickname, or clear the box to delete it.", t.Nick);
                        if (n == null) return;
                        if (string.IsNullOrWhiteSpace(n)) hiddenTags.Remove(t); else t.Nick = n.Trim();
                        SaveHiddenTags(); ShowHidden();
                    };
                    card.Click += editIt; nick.Click += editIt; d1.Click += editIt; d2.Click += editIt; d3.Click += editIt;
                    card.Controls.Add(nick); card.Controls.Add(pill); card.Controls.Add(d1); card.Controls.Add(d2); card.Controls.Add(d3);
                    return card;
                }
                void ShowHidden()
                {
                    StylePrimary(3); subBar2.Visible = false;
                    hiddenHost.Controls.Clear();
                    if (hiddenTags.Count == 0)
                    {
                        hiddenHost.Controls.Add(new Label { Location = new Point(S(20), S(20)), AutoSize = true, ForeColor = Theme.Dim, Font = Theme.F(10.5f),
                            Text = "No nicknamed hidden players yet.\r\n\r\nOpen a match scoreboard (double-click a Recent Match) or a live game, click a\r\n“Private” row, and give them a nickname — they'll show up here with a confidence score." });
                        ShowStage(5); hint.ForeColor = Theme.Dim; hint.Text = "Hidden players you've nicknamed."; return;
                    }
                    int y = S(12);
                    foreach (var t in hiddenTags.OrderByDescending(HiddenConfidence).ThenByDescending(x => x.Seen))
                    { hiddenHost.Controls.Add(MakeHiddenCard(t, y)); y += S(116); }
                    ShowStage(5);
                    hint.ForeColor = Theme.Dim; hint.Text = hiddenTags.Count + " nicknamed hidden player" + (hiddenTags.Count == 1 ? "" : "s") + " — click a card to rename or clear.";
                }
                void SelectPrimary(int i)
                {
                    StylePrimary(i);
                    if (i == 0)   // Track: show the player-context strip + the current player view (or the idle search)
                    {
                        subBar2.Visible = PlayerLoaded();
                        if (PlayerLoaded()) SelectSecondary(curSecondary); else ShowSearchView();
                    }
                    else { subBar2.Visible = false; if (i == 1) ShowFavorites(); else if (i == 2) ShowRecents(); else ShowHidden(); }
                }
                for (int i = 0; i < primaryTabs.Length; i++) { int k = i; primaryTabs[i].Click += (s, e) => SelectPrimary(k); }
                for (int j = 0; j < secondaryTabs.Length; j++) { int k = j; secondaryTabs[j].Click += (s, e) => SelectSecondary(k); }
                // when a player finishes loading, reveal the secondary strip and default it to Overview
                _trkPlayerLoaded = () => { StylePrimary(0); subBar2.Visible = true; curSecondary = 0; StyleSecondary(0); ShowStage(0); };
                _trkSubTab = SelectPrimary;
                StylePrimary(0); StyleSecondary(0);
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
            try
            {
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                if (File.Exists(path))
                {
                    try { string old = File.ReadAllText(path); if (old.Trim().Length > 2 && old.Trim() != json.Trim()) File.Copy(path, path + ".bak", true); } catch { }
                }
                File.WriteAllText(path, json);
            }
            catch { }
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
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (s != null) { settings.StartupTab = s.StartupTab; settings.TimeFormat = s.TimeFormat; }
            }
            catch { }
        }
        void SaveSettings()
        {
            SaveJson(SettingsFile, settings);
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
            try
            {
                if (!File.Exists(HiddenFile)) return;
                var list = JsonSerializer.Deserialize<List<HiddenTag>>(File.ReadAllText(HiddenFile));
                if (list != null) foreach (var t in list) if (t != null && !string.IsNullOrWhiteSpace(t.Nick)) hiddenTags.Add(t);
            }
            catch { }
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
                bool statsClose = dl <= 8 && dm <= 6, statsLoose = dl <= 25 && dm <= 15;
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
                return;
            }
            var t = new HiddenTag { ClanId = clanId, Clan = clan, Level = level, Mastery = mastery, Nick = nick.Trim(), Seen = 1, LastSeen = DateTime.Now.ToString("yyyy-MM-dd") };
            if (companions != null) foreach (var c in companions) if (!string.IsNullOrEmpty(c) && !t.Companions.Contains(c)) t.Companions.Add(c);
            if (!string.IsNullOrEmpty(god) && !t.Gods.Contains(god)) t.Gods.Add(god);
            hiddenTags.Add(t);
            SaveHiddenTags();
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
                string queue = GS(first, "name"); if (string.IsNullOrEmpty(queue)) queue = GS(first, "Queue");
                int minutes = GI(first, "Minutes");

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
                        foreach (var pl in team) { body.Controls.Add(MakeScoreRow(pl, y, rowW, dtip, partyColors.TryGetValue(GI(pl, "PartyId"), out var pc) ? pc : Color.Empty, partyNamed.TryGetValue(GI(pl, "PartyId"), out var comp) ? comp : null)); y += S(46); }
                        y += S(10);
                    }
                    try { int on = 1; if (DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(dlg.Handle, 19, ref on, 4); } catch { }
                    dlg.ShowDialog(this);
                }
                }
                catch (Exception ex) { MessageBox.Show(this, "Match details failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        Control MakeScoreRow(JsonElement pl, int y, int rowW, ToolTip dtip, Color partyCol = default, List<string> companions = null)
        {
            var row = new Panel { Location = new Point(S(4), y), Size = new Size(rowW, S(42)), BackColor = Theme.Panel };
            if (partyCol != Color.Empty)   // premade-party accent bar on the left
            {
                row.Controls.Add(new Panel { Location = new Point(0, 0), Size = new Size(S(4), S(42)), BackColor = partyCol });
                dtip.SetToolTip(row, "Premade party");
            }
            row.Controls.Add(new PictureBox { Location = new Point(S(6), S(5)), Size = new Size(S(32), S(32)), SizeMode = PictureBoxSizeMode.Zoom, Image = GodListIcon(GS(pl, "Reference_Name")) });
            string god = GS(pl, "Reference_Name");
            string nm = GS(pl, "playerName"); if (string.IsNullOrEmpty(nm)) nm = GS(pl, "hz_player_name");
            bool priv = string.IsNullOrEmpty(nm);   // privacy flag strips the name + every id; clan/level/mastery survive
            string clan = GS(pl, "Team_Name");
            int clanId = GI(pl, "TeamId"), acct = GI(pl, "Account_Level"), mast = GI(pl, "Mastery_Level");
            string kda = GI(pl, "Kills_Player") + "/" + GI(pl, "Deaths") + "/" + GI(pl, "Assists");
            // Name line + sub line, both repainted by PaintName():
            //  - hidden + saved tag  -> "God — ★ Nick [clan]" (blue)
            //  - hidden, no tag      -> "God — Private [clan] (click to name)" (gold)
            //  - PUBLIC (name wins) but a tag matches -> real name (blue) + sub starts "tagged ★ Nick" so you learn who it was
            var nameLbl = new Label { Location = new Point(S(46), S(3)), Size = new Size(S(282), S(18)), Font = Theme.F(9.5f, FontStyle.Bold), AutoEllipsis = true };
            var subLbl = new Label { Location = new Point(S(46), S(22)), Size = new Size(S(282), S(16)), Font = Theme.F(8.5f), ForeColor = Theme.Dim, AutoEllipsis = true };
            void PaintName()
            {
                var tag = MatchHidden(clanId, acct, mast, companions, god);
                if (!priv)
                {
                    nameLbl.ForeColor = tag != null ? Theme.Blue : Theme.Text;
                    nameLbl.Text = nm;   // god is shown by the icon — drop the name prefix so the player name reads clearly
                    string gp = "KDA " + kda + "   ·   Lv " + GI(pl, "Final_Match_Level") + "   ·   " + GI(pl, "Gold_Earned").ToString("N0") + "g   ·   " + GI(pl, "Damage_Player").ToString("N0") + " dmg";
                    subLbl.ForeColor = tag != null ? Theme.Blue : Theme.Dim;
                    subLbl.Text = tag != null ? "tagged ★ " + tag.Nick + "   ·   " + gp : gp;
                    return;
                }
                if (tag != null) UpdateSighting(tag, clan, acct, mast, companions, god);   // fold this sighting in (level/mastery/companions/gods)
                string tail = clan.Length > 0 ? "  ·  [" + clan + "]" : "";
                nameLbl.ForeColor = tag != null ? Theme.Blue : Theme.Yellow;
                nameLbl.Text = tag != null ? "★ " + tag.Nick + tail : "Private" + tail + "   (click to name)";
                subLbl.ForeColor = Theme.Dim;
                string conf = tag != null && tag.Seen > 1 ? "   ·   seen " + tag.Seen + "×" : "";
                subLbl.Text = "Acct Lv " + acct + "   ·   Mastery " + mast + "   ·   " + kda + conf;
            }
            PaintName();
            row.Controls.Add(nameLbl); row.Controls.Add(subLbl);
            // Clickable to set/edit/clear the nickname when the row is private, OR when a public row matches a tag (cleanup).
            if (priv || MatchHidden(clanId, acct, mast, companions, god) != null)
            {
                EventHandler tagIt = (s, e) =>
                {
                    var cur = MatchHidden(clanId, acct, mast, companions, god);
                    string head = priv ? "Name this hidden player" : "Edit the tag for " + nm;
                    int compCount = companions?.Count ?? 0;
                    string matchNote = compCount > 0 ? "matched by clan + level + mastery + " + compCount + " party-mate" + (compCount == 1 ? "" : "s") : "matched by clan + level + mastery";
                    string nick = PromptText(head,
                        god + (clan.Length > 0 ? "  ·  [" + clan + "]" : "") + "   ·   Acct Lv " + acct + "   ·   Mastery " + mast + "\r\n(" + matchNote + "; clear the box to remove)",
                        cur?.Nick ?? "");
                    if (nick == null) return;
                    SetHiddenTag(clanId, clan, acct, mast, nick, companions, god);
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
                            foreach (var pl in team) { body.Controls.Add(MakeLiveRow(pl, y, rowW)); y += S(44); }
                            y += S(10);
                        }
                        try { int on = 1; if (DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(dlg.Handle, 19, ref on, 4); } catch { }
                        dlg.ShowDialog(this);
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, "Live match failed: " + ex.Message, "SMITE", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        Control MakeLiveRow(JsonElement pl, int y, int rowW)
        {
            var row = new Panel { Location = new Point(S(4), y), Size = new Size(rowW, S(40)), BackColor = Theme.Panel };
            string god = GS(pl, "GodName");
            row.Controls.Add(new PictureBox { Location = new Point(S(6), S(4)), Size = new Size(S(32), S(32)), SizeMode = PictureBoxSizeMode.Zoom, Image = GodListIcon(god) });
            string nm = GS(pl, "playerName"); bool priv = string.IsNullOrEmpty(nm);
            int clanId = GI(pl, "TeamId"), acct = GI(pl, "Account_Level"), mast = GI(pl, "Mastery_Level"), tier = GI(pl, "Tier");
            string clan = GS(pl, "Team_Name");
            string tail = clan.Length > 0 ? "  ·  [" + clan + "]" : "";
            var nameLbl = new Label { Location = new Point(S(46), S(3)), Size = new Size(S(560), S(18)), Font = Theme.F(9.5f, FontStyle.Bold), AutoEllipsis = true };
            var subLbl = new Label { Location = new Point(S(46), S(21)), Size = new Size(S(620), S(16)), Font = Theme.F(8.5f), ForeColor = Theme.Dim };
            void Paint()
            {
                var tag = priv ? MatchHidden(clanId, acct, mast, null, god) : null;
                nameLbl.ForeColor = !priv ? Theme.Text : tag != null ? Theme.Blue : Theme.Yellow;
                nameLbl.Text = !priv ? nm : tag != null ? "★ " + tag.Nick + tail : "Private profile" + tail + "   (click to name)";
                string conf = priv && tag != null && tag.Seen > 1 ? "   ·   seen " + tag.Seen + "×" : "";
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
