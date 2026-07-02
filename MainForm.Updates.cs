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

        // Checks GitHub for a newer release. userInitiated (Settings button) always prompts and reports "up to date";
        // the startup check stays quiet unless there's an update the user hasn't already declined.
        async Task CheckForUpdate(bool userInitiated)
        {
            try
            {
                string tag = null, setupUrl = null, bareUrl = null; long setupSize = 0, bareSize = 0; bool prerelease = false;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                {
                    http.DefaultRequestHeaders.Add("User-Agent", "Smite1Inspector");
                    // Stable users read /releases/latest (GitHub omits pre-releases there). Beta users read the full
                    // release list (newest first) and take the most recent entry, which may be a pre-release build.
                    string api = settings.BetaChannel ? ReleasesListApi : ReleasesApi;
                    using var doc = JsonDocument.Parse(await http.GetStringAsync(api));
                    JsonElement rel;
                    if (settings.BetaChannel)
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                        { if (userInitiated) MessageBox.Show(this, "Couldn't read the latest release.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        rel = doc.RootElement[0];
                    }
                    else rel = doc.RootElement;
                    if (rel.TryGetProperty("tag_name", out var t)) tag = t.GetString();
                    if (rel.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) prerelease = true;
                    if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        foreach (var a in assets.EnumerateArray())
                        {
                            string nm = GS(a, "name");
                            if (!nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            long asz = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                            if (nm.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0) { if (setupUrl == null) { setupUrl = GS(a, "browser_download_url"); setupSize = asz; } }
                            else { if (bareUrl == null) { bareUrl = GS(a, "browser_download_url"); bareSize = asz; } }
                        }
                }
                // An installed build (Program Files / has an uninstaller) must update via the installer (in-place upgrade
                // that also refreshes the engine + shortcuts); a portable build swaps the bare exe. Prefer the matching
                // asset, fall back to the other so a release with only one of them still updates everyone.
                bool installed = IsInstalled();
                string assetUrl; long assetSize; bool isInstaller;
                if (installed) { if (setupUrl != null) { assetUrl = setupUrl; assetSize = setupSize; isInstaller = true; } else { assetUrl = bareUrl; assetSize = bareSize; isInstaller = false; } }
                else { if (bareUrl != null) { assetUrl = bareUrl; assetSize = bareSize; isInstaller = false; } else { assetUrl = setupUrl; assetSize = setupSize; isInstaller = true; } }
                if (string.IsNullOrEmpty(tag)) { if (userInitiated) MessageBox.Show(this, "Couldn't read the latest release.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                // Newer by version, OR (beta channel) a different-tagged beta of the SAME numeric version than the one we
                // last applied in-app — ParseVer strips the "-betaN" suffix, so iterative betas would otherwise be skipped.
                bool isNewer = ParseVer(tag) > ParseVer(AppVersion)
                    || (settings.BetaChannel && ParseVer(tag) == ParseVer(AppVersion)
                        && !string.IsNullOrEmpty(settings.AppliedTag) && tag != settings.AppliedTag);
                if (!isNewer)
                { if (userInitiated) MessageBox.Show(this, "You're on the latest version (v" + AppVersion + ").", "Up to date", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!userInitiated && settings.SkippedVersion == tag) return;   // already declined this version at startup
                if (string.IsNullOrEmpty(assetUrl))
                { if (userInitiated) MessageBox.Show(this, tag + " is available, but no exe was attached. Get it from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (settings.AutoUpdate && !userInitiated) { await ApplyUpdate(assetUrl, assetSize, tag, isInstaller); return; }
                string sizeTxt = assetSize > 0 ? "  (download ~" + (assetSize / 1048576) + " MB)" : "";
                string betaTxt = prerelease ? "  [BETA]" : "";
                var r = MessageBox.Show(this, "A new version is available: " + tag + betaTxt + "\nYou have v" + AppVersion + "." + sizeTxt + "\n\nUpdate now?",
                    "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { settings.SkippedVersion = tag; SaveSettings(); return; }   // remember the "no"
                await ApplyUpdate(assetUrl, assetSize, tag, isInstaller);
            }
            catch (Exception ex) { if (userInitiated) MessageBox.Show(this, "Update check failed: " + ex.Message, "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // Downloads the update (with a progress dialog), then either runs the new INSTALLER (in-place upgrade — works for
        // installed users in Program Files, and updates the whisper engine + shortcuts too) or, for a bare-exe asset,
        // swaps the running exe in place (portable build).
        async Task ApplyUpdate(string url, long size, string tag, bool isInstaller)
        {
            string exe = Environment.ProcessPath, dir = Path.GetDirectoryName(exe ?? "");
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Auto-update only works on the packaged app. Download " + tag + " from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            // Installer → TEMP; portable update → next to the exe.
            string dlPath = isInstaller
                ? Path.Combine(Path.GetTempPath(), "SmiteInspector-Setup-" + (tag ?? "new").TrimStart('v', 'V') + ".exe")
                : Path.Combine(dir, "SmiteInspector.update.exe");
            bool ok = false;
            using (var dlg = new Form { Text = "Updating", BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.F(9.5f), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ControlBox = false, ClientSize = new Size(S(430), S(96)) })
            {
                dlg.Controls.Add(new Label { Location = new Point(S(16), S(14)), AutoSize = true, ForeColor = Theme.Dim, Text = "Downloading " + tag + "…" });
                var bar = new ProgressBar { Location = new Point(S(16), S(44)), Size = new Size(S(398), S(22)), Style = ProgressBarStyle.Continuous, Maximum = 100 };
                dlg.Controls.Add(bar);
                try { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); } catch { }
                var prog = new Progress<int>(p => bar.Value = Math.Min(100, Math.Max(0, p)));
                dlg.Shown += async (s, e) => { try { ok = await DownloadFile(url, dlPath, prog); } catch { ok = false; } dlg.Close(); };
                dlg.ShowDialog(this);
            }
            // reject a truncated/incomplete download before applying it
            if (ok && size > 0) { try { ok = new FileInfo(dlPath).Length == size; } catch { ok = false; } }
            if (!ok) { try { File.Delete(dlPath); } catch { } MessageBox.Show(this, "Download failed or was incomplete. You can update manually from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!string.IsNullOrEmpty(tag)) { settings.AppliedTag = tag; SaveSettings(); }   // remember the exact tag we applied (iterative-beta tracking)

            if (isInstaller)
            {
                // Run the installer silently. With CloseApplications=yes it closes this app, upgrades in place (app + engine
                // + shortcuts), then relaunches it. UseShellExecute lets it elevate (one UAC prompt). If the user cancels
                // UAC, Process.Start throws — keep the app running and tell them.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true }); }
                catch (Exception ex)
                { MessageBox.Show(this, "Update was cancelled or couldn't start (" + ex.Message + ").\n\nYou can get " + tag + " from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (settings.SkippedVersion == tag) { settings.SkippedVersion = ""; SaveSettings(); }
                Application.Exit();   // release the exe so the elevated installer can replace it; it relaunches us on finish
                return;
            }

            // ---- portable build: swap the running exe by renaming it aside (a running exe can be renamed, not overwritten) ----
            try
            {
                string bak = Path.Combine(dir, "SmiteInspector.old.exe");
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                File.Move(exe, bak);
                try { File.Move(dlPath, exe); }
                catch { try { if (!File.Exists(exe) && File.Exists(bak)) File.Move(bak, exe); } catch { } throw; }   // restore on failure so we're never left with NO exe
            }
            catch (Exception ex)
            {
                try { File.Delete(dlPath); } catch { }
                MessageBox.Show(this, "Couldn't replace the app (is it in a read-only folder?): " + ex.Message + "\n\nUpdate manually from the Releases page.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (settings.SkippedVersion == tag) { settings.SkippedVersion = ""; SaveSettings(); }
            if (MessageBox.Show(this, tag + " installed. Restart now to use it?", "Update ready", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true }); } catch { } Application.Exit(); }
        }

        async Task<bool> DownloadFile(string url, string dest, IProgress<int> progress)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.Add("User-Agent", "Smite1Inspector");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, read = 0;
            using var src = await resp.Content.ReadAsStreamAsync();
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920]; int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            { await dst.WriteAsync(buf, 0, n); read += n; if (total > 0) progress?.Report((int)(read * 100 / total)); }
            return true;
        }
        // Remove the renamed-aside previous exe left by a successful update.
        void CleanupOldExe()
        {
            try { var bak = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "SmiteInspector.old.exe"); if (File.Exists(bak)) File.Delete(bak); } catch { }
        }

        // Settings → Uninstall. Confirms, optionally erases the Documents\Smite Inspector data folder, then either runs the
        // Inno uninstaller (installed build) or self-deletes the exe (portable build). The whisper engine is stopped first
        // so its relay files unlock. Destructive — only ever reached by an explicit click + confirmations.
        void UninstallApp()
        {
            bool installed = IsInstalled();
            string exe = Environment.ProcessPath ?? "";
            string dir = Path.GetDirectoryName(exe) ?? "";
            string unins = Path.Combine(dir, "unins000.exe");
            bool haveUninstaller = installed && File.Exists(unins);
            // Normalize once so the path we safety-check is exactly the path we delete (no trailing-slash quoting hazard).
            string dataDir; try { dataDir = Path.GetFullPath(Theme.DataDir).TrimEnd('\\'); } catch { dataDir = Theme.DataDir; }

            string intro = installed
                ? "Uninstall Smite 1 Inspector?\n\nThis closes the app and removes it from your PC."
                : "This is the portable version, so there is nothing to formally uninstall.\n\nYou can close the app and (optionally) delete its saved data now; afterwards just delete SmiteInspector.exe.";
            if (MessageBox.Show(this, intro, "Uninstall", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            // Installed in Program Files but the uninstaller is missing/damaged: never self-delete the program exe — that
            // would orphan the install. Send the user to Windows' own uninstaller and leave everything (incl. data) intact.
            if (installed && !haveUninstaller)
            {
                MessageBox.Show(this, "The uninstaller couldn't be found next to the app. Please uninstall Smite 1 Inspector from Windows Settings → Apps.\n\n(Your saved data was left untouched.)", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var dataChoice = MessageBox.Show(this,
                "Also delete your saved data?\n\nThis permanently removes your conversations, hidden-player tags, friend list, notes and settings stored in:\n" + dataDir + "\n\nYes — delete my data        No — keep my data        Cancel — stop",
                "Delete saved data?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (dataChoice == DialogResult.Cancel) return;
            bool clearData = dataChoice == DialogResult.Yes;

            // Safety: only wipe the data folder when it is NOT the app/exe directory (the rare Documents-unavailable
            // fallback) — recursively deleting the program folder would nuke the install out from under the uninstaller.
            bool dataIsAppDir;
            try { dataIsAppDir = string.Equals(dataDir, Path.GetFullPath(Theme.AppDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { dataIsAppDir = true; }   // can't prove it's safe → don't recursively delete

            // Stop the background whisper engine(s) so the relay files release before any delete.
            try { foreach (var p in System.Diagnostics.Process.GetProcessesByName("Probe5")) { try { p.Kill(); } catch { } } } catch { }

            // Start the removal FIRST. Only once it is under way do we schedule the optional data wipe, so a cancelled/failed
            // uninstaller launch (e.g. the user declines UAC) never erases data and then bails.
            if (haveUninstaller)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(unins) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't start the uninstaller: " + ex.Message + "\n\nYou can uninstall from Windows Settings → Apps instead.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            else if (!string.IsNullOrEmpty(exe))
            {
                ScheduleDetachedDelete(exe, false);   // portable: remove the exe after we exit
            }
            if (clearData && !dataIsAppDir) ScheduleDetachedDelete(dataDir, true);
            Application.Exit();
        }

        // Detached, fire-and-forget delete that runs AFTER this app exits (a short ping delay lets file locks release),
        // so it can remove the running exe or the data folder. Best-effort: whatever is still locked is simply left behind.
        static void ScheduleDetachedDelete(string path, bool isDir)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                path = path.TrimEnd('\\');   // a trailing backslash would escape the closing quote in the cmd line
                string inner = isDir ? "rd /s /q \"" + path + "\"" : "del /f /q \"" + path + "\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & " + inner)
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        // True when this build was placed by the installer (so updates must go through the installer, not an exe-swap into
        // a read-only Program Files folder). Detected by the Inno uninstaller sitting next to us, or a Program Files path.
        static bool IsInstalled()
        {
            try
            {
                string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                if (string.IsNullOrEmpty(dir)) return false;
                if (File.Exists(Path.Combine(dir, "unins000.exe"))) return true;
                foreach (var sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                { string pf = Environment.GetFolderPath(sf); if (!string.IsNullOrEmpty(pf) && dir.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return true; }
            }
            catch { }
            return false;
        }

        static Version ParseVer(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Version(0, 0);
            s = s.Trim().TrimStart('v', 'V');
            int sp = s.IndexOfAny(new[] { ' ', '-' }); if (sp > 0) s = s.Substring(0, sp);
            return Version.TryParse(s, out var v) ? v : new Version(0, 0);
        }
    }
}
