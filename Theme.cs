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
}
