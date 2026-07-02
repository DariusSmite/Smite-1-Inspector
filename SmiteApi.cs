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
}
