// Quest Recorder - made by LethToxic.
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using BuddyCron;
using BuddyCron.Objects;
using BuddyCron.Profiles;

namespace QuestRecorder
{
    /// <summary>While a profile runs: if the bot sits on one step for 5 minutes with no quest progress, offer to fix it.</summary>
    internal static class StuckWatch
    {
        private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);
        private static System.Threading.Timer _timer;
        private static string _key;
        private static string _progress;
        private static DateTime _since;
        private static bool _asked;
        private static int _busy;

        public static void Start()
        {
            if (_timer == null)
                _timer = new System.Threading.Timer(_ => Tick(), null, 15000, 15000);
        }

        public static void Stop()
        {
            System.Threading.Timer t = _timer;
            _timer = null;
            if (t != null)
                t.Dispose();
        }

        private static void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                return;
            try
            {
                if (!RrSettings.Instance.StuckPopup || !TreeRoot.IsRunning)
                {
                    _key = null;
                    return;
                }
                NeoProfile profile = NeoProfileManager.CurrentProfile;
                if (profile == null || string.IsNullOrEmpty(profile.Path))
                    return;
                int line = CurrentLine();
                string key = profile.Path + "|" + line;
                string progress = Progress();
                if (key != _key || progress != _progress)
                {
                    _key = key;
                    _progress = progress;
                    _since = DateTime.Now;
                    _asked = false;
                    return;
                }
                if (_asked || DateTime.Now - _since < StuckAfter)
                    return;
                _asked = true;
                Ask(profile.Path, line);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Stuck check: {0}", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        /// <summary>The profile line BuddyCron is on (0 if it can't be read). Read by name, so a BuddyCron change can't break the build.</summary>
        private static int CurrentLine()
        {
            try
            {
                var prop = typeof(NeoProfileManager).GetProperty("CurrentProfileBehavior",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                object step = prop != null ? prop.GetValue(null) : null;
                if (step == null)
                    return 0;
                var lineProp = step.GetType().GetProperty("LineNumber");
                object v = lineProp != null ? lineProp.GetValue(step) : null;
                return v is int ? (int)v : 0;
            }
            catch
            {
                return 0;
            }
        }
        /// <summary>Every active quest's step and counts in one string: it changes whenever anything ticks.</summary>
        private static string Progress()
        {
            var sb = new StringBuilder();
            using (Core.Memory.AcquireFrame(true))
            {
                HeroLocalPlayer me = Core.Me;
                if (me == null)
                    return string.Empty;
                foreach (QuestState st in me.Quests.ActiveQuestStates)
                {
                    if (st == null)
                        continue;
                    sb.Append(st.QuestId).Append(':').Append(st.BranchId).Append('/').Append(st.StepId);
                    try
                    {
                        foreach (ulong c in st.TaskCount)
                            sb.Append(',').Append(c);
                    }
                    catch
                    {
                        // no counts for this one
                    }
                    sb.Append(';');
                }
            }
            return sb.ToString();
        }

        /// <summary>The comment above the step at this line (what the panel shows as the step's name).</summary>
        private static string LabelAt(string path, int line)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = Math.Min(line, lines.Length) - 1; i >= 0; i--)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("<!--", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal) && lines[i].StartsWith("\t\t<!--", StringComparison.Ordinal))
                        return t.Substring(4, t.Length - 7).Trim();
                }
            }
            catch
            {
                // unreadable: no name
            }
            return string.Empty;
        }

        private static void Ask(string path, int line)
        {
            string label = LabelAt(path, line);
            RrLog.Warn("The bot seems stuck{0} (5 minutes on one step, no quest progress).", label.Length > 0 ? " on \"" + label + "\"" : string.Empty);
            var th = new Thread(() =>
            {
                Form owner = null;
                try
                {
                    owner = new Form { TopMost = true, ShowInTaskbar = false, Width = 1, Height = 1, Opacity = 0, StartPosition = FormStartPosition.CenterScreen };
                    owner.Show();
                    if (MessageBox.Show(owner,
                            "The bot seems stuck" + (label.Length > 0 ? " on:\r\n\r\n" + label : ".") +
                            "\r\n\r\nIt's been on the same step for 5 minutes and nothing in your quest log moved.\r\n\r\nFix it? (Stop the bot first, then fix the step and load the profile again.)",
                            "Quest Recorder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                    owner.Dispose();
                    owner = null;
                    var panel = new ProfileFixerForm();
                    panel.OpenAt(path, label);
                    Application.Run(panel);
                }
                catch (Exception ex)
                {
                    RrLog.Debug("Stuck pop-up: {0}", ex.Message);
                }
                finally
                {
                    if (owner != null)
                        owner.Dispose();
                }
            });
            th.IsBackground = true;
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }
    }
}
