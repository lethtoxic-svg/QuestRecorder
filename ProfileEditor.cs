// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace QuestRecorder
{
    /// <summary>Profile Fixer: lets a player fix a profile without touching the XML.</summary>
    internal sealed class ProfileEditor
    {
        /// <summary>One step as the player sees it.</summary>
        internal sealed class StepView
        {
            public string Text = string.Empty;
            public XmlNode Node;
            public XmlComment Comment;
        }

        private readonly XmlDocument _doc = new XmlDocument();
        private XmlElement _questing;
        private string _ns = string.Empty;
        private bool _crlf = true;
        // the file as it was read (time + size): Save never writes this copy over a newer file
        private string _stamp = string.Empty;

        /// <summary>What Save says when someone else saved the profile after it was read here.</summary>
        public const string ChangedOnDiskText = "The profile was saved somewhere else after it was opened here (the recorder, Record from here or the review list).";

        public string Path { get; private set; }

        public string LastBackup { get; private set; }

        public static ProfileEditor Open(string path)
        {
            var e = new ProfileEditor();
            e.Path = path;
            e._stamp = StampOf(path); // before reading: a save in between makes the copy look older, never newer
            string text = File.ReadAllText(path, Encoding.UTF8);
            e._crlf = text.Contains("\r\n");
            e._doc.PreserveWhitespace = true;
            e._doc.LoadXml(text);
            XmlElement root = e._doc.DocumentElement;
            if (root == null)
                throw new InvalidDataException("Not a profile: no root element.");
            e._ns = root.NamespaceURI ?? string.Empty;
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is XmlElement && n.LocalName == "Questing")
                {
                    e._questing = (XmlElement)n;
                    break;
                }
            }
            if (e._questing == null)
                throw new InvalidDataException("Not a questing profile: it has no <Questing> section.");
            return e;
        }

        private static string StampOf(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + fi.Length.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Someone else saved the profile after it was read here (the recorder, Record from here, the review list, another panel).</summary>
        public bool ChangedOnDisk()
        {
            return StampOf(Path) != _stamp;
        }

        // ------------------------------------------------------------------ reading

        /// <summary>Made by Quest Recorder (its credit comment at the top): there a step is its label and every element up to the next label.</summary>
        public bool RecorderMade
        {
            get
            {
                foreach (XmlNode n in _doc.DocumentElement.ChildNodes)
                    if (n is XmlComment && n.Value.Trim().StartsWith("Made by Quest Recorder", StringComparison.Ordinal))
                        return true;
                return false;
            }
        }

        public List<StepView> Steps()
        {
            var list = new List<StepView>();
            XmlComment pending = null;
            foreach (XmlNode n in _questing.ChildNodes)
            {
                if (n is XmlComment)
                {
                    pending = (XmlComment)n;
                    continue;
                }
                if (!(n is XmlElement))
                    continue;
                var v = new StepView { Node = n, Comment = pending };
                string what = Describe((XmlElement)n);
                string label = pending != null ? OneLine(pending.Value) : string.Empty;
                v.Text = label.Length > 0 ? label + "  ->  " + what : what;
                list.Add(v);
                pending = null;
            }
            return list;
        }

        private static string OneLine(string s)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (s.Contains("  "))
                s = s.Replace("  ", " ");
            return s.Length > 140 ? s.Substring(0, 140) + "..." : s;
        }

        private static string Attr(XmlElement e, string name)
        {
            return e.HasAttribute(name) ? e.GetAttribute(name) : string.Empty;
        }

        /// <summary>A plain description of what a step does (looks inside If blocks).</summary>
        private static string Describe(XmlElement e)
        {
            var parts = new List<string>();
            Collect(e, parts);
            if (parts.Count == 0)
                return e.LocalName;
            string s = string.Join(", then ", parts.ToArray());
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        private static void Collect(XmlElement e, List<string> parts)
        {
            if (parts.Count >= 4)
                return;
            switch (e.LocalName)
            {
                case "If":
                case "While":
                    foreach (XmlNode c in e.ChildNodes)
                        if (c is XmlElement)
                            Collect((XmlElement)c, parts);
                    return;
                case "UseObject":
                    parts.Add("use " + (Attr(e, "ObjectName").Length > 0 ? Attr(e, "ObjectName") : FirstChildText(e, "Placeable", "object")));
                    return;
                case "Interact":
                    parts.Add("click " + Attr(e, "Name"));
                    return;
                case "TalkTo":
                    parts.Add("talk to " + Attr(e, "Name"));
                    return;
                case "PickUpQuest":
                case "PickupQuest":
                    parts.Add("pick up " + (Attr(e, "QuestName").Length > 0 ? Attr(e, "QuestName") : Attr(e, "QuestId")));
                    return;
                case "TurnInQuest":
                    parts.Add("hand in to " + Attr(e, "TurnInName"));
                    return;
                case "CompleteQuest":
                    parts.Add("take the reward");
                    return;
                case "Grind":
                    parts.Add("fight " + (Attr(e, "GrindRef").Length > 0 ? Attr(e, "GrindRef") : AllChildText(e, "Mob")));
                    return;
                case "MoveTo":
                case "NoCombatMoveTo":
                    parts.Add("walk to " + (Attr(e, "Name").Length > 0 ? Attr(e, "Name") : Attr(e, "XYZ")));
                    return;
                case "UseTransport":
                    parts.Add("take " + Attr(e, "TransporterName") + " to " + Attr(e, "Option"));
                    return;
                case "UseItem":
                    parts.Add("use item " + Attr(e, "ItemName"));
                    return;
                case "AnswerHolocomCall":
                    parts.Add("answer the holocom");
                    return;
                case "WaitTimer":
                    parts.Add("wait " + Attr(e, "WaitTime") + " s");
                    return;
                case "UserDialog":
                    parts.Add("message: " + Attr(e, "Message"));
                    return;
                case "LoadProfile":
                    parts.Add("load " + Attr(e, "Path"));
                    return;
                case "ClickObject":
                    parts.Add("click " + Attr(e, "Name") + " (that exact one)");
                    return;
                case "UseQuestAbility":
                    parts.Add("use ability " + Attr(e, "AbilityName"));
                    return;
                case "EquipItem":
                    parts.Add("put on " + Attr(e, "ItemName"));
                    return;
                case "UseMissionItemOnce":
                    parts.Add("use " + Attr(e, "ItemName") + " once");
                    return;
                case "UseInventoryItem":
                    parts.Add("use bag item " + Attr(e, "ItemName"));
                    return;
                case "SpaceTravel":
                    parts.Add("fly to " + Attr(e, "Planet"));
                    return;
                case "ForceVendor":
                    parts.Add("repair and sell");
                    return;
                case "EnablePlugin":
                    parts.Add("turn on plugin " + Attr(e, "Name"));
                    return;
                case "DisablePlugin":
                    parts.Add("turn off plugin " + Attr(e, "Name"));
                    return;
                case "JumpTo":
                    parts.Add("jump");
                    return;
                default:
                    parts.Add(e.LocalName);
                    return;
            }
        }

        private static string FirstChildText(XmlElement e, string name, string fallback)
        {
            foreach (XmlNode n in e.GetElementsByTagName(name, e.NamespaceURI))
                return n.InnerText;
            return fallback;
        }

        private static string AllChildText(XmlElement e, string name)
        {
            var names = new List<string>();
            foreach (XmlNode n in e.GetElementsByTagName(name, e.NamespaceURI))
                if (!names.Contains(n.InnerText))
                    names.Add(n.InnerText);
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "mobs";
        }

        // ------------------------------------------------------------------ changing

        public void Remove(StepView s)
        {
            RemoveWithSpace(s.Comment);
            RemoveWithSpace(s.Node);
        }

        private void RemoveWithSpace(XmlNode n)
        {
            if (n == null || n.ParentNode == null)
                return;
            XmlNode prev = n.PreviousSibling;
            if (prev != null && prev.NodeType == XmlNodeType.Whitespace)
                prev.ParentNode.RemoveChild(prev);
            n.ParentNode.RemoveChild(n);
        }

        /// <summary>Moves a step one place up (-1) or down (+1).</summary>
        public void Move(StepView s, int dir)
        {
            List<StepView> steps = Steps();
            int i = steps.FindIndex(x => x.Node == s.Node);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= steps.Count)
                return;
            StepView other = steps[j];
            XmlNode anchor = dir < 0 ? (other.Comment ?? other.Node) : null;
            // take the step (comment + node) out and put it back before the other one (up) or after it (down)
            RemoveWithSpace(s.Comment);
            RemoveWithSpace(s.Node);
            if (dir < 0)
                InsertBefore(anchor, s.Comment, s.Node);
            else
                InsertAfter(other.Node, s.Comment, s.Node);
        }

        private string Indent()
        {
            return (_crlf ? "\r\n" : "\n") + "\t\t";
        }

        private void InsertBefore(XmlNode anchor, XmlNode comment, XmlNode node)
        {
            if (anchor == null)
            {
                InsertAtEnd(comment, node);
                return;
            }
            if (comment != null)
            {
                _questing.InsertBefore(comment, anchor);
                _questing.InsertBefore(_doc.CreateWhitespace(Indent()), anchor);
            }
            _questing.InsertBefore(node, anchor);
            _questing.InsertBefore(_doc.CreateWhitespace(Indent()), anchor);
        }

        private void InsertAfter(XmlNode after, XmlNode comment, XmlNode node)
        {
            XmlNode at = after;
            if (comment != null)
            {
                XmlNode ws = _doc.CreateWhitespace(Indent());
                _questing.InsertAfter(ws, at);
                _questing.InsertAfter(comment, ws);
                at = comment;
            }
            XmlNode ws2 = _doc.CreateWhitespace(Indent());
            _questing.InsertAfter(ws2, at);
            _questing.InsertAfter(node, ws2);
        }

        private void InsertAtEnd(XmlNode comment, XmlNode node)
        {
            // keep the closing </Questing> on its own line: insert before the last whitespace
            XmlNode last = _questing.LastChild;
            XmlNode before = last != null && last.NodeType == XmlNodeType.Whitespace ? last : null;
            if (comment != null)
            {
                _questing.InsertBefore(_doc.CreateWhitespace(Indent()), before);
                _questing.InsertBefore(comment, before);
            }
            _questing.InsertBefore(_doc.CreateWhitespace(Indent()), before);
            _questing.InsertBefore(node, before);
        }

        /// <summary>The elements of a piece of profile XML (written without a namespace), made part of this profile.</summary>
        private List<XmlNode> Parse(string xml)
        {
            // parse inside the profile's own namespace, so the new step looks like the rest of the file
            var tmp = new XmlDocument { PreserveWhitespace = true }; // keep the step laid out like the rest of the file
            tmp.LoadXml("<x" + (_ns.Length > 0 ? " xmlns=\"" + _ns + "\"" : string.Empty) + ">" + xml + "</x>");
            var nodes = new List<XmlNode>();
            foreach (XmlNode n in tmp.DocumentElement.ChildNodes)
            {
                if (n is XmlElement)
                    nodes.Add(_doc.ImportNode(n, true));
            }
            if (nodes.Count == 0)
                throw new InvalidDataException("Nothing to add.");
            return nodes;
        }

        /// <summary>
        /// Adds a new step (profile XML for one or more elements in a row, written without a namespace) with a comment
        /// before the first one (none when the comment is null).
        /// </summary>
        public void Add(string xml, string comment, StepView before)
        {
            List<XmlNode> nodes = Parse(xml);
            XmlComment c = comment != null ? _doc.CreateComment(" " + Fmt.XmlComment(comment) + " ") : null;
            XmlNode anchor = before != null ? (before.Comment ?? before.Node) : null;
            foreach (XmlNode node in nodes)
            {
                InsertBefore(anchor, c, node); // (no anchor: at the end)
                c = null;
            }
        }

        // ------------------------------------------------------------------ the header (GrindAreas, CodeChunks)

        private XmlElement Section(string name)
        {
            foreach (XmlNode n in _doc.DocumentElement.ChildNodes)
                if (n is XmlElement && n.LocalName == name)
                    return (XmlElement)n;
            return null;
        }

        /// <summary>The Name of everything in a header section (e.g. every GrindArea of GrindAreas).</summary>
        public HashSet<string> HeaderNames(string section)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            XmlElement sec = Section(section);
            if (sec != null)
                foreach (XmlNode n in sec.ChildNodes)
                    if (n is XmlElement)
                        names.Add(((XmlElement)n).GetAttribute("Name"));
            return names;
        }

        /// <summary>All the text in a header section (the code of CodeChunks), or empty.</summary>
        public string HeaderText(string section)
        {
            XmlElement sec = Section(section);
            return sec != null ? sec.InnerText : string.Empty;
        }

        /// <summary>This name, or "name (2)", "name (3)"... when the header section already has one.</summary>
        public string UniqueHeaderName(string section, string name)
        {
            HashSet<string> taken = HeaderNames(section);
            string unique = name;
            for (int n = 2; taken.Contains(unique); n++)
                unique = name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            return unique;
        }

        /// <summary>
        /// Adds an element (XML written without a namespace) to a header section such as GrindAreas or CodeChunks, and
        /// makes the section (where the recorder puts it) when the profile has none.
        /// </summary>
        public void AddToHeader(string section, string xml)
        {
            XmlNode node = Parse(xml)[0];
            string nl = _crlf ? "\r\n" : "\n";
            XmlElement sec = Section(section);
            if (sec == null)
            {
                sec = _doc.CreateElement(section, _ns);
                sec.AppendChild(_doc.CreateWhitespace(nl + "\t"));
                XmlNode at = _questing;
                if (section == "CodeChunks" && Section("GrindAreas") != null)
                    at = Section("GrindAreas");
                _doc.DocumentElement.InsertBefore(sec, at);
                _doc.DocumentElement.InsertBefore(_doc.CreateWhitespace(nl + "\t"), at);
            }
            // keep the closing tag on its own line: insert before the last whitespace
            XmlNode last = sec.LastChild;
            XmlNode before = last != null && last.NodeType == XmlNodeType.Whitespace ? last : null;
            sec.InsertBefore(_doc.CreateWhitespace(nl + "\t\t"), before);
            sec.InsertBefore(node, before);
        }

        /// <summary>A new file for a backup of this profile in "_fixer backups" ("X (before fix 2026-11-08 10-11-12-345).xml"), never one that's there.</summary>
        internal static string NewBackupPath(string profile, string what)
        {
            string bdir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(profile) ?? ".", "_fixer backups");
            Directory.CreateDirectory(bdir);
            // to the millisecond: two quick fixes (Move up, Move up) each keep their own, so Undo goes back step by step
            string stem = System.IO.Path.GetFileNameWithoutExtension(profile) + " (" + what + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff", CultureInfo.InvariantCulture);
            string p = System.IO.Path.Combine(bdir, stem + ").xml");
            for (int n = 2; File.Exists(p); n++)
                p = System.IO.Path.Combine(bdir, stem + " " + n.ToString(CultureInfo.InvariantCulture) + ").xml");
            return p;
        }

        /// <summary>Saves the profile, keeping a backup of the previous version next to it (…\_fixer backups).</summary>
        public void Save()
        {
            // someone else saved it after it was read here: writing this copy would throw their steps away
            if (ChangedOnDisk())
                throw new InvalidOperationException(ChangedOnDiskText + " Nothing was saved.");
            LastBackup = NewBackupPath(Path, "before fix");
            File.Copy(Path, LastBackup, false);
            var sb = new StringBuilder();
            var ws = new XmlWriterSettings { OmitXmlDeclaration = _doc.FirstChild == null || _doc.FirstChild.NodeType != XmlNodeType.XmlDeclaration, Encoding = new UTF8Encoding(false) };
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (XmlWriter w = XmlWriter.Create(sw, ws))
                _doc.Save(w);
            string text = sb.ToString();
            if (text.StartsWith("<?xml", StringComparison.Ordinal) && ws.OmitXmlDeclaration)
                text = text.Substring(text.IndexOf("?>", StringComparison.Ordinal) + 2).TrimStart('\r', '\n');
            else if (text.StartsWith("<?xml", StringComparison.Ordinal))
            {
                // a StringWriter makes the declaration say utf-16, but the file is written as UTF-8 (a loader would refuse it)
                int end = text.IndexOf("?>", StringComparison.Ordinal);
                if (end > 0)
                    text = text.Substring(0, end).Replace("\"utf-16\"", "\"utf-8\"").Replace("'utf-16'", "'utf-8'") + text.Substring(end);
            }
            text = text.Replace("\r\n", "\n");
            if (_crlf)
                text = text.Replace("\n", "\r\n");
            File.WriteAllText(Path, text, new UTF8Encoding(false));
            _stamp = StampOf(Path); // our own save
        }

        /// <summary>Puts the most recent backup of this profile back (undo the last fix).</summary>
        public static string UndoLast(string path)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "_fixer backups");
            if (!Directory.Exists(dir))
                return null;
            string stem = System.IO.Path.GetFileNameWithoutExtension(path) + " (before fix ";
            string newest = null;
            DateTime best = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(dir, "*.xml"))
            {
                if (!System.IO.Path.GetFileName(f).StartsWith(stem, StringComparison.Ordinal))
                    continue;
                DateTime w = File.GetLastWriteTime(f);
                if (newest == null || w > best || (w == best && string.CompareOrdinal(f, newest) > 0))
                {
                    newest = f;
                    best = w;
                }
            }
            if (newest == null)
                return null;
            File.Copy(newest, path, true);
            File.Delete(newest);
            return newest;
        }

        // ------------------------------------------------------------------ the steps a player can add

        private static string X(float x, float y, float z)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", x, y, z);
        }

        private static string Esc(string s)
        {
            return Fmt.XmlAttr(s ?? string.Empty);
        }

        /// <summary>
        /// "until this quest step is done": the If condition and the step ids, or nulls for "just once". Branch 0 = the
        /// branch isn't known (the step of any branch).
        /// </summary>
        public static void Gate(ulong quest, ulong branch, ulong step, out string condition, out string ids)
        {
            if (quest == 0)
            {
                condition = null;
                ids = null;
                return;
            }
            string q = Fmt.Hex(quest);
            if (branch == 0)
            {
                condition = string.Format(CultureInfo.InvariantCulture, "HasQuest({0}) and not IsStepComplete({0}, {1})", q, step);
                ids = string.Format(CultureInfo.InvariantCulture, " QuestId=\"{0}\" StepId=\"{1}\"", q, step);
                return;
            }
            condition = string.Format(CultureInfo.InvariantCulture, "HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})", q, branch, step);
            ids = string.Format(CultureInfo.InvariantCulture, " QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\"", q, branch, step);
        }

        private static string Wrap(string condition, string inner)
        {
            return condition == null ? inner : "<If Condition=\"" + condition + "\">" + inner + "</If>";
        }

        private static float Dist(float x, float y, float z, float sx, float sy, float sz)
        {
            return (float)Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy) + (z - sz) * (z - sz));
        }

        /// <summary>
        /// Click / use an object. (x,y,z) = the object, (sx,sy,sz) = where you stand: the bot walks there first, as the
        /// recorder's own object steps do (the object's own spot can be out of reach, and BuddyCron's Interact takes the
        /// object of that name nearest to the bot).
        /// </summary>
        public static string ClickObject(string name, float x, float y, float z, float sx, float sy, float sz, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            float d = Dist(x, y, z, sx, sy, sz);
            bool near = d < 10f; // far away (targeted from a distance): the object's own spot
            if (cond == null)
                return "<MoveTo QuestId=\"1\" Name=\"" + Esc(name) + "\" XYZ=\"" + (near ? X(sx, sy, sz) : X(x, y, z)) + "\" />" +
                       "<Interact Name=\"" + Esc(name) + "\" XYZ=\"" + X(x, y, z) + "\" />";
            // used until the step is done; it waits there if the object has to respawn first
            string walk = string.Empty;
            int range = 20;
            if (near && (d > 1.5f || Math.Abs(sy - y) > 1f))
            {
                walk = "<MoveTo QuestId=\"1\" Name=\"Stand for " + Esc(name) + "\" XYZ=\"" + X(sx, sy, sz) + "\" />";
                range = Math.Max(20, (int)Math.Ceiling((d + 1f) * 10f));
            }
            // Radius 2: only the object within 2 m of its spot (not a same-name one nearby)
            return Wrap(cond, walk + "<UseObject ObjectName=\"" + Esc(name) + "\"" + ids + " UseRange=\"" + range.ToString(CultureInfo.InvariantCulture) +
                              "\" IgnoreLOS=\"True\" WaitTime=\"3000\" BlacklistDuration=\"5\" Radius=\"2\" XYZ=\"" + X(x, y, z) + "\" />");
        }

        public static string TalkTo(string name, float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            return Wrap(cond, "<TalkTo QuestId=\"" + (quest != 0 ? Fmt.Hex(quest) : "1") + "\" Name=\"" + Esc(name) + "\" WaitTime=\"5\" XYZ=\"" + X(x, y, z) + "\" />");
        }

        /// <summary>A fight's area for the profile's header (GrindAreas): mobs with this name around this spot.</summary>
        public static string FightArea(string area, string mob, float x, float y, float z)
        {
            return "<GrindArea Name=\"" + Esc(area) + "\"><MinLevel>1</MinLevel><MaxLevel>100</MaxLevel><Mobs><Mob>" + Esc(mob) +
                   "</Mob></Mobs><Hotspots><Hotspot Name=\"" + Esc(mob) + "\" XYZ=\"" + X(x, y, z) + "\" /></Hotspots></GrindArea>";
        }

        /// <summary>Fight in that header area until the quest step is done (Grind finds its area by GrindRef, like every hand-made profile).</summary>
        public static string Fight(string area, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            if (cond == null)
                throw new InvalidOperationException("Pick the quest step the fight is for, so the bot knows when to stop.");
            return Wrap(cond, "<Grind GrindRef=\"" + Esc(area) + "\" While=\"" + cond + "\" />");
        }

        public static string WalkHere(float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            return Wrap(cond, "<MoveTo QuestId=\"1\" Name=\"Walk point\" XYZ=\"" + X(x, y, z) + "\" />");
        }

        public static string Elevator(string name, string floor, float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            return Wrap(cond, "<UseTransport TransporterName=\"" + Esc(name) + "\" Type=\"Wonkavator\" Option=\"" + Esc(floor) + "\" XYZ=\"" + X(x, y, z) + "\" />");
        }

        public static string UseMissionItem(string item, float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            string spot = "<Hotspots><Hotspot Name=\"Where you used it\" XYZ=\"" + X(x, y, z) + "\" /></Hotspots>";
            if (cond == null)
                // just once: walk to where you stood, use it there
                return "<MoveTo QuestId=\"1\" Name=\"Where you used it\" XYZ=\"" + X(x, y, z) + "\" />" +
                       "<UseItem ItemName=\"" + Esc(item) + "\" WaitTime=\"3000\" Range=\"2000\">" + spot + "</UseItem>";
            // at your spot until the step is done: the step ids tell BuddyCron's UseItem when it's done (like the recorder's own item uses)
            return Wrap(cond, "<UseItem ItemName=\"" + Esc(item) + "\"" + ids + " WaitTime=\"3000\" XYZ=\"" + X(x, y, z) + "\">" + spot + "</UseItem>");
        }

        public static string Wait(int seconds)
        {
            return "<WaitTimer QuestId=\"1\" WaitTime=\"" + Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture) + "\" />";
        }

        /// <summary>A pop-up the player closes with Continue (BuddyCron's Continue button is off unless the step allows it).</summary>
        public static string Message(string text)
        {
            return "<UserDialog Title=\"Quest Recorder\" Message=\"" + Esc(text) + "\" IsBotContinueAllowed=\"True\" />";
        }

        // ---- control panel actions: every BuddyCron profile action in plain words

        private static string Q(ulong quest)
        {
            return Fmt.Hex(quest);
        }

        private static string NotYet(ulong quest)
        {
            return "HasQuest(" + Q(quest) + ") and not IsQuestComplete(" + Q(quest) + ")";
        }

        /// <summary>
        /// Pick up a quest from an NPC or a mission board / object (skipped once you have it or it is done; a weekly only
        /// while it isn't in your log, as the recorder does: the game can still call it complete when it's offered again).
        /// (x,y,z) = the giver, (sx,sy,sz) = where you stand.
        /// </summary>
        public static string PickUp(string questName, ulong quest, string giver, bool giverIsObject, float x, float y, float z, float sx, float sy, float sz)
        {
            bool weekly = ReplayBuilder.IsWeekly(questName); // (its tag in any client language)
            string gate = weekly ? "(not HasQuest(" + Q(quest) + "))" : "(not HasQuest(" + Q(quest) + ")) and (not IsQuestComplete(" + Q(quest) + "))";
            // an object: stand where you stood (its own spot can be out of reach), like the recorder's object pick-ups
            string walk = string.Empty;
            int range = 20;
            float d = Dist(x, y, z, sx, sy, sz);
            if (giverIsObject && d >= 0.3f && d < 10f)
            {
                walk = "<MoveTo QuestId=\"1\" Name=\"Stand for " + Esc(giver) + "\" XYZ=\"" + X(sx, sy, sz) + "\" />";
                range = Math.Max(20, (int)Math.Ceiling((d + 1f) * 10f));
            }
            return "<If Condition=\"" + gate + "\">" + walk + "<PickUpQuest QuestName=\"" + Esc(questName) + "\" QuestId=\"" + Q(quest) + "\"" +
                   (giverIsObject ? " SearchRadius=\"0.001\" UseRange=\"" + range.ToString(CultureInfo.InvariantCulture) + "\"" : string.Empty) +
                   " GiverName=\"" + Esc(giver) + "\" XYZ=\"" + X(x, y, z) + "\" /></If>";
        }

        /// <summary>Hand a quest in to an NPC or object, then take the reward.</summary>
        public static string HandIn(string questName, ulong quest, string to, bool toIsObject, float x, float y, float z)
        {
            return "<If Condition=\"" + NotYet(quest) + "\"><TurnInQuest QuestName=\"" + Esc(questName) + "\" QuestId=\"" + Q(quest) + "\"" +
                   (toIsObject ? " Type=\"Placeable\" SearchRadius=\"20\" UseRange=\"20\"" : string.Empty) + " TurnInName=\"" + Esc(to) + "\" XYZ=\"" + X(x, y, z) + "\" />" +
                   "<WaitTimer QuestId=\"1\" WaitTime=\"3\" /><CompleteQuest QuestId=\"" + Q(quest) + "\" /></If>";
        }

        /// <summary>
        /// Accept the reward of a quest that finished without a hand-in. The game counts the quest complete (or drops it
        /// from the log) while its reward window still waits, so "not complete" alone skipped it: also while a reward waits.
        /// </summary>
        public static string Reward(ulong quest)
        {
            return "<If Condition=\"(" + NotYet(quest) + ") or any(r.QuestId == " + Q(quest) + " for r in Me.Quests.PendingQuestRewards)\">" +
                   "<WaitTimer QuestId=\"1\" WaitTime=\"5\" /><CompleteQuest QuestId=\"" + Q(quest) + "\" /></If>";
        }

        public static string Holocom(string questName, ulong quest)
        {
            return "<If Condition=\"" + NotYet(quest) + "\"><AnswerHolocomCall QuestName=\"" + Esc(questName) + "\" QuestId=\"" + Q(quest) + "\" /></If>";
        }

        public static string WalkNoFight(float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            return Wrap(cond, "<NoCombatMoveTo Name=\"Walk point (no fighting)\" XYZ=\"" + X(x, y, z) + "\" />");
        }

        public static string Taxi(string terminal, string destination, float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            return Wrap(cond, "<UseTransport TransporterName=\"" + Esc(terminal) + "\" Type=\"Taxi\" Option=\"" + Esc(destination) + "\" XYZ=\"" + X(x, y, z) + "\" />");
        }

        /// <summary>Use a mission item on mobs/NPCs with your target's name around here, until the quest step is done.</summary>
        public static string UseItemOn(string item, string npc, float x, float y, float z, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            if (cond == null)
                throw new InvalidOperationException("Pick the quest step this is for, so the bot knows when to stop.");
            return Wrap(cond, "<UseItem ItemName=\"" + Esc(item) + "\"" + ids + " WaitTime=\"3000\" Range=\"30\" InCombat=\"False\" Radius=\"100\"><Npcs><Npc>" + Esc(npc) +
                              "</Npc></Npcs><Hotspots><Hotspot Name=\"Around here\" XYZ=\"" + X(x, y, z) + "\" /></Hotspots></UseItem>");
        }

        public static string UseBagItem(string item, ulong quest, ulong branch, ulong step)
        {
            string cond, ids;
            Gate(quest, branch, step, out cond, out ids);
            return Wrap(cond, "<UseInventoryItem ItemName=\"" + Esc(item) + "\" WaitTime=\"3\" />");
        }

        public static string FlyTo(string planet)
        {
            return "<SpaceTravel Planet=\"" + Esc(planet) + "\" />";
        }

        public static string Vendor()
        {
            return "<ForceVendor VendorType=\"Repair\" />";
        }

        public static string LoadOther(string path)
        {
            return "<LoadProfile Path=\"" + Esc(path) + "\" />";
        }

        public static string Plugin(string name, bool on)
        {
            return "<" + (on ? "EnablePlugin" : "DisablePlugin") + " Name=\"" + Esc(name) + "\" />";
        }

        /// <summary>A message box that stops the bot when the player presses Continue (the end of a profile).</summary>
        public static string StopBot(string text)
        {
            return "<UserDialog Title=\"Quest Recorder\" Message=\"" + Esc(text) + "\" IsStopOnContinue=\"True\" />";
        }
    }
}
