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

        /// <summary>
        /// Every character name in a recording's .txt: the "Character   Name, level 80" line, and both sides of each
        /// character change ("Character changed: A -> B (level 80)") in older recordings.
        /// </summary>
        private static List<string> CharacterNames(string txtPath)
        {
            var names = new List<string>();
            if (!File.Exists(txtPath))
                return names;
            // shared: the recording going on now still has its .txt open for writing
            string text = ReadShared(txtPath);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith("Character  ", StringComparison.Ordinal)) // the header line ("Character   Name, level 80")
                    continue;
                string rest = line.Substring("Character".Length).Trim();
                int comma = rest.IndexOf(',');
                AddName(names, comma > 0 ? rest.Substring(0, comma) : rest);
                break;
            }
            foreach (Match m in Regex.Matches(text, @"Character changed: (.+?) -> (.+?) \(level"))
            {
                AddName(names, m.Groups[1].Value);
                AddName(names, m.Groups[2].Value);
            }
            return names;
        }

        private static void AddName(List<string> names, string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length >= 2 && !name.StartsWith("(", StringComparison.Ordinal) && !names.Contains(name))
                names.Add(name);
        }

        private static string Hide(string text, List<string> names)
        {
            // the whole name only: "Tor" stays in "Torvix", "Max" in <MaxLevel> ("@Name#" in the combat log goes too)
            foreach (string name in names)
                text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", "Player");
            // the Windows user name in paths (C:\Users\<name>\...)
            string user = Environment.UserName ?? string.Empty;
            if (user.Length >= 2)
                text = Regex.Replace(text, @"(?i)([\\/]Users[\\/])" + Regex.Escape(user) + @"(?=[\\/])", "${1}User");
            return text;
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
            return ProfileFor(csvPath, true);
        }

        /// <param name="firstPart">Also look for the profile of the recording this one carried on (it saved into that one).</param>
        private static string ProfileFor(string csvPath, bool firstPart)
        {
            // the profile the recorder saved this recording in (its .txt footer says; also a "(recorder copy)")
            try
            {
                string saved = RecordingSession.FooterProfile(Path.ChangeExtension(csvPath, ".txt"));
                if (saved != null && File.Exists(saved))
                    return saved;
            }
            catch (Exception ex)
            {
                RrLog.Debug("Reading where the profile was saved: {0}", ex.Message);
            }
            string named = NamedProfile(csvPath);
            if (named != null || !firstPart)
                return named;
            // a recording that carried on an earlier one saved into the first part's profile
            try
            {
                List<string> parts = RecordingSession.PartsOf(csvPath, false);
                if (parts.Count > 1 && !string.Equals(parts[0], csvPath, StringComparison.OrdinalIgnoreCase))
                    return ProfileFor(parts[0], false);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Looking for the recording this one carried on: {0}", ex.Message);
            }
            return null;
        }

        /// <summary>The profile named after the recording, wherever it is in BuddyCron's folders (or your own profile folder).</summary>
        private static string NamedProfile(string csvPath)
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
            // a profile folder of your own outside BuddyCron (Settings, Profile folder): the profile is saved right in it
            try
            {
                string chosen = (RrSettings.Instance.ProfileFolder ?? string.Empty).Trim();
                if (chosen.Length > 0 && Path.IsPathRooted(chosen) && Directory.Exists(chosen))
                    foreach (string f in Directory.GetFiles(chosen, name, SearchOption.TopDirectoryOnly))
                        return f;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
            return null;
        }

        /// <summary>Packs a recording, its profile and the newest BuddyCron log. Returns the zip's path.</summary>
        public static string MakeReport(string csvPath, string note)
        {
            string txtPath = Path.ChangeExtension(csvPath, ".txt");
            List<string> who = CharacterNames(txtPath);
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

    /// <summary>"Record from here": play just the bit the bot got wrong, and it goes into the profile after the picked step.</summary>
    internal static class Merge
    {
        private static volatile string _target;
        private static volatile Recorder _owner; // the recording the bit comes from (null while it is just starting)
        public static volatile string AnchorXml;
        public static volatile string AnchorLabel;

        /// <summary>
        /// The profile the bit goes into, or null. It belongs to the recording "Record from here" started: once that
        /// one has ended without adding it (stopped before your character was in the world, the bot ran the whole time),
        /// a later recording is a normal one again.
        /// </summary>
        public static string TargetPath
        {
            get
            {
                string t = _target;
                Recorder owner = _owner;
                if (t != null && owner != null && !ReferenceEquals(owner, Recorder.Current))
                    return null;
                return t;
            }
            set
            {
                _target = value;
                if (value == null)
                    _owner = null;
            }
        }

        public static void Begin(string target, string anchorXml, string anchorLabel)
        {
            _owner = null;
            AnchorXml = anchorXml;
            AnchorLabel = anchorLabel;
            _target = target;
        }

        /// <summary>The recording that just started is the one the bit comes from.</summary>
        public static void BindTo(Recorder r)
        {
            if (_target != null)
                _owner = r;
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

        /// <summary>
        /// Copies the steps of the bit (not its own end-of-profile checks) in after the anchor, with the fight areas and
        /// code they use. Returns how many.
        /// </summary>
        public static int Insert(string bitPath, string target, string anchorXml, string anchorLabel)
        {
            // a bit that isn't plain BuddyCron never goes in: the profile would stop loading
            List<string> problems = ReplayBuilder.PlainProblems(File.ReadAllText(bitPath, Encoding.UTF8));
            if (problems.Count > 0)
                throw new InvalidDataException("it is NOT finished (" + string.Join(", ", problems.ToArray()) + "), so nothing was added. Send the recording to LethToxic");
            var bit = new XmlDocument();
            bit.Load(bitPath);
            XmlElement questing = Section(bit, "Questing");
            if (questing == null)
                return 0;
            var steps = new List<KeyValuePair<string, XmlElement>>();
            string comment = null;
            foreach (XmlNode n in questing.ChildNodes)
            {
                if (n is XmlComment)
                {
                    comment = n.Value.Trim();
                    if (EndMarker(comment))
                        break;
                    if (comment == "Not finished")
                        throw new InvalidDataException("it is NOT finished, so nothing was added. Send the recording to LethToxic");
                    continue;
                }
                var e = n as XmlElement;
                if (e == null)
                    continue;
                // the bit's own end ("a quest from this profile isn't finished", "Done!"): it ends the profile, not this bit
                if (comment == null && EndStep(e))
                    break;
                steps.Add(new KeyValuePair<string, XmlElement>(comment, e)); // (no label: more of the same step)
                comment = null;
            }
            if (steps.Count == 0)
                return 0;
            ProfileEditor ed = ProfileEditor.Open(target);
            // the fights and code the bit's steps use go into the profile's header too, under names it doesn't have yet
            var head = new XmlDocument { PreserveWhitespace = true };
            head.Load(bitPath);
            var els = new List<XmlElement>();
            foreach (KeyValuePair<string, XmlElement> s in steps)
                els.Add(s.Value);
            CopyAreas(head, els, ed);
            CopyCode(head, els, ed);
            // after the picked step, never past the profile's own end checks
            List<ProfileEditor.StepView> views = ed.Steps();
            int end = EndOf(views);
            int at = -1;
            for (int i = 0; i < views.Count; i++)
                if (anchorXml != null && views[i].Node.OuterXml == anchorXml)
                    at = i;
            if (at < 0 && !string.IsNullOrEmpty(anchorLabel))
                for (int i = 0; i < views.Count && at < 0; i++)
                    if (views[i].Comment != null && views[i].Comment.Value.Trim() == anchorLabel)
                        at = i;
            int next = end;
            if (at >= 0 && at < end)
            {
                next = at + 1;
                // a recorder step is its label and every element up to the next label (a walk and its click, a Hook and its wait)
                if (ed.RecorderMade)
                    while (next < end && views[next].Comment == null)
                        next++;
            }
            ProfileEditor.StepView before = next < views.Count ? views[next] : null;
            foreach (KeyValuePair<string, XmlElement> s in steps)
                ed.Add(StripNs(Pretty(s.Value)), s.Key, before);
            ed.Save();
            return steps.Count;
        }

        private static XmlElement Section(XmlDocument doc, string name)
        {
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
                if (n is XmlElement && n.LocalName == name)
                    return (XmlElement)n;
            return null;
        }

        /// <summary>
        /// A step of a profile's own end: a pop-up the player can't continue from ("Done!", "a quest from this profile
        /// isn't finished") on its own or in an If, or a switch to another profile.
        /// </summary>
        private static bool EndStep(XmlElement e)
        {
            if (e.LocalName == "LoadProfile")
                return true;
            if (e.LocalName == "UserDialog")
                return e.GetAttribute("IsBotContinueAllowed") != "True";
            if (e.LocalName != "If")
                return false;
            XmlElement only = null;
            foreach (XmlNode c in e.ChildNodes)
            {
                if (!(c is XmlElement))
                    continue;
                if (only != null)
                    return false;
                only = (XmlElement)c;
            }
            return only != null && only.LocalName == "UserDialog" && only.GetAttribute("IsBotContinueAllowed") != "True";
        }

        /// <summary>Where a profile's own end checks start (the reward sweep, the end pop-ups): that step's index, or the number of steps.</summary>
        private static int EndOf(List<ProfileEditor.StepView> views)
        {
            for (int i = 0; i < views.Count; i++)
                if (views[i].Comment != null && EndMarker(views[i].Comment.Value.Trim()))
                    return i;
            int end = views.Count;
            while (end > 0 && views[end - 1].Comment == null && views[end - 1].Node is XmlElement && EndStep((XmlElement)views[end - 1].Node))
                end--;
            return end;
        }

        /// <summary>The bit's fight areas its steps use, into the profile's GrindAreas (renamed "X (2)" when it has an X); GrindRef follows.</summary>
        private static void CopyAreas(XmlDocument head, List<XmlElement> steps, ProfileEditor ed)
        {
            var refs = new List<XmlElement>();
            foreach (XmlElement s in steps)
                foreach (XmlNode n in s.SelectNodes("descendant-or-self::*[@GrindRef]"))
                    refs.Add((XmlElement)n);
            XmlElement areas = Section(head, "GrindAreas");
            if (refs.Count == 0 || areas == null)
                return;
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlNode n in areas.ChildNodes)
            {
                var a = n as XmlElement;
                if (a == null || a.LocalName != "GrindArea")
                    continue;
                string name = a.GetAttribute("Name");
                if (renamed.ContainsKey(name) || !refs.Exists(r => r.GetAttribute("GrindRef") == name))
                    continue;
                string unique = ed.UniqueHeaderName("GrindAreas", name);
                renamed[name] = unique;
                a.SetAttribute("Name", unique);
                ed.AddToHeader("GrindAreas", StripNs(a.OuterXml));
            }
            foreach (XmlElement r in refs)
            {
                string to;
                if (renamed.TryGetValue(r.GetAttribute("GrindRef"), out to))
                    r.SetAttribute("GrindRef", to);
            }
        }

        /// <summary>
        /// The bit's code its steps run (each Hook's chunk, its "...State" fields, helpers the profile lacks), into the
        /// profile's CodeChunks. The recorder numbers its code per profile (UseInOrder1 with fields _r1_...), so the bit's
        /// numbers go on after the profile's highest one; the Hook steps follow.
        /// </summary>
        private static void CopyCode(XmlDocument head, List<XmlElement> steps, ProfileEditor ed)
        {
            var hooks = new List<XmlElement>();
            foreach (XmlElement s in steps)
                foreach (XmlNode n in s.SelectNodes("descendant-or-self::Hook[@Name]"))
                    hooks.Add((XmlElement)n);
            XmlElement chunks = Section(head, "CodeChunks");
            if (hooks.Count == 0 || chunks == null)
                return;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElement h in hooks)
                used.Add(h.GetAttribute("Name"));
            HashSet<string> taken = ed.HeaderNames("CodeChunks");
            var copy = new List<XmlElement>();
            foreach (XmlNode n in chunks.ChildNodes)
            {
                var c = n as XmlElement;
                if (c == null || c.LocalName != "CodeChunk")
                    continue;
                string name = c.GetAttribute("Name");
                bool state = name.EndsWith("State", StringComparison.Ordinal) && used.Contains(name.Substring(0, name.Length - 5));
                bool helper = c.GetAttribute("Type") == "Definition" && NumberOf(name) == 0 && !taken.Contains(name);
                if (used.Contains(name) || state || helper)
                    copy.Add(c);
            }
            int top = 0;
            foreach (string t in taken)
                top = Math.Max(top, NumberOf(t));
            foreach (Match m in Regex.Matches(ed.HeaderText("CodeChunks"), @"\b_[A-Za-z]+(\d+)_\w"))
            {
                int k;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out k))
                    top = Math.Max(top, k);
            }
            var renum = new Dictionary<string, string>(StringComparer.Ordinal);
            if (top > 0)
            {
                var nums = new List<int>();
                foreach (XmlElement c in copy)
                {
                    int k = NumberOf(c.GetAttribute("Name"));
                    if (k > 0 && !nums.Contains(k))
                        nums.Add(k);
                }
                nums.Sort();
                foreach (int k in nums)
                    renum[k.ToString(CultureInfo.InvariantCulture)] = (++top).ToString(CultureInfo.InvariantCulture);
            }
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlElement c in copy)
            {
                string name = c.GetAttribute("Name");
                string to = name;
                Match nm = Regex.Match(name, @"^(.*?)(\d+)(State)?$");
                string nn;
                if (nm.Success && renum.TryGetValue(nm.Groups[2].Value, out nn))
                    to = nm.Groups[1].Value + nn + nm.Groups[3].Value;
                for (int k = 2; taken.Contains(to); k++)
                    to = name + "_" + k.ToString(CultureInfo.InvariantCulture); // a name of another kind already there
                taken.Add(to);
                renamed[name] = to;
                c.SetAttribute("Name", to);
                if (renum.Count > 0)
                    foreach (XmlNode t in c.ChildNodes)
                        if (t is XmlCDataSection || t is XmlText)
                            t.Value = Regex.Replace(t.Value, @"\b(_[A-Za-z]+)(\d+)_", m => renum.ContainsKey(m.Groups[2].Value) ? m.Groups[1].Value + renum[m.Groups[2].Value] + "_" : m.Value);
                ed.AddToHeader("CodeChunks", StripNs(c.OuterXml));
            }
            foreach (XmlElement h in hooks)
            {
                string to;
                if (renamed.TryGetValue(h.GetAttribute("Name"), out to))
                    h.SetAttribute("Name", to);
            }
        }

        /// <summary>The number a recorder code chunk ends with (UseInOrder3, UseInOrder3State: 3), or 0.</summary>
        private static int NumberOf(string name)
        {
            Match m = Regex.Match(name ?? string.Empty, @"(\d+)(State)?$");
            int k;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out k) ? k : 0;
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
            // (profiles made by 1.3 and older label their "a quest isn't finished" check "Not finished? ...")
            return comment.StartsWith("Grab any rewards", StringComparison.Ordinal) || comment.StartsWith("Last check", StringComparison.Ordinal) ||
                   comment.StartsWith("Not finished?", StringComparison.Ordinal);
        }

        private static string StripNs(string xml)
        {
            return Regex.Replace(xml, @"\sxmlns(:\w+)?=""[^""]*""", string.Empty);
        }
    }
}
