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
    partial class MainForm
    {

        Panel BuildExtraPanel()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Location = new Point(S(32), S(26)), Padding = new Padding(0) };

            col.Controls.Add(new Label { AutoSize = true, Text = "Extra", ForeColor = Theme.Text, Font = Theme.F(20f, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(3)) });
            col.Controls.Add(new Label { AutoSize = true, Text = "Tools for your installed SMITE. Do step 1 before step 2.", ForeColor = Theme.Dim, Font = Theme.F(9.5f), Margin = new Padding(0, 0, 0, S(22)) });

            // ===== 1. Disable EasyAntiCheat (prerequisite) =====
            col.Controls.Add(ExtraSectionTitle("1.   Disable EasyAntiCheat"));
            var prereq = new Panel { Width = S(700), Height = S(44), BackColor = Color.FromArgb(34, 12, 12), Margin = new Padding(0, 0, 0, S(12)) };
            prereq.Paint += (s, e) => { using var pen = new Pen(Theme.Accent); e.Graphics.DrawRectangle(pen, 0, 0, prereq.Width - 1, prereq.Height - 1); };
            prereq.Controls.Add(new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.F(9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(14), 0, S(14), 0), Text = "Required first. EasyAntiCheat must be disabled for the Reveal patch (and any other Extra) to work." });
            col.Controls.Add(prereq);
            var eacRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            var eacEpic  = MkBigPlatformBtn("epic",  "Disable EAC", "Epic Games install", true);
            var eacSteam = MkBigPlatformBtn("steam", "Disable EAC", "Steam install",      true);
            eacRow.Controls.Add(eacEpic); eacRow.Controls.Add(eacSteam);
            col.Controls.Add(eacRow);
            var eacStatus = new Label { AutoSize = false, Width = S(700), Height = S(22), ForeColor = Theme.Dim, Font = Theme.F(10f, FontStyle.Bold), Margin = new Padding(0, S(12), 0, S(8)), Text = "" };
            col.Controls.Add(eacStatus);
            var eacUndo = MkBtn("↩  Re-enable EAC (undo)", 210, false);
            eacUndo.Height = S(32); eacUndo.Margin = new Padding(0, 0, 0, 0);
            col.Controls.Add(eacUndo);
            WireBigBtn(eacEpic,  () => EacApply("epic",  true, eacStatus));
            WireBigBtn(eacSteam, () => EacApply("steam", true, eacStatus));
            eacUndo.Click += (s, e) => EacUndoAll(eacStatus);

            col.Controls.Add(ExtraDivider());

            // ===== 2. Reveal Private Profiles In Game =====
            col.Controls.Add(ExtraSectionTitle("2.   Reveal Private Profiles In Game"));
            col.Controls.Add(new Label {
                AutoSize = false, Width = S(720), Height = S(64), ForeColor = Theme.Dim, Font = Theme.F(9.5f), Margin = new Padding(0, 0, 0, S(14)),
                Text = "Patches your installed SMITE so its own profile screen shows private and hidden players' stats and match history instead of “PROFILE UNAVAILABLE”. It is baked into the game files, with no injection and nothing running in the background. A backup is made automatically; use “Restore backup” to undo at any time." });

            var status = new Label { AutoSize = false, Width = S(720), Height = S(46), ForeColor = Theme.Dim, Font = Theme.F(10f, FontStyle.Bold), Margin = new Padding(0, S(4), 0, S(8)), Text = "" };

            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            var epic  = MkBigPlatformBtn("epic",  "Patch Epic Games", "Epic Games install", true);
            var steam = MkBigPlatformBtn("steam", "Patch Steam",      "Steam install",      true);
            row.Controls.Add(epic); row.Controls.Add(steam);
            col.Controls.Add(row);
            col.Controls.Add(status);

            var restore = MkBtn("↩  Restore backup (undo)", 210, false);
            restore.Height = S(32); restore.Margin = new Padding(0, S(2), 0, 0);
            col.Controls.Add(restore);

            WireBigBtn(epic,  async () => await RevealPatch("epic",  status));
            WireBigBtn(steam, async () => await RevealPatch("steam", status));
            restore.Click += (s, e) => RevealRestoreAll(status);

            host.Controls.Add(col);

            _extraOnShow = () =>
            {
                try
                {
                    string ack = Path.Combine(Theme.DataDir, "extra_ack.txt");
                    if (File.Exists(ack)) return;
                    MessageBox.Show(this,
                        "This tool modifies your local SMITE game files to reveal information the game normally keeps hidden.\n\n" +
                        "Use it at your own risk. We are NOT responsible for anything that happens to your account, your game install, or your data as a result of using it.\n\n" +
                        "A backup of the changed files is made automatically, and you can restore it at any time with “Restore backup”.\n\n" +
                        "By continuing you acknowledge that you understand what this does.",
                        "Reveal Private Profiles (Disclaimer)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    try { File.WriteAllText(ack, DateTime.Now.ToString("o")); } catch { }
                }
                catch { }
            };
            return host;
        }
    }
}
