// Quest Recorder - made by LethToxic.
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace QuestRecorder
{
    /// <summary>Checks GitHub for a newer version and installs it when the player says yes.</summary>
    internal static class Updater
    {
        /// <summary>Where updates come from (LethToxic's GitHub repository, raw files of its main branch).</summary>
        public const string Source = "https://raw.githubusercontent.com/lethtoxic-svg/QuestRecorder/main/";

        /// <summary>The whole repository (the plugin's source sits at its top), packed as a zip by GitHub.</summary>
        public const string RepoZip = "https://github.com/lethtoxic-svg/QuestRecorder/archive/refs/heads/main.zip";

        public static volatile string LatestVersion = string.Empty;
        public static volatile string LatestZip = string.Empty;
        public static volatile string WhatsNew = string.Empty;
        public static volatile string Problem = string.Empty;
        /// <summary>The version installed this session (waiting for a BuddyCron restart).</summary>
        public static volatile string InstalledVersion = string.Empty;

        /// <summary>Turns a SWTOR-style patch name into something we can compare.</summary>
        public static Version ToVersion(string patch)
        {
            var m = System.Text.RegularExpressions.Regex.Match((patch ?? string.Empty).Trim().ToLowerInvariant(), @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?([a-z])?$");
            if (!m.Success)
                return null;
            int major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            int build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            int letter = m.Groups[4].Success ? m.Groups[4].Value[0] - 'a' + 1 : 0;
            return new Version(major, minor, build, letter);
        }
        public static bool UpdateAvailable
        {
            get
            {
                Version v = ToVersion(LatestVersion);
                return v != null && v > QuestRecorderPlugin.PluginVersion && LatestZip.Length > 0;
            }
        }

        private static HttpClient Http()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("QuestRecorder/" + QuestRecorderPlugin.Patch);
            return h;
        }

        /// <summary>Reads patch.txt in the background; never throws.</summary>
        public static void CheckInBackground()
        {
            // Installed with git clone: BuddyCron's UpdateBuddy keeps it up to date, so no pop-up (and no files
            // copied over the clone, which would get in the way of UpdateBuddy's own update).
            if (IsGitClone())
            {
                RrLog.Debug("Installed with git clone - UpdateBuddy keeps Quest Recorder up to date.");
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    using (HttpClient h = Http())
                    {
                        string text = await h.GetStringAsync(Source + "patch.txt").ConfigureAwait(false);
                        string[] lines = text.Replace("\r", string.Empty).Split('\n');
                        LatestVersion = lines.Length > 0 ? lines[0].Trim() : string.Empty;
                        LatestZip = lines.Length > 1 ? lines[1].Trim() : string.Empty;
                        WhatsNew = lines.Length > 2 ? string.Join("\r\n", lines, 2, lines.Length - 2).Trim() : string.Empty;
                        Problem = string.Empty;
                        if (UpdateAvailable)
                            ShowPopup();
                    }
                }
                catch (Exception ex)
                {
                    Problem = ex.Message;
                    RrLog.Debug("Update check failed: {0}", ex.Message);
                }
            });
        }

        /// <summary>An invisible always-on-top window, so the update box shows in front of the game.</summary>
        private static System.Windows.Forms.Form Owner()
        {
            var f = new System.Windows.Forms.Form { TopMost = true, ShowInTaskbar = false, Width = 1, Height = 1, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen, Opacity = 0 };
            f.Show();
            f.Activate();
            return f;
        }
        private static int _popupShown;

        /// <summary>The update pop-up: "Quest Recorder X is out - Update / Later". Update installs it and says to restart.</summary>
        private static void ShowPopup()
        {
            if (System.Threading.Interlocked.Exchange(ref _popupShown, 1) == 1)
                return; // once per BuddyCron start
            RrLog.Info("Quest Recorder {0} is available (you have {1}).", LatestVersion, QuestRecorderPlugin.Patch);
            var t = new System.Threading.Thread(() =>
            {
                System.Windows.Forms.Form owner = null;
                try
                {
                    string news = WhatsNew.Length > 0 ? "\r\n\r\nWhat's new:\r\n" + WhatsNew : string.Empty;
                    owner = Owner();
                    var answer = System.Windows.Forms.MessageBox.Show(owner,
                        "Quest Recorder " + LatestVersion + " is out (you have " + QuestRecorderPlugin.Patch + ")." + news +
                        "\r\n\r\nUpdate now?",
                        "Quest Recorder update", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Information,
                        System.Windows.Forms.MessageBoxDefaultButton.Button1);
                    if (answer != System.Windows.Forms.DialogResult.Yes)
                        return;
                    string msg = InstallAsync().GetAwaiter().GetResult();
                    RrLog.Info("{0}", msg);
                    System.Windows.Forms.MessageBox.Show(owner, msg, "Quest Recorder update", System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information, System.Windows.Forms.MessageBoxDefaultButton.Button1);
                }
                catch (Exception ex)
                {
                    RrLog.Info("The update did not work: {0}. Click Updates in the Quest Recorder window to try again.", ex.Message);
                }
                finally
                {
                    if (owner != null)
                        owner.Dispose();
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }

        /// <summary>True when the plugin folder is a git clone of the repository (UpdateBuddy updates those).</summary>
        public static bool IsGitClone()
        {
            try { return Directory.Exists(Path.Combine(PluginFolder(), ".git")) || File.Exists(Path.Combine(PluginFolder(), ".git")); }
            catch { return false; }
        }

        private static string PluginFolder()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "QuestRecorder");
        }

        private static string BackupRoot()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PluginBackups");
        }

        /// <summary>Downloads and installs the latest version. Returns a message for the player.</summary>
        public static async Task<string> InstallAsync()
        {
            if (IsGitClone())
                return "You installed Quest Recorder with git clone - BuddyCron's UpdateBuddy keeps it up to date, nothing to do here.";
            if (!UpdateAvailable)
                return "No update available.";
            if (InstalledVersion == LatestVersion)
                return "Quest Recorder " + LatestVersion + " is already installed. Restart BuddyCron to finish.";
            string temp = Path.Combine(Path.GetTempPath(), "QuestRecorderUpdate");
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            string zipPath = Path.Combine(temp, "update.zip");
            // The plugin's source sits at the top of the GitHub repository: download the repository itself
            // (GitHub packs the main branch as a zip for us).
            using (HttpClient h = Http())
            {
                byte[] data = await h.GetByteArrayAsync(RepoZip).ConfigureAwait(false);
                File.WriteAllBytes(zipPath, data);
            }
            string unpacked = Path.Combine(temp, "files");
            ZipFile.ExtractToDirectory(zipPath, unpacked);
            // GitHub's zip holds one folder (QuestRecorder-main) with the plugin's files in it
            string src = File.Exists(Path.Combine(unpacked, "QuestRecorder.cs")) ? unpacked : null;
            foreach (string d in Directory.GetDirectories(unpacked))
                if (src == null && File.Exists(Path.Combine(d, "QuestRecorder.cs")))
                    src = d;
            if (src == null)
                return "The update does not look like Quest Recorder (no QuestRecorder.cs); nothing was changed.";

            string dest = PluginFolder();
            string backup = Path.Combine(BackupRoot(), "QuestRecorder " + QuestRecorderPlugin.Patch + " " +
                                         DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backup);
            foreach (string f in Directory.GetFiles(dest))
                File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), true);
            // replace the code files (old .cs files the new version no longer has are removed, so nothing stale compiles)
            foreach (string f in Directory.GetFiles(dest, "*.cs"))
                File.Delete(f);
            foreach (string f in Directory.GetFiles(src))
            {
                string n = Path.GetFileName(f);
                // the repository's own files (update info, installer, git settings) aren't part of the plugin
                if (n.Equals("patch.txt", StringComparison.OrdinalIgnoreCase) || n.Equals("latest.txt", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || n.StartsWith(".", StringComparison.Ordinal))
                    continue;
                File.Copy(f, Path.Combine(dest, n), true);
            }
            InstalledVersion = LatestVersion;
            return "Quest Recorder " + LatestVersion + " is installed. Restart BuddyCron to finish (it builds the plugin when it starts).\r\n" +
                   "Your old version was saved in " + backup + " - \"Undo update\" puts it back.";
        }

        /// <summary>Puts the most recent backup back. Returns a message for the player.</summary>
        public static string UndoLast()
        {
            string root = BackupRoot();
            if (!Directory.Exists(root))
                return "There is no backup to go back to.";
            string newest = null;
            DateTime best = DateTime.MinValue;
            foreach (string d in Directory.GetDirectories(root, "QuestRecorder *"))
            {
                DateTime w = Directory.GetCreationTime(d);
                if (newest == null || w > best)
                {
                    newest = d;
                    best = w;
                }
            }
            if (newest == null)
                return "There is no backup to go back to.";
            string dest = PluginFolder();
            foreach (string f in Directory.GetFiles(dest, "*.cs"))
                File.Delete(f);
            foreach (string f in Directory.GetFiles(newest))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            Directory.Delete(newest, true);
            return "Put back " + Path.GetFileName(newest) + ". Restart BuddyCron to finish.";
        }
    }
}
