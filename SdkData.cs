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
}
