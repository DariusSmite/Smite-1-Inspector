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

    static class MctsPortal
    {
        public class MatchPlayer
        {
            public int TaskForce { get; set; }
            public int Kills { get; set; }
            public int Deaths { get; set; }
            public int Assists { get; set; }
            public int Mastery { get; set; }
            public int Level { get; set; }
            public int PortalId { get; set; }
            public string PortalUserId { get; set; } = "";
            public string PlayerName { get; set; } = "";
            public string PlayerId { get; set; } = "";
            public int ClanId { get; set; }
            public string ClanName { get; set; } = "";
            public int Gold { get; set; }
            public bool Privacy { get; set; }
        }
        class Persist { public Dictionary<string, List<MatchPlayer>> Matches { get; set; } = new(); }
        static Dictionary<string, List<MatchPlayer>> _matches = new();
        static readonly object _lock = new();
        static string CacheFile => Path.Combine(Theme.DataDir, "mcts_portal.json");

        public static void Load()
        {
            lock (_lock)
            {
                try { if (File.Exists(CacheFile)) { var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(CacheFile)); _matches = p?.Matches ?? new(); } }
                catch { _matches = new(); }
            }
        }
        public static void Save()
        {
            Persist p; lock (_lock) { p = new Persist { Matches = new(_matches) }; }
            try { File.WriteAllText(CacheFile, JsonSerializer.Serialize(p)); } catch { }
        }
        public static int MatchCount { get { lock (_lock) return _matches.Count; } }

        public static int AutoImport()
        {
            int total = 0;
            try
            {
                var dir = Theme.DataDir;
                if (!Directory.Exists(dir)) return 0;
                foreach (var f in Directory.GetFiles(dir, "mcts_import*.txt"))
                {
                    int n = ImportProbeFile(f);
                    if (n > 0) { total += n; try { File.Move(f, f + ".imported", true); } catch { } }
                }
            }
            catch { }
            if (total > 0) Save();
            return total;
        }

        public static int ImportProbeFile(string path)
        {
            if (!File.Exists(path)) return 0;
            var lines = File.ReadAllLines(path);
            int imported = 0;
            int i = 0;
            while (i < lines.Length)
            {
                if (!lines[i].Contains("REQUEST_MATCH_DETAILS")) { i++; continue; }
                i++;
                string matchId = null;
                var players = new List<MatchPlayer>();
                while (i < lines.Length && !lines[i].StartsWith("===") && !lines[i].StartsWith("}"))
                {
                    string line = lines[i].Trim().TrimEnd('|');
                    if (line.StartsWith("MAP_INSTANCE_ID"))
                    {
                        var val = ExtractVal(line);
                        matchId = ParseMctsVal(val);
                    }
                    if (line.StartsWith("Row Dump") && matchId != null)
                    {
                        var pl = ParsePlayerBlock(lines, ref i);
                        if (pl != null) players.Add(pl);
                        continue;
                    }
                    i++;
                }
                if (matchId != null && players.Count > 0)
                {
                    lock (_lock) { _matches[matchId] = players; }
                    imported++;
                    if (NameDb.Enabled) foreach (var pl in players.Where(p => !p.Privacy && !string.IsNullOrEmpty(p.PlayerName) && !string.IsNullOrEmpty(p.PlayerId) && p.PlayerId != "0" && !string.IsNullOrEmpty(p.PortalUserId)))
                        NameDb.Learn(pl.PlayerId, pl.PlayerName, pl.PortalId, pl.ClanId, pl.ClanName, pl.Level, "", pl.Mastery, portalUserId: pl.PortalUserId);
                }
                i++;
            }
            return imported;
        }

        static MatchPlayer ParsePlayerBlock(string[] lines, ref int i)
        {
            i++;
            var pl = new MatchPlayer();
            bool hasData = false;
            while (i < lines.Length)
            {
                string line = lines[i].Trim().TrimEnd('|');
                if (line.StartsWith("---") || line.StartsWith("Row Dump") || line.StartsWith("===") || line.StartsWith("}")) break;
                if (line.Contains(" = "))
                {
                    int eq = line.IndexOf(" = ");
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 3).Trim();
                    switch (key)
                    {
                        case "PLAYER_NAME": pl.PlayerName = val; break;
                        case "PLAYER_ID": pl.PlayerId = ParseMctsVal(val); break;
                        case "PORTAL_ID": if (int.TryParse(val, out var pid)) pl.PortalId = pid; break;
                        case "PORTAL_USERID": pl.PortalUserId = val; break;
                        case "TASK_FORCE": if (int.TryParse(val, out var tf)) pl.TaskForce = tf; break;
                        case "KILLS_PLAYER": if (int.TryParse(val, out var k)) pl.Kills = k; break;
                        case "DEATHS": if (int.TryParse(val, out var d)) pl.Deaths = d; break;
                        case "ASSISTS": if (int.TryParse(val, out var a)) pl.Assists = a; break;
                        case "MASTERY_LEVEL": if (int.TryParse(val, out var m)) pl.Mastery = m; break;
                        case "GOLD_EARNED": if (int.TryParse(val, out var g)) pl.Gold = g; break;
                        case "PRIVACY_FLAG": pl.Privacy = val == "1"; break;
                        case "CLAN_ID": var cv = ParseMctsVal(val); if (int.TryParse(cv, out var ci)) pl.ClanId = ci; break;
                        case "CLAN_NAME": pl.ClanName = val; break;
                        case "DATA_SET": break;
                        case "SUCCESS": break;
                        default: break;
                    }
                    hasData = true;
                }
                i++;
            }
            return hasData && pl.TaskForce > 0 ? pl : null;
        }

        static string ParseMctsVal(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            int colon = v.LastIndexOf(':');
            return colon >= 0 ? v.Substring(colon + 1) : v;
        }
        static string ExtractVal(string line)
        {
            int eq = line.IndexOf(" = ");
            return eq >= 0 ? line.Substring(eq + 3).Trim() : "";
        }

        public static MatchPlayer FindPlayer(string matchId, int taskForce, int kills, int deaths, int assists)
        {
            lock (_lock)
            {
                if (!_matches.TryGetValue(matchId, out var players)) return null;
                return players.FirstOrDefault(p => p.TaskForce == taskForce && p.Kills == kills && p.Deaths == deaths && p.Assists == assists);
            }
        }

        public static bool HasMatch(string matchId) { lock (_lock) return _matches.ContainsKey(matchId); }

        public static async Task<bool> QueryLive(string matchId, string relayDir, int timeoutMs = 5000)
        {
            if (HasMatch(matchId)) return true;
            string cmdFile = Path.Combine(relayDir, "probe_cmd.txt");
            string respFile = Path.Combine(relayDir, "probe_responses.txt");
            long baseline = 0;
            try { if (File.Exists(respFile)) baseline = new FileInfo(respFile).Length; } catch { }
            try { File.WriteAllText(cmdFile, $"FC5F3FE1 685:u={matchId}\n"); } catch { return false; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(200);
                try
                {
                    if (!File.Exists(respFile)) continue;
                    var fi = new FileInfo(respFile);
                    if (fi.Length <= baseline) continue;
                    using var fs = new FileStream(respFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(baseline, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string tail = sr.ReadToEnd();
                    if (!tail.Contains("REQUEST_MATCH_DETAILS")) continue;
                    var lines = tail.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                    int i = 0;
                    while (i < lines.Length)
                    {
                        if (!lines[i].Contains("REQUEST_MATCH_DETAILS")) { i++; continue; }
                        i++;
                        string mid = null;
                        var players = new List<MatchPlayer>();
                        while (i < lines.Length && !lines[i].StartsWith("===") && !lines[i].StartsWith("}"))
                        {
                            string line = lines[i].Trim().TrimEnd('|');
                            if (line.StartsWith("MAP_INSTANCE_ID")) { mid = ParseMctsVal(ExtractVal(line)); }
                            if (line.StartsWith("Row Dump") && mid != null)
                            {
                                var pl = ParsePlayerBlock(lines, ref i);
                                if (pl != null) players.Add(pl);
                                continue;
                            }
                            i++;
                        }
                        if (mid != null && players.Count > 0)
                        {
                            lock (_lock) { _matches[mid] = players; }
                            if (NameDb.Enabled) foreach (var pl in players.Where(p => !p.Privacy && !string.IsNullOrEmpty(p.PlayerName) && !string.IsNullOrEmpty(p.PlayerId) && p.PlayerId != "0" && !string.IsNullOrEmpty(p.PortalUserId)))
                                NameDb.Learn(pl.PlayerId, pl.PlayerName, pl.PortalId, pl.ClanId, pl.ClanName, pl.Level, "", pl.Mastery, portalUserId: pl.PortalUserId);
                            if (mid == matchId) { Save(); return true; }
                        }
                        i++;
                    }
                }
                catch { }
            }
            return HasMatch(matchId);
        }
    }
}
