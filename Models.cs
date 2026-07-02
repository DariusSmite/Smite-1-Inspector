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

    // ===================== Whispers: standalone (game-closed) MCTS chat =====================
    // One persisted whisper conversation with a player.
    // St: "" normal · "queued" (sent before the engine finished logging in — still cancellable) · "cancelled".
    sealed class WMsg  { public long T { get; set; } public string Dir { get; set; } = "in"; public string Text { get; set; } = ""; public string St { get; set; } = ""; }
    // Pin = sticks to the top of the list. Hidden = soft-deleted (removed from the list but history kept; reopening restores it).
    sealed class WConv { public string Key { get; set; } = ""; public string Display { get; set; } = ""; public string Id { get; set; } = ""; public long Last { get; set; } public List<WMsg> Msgs { get; set; } = new(); public bool Pin { get; set; } public bool Hidden { get; set; } }
}
