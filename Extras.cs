// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace QuestRecorder
{
    /// <summary>Send report and Share: pack files into a zip people can drop in Discord.</summary>
    internal static class Reports
    {
        private static string Root()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>Character name from a recording's .txt ("Character   Name, level 80"), or empty.</summary>
        private static string CharacterName(string txtPath)
        {
            if (!File.Exists(txtPath))
                return string.Empty;
            foreach (string line in File.ReadLines(txtPath))
            {
                if (!line.StartsWith("Character", StringComparison.Ordinal))
                    continue;
                string rest = line.Substring("Character".Length).Trim();
                int comma = rest.IndexOf(',');
                return comma > 0 ? rest.Substring(0, comma).Trim() : rest;
            }
            return string.Empty;
        }

        private static string Hide(string text, string name)
        {
            if (name.Length < 2)
                return text;
            return text.Replace(name, "Player");
        }

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            ZipArchiveEntry e = zip.CreateEntry(entryName);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                w.Write(text);
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
        }

        /// <summary>The profile the recorder made from this recording, if it can be found.</summary>
        public static string ProfileFor(string csvPath)
        {
            // "Dromund Kaas-2026-11-03_08-25(-2).csv" -> "Dromund Kaas - Recorded 2026-11-03 08-25( 2).xml"
            string stem = Path.GetFileNameWithoutExtension(csvPath);
            Match m = Regex.Match(stem, @"^(.*)-(\d{4}-\d\d-\d\d)_(\d\d-\d\d)(-\d+)?$");
            if (!m.Success)
                return null;
            string name = m.Groups[1].Value + " - Recorded " + m.Groups[2].Value + " " + m.Groups[3].Value +
                          (m.Groups[4].Success ? " " + m.Groups[4].Value.Substring(1) : string.Empty) + ".xml";
            // usually Profiles\Recorded\<place>, but a place folder elsewhere in BuddyCron is used too (e.g. "CZ-198 Dailies")
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Logs", "Recordings", "Plugins", "PluginBackups", "CompiledAssemblies", "Settings", "Routines", "BotBases", "Quest Behaviors" };
            foreach (string d in Directory.GetDirectories(Root()))
            {
                if (skip.Contains(Path.GetFileName(d)))
                    continue;
                try
                {
                    foreach (string f in Directory.GetFiles(d, name, SearchOption.AllDirectories))
                        return f;
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return null;
        }

        /// <summary>Packs a recording, its profile and the newest BuddyCron log. Returns the zip's path.</summary>
        public static string MakeReport(string csvPath, string note)
        {
            string txtPath = Path.ChangeExtension(csvPath, ".txt");
            string who = CharacterName(txtPath);
            string dir = Path.Combine(Path.GetDirectoryName(csvPath), "Reports");
            Directory.CreateDirectory(dir);
            string zipPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(csvPath) + " - report.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddText(zip, "about.txt", "Quest Recorder " + QuestRecorderPlugin.Patch + " report" + Environment.NewLine +
                                          DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + Environment.NewLine + Environment.NewLine +
                                          (note ?? string.Empty));
                AddText(zip, Path.GetFileName(csvPath), Hide(ReadShared(csvPath), who));
                if (File.Exists(txtPath))
                    AddText(zip, Path.GetFileName(txtPath), Hide(ReadShared(txtPath), who));
                string profile = ProfileFor(csvPath);
                if (profile != null)
                    AddText(zip, Path.GetFileName(profile), Hide(ReadShared(profile), who));
                string log = NewestLog();
                if (log != null)
                    AddText(zip, "BuddyCron log.txt", Hide(ReadShared(log), who));
            }
            return zipPath;
        }

        private static string NewestLog()
        {
            string logs = Path.Combine(Root(), "Logs");
            if (!Directory.Exists(logs))
                return null;
            string best = null;
            DateTime when = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(logs, "*.txt"))
            {
                DateTime w = File.GetLastWriteTime(f);
                if (best == null || w > when)
                {
                    best = f;
                    when = w;
                }
            }
            return best;
        }

        /// <summary>Packs a profile on its own so it can be shared. Returns the zip's path.</summary>
        public static string Share(string profilePath)
        {
            string dir = Path.Combine(RecordingSession.ResolveFolder(), "Shared");
            Directory.CreateDirectory(dir);
            string zipPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(profilePath) + ".zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                AddText(zip, Path.GetFileName(profilePath), ReadShared(profilePath));
                AddText(zip, "How to use.txt", "Put the .xml in your BuddyCron Profiles folder and load it with Load Profile." + Environment.NewLine +
                                               "Made with Quest Recorder by LethToxic.");
            }
            return zipPath;
        }
    }

    /// <summary>Check my profile: finds steps that will probably go wrong.</summary>
    internal static class ProfileCheck
    {
        public static List<string> Check(string path)
        {
            var found = new List<string>();
            var doc = new XmlDocument();
            doc.Load(path);
            XmlElement questing = null;
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
                if (n is XmlElement && n.LocalName == "Questing")
                    questing = (XmlElement)n;
            if (questing == null)
            {
                found.Add("There's no <Questing> part, so BuddyCron won't do anything with it.");
                return found;
            }
            var picked = new Dictionary<string, int>();
            var names = new Dictionary<string, string>();
            var rewarded = new HashSet<string>();
            int step = 0;
            bool ended = false;
            foreach (XmlNode n in questing.ChildNodes)
            {
                var e = n as XmlElement;
                if (e == null)
                    continue;
                step++;
                if (ended)
                {
                    found.Add("Step " + step + ": comes after the profile already stopped or switched, so it never runs.");
                    ended = false;
                }
                foreach (XmlElement x in All(e))
                {
                    string q = x.GetAttribute("QuestId");
                    switch (x.LocalName)
                    {
                        case "PickUpQuest":
                        case "PickupQuest":
                            if (q.Length > 0 && !picked.ContainsKey(q))
                            {
                                picked[q] = step;
                                names[q] = x.GetAttribute("QuestName").Length > 0 ? x.GetAttribute("QuestName") : q;
                            }
                            break;
                        case "CompleteQuest":
                        case "TurnInQuest":
                            if (q.Length > 0)
                                rewarded.Add(q);
                            break;
                        case "Grind":
                            if (x.GetAttribute("While").Length == 0)
                                found.Add("Step " + step + ": a fight with no end - it has no \"until\" quest step, so the bot fights forever.");
                            break;
                        case "LoadProfile":
                            string p = x.GetAttribute("Path");
                            string full = Path.IsPathRooted(p) ? p : Path.Combine(Path.GetDirectoryName(path), p);
                            if (!File.Exists(full) && Path.GetFileName(path) != p)
                                found.Add("Step " + step + ": loads \"" + p + "\" but that file isn't there.");
                            if (x == e)
                                ended = true;
                            break;
                        case "UserDialog":
                            if (x.GetAttribute("IsStopOnContinue") == "True" && x == e)
                                ended = true;
                            break;
                    }
                }
            }
            found = new List<string>(new HashSet<string>(found));
            found.Sort(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in picked)
                if (!rewarded.Contains(kv.Key))
                    found.Add("Step " + kv.Value + ": picks up " + names[kv.Key] + " but nothing hands it in or takes the reward (fine if the recording stopped early).");
            return found;
        }

        private static IEnumerable<XmlElement> All(XmlElement e)
        {
            yield return e;
            foreach (XmlNode c in e.ChildNodes)
            {
                var ce = c as XmlElement;
                if (ce == null)
                    continue;
                foreach (XmlElement d in All(ce))
                    yield return d;
            }
        }
    }

    /// <summary>"Record from here": play just the bit the bot got wrong, and it goes into the profile before the picked step.</summary>
    internal static class Merge
    {
        public static volatile string TargetPath;
        public static volatile string AnchorXml;
        public static volatile string AnchorLabel;

        public static void Begin(string target, string anchorXml, string anchorLabel)
        {
            TargetPath = target;
            AnchorXml = anchorXml;
            AnchorLabel = anchorLabel;
        }

        /// <summary>Called when that recording stops: moves its steps into the target profile.</summary>
        public static void Finish(string bitPath)
        {
            string target = TargetPath;
            TargetPath = null;
            if (target == null)
                return;
            string msg;
            try
            {
                int added = Insert(bitPath, target, AnchorXml, AnchorLabel);
                msg = added == 0
                    ? "Nothing to add - the bit you recorded had no quest progress in it."
                    : "Added " + added + " step(s) from what you just played into " + Path.GetFileName(target) + ". Load the profile again in BuddyCron.";
            }
            catch (Exception ex)
            {
                msg = "Couldn't add the recorded bit: " + ex.Message;
            }
            RrLog.Info("{0}", msg);
            GameToast.Show("Quest Recorder: " + msg, 10000);
        }

        /// <summary>Copies the steps of the bit (not its end-of-profile checks) in before the anchor. Returns how many.</summary>
        public static int Insert(string bitPath, string target, string anchorXml, string anchorLabel)
        {
            var bit = new XmlDocument();
            bit.Load(bitPath);
            XmlElement questing = null;
            foreach (XmlNode n in bit.DocumentElement.ChildNodes)
                if (n is XmlElement && n.LocalName == "Questing")
                    questing = (XmlElement)n;
            if (questing == null)
                return 0;
            var steps = new List<KeyValuePair<string, string>>();
            string comment = null;
            foreach (XmlNode n in questing.ChildNodes)
            {
                if (n is XmlComment)
                {
                    comment = n.Value.Trim();
                    if (EndMarker(comment))
                        break;
                    continue;
                }
                var e = n as XmlElement;
                if (e == null)
                    continue;
                steps.Add(new KeyValuePair<string, string>(comment ?? string.Empty, StripNs(Pretty(e))));
                comment = null;
            }
            if (steps.Count == 0)
                return 0;
            ProfileEditor ed = ProfileEditor.Open(target);
            ProfileEditor.StepView before = null;
            List<ProfileEditor.StepView> views = ed.Steps();
            foreach (ProfileEditor.StepView v in views)
                if (anchorXml != null && v.Node.OuterXml == anchorXml)
                    before = v;
            if (before == null && !string.IsNullOrEmpty(anchorLabel))
                foreach (ProfileEditor.StepView v in views)
                    if (before == null && v.Comment != null && v.Comment.Value.Trim() == anchorLabel)
                        before = v;
            if (before == null)
                foreach (ProfileEditor.StepView v in views)
                    if (before == null && v.Comment != null && EndMarker(v.Comment.Value.Trim()))
                        before = v; // no anchor: at the end, but before the profile's own end checks
            foreach (KeyValuePair<string, string> s in steps)
                ed.Add(s.Value, s.Key, before);
            ed.Save();
            return steps.Count;
        }

        /// <summary>The step laid out on several lines, indented like the profile's own steps (not one long line).</summary>
        private static string Pretty(XmlElement e)
        {
            var sb = new StringBuilder();
            var set = new XmlWriterSettings { Indent = true, IndentChars = "\t", NewLineChars = "\r\n", OmitXmlDeclaration = true, ConformanceLevel = ConformanceLevel.Fragment };
            using (XmlWriter w = XmlWriter.Create(sb, set))
                e.WriteTo(w);
            return sb.ToString().Replace("\r\n", "\r\n\t\t");
        }

        private static bool EndMarker(string comment)
        {
            return comment.StartsWith("Grab any rewards", StringComparison.Ordinal) || comment.StartsWith("Last check", StringComparison.Ordinal);
        }

        private static string StripNs(string xml)
        {
            return Regex.Replace(xml, @"\sxmlns(:\w+)?=""[^""]*""", string.Empty);
        }
    }
}
