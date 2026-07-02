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

    partial class MainForm : Form
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
        Panel settingsHost, friendListHost, codexHost, whispersHost, extraHost;   // Settings / Friend List / Codex / Whispers / Extra tab content
        Action _extraOnShow;                          // entering the Extra tab: show the one-time disclaimer
        Action _wsOnShow;                            // entering the Whispers tab: ensure the engine is started
        Action _wsAutoStart;                          // app startup: connect the engine in the background IF that option is on
        Action<string, string> _openWhisper;         // open/create a conversation with (player name, player id) — id may be "" for typed names
        Func<bool> _mctsConnected;                   // returns true if the MCTS engine is logged in and running
        string _mctsRelayDir;                         // relay directory for probe commands/responses
        Func<int, Task<bool>> _mctsEnsureConnected;  // start engine if needed, wait up to N ms for connection
        Action _mctsPreconnect;                       // start MCTS engine at app startup for hidden player reveal
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
            extraHost = BuildExtraPanel();
            extraHost.Dock = DockStyle.Fill; extraHost.Visible = false;
            bodyHost.Controls.Add(extraHost);
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
            navBtns = new[] { MkNav("God Inspector"), MkNav("Player Tracker"), MkNav("Friend List"), MkNav("Settings"), MkNav("Codex"), MkNav("Whispers"), MkNav("Extra") };
            foreach (var k in new[] { 1, 2, 5, 0, 4, 6, 3 }) navFlow.Controls.Add(navBtns[k]);
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
            if (idx == 6) { SwitchMode(6); _extraOnShow?.Invoke(); return; }
            bool wasTracker = curMode == 1;
            SwitchMode(1);
            if (!wasTracker) _trkSubTab?.Invoke(1);   // entering the tracker → default to the Track tab (index 1; 0 is My profile)
        }

        // mode visibility: 0 = God Inspector, 1 = Player Tracker, 2 = Friend List, 3 = Settings
        void SwitchMode(int mode)
        {
            curMode = mode;
            if (mode != 2) _flPause?.Invoke();   // stop the Friend List live poller whenever another tab is showing (zero FL calls while hidden)
            bool insp = mode == 0, trk = mode == 1, fl = mode == 2, set = mode == 3, cod = mode == 4, wsp = mode == 5, ext = mode == 6;
            split.Visible = insp;
            trackerHost.Visible = trk;
            if (friendListHost != null) friendListHost.Visible = fl;
            if (settingsHost != null) settingsHost.Visible = set;
            if (codexHost != null) codexHost.Visible = cod;
            if (whispersHost != null) whispersHost.Visible = wsp;
            if (extraHost != null) extraHost.Visible = ext;
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
            BeginInvoke(new Action(() => { try { _mctsPreconnect?.Invoke(); } catch { } }));   // pre-connect MCTS for hidden player reveal
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

        // ===== "Extra" tab — bake the hidden-profile reveal into the installed game (permanent on-disk patch) =====
        const string RevealOrigUpkSha1    = "6F1A631BE4586A581C2342495ABECC3277F20DDD";  // pristine TgClient.upk
        const string RevealPatchedUpkSha1 = "17A8346EFB13E2855BDC2AC179801A840DB27A4A";  // patched TgClient.upk (bundled)
        const int    RevealExeHashOffset  = 0x4036c6b;                                     // stored-hash offset in Smite.exe (fast path)

        Label ExtraSectionTitle(string t) => new Label { AutoSize = true, Text = t, ForeColor = Theme.Text, Font = Theme.F(13.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(10)) };
        Panel ExtraDivider() => new Panel { Width = S(700), Height = 1, BackColor = Theme.Line, Margin = new Padding(0, S(6), 0, S(24)) };

        // A big platform button: circle logo + title + subtitle, hover accent border. Disabled = dimmed, no click.
        Panel MkBigPlatformBtn(string logoKey, string title, string subtitle, bool enabled)
        {
            var p = new Panel { Width = S(330), Height = S(102), BackColor = enabled ? Theme.Panel : Color.FromArgb(9, 9, 9), Cursor = enabled ? Cursors.Hand : Cursors.Default, Margin = new Padding(0, 0, S(16), 0) };
            bool hover = false;
            p.Paint += (s, e) =>
            {
                Color b = !enabled ? Color.FromArgb(30, 30, 30) : (hover ? Theme.Accent : Theme.Line);
                using var pen = new Pen(b, (hover && enabled) ? 2f : 1f);
                e.Graphics.DrawRectangle(pen, 1, 1, p.Width - 3, p.Height - 3);
            };
            var icon = new PictureBox { Size = new Size(S(54), S(54)), Location = new Point(S(20), S(24)), BackColor = Color.Transparent, SizeMode = PictureBoxSizeMode.Zoom };
            try { icon.Image = PlatformLogo(logoKey, S(54)); } catch { }
            var t = new Label { AutoSize = true, Text = title, ForeColor = enabled ? Theme.Text : Theme.Dim, Font = Theme.F(13f, FontStyle.Bold), Location = new Point(S(90), S(28)), BackColor = Color.Transparent };
            var sub = new Label { AutoSize = true, Text = subtitle, ForeColor = Theme.Dim, Font = Theme.F(8.5f), Location = new Point(S(90), S(56)), BackColor = Color.Transparent };
            p.Controls.Add(icon); p.Controls.Add(t); p.Controls.Add(sub);
            if (enabled)
            {
                EventHandler en = (s, e) => { hover = true;  p.BackColor = Color.FromArgb(22, 22, 26); p.Invalidate(); };
                EventHandler lv = (s, e) => { hover = false; p.BackColor = Theme.Panel;                 p.Invalidate(); };
                foreach (Control c in new Control[] { p, icon, t, sub }) { c.MouseEnter += en; c.MouseLeave += lv; }
            }
            p.Tag = enabled;
            return p;
        }

        void WireBigBtn(Panel p, Action onClick)
        {
            if (!(p.Tag is bool en) || !en) return;
            void handler(object s, EventArgs e) => onClick();
            p.Click += handler;
            foreach (Control c in p.Controls) c.Click += handler;
        }

        static string RevealSmiteRoot(string platform)
        {
            string[] cands = platform == "epic"
                ? new[] { @"C:\Program Files\Epic Games\SMITE", @"C:\Program Files (x86)\Epic Games\SMITE" }
                : new[] { @"C:\Program Files (x86)\Steam\steamapps\common\SMITE", @"C:\Program Files\Steam\steamapps\common\SMITE" };
            foreach (var c in cands)
                if (File.Exists(Path.Combine(c, @"Binaries\Win64\Smite.exe"))) return c;
            return null;
        }

        static string RevealSha1(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b));

        static byte[] ReadEmbedded(string logicalName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream(logicalName);
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        static bool RegionEquals(byte[] hay, int at, byte[] needle)
        {
            if (at < 0 || at + needle.Length > hay.Length) return false;
            for (int j = 0; j < needle.Length; j++) if (hay[at + j] != needle[j]) return false;
            return true;
        }

        static int IndexOfBytes(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { m = false; break; }
                if (m) return i;
            }
            return -1;
        }

        async Task RevealPatch(string platform, Label status)
        {
            void Set(string msg, Color c) { try { if (status.IsHandleCreated) status.BeginInvoke(new Action(() => { status.Text = msg; status.ForeColor = c; })); else { status.Text = msg; status.ForeColor = c; } } catch { } }
            try
            {
                string root = RevealSmiteRoot(platform);
                if (root == null) { Set("Couldn't find the " + (platform == "epic" ? "Epic Games" : "Steam") + " SMITE install.", Theme.Accent); return; }
                string upk = Path.Combine(root, @"BattleGame\CookedPCConsole\TgClient.upk");
                string exe = Path.Combine(root, @"Binaries\Win64\Smite.exe");
                if (!File.Exists(upk) || !File.Exists(exe)) { Set("SMITE files not found under " + root, Theme.Accent); return; }

                var running = System.Diagnostics.Process.GetProcessesByName("Smite");
                if (running.Length > 0)
                {
                    var r = MessageBox.Show(this, "SMITE is running and must be closed to patch it. Close it now?", "Close SMITE", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) { Set("Cancelled. Close SMITE and try again.", Theme.Dim); return; }
                    foreach (var pr in running) { try { pr.Kill(); pr.WaitForExit(4000); } catch { } }
                    await Task.Delay(800);
                }

                Set("Working…", Theme.Dim);
                await Task.Run(() =>
                {
                    string curSha = RevealSha1(File.ReadAllBytes(upk));
                    if (string.Equals(curSha, RevealPatchedUpkSha1, StringComparison.OrdinalIgnoreCase)) { Set("Already patched. Launch SMITE and open a hidden profile.", Theme.Green); return; }
                    if (!string.Equals(curSha, RevealOrigUpkSha1, StringComparison.OrdinalIgnoreCase)) { Set("Your SMITE version differs from the one this patch supports, so it was not applied (your game is untouched).", Theme.Accent); return; }

                    byte[] patched = ReadEmbedded("reveal.tgclient.upk");
                    if (patched == null || !string.Equals(RevealSha1(patched), RevealPatchedUpkSha1, StringComparison.OrdinalIgnoreCase)) { Set("Internal error: bundled patch missing/corrupt.", Theme.Accent); return; }

                    if (!File.Exists(upk + ".orig_backup")) File.Copy(upk, upk + ".orig_backup");
                    if (!File.Exists(exe + ".orig_backup")) File.Copy(exe, exe + ".orig_backup");

                    // patch the exe's stored hash (known offset fast-path, else scan)
                    byte[] eb = File.ReadAllBytes(exe);
                    byte[] needle = Convert.FromHexString(RevealOrigUpkSha1);
                    byte[] repl   = Convert.FromHexString(RevealPatchedUpkSha1);
                    int off = RegionEquals(eb, RevealExeHashOffset, needle) ? RevealExeHashOffset : IndexOfBytes(eb, needle);
                    if (off < 0)
                    {
                        if (IndexOfBytes(eb, repl) < 0) { Set("Couldn't find the game's stored hash, so it was not applied (your game is untouched).", Theme.Accent); return; }
                        // exe already carries the patched hash; only the package needs installing
                    }
                    else { Array.Copy(repl, 0, eb, off, 20); File.WriteAllBytes(exe, eb); }

                    File.WriteAllBytes(upk, patched);
                    Set("Done. Launch SMITE and open any hidden profile to see their stats and history.", Theme.Green);
                });
            }
            catch (Exception ex) { Set("Error: " + ex.Message, Theme.Accent); }
        }

        // Restore the original files for BOTH installs (whichever have backups).
        void RevealRestoreAll(Label status)
        {
            void Set(string msg, Color c) { status.Text = msg; status.ForeColor = c; }
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("Smite").Length > 0) { Set("Close SMITE first, then restore.", Theme.Accent); return; }
                var done = new List<string>();
                foreach (var plat in new[] { "epic", "steam" })
                {
                    string root = RevealSmiteRoot(plat);
                    if (root == null) continue;
                    string upk = Path.Combine(root, @"BattleGame\CookedPCConsole\TgClient.upk");
                    string exe = Path.Combine(root, @"Binaries\Win64\Smite.exe");
                    bool did = false;
                    try
                    {
                        if (File.Exists(upk + ".orig_backup")) { File.Copy(upk + ".orig_backup", upk, true); did = true; }
                        if (File.Exists(exe + ".orig_backup")) { File.Copy(exe + ".orig_backup", exe, true); did = true; }
                    }
                    catch { }
                    if (did) done.Add(plat == "epic" ? "Epic" : "Steam");
                }
                Set(done.Count > 0 ? ("Restored original files for: " + string.Join(", ", done) + ".") : "No backups found (nothing to restore).",
                    done.Count > 0 ? Theme.Green : Theme.Dim);
            }
            catch (Exception ex) { Set("Error: " + ex.Message, Theme.Accent); }
        }

        // ===== EasyAntiCheat disable / re-enable (edits EasyAntiCheat\Settings.json) =====
        // SMITE is EOL so these EAC ids are fixed. Disabling writes an invalid id → EAC can't initialize → the game runs without it.
        const string EacProductId    = "f71b1231985f48d1af3de723e0a6acdd";
        const string EacSandboxId    = "076207fa2b5c4803a636af606c3c28b7";
        const string EacDeploymentId = "e03ac5a2b3444159b50aded07f1ed69b";
        const string EacOffSuffix    = "aaa";

        static string EacSettingsPath(string platform)
        {
            string root = RevealSmiteRoot(platform);
            return root == null ? null : Path.Combine(root, @"EasyAntiCheat\Settings.json");
        }

        // Replace the three id values in-place via regex so the file's exact formatting (incl. the unescaped backslash in the exe path) is preserved.
        static string EacSetIds(string text, string suffix)
        {
            text = Regex.Replace(text, "(\"productid\"\\s*:\\s*\")[^\"]*(\")",    "$1" + EacProductId    + suffix + "$2");
            text = Regex.Replace(text, "(\"sandboxid\"\\s*:\\s*\")[^\"]*(\")",    "$1" + EacSandboxId    + suffix + "$2");
            text = Regex.Replace(text, "(\"deploymentid\"\\s*:\\s*\")[^\"]*(\")", "$1" + EacDeploymentId + suffix + "$2");
            return text;
        }

        void EacApply(string platform, bool disable, Label status)
        {
            void Set(string m, Color c) { status.Text = m; status.ForeColor = c; }
            string who = platform == "epic" ? "Epic" : "Steam";
            try
            {
                string path = EacSettingsPath(platform);
                if (path == null || !File.Exists(path)) { Set(who + ": EAC Settings.json not found.", Theme.Accent); return; }
                if (System.Diagnostics.Process.GetProcessesByName("Smite").Length > 0) { Set("Close SMITE first, then try again.", Theme.Accent); return; }
                if (!File.Exists(path + ".orig_backup")) File.Copy(path, path + ".orig_backup");
                File.WriteAllText(path, EacSetIds(File.ReadAllText(path), disable ? EacOffSuffix : ""));
                Set(disable ? (who + ": EasyAntiCheat disabled ✓  (EAC will not run for this install)") : (who + ": EasyAntiCheat re-enabled."), Theme.Green);
            }
            catch (Exception ex) { Set("Error: " + ex.Message, Theme.Accent); }
        }

        // Undo = re-enable EAC on both installs (write the valid ids back).
        void EacUndoAll(Label status)
        {
            void Set(string m, Color c) { status.Text = m; status.ForeColor = c; }
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("Smite").Length > 0) { Set("Close SMITE first, then try again.", Theme.Accent); return; }
                var done = new List<string>();
                foreach (var plat in new[] { "epic", "steam" })
                {
                    string path = EacSettingsPath(plat);
                    if (path == null || !File.Exists(path)) continue;
                    try { File.WriteAllText(path, EacSetIds(File.ReadAllText(path), "")); done.Add(plat == "epic" ? "Epic" : "Steam"); } catch { }
                }
                Set(done.Count > 0 ? ("EasyAntiCheat re-enabled for: " + string.Join(", ", done) + ".") : "No SMITE install found.", done.Count > 0 ? Theme.Green : Theme.Dim);
            }
            catch (Exception ex) { Set("Error: " + ex.Message, Theme.Accent); }
        }

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
            return "1.3.1";
        }
        const string ReleasesApi = "https://api.github.com/repos/DariusSmite/Smite-1-Inspector/releases/latest";
        const string ReleasesListApi = "https://api.github.com/repos/DariusSmite/Smite-1-Inspector/releases?per_page=10";   // beta channel: includes pre-releases (newest first)
        const string ReleasesPage = "https://github.com/DariusSmite/Smite-1-Inspector/releases/latest";
        bool _updateChecked;   // startup check runs once per launch
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

        // getplayeridbyportaluserid: the Hi-Rez API maps a platform user id (Steam64 / Epic / Xbox XUID) → the real Hi-Rez
        // player_id EVEN for a privacy-hidden account — it returns player_id with privacy_flag=y but does NOT zero the id
        // (only getplayer/getmatchhistory/etc. blank it). MCTS leaks the PORTAL_USERID for hidden players; this turns that
        // platform id into the real account anchor, so a hidden player we've never seen visible still de-anonymizes.
        // Returns the player_id string (never "0"/empty) or null. Costs one API request per call.
        static async Task<string> RecoverIdByPortal(int portalId, string portalUserId)
        {
            if (portalId <= 0 || string.IsNullOrEmpty(portalUserId)) return null;
            try
            {
                var raw = await SmiteApi.Call("getplayeridbyportaluserid", portalId.ToString(), portalUserId);
                if (string.IsNullOrEmpty(raw)) return null;
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var el = root.ValueKind == JsonValueKind.Array ? (root.GetArrayLength() > 0 ? root[0] : default) : root;
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("player_id", out var pid))
                {
                    string id = pid.ValueKind == JsonValueKind.Number ? pid.GetInt64().ToString() : pid.GetString();
                    if (!string.IsNullOrEmpty(id) && id != "0") return id;
                }
            }
            catch { }
            return null;
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

                // MCTS PORTAL reveal: the MCTS server's REQUEST_MATCH_DETAILS leaks PORTAL_USERID (Steam/Epic/Xbox
                // account ID) for PRIVACY_FLAG=1 players — the privacy filter blanks PLAYER_NAME and zeros PLAYER_ID
                // but forgets to strip the platform user ID. If we've seen that PORTAL_USERID before (from a match
                // where the same player was visible), NameDb instantly resolves it → exact reveal, 100% confidence.
                // Live query: auto-start the MCTS engine if needed, query the match for hidden player portal IDs.
                bool hasHidden = players.Any(p => { string n = GS(p, "playerName"); if (string.IsNullOrEmpty(n)) n = GS(p, "hz_player_name"); return string.IsNullOrEmpty(n); });
                if (hasHidden && NameDb.Enabled) UseWaitCursor = true;   // de-anonymizing hidden players can hit the network (MCTS query + getplayeridbyportaluserid + smite.guru)
                if (hasHidden && !MctsPortal.HasMatch(matchId) && !string.IsNullOrEmpty(_mctsRelayDir))
                {
                    bool connected = _mctsConnected != null && _mctsConnected();
                    if (!connected && _mctsEnsureConnected != null)
                        connected = await _mctsEnsureConnected(20000);
                    if (connected)
                        await MctsPortal.QueryLive(matchId, _mctsRelayDir, 8000);
                }
                Dictionary<string, (string name, string id)> mctsMap = null;
                if (NameDb.Enabled && MctsPortal.HasMatch(matchId))
                {
                    mctsMap = new();
                    // hidden slots with an MCTS portal id, not already revealed by the local game log
                    var unresolved = new List<(string key, MctsPortal.MatchPlayer mp, string id)>();
                    foreach (var pl in players)
                    {
                        string nmh = GS(pl, "playerName"); if (string.IsNullOrEmpty(nmh)) nmh = GS(pl, "hz_player_name");
                        if (!string.IsNullOrEmpty(nmh)) continue;
                        if (logMap != null && logMap.ContainsKey(GS(pl, "GodId") + "|" + GI(pl, "TaskForce"))) continue;
                        var mp = MctsPortal.FindPlayer(matchId, GI(pl, "TaskForce"), GI(pl, "Kills_Player"), GI(pl, "Deaths"), GI(pl, "Assists"));
                        if (mp == null || string.IsNullOrEmpty(mp.PortalUserId)) continue;
                        string key = GS(pl, "GodId") + "|" + GI(pl, "TaskForce");
                        // (a) seen this PORTAL_USERID visible before → instant exact reveal (rId may be set with no name yet)
                        var (rName, rId) = NameDb.ResolveByPortal(mp.PortalUserId);
                        if (!string.IsNullOrEmpty(rName) && !present.Contains(rName)) { mctsMap[key] = (rName, rId); present.Add(rName); }
                        else unresolved.Add((key, mp, rId));
                    }
                    // (b) never seen visible → recover the REAL player_id from the platform id (getplayeridbyportaluserid
                    //     leaks it through the privacy flag), then resolve the name from our cache, finally from smite.guru's
                    //     permanent index (which still has the name for currently-private accounts it ever indexed).
                    if (unresolved.Count > 0)
                    {
                        var needName = new List<(string key, MctsPortal.MatchPlayer mp, string id)>();
                        foreach (var (key, mp, cachedId) in unresolved)
                        {
                            string recId = !string.IsNullOrEmpty(cachedId) ? cachedId : await RecoverIdByPortal(mp.PortalId, mp.PortalUserId);
                            if (string.IsNullOrEmpty(recId))
                            {
                                string portalLabel = mp.PortalId == 5 ? "Steam " + mp.PortalUserId : mp.PortalId == 9 ? "Epic " + mp.PortalUserId : mp.PortalId == 10 ? "Xbox " + mp.PortalUserId : "Portal" + mp.PortalId + " " + mp.PortalUserId;
                                mctsMap[key] = (portalLabel, null);
                                continue;
                            }
                            string cachedName = NameDb.NameById(recId);
                            if (!string.IsNullOrEmpty(cachedName))
                            {
                                NameDb.LinkPortal(recId, mp.PortalUserId);
                                if (!present.Contains(cachedName)) { mctsMap[key] = (cachedName, recId); present.Add(cachedName); }
                                else mctsMap[key] = ("Player #" + recId, null);   // name already shown for another slot → id-only anchor (id=null so the placeholder isn't re-learned as a name)
                            }
                            else needName.Add((key, mp, recId));
                        }
                        // (c) recovered ids with no cached name → one smite.guru batch (resolves private-but-indexed accounts)
                        if (needName.Count > 0)
                        {
                            Dictionary<string, (string name, int level, string clan)> sg = null;
                            try
                            {
                                _sguru ??= new SmiteGuru(this);
                                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                                sg = await _sguru.ResolveProfilesBatch(needName.Select(n => n.id).Distinct().ToList(), cts.Token);
                            }
                            catch { }
                            foreach (var (key, mp, id) in needName)
                            {
                                string nm = (sg != null && sg.TryGetValue(id, out var pr) && !string.IsNullOrWhiteSpace(pr.name)) ? pr.name : null;
                                if (!string.IsNullOrEmpty(nm))
                                {
                                    NameDb.Learn(id, nm, mp.PortalId, mp.ClanId, mp.ClanName, sg[id].level, "", mp.Mastery, portalUserId: mp.PortalUserId);
                                    if (!present.Contains(nm)) { mctsMap[key] = (nm, id); present.Add(nm); }
                                    else mctsMap[key] = ("Player #" + id, null);
                                }
                                else
                                {
                                    // private + never indexed anywhere: no name exists to recover, but the real account id is
                                    // still a far better anchor than the raw platform id (clickable, future-resolvable).
                                    NameDb.LinkPortal(id, mp.PortalUserId);
                                    mctsMap[key] = ("Player #" + id, null);
                                }
                            }
                        }
                    }
                    NameDb.Save();
                    if (mctsMap.Count == 0) mctsMap = null;
                }
                if (hasHidden && NameDb.Enabled) UseWaitCursor = false;

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
                        foreach (var pl in team) { body.Controls.Add(MakeScoreRow(pl, y, rowW, dtip, partyColors.TryGetValue(GI(pl, "PartyId"), out var pc) ? pc : Color.Empty, partyNamed.TryGetValue(GI(pl, "PartyId"), out var comp) ? comp : null, matchId, teamNamed.TryGetValue(GI(pl, "TaskForce"), out var nbr) ? nbr : null, logMap, present, gbMap, mctsMap)); y += S(46); }
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

        Control MakeScoreRow(JsonElement pl, int y, int rowW, ToolTip dtip, Color partyCol = default, List<string> companions = null, string matchId = null, List<string> neighbors = null, Dictionary<string, (string name, string id)> logMap = null, HashSet<string> present = null, Dictionary<string, (string name, int conf)> gbMap = null, Dictionary<string, (string name, string id)> mctsMap = null)
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
            string mctsName = null; string mctsId = null;
            if (NameDb.Enabled && priv && mctsMap != null && mctsMap.TryGetValue(GS(pl, "GodId") + "|" + tf, out var mv)) { mctsName = mv.name; mctsId = mv.id; }
            Color mctsCol = Color.FromArgb(100, 200, 255);   // sky-blue = MCTS portal reveal (PORTAL_USERID → NameDb)
            if (NameDb.Enabled && priv && !string.IsNullOrEmpty(mctsName) && !string.IsNullOrEmpty(mctsId) && mctsId != "0")
                NameDb.Learn(mctsId, mctsName, 0, clanId, clan, acct, god, mast, companions, nbrSelf, rareSkin, RankedFromRow(pl));
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
                bool hasExact = !string.IsNullOrEmpty(logName) || revExact || !string.IsNullOrEmpty(mctsName);
                if (tag != null && !hasExact) UpdateSighting(tag, clan, acct, mast, companions, god);   // fold only when no exact source supersedes this slot
                string tail = clan.Length > 0 ? "  ·  [" + clan + "]" : "";
                // Priority: EXACT ✔ (ground truth) > your ★ tag > MCTS ⊕ (portal reveal) > ✦ god-board > ⚑ community > ≈ fingerprint > Hidden
                if (!string.IsNullOrEmpty(logName))
                { nameLbl.ForeColor = logCol; nameLbl.Text = "✔ " + logName + tail; }
                else if (revExact)
                { nameLbl.ForeColor = revCol; nameLbl.Text = "✔ " + revName + tail; }
                else if (tag != null)
                { nameLbl.ForeColor = Theme.Blue; nameLbl.Text = "★ " + tag.Nick + tail; }
                else if (!string.IsNullOrEmpty(mctsName))
                { nameLbl.ForeColor = mctsCol; nameLbl.Text = "⊕ " + mctsName + tail; }
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
                else if (tag != null) note = !string.IsNullOrEmpty(mctsName) ? "   ·   portal: " + mctsName : (!string.IsNullOrEmpty(revName) ? "   ·   maybe " + revName : (!string.IsNullOrEmpty(gbName) ? "   ·   maybe " + gbName : (!string.IsNullOrEmpty(cName) ? "   ·   community: " + cName : "")));
                else if (!string.IsNullOrEmpty(mctsName)) { note = mctsId != null ? "   ·   MCTS portal reveal" : "   ·   platform account ID (click to tag)"; noteCol = mctsCol; }
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
                    string suggested = cur?.Nick ?? (!string.IsNullOrEmpty(logName) ? logName : (!string.IsNullOrEmpty(mctsName) && mctsId != null ? mctsName : (!string.IsNullOrEmpty(gbName) ? gbName : (string.IsNullOrEmpty(revName) ? "" : revName))));
                    bool isGuess = cur == null && (!string.IsNullOrEmpty(revName) || !string.IsNullOrEmpty(gbName));
                    string head = priv ? (isGuess ? "Confirm or correct this player" : "Name this hidden player") : "Edit the tag for " + nm;
                    int compCount = companions?.Count ?? 0;
                    string portalHint = !string.IsNullOrEmpty(mctsName) && mctsId == null ? "\r\nPlatform account: " + mctsName : "";
                    string matchNote = isGuess ? "suggested name pre-filled — Save to confirm, edit to correct, or clear to dismiss"
                        : (compCount > 0 ? "matched by clan + level + mastery + " + compCount + " party-mate" + (compCount == 1 ? "" : "s") : "matched by clan + level + mastery");
                    string nick = PromptText(head,
                        god + (clan.Length > 0 ? "  ·  [" + clan + "]" : "") + "   ·   Acct Lv " + acct + "   ·   Mastery " + mast + portalHint + "\r\n(" + matchNote + ")",
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
                string mctsToolTip = !string.IsNullOrEmpty(mctsName) && mctsId == null && mctsName.StartsWith("Steam ") ? "\nSteam profile: https://steamcommunity.com/profiles/" + mctsName.Substring(6) : "";
                dtip.SetToolTip(nameLbl, priv ? "Click to set a custom nickname for this hidden player" + mctsToolTip : "You tagged this player while hidden — click to edit/clear the tag");
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
