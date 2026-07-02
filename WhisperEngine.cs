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
        // Diagnostics: exposed so Export Logs can summarize connection HEALTH at a glance (a "connected" state with no
        // real traffic for minutes is a dead session — the exact case that used to be invisible in a bug report).
        public bool EverConnected { get { return _everConnected; } }
        public double SecondsSinceTraffic { get { return (DateTime.Now - _lastTrafficAt).TotalSeconds; } }
        public double SecondsSinceLine { get { return (DateTime.Now - _lastLineAt).TotalSeconds; } }
        public int InboundCount { get { return _inboundCount; } }
        volatile int _inboundCount;   // real inbound lines (replies + delivery echoes) seen this run — frozen count == dead link

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
            try { File.Delete(Path.Combine(_relay, "probe_cmd.txt")); } catch { }
            try { File.Delete(Path.Combine(_relay, "probe_responses.txt")); } catch { }
            _presPos = 0;
            _inPos = 0;
            // Fresh run, fresh grace period: a brand-new child's own normal pre-login "connected=False" chatter must
            // NOT be mistaken for a lost-and-regained session (that gate is keyed on _everConnected, see OnLine()).
            _everConnected = false; _inboundCount = 0;
            _lastLineAt = DateTime.Now; _lastTrafficAt = DateTime.Now; _lastQueryAt = DateTime.MinValue;
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
            e["CHATDIAG"] = "0"; e["PROBE"] = "1";
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
        volatile bool _everConnected;          // true once we've reached "connected" at least once this run
        DateTime _lastLineAt = DateTime.Now;       // last time Probe5 produced ANY log output — catches a fully hung process
        DateTime _lastTrafficAt = DateTime.Now;    // last time a REAL over-the-wire reply landed (whisper_in.txt / presence.tsv growth)
        DateTime _lastQueryAt = DateTime.MinValue; // last time the UI asked us to poll presence — pairs with _lastTrafficAt below
        void OnLine(string line)
        {
            if (line == null) return;
            _lastLineAt = DateTime.Now;
            lock (_logLock) { _log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + line); if (_log.Count > 5000) _log.RemoveRange(0, _log.Count - 5000); }
            if (line.Contains("LOGGED IN & READY") || line.Contains("loggedOn=True")) SetState("connected");
            // Probe5 logs its own "state: connected=False loggedOn=False ..." line whenever the chat session's
            // IsConnected()/IsLoggedOn() flip false — e.g. the socket silently died after a long idle, a sleep/resume,
            // or a network blip. We previously ignored this negative signal entirely, so a dead session stayed
            // reported as "connected" forever. Only react to it AFTER we've connected once (the same text is normal
            // and expected during the initial login handshake, before there's anything real to lose).
            else if (_everConnected && line.Contains("connected=False")) SetState("disconnected");
            else if (line.Contains("FATAL") || line.Contains("EXCEPTION:")) SetState("stopped");
        }
        void SetState(string s)
        {
            if (State == s) return;
            State = s;
            if (s == "connected") _everConnected = true;
            try { Status?.Invoke(s); } catch { }
        }

        void Worker()
        {
            string inbox = Path.Combine(_relay, "whisper_in.txt");
            string outbox = Path.Combine(_relay, "whisper_out.txt");
            string presFile = Path.Combine(_relay, "presence.tsv");
            while (_run)
            {
                // Watchdog #1: Probe5 logs something (at minimum a "[hb]" heartbeat) roughly every 3s while its tick
                // loop is alive — connected or not, since the heartbeat isn't gated on real connectivity. Total
                // silence for 45s while we still believe we're connected means the PROCESS itself wedged (e.g. a
                // native call stuck across a sleep/resume).
                if (State == "connected" && (DateTime.Now - _lastLineAt).TotalSeconds > 45) SetState("disconnected");
                // Watchdog #2: Probe5's own IsConnected()/IsLoggedOn() flags can stay stuck reporting true even when
                // the socket is dead at a layer the library never notices (e.g. a half-open connection the OS hasn't
                // torn down) — so "[hb]" keeps logging and watchdog #1 above never trips either. The presence poll the
                // UI already sends every 5 seconds is a genuine over-the-wire round trip: if we've been actively
                // asking and getting NOTHING back for 90s, the link is dead regardless of what the library claims.
                if (State == "connected" && _lastQueryAt != DateTime.MinValue
                    && (DateTime.Now - _lastQueryAt).TotalSeconds < 30 && (DateTime.Now - _lastTrafficAt).TotalSeconds > 90)
                    SetState("disconnected");
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
                                _lastTrafficAt = DateTime.Now;
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
                                        if (text.Length > 0) { _inboundCount++; try { Inbound?.Invoke(sender, text); } catch { } }
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
                                _lastTrafficAt = DateTime.Now;
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
                _lastQueryAt = DateTime.Now;   // we ARE actively asking for a round trip now — feeds the dead-link watchdog above
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
            // Wait for the Worker loop to actually exit before returning. Without this, an immediate Start() (the
            // normal auto-reconnect path) can reset _inPos/_presPos/relay files and spin up a NEW Worker thread while
            // the OLD one is still mid-iteration on this same instance's shared fields — a real, if narrow, race.
            try { _worker?.Join(2000); } catch { }
            SetState("stopped");
        }
    }
}
