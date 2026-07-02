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
}
