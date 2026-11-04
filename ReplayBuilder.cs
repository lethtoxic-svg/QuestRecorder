// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestRecorder
{
    /// <summary>The ready-to-load profile: a REPLAY of the recording.</summary>
    internal static class ReplayBuilder
    {
        private sealed class Use
        {
            public string Name;
            public Vec3 Pos;
            public double At;
            public bool Cast;
            public Vec3 Stand;
        }

        private sealed class Kill
        {
            public string Name;
            public Vec3 Pos;
            public double At;
        }

        /// <summary>Stands in for "Heroic Transport: &lt;mission&gt;" until the mission is known.</summary>
        private const string HeroicMark = "Heroic Transport: @mission@";

        private sealed class Step
        {
            public string Kind = string.Empty;
            public string Gate;
            public string Comment = string.Empty;
            public readonly List<string> Lines = new List<string>();
            public ulong Quest;
            public int Task = -1;
            public List<Kill> Kills;
            /// <summary>Other quests the same kills counted for: the fight goes on until they're done too.</summary>
            public Dictionary<ulong, string> Also;
            /// <summary>When the step was made (recording time).</summary>
            public double At = -1e9;
        }

        /// <summary>A conversation and the replies you picked ("2 dark"; "?" when not seen).</summary>
        private sealed class Convo
        {
            public string Partner = string.Empty;
            public double Start, End;
            public readonly List<string> Picks = new List<string>();
            public readonly List<List<string>> Wheels = new List<List<string>>();
        }

        /// <summary>
        /// Your replies in a conversation: light or dark picks wrap the step in &lt;TempConvoSkip Type="Light/Dark"&gt;;
        /// picks the bot wouldn't make by itself become &lt;TalkAndReply Replies="2,1"&gt; (Quest Recorder's own tag,
        /// it picks those wheel numbers). Picks the bot makes anyway change nothing.
        /// </summary>
        private static void ApplyReplies(List<Step> steps, List<Convo> convos, string side)
        {
            foreach (Step s in steps)
            {
                int li = s.Lines.FindIndex(l => l.StartsWith("<TalkTo", StringComparison.Ordinal) || l.StartsWith("<TurnInQuest", StringComparison.Ordinal) ||
                                                l.StartsWith("<PickUpQuest", StringComparison.Ordinal) || l.StartsWith("<PickUpIfOffered", StringComparison.Ordinal));
                if (li < 0)
                    continue;
                string line = s.Lines[li];
                string npc = line.StartsWith("<TalkTo", StringComparison.Ordinal) ? Attr(line, "Name") : Attr(line, line.StartsWith("<TurnInQuest", StringComparison.Ordinal) ? "TurnInName" : "GiverName");
                if (npc.Length == 0)
                    npc = Attr(line, "Name");
                Convo c = convos.FindLast(v => v.Partner == npc && v.Start <= s.At + 1 && s.At - v.End <= 30);
                if (c == null || c.Picks.Count == 0 || c.Picks.Contains("?"))
                    continue; // no wheel, or a pick the recorder didn't see: leave it to the bot
                var sides = new HashSet<string>();
                bool asBot = true;
                for (int i = 0; i < c.Picks.Count; i++)
                {
                    string align = c.Picks[i].Substring(c.Picks[i].IndexOf(' ') + 1);
                    if (align == "light" || align == "dark")
                        sides.Add(align);
                    if (AutoPick(c.Wheels[i], align == "light" || align == "dark" ? align : side) != c.Picks[i])
                        asBot = false;
                }
                if (sides.Count == 1 && asBot)
                {
                    string type = sides.Contains("dark") ? "Dark" : "Light";
                    s.Lines.Insert(0, "<TempConvoSkip Type=\"" + type + "\" AutoSkip=\"True\">");
                    s.Lines.Add("</TempConvoSkip>");
                    s.Comment += " (your " + type.ToLowerInvariant() + " replies)";
                }
                else if (!asBot || sides.Count > 1)
                {
                    var nums = c.Picks.ConvertAll(p => p.Substring(0, p.IndexOf(' ')));
                    string q = Attr(line, "QuestId");
                    s.Lines[li] = "<TalkAndReply QuestId=\"" + (q.Length > 0 ? q : "1") + "\" Name=\"" + A(npc) + "\" Replies=\"" + string.Join(",", nums.ToArray()) +
                                  "\" XYZ=\"" + Attr(line, "XYZ") + "\" />";
                    if (!line.StartsWith("<TalkTo", StringComparison.Ordinal))
                        s.Lines.Insert(li + 1, line); // still pick up / hand in afterwards if the talk didn't do it
                    s.Comment += " (your replies: " + string.Join(", ", c.Picks.ToArray()) + ")";
                }
            }
        }

        /// <summary>What the bot picks on a wheel for a side: that side's reply, else the first neutral, else the first.</summary>
        private static string AutoPick(List<string> wheel, string side)
        {
            string s = wheel.Find(w => w.EndsWith(" " + side, StringComparison.Ordinal));
            if (s == null)
                s = wheel.Find(w => w.EndsWith(" neutral", StringComparison.Ordinal));
            return s ?? (wheel.Count > 0 ? wheel[0] : string.Empty);
        }

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.Ordinal)
        {
            "container", "containers", "return", "defeat", "destroy", "recover", "speak", "mission", "with", "from",
            "acquire", "take", "find", "into", "your",
        };

        private static string X(Vec3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", v.X, v.Y, v.Z);
        }

        /// <summary>The "Undo last action" hotkey writes a marker with this note.</summary>
        public const string UndoNote = "Undo last action";
        /// <summary>Ctrl+Shift+P while recording: the profile pauses here so you do this part by hand.</summary>
        public const string PauseNote = "Pause here";
        /// <summary>Ctrl+Shift+W while recording: the bot waits here 10 seconds (a respawn, a door, a cut scene).</summary>
        public const string WaitNote = "Wait here";

        /// <summary>The player's own name for the "Recorded by" credit (setting "Your name for credits"); empty = none.</summary>
        /// <summary>Metres between walk points along your route (the navigator does the rest).</summary>
        public static float WalkSpacing = 0f; // 0 = no footsteps: BuddyCron walks between the steps by itself

        public static string CreditName = string.Empty;

        private const string RideKey = "\u0001ride|";

        /// <summary>The game's own travel abilities (BuddyCron's navigator does travel itself, so these are not mission items).</summary>
        private static bool IsTravelAbility(string name)
        {
            foreach (string s in new[] { "Quick Travel", "Fleet Pass", "Priority Transport", "Stronghold", "Return to", "Recall", "Travel to" })
                if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>"You are still on the side you rode from": the height when the lift changes floors by 2 m or more,
        /// else the axis you moved most along, split halfway between where you stood and where you came out.</summary>
        private static string SideOf(Vec3 from, Vec3 to)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
            string axis;
            float a, b;
            if ((Math.Abs(dy) >= 2f && Math.Sqrt(dx * dx + dz * dz) < 10.0) || (Math.Abs(dy) >= Math.Abs(dx) && Math.Abs(dy) >= Math.Abs(dz)))
            {
                axis = "Y"; a = from.Y; b = to.Y;
            }
            else if (Math.Abs(dx) >= Math.Abs(dz))
            {
                axis = "X"; a = from.X; b = to.X;
            }
            else
            {
                axis = "Z"; a = from.Z; b = to.Z;
            }
            float mid = (a + b) / 2f;
            return string.Format(CultureInfo.InvariantCulture, "(Me.Position.{0} {1} {2:0.##})", axis, a > b ? "&gt;" : "&lt;", mid);
        }

        private static string A(string s)
        {
            return Fmt.XmlAttr(s ?? string.Empty);
        }

        /// <summary>
        /// Every interaction in the recording - object, NPC and terminal clicks, quest windows, mission boards, reply
        /// picks, item uses - with what was clicked, where, and what changed in your quest log in the 10 s after.
        /// </summary>
        public static List<string> Interactions(IList<RecEvent> events)
        {
            return Interactions(events, true);
        }

        /// <summary>
        /// The same list, each line marked: OK = the bot does this itself; !! = it can't (and why), so you know while
        /// you're still standing there. Quest log changes nothing you did caused are listed too (the bot only walks there).
        /// </summary>
        public static List<string> Interactions(IList<RecEvent> events, bool header)
        {
            var list = new List<string>();
            if (header)
                list.AddRange(new[] { "What you did - Quest Recorder (LethToxic)", "OK = the bot does this by itself.  !! = it can't - the reason is on the line.", "" });
            var explained = new HashSet<RecEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                RecEvent e = events[i];
                // a quest change nothing you did caused (no click, item, talk or kill just before): flag it
                if ((e.Kind == EvKind.QuestTask || e.Kind == EvKind.QuestStep) && !e.QuestHidden && e.QuestId != 0 && !explained.Contains(e) &&
                    !(e.QuestName.Length > 0 && char.IsLower(e.QuestName[0])) && // the game's own helper quests
                    !(e.Kind == EvKind.QuestTask && (e.Max == 0 || e.Text.StartsWith("Complete ", StringComparison.OrdinalIgnoreCase))))
                {
                    bool cause = false;
                    for (int j = i - 1; j >= 0 && e.Elapsed - events[j].Elapsed <= 5 && !cause; j--)
                        cause = events[j].Kind == EvKind.Kill || events[j].Kind == EvKind.Cast || events[j].Kind == EvKind.Loading ||
                                events[j].Kind == EvKind.AreaChange || events[j].Kind == EvKind.Jump || events[j].Kind == EvKind.HolocomAnswered ||
                                events[j].Kind == EvKind.ConvoEnd || events[j].Kind == EvKind.ElevatorRide;
                    string change = "\"" + e.QuestName + "\" " + (e.Kind == EvKind.QuestTask ? e.Text + " " + e.Count + "/" + e.Max : "step " + (e.HasOldStep ? e.OldStep : e.Step) + " done");
                    // a step done with no click is usually "go there" (it ticked when you arrived): fine. A count with no click isn't.
                    bool arrived = !cause && e.Kind == EvKind.QuestStep;
                    list.Add((cause || arrived ? "OK " : "!! ") + Fmt.Duration(e.Elapsed).PadRight(10) + change + "  at " + X(e.Pos) +
                             (cause ? "  (after a fight / cast / ride)" : arrived ? "  (you got there - the bot walks here)" : "  - nothing you clicked caused it: the bot will only walk here"));
                    continue;
                }
                string what;
                switch (e.Kind)
                {
                    case EvKind.ObjectClick: what = "clicked object \"" + (e.Target != null ? e.Target.Name : "?") + "\""; break;
                    case EvKind.NpcClick: what = "clicked NPC \"" + (e.Target != null ? e.Target.Name : "?") + "\""; break;
                    case EvKind.BoardOpen: what = "mission board \"" + e.Option + "\" opened"; break;
                    case EvKind.InteractionOpen: what = e.Option + " window with \"" + (e.Target != null ? e.Target.Name : "?") + "\""; break;
                    case EvKind.ConvoStart: what = "talked to \"" + (e.Target != null ? e.Target.Name : "?") + "\""; break;
                    case EvKind.ConvoChoice: what = "reply " + (e.OptionGuessed ? "? (not seen)" : e.Option) + (e.Choices != null ? " of [" + string.Join(" | ", e.Choices.ToArray()) + "]" : string.Empty); break;
                    case EvKind.QuestItem: what = "used item \"" + e.Option + "\"" + (e.Target != null ? " on \"" + e.Target.Name + "\"" : string.Empty); break;
                    case EvKind.ElevatorRide: what = "rode \"" + (e.Target != null ? e.Target.Name : "?") + "\" to " + (e.Option.Length > 0 ? e.Option : "?"); break;
                    case EvKind.TaxiStart: what = "took a taxi"; break;
                    case EvKind.Marker: what = e.Note == UndoNote ? "you ignored your last action" : e.Note == PauseNote ? "pause here (you do it by hand)" : e.Note == WaitNote ? "wait here 10 s" : "marker " + e.Note; break;
                    case EvKind.Command: what = "key/mouse \"" + e.Option + "\" over \"" + (e.Target != null ? e.Target.Name : "?") + "\""; break;
                    default: continue;
                }
                Vec3 where = e.Target != null && e.Target.HasPos ? e.Target.Pos : e.Pos;
                var after = new List<string>();
                for (int j = i + 1; j < events.Count && events[j].Elapsed - e.Elapsed <= 10; j++)
                {
                    RecEvent q = events[j];
                    if (q.Kind == EvKind.ObjectClick || q.Kind == EvKind.NpcClick)
                        break; // the next thing you did
                    if (q.QuestHidden)
                        continue;
                    if (q.Kind == EvKind.QuestAdded || q.Kind == EvKind.QuestTask || q.Kind == EvKind.QuestStep || q.Kind == EvKind.QuestCompleted)
                        explained.Add(q); // this action caused it
                    if (q.Kind == EvKind.QuestAdded) after.Add("got \"" + q.QuestName + "\"");
                    else if (q.Kind == EvKind.QuestTask && q.Max > 0) after.Add("\"" + q.QuestName + "\" " + q.Text + " " + q.Count + "/" + q.Max);
                    else if (q.Kind == EvKind.QuestStep) after.Add("\"" + q.QuestName + "\" step " + (q.HasOldStep ? q.OldStep : q.Step) + " done");
                    else if (q.Kind == EvKind.QuestCompleted) after.Add("\"" + q.QuestName + "\" completed");
                }
                // what the bot can't do by itself, said right away
                string why = null;
                if (e.Kind == EvKind.ConvoChoice && e.OptionGuessed && e.Choices != null && e.Choices.Exists(c => !c.EndsWith(" neutral", StringComparison.Ordinal)))
                    why = "your reply wasn't seen - the bot will take its own side's reply here";
                if (e.Kind == EvKind.Command && after.Count == 0)
                    continue; // a key over something that did nothing: not worth a line
                list.Add((why != null ? "!! " : "OK ") + Fmt.Duration(e.Elapsed).PadRight(10) + what.PadRight(47) + X(where).PadRight(27) + "-> " +
                         (after.Count > 0 ? string.Join("; ", after.ToArray()) : "nothing") + (why != null ? "  - " + why : string.Empty));
            }
            return list;
        }

        /// <summary>"Using Rune of Passion..." -> "Rune of Passion"; null for any other cast.</summary>
        private static string ItemFromCast(string cast)
        {
            string c = (cast ?? string.Empty).Trim();
            if (!c.StartsWith("Using ", StringComparison.Ordinal))
                return null;
            c = c.Substring(6).TrimEnd('.', ' ');
            return c.Length > 0 ? c : null;
        }

        private static List<string> Words(string s)
        {
            var list = new List<string>();
            foreach (string w in (s ?? string.Empty).ToLowerInvariant().Split(new[] { ' ', ',', '.', ':', '(', ')', '\'', '"', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.Length >= 4 && !Stop.Contains(w))
                    list.Add(w);
            }
            return list;
        }

        private static bool Shares(string objectName, List<string> objective)
        {
            foreach (string w in Words(objectName))
                if (objective.Contains(w))
                    return true;
            return false;
        }

        public static string Build(IList<RecEvent> events, string profileName, string fileName, string recordingName)
        {
            return Build(events, profileName, fileName, recordingName, null);
        }

        /// <summary>Task tracker: one line per quest task / step the recording caught, with what the profile will do for it.</summary>
        public static List<string> Trace(IList<RecEvent> events)
        {
            var trace = new List<string>();
            Build(events, "trace", string.Empty, string.Empty, trace);
            return trace;
        }

        private static string Attr(string line, string name)
        {
            int i = line.IndexOf(" " + name + "=\"", StringComparison.Ordinal);
            if (i < 0)
                return string.Empty;
            i += name.Length + 3;
            int j = line.IndexOf('"', i);
            return j > i ? line.Substring(i, j - i) : string.Empty;
        }

        private static void FillTrace(List<Step> steps, List<string> trace)
        {
            int n = 0;
            foreach (Step s in steps)
            {
                string what;
                switch (s.Kind)
                {
                    case "Pickup":
                        what = "pick up";
                        break;
                    case "Complete":
                        what = "hand in";
                        break;
                    case "Ride":
                        what = s.Lines.Count > 0 && s.Lines[0].StartsWith("<UseItem", StringComparison.Ordinal) ? "use " + Attr(s.Lines[0], "ItemName") + " (it takes you there)" : s.Lines.Count > 0 && s.Lines[0].StartsWith("<UseTransport", StringComparison.Ordinal)
                            ? "take the lift to " + Attr(s.Lines[0], "Option")
                            : "walk out of the lift (BuddyCron takes it)";
                        break;
                    case "Grind":
                    {
                        var mobs = new List<string>();
                        if (s.Kills != null)
                            foreach (Kill k in s.Kills)
                                if (!mobs.Contains(k.Name))
                                    mobs.Add(k.Name);
                        what = "fight " + string.Join(", ", mobs);
                        foreach (string l in s.Lines)
                            if (l.StartsWith("<UseObject", StringComparison.Ordinal))
                                what = "use " + Attr(l, "ObjectName") + " at " + Attr(l, "XYZ") + ", then " + what;
                            else if (l.StartsWith("<Interact", StringComparison.Ordinal))
                                what = "click " + Attr(l, "Name") + " (it starts the fight), then " + what;
                        break;
                    }
                    case "Use":
                    {
                        var objs = new List<string>();
                        foreach (string l in s.Lines)
                            if (l.StartsWith("<UseObject", StringComparison.Ordinal))
                                objs.Add("use " + Attr(l, "ObjectName") + " at " + Attr(l, "XYZ"));
                            else if (l.StartsWith("<Interact", StringComparison.Ordinal))
                                objs.Add("click " + Attr(l, "Name"));
                        what = string.Join(" -> ", objs);
                        break;
                    }
                    case "Talk":
                        what = "talk to " + Attr(s.Lines.Count > 0 ? s.Lines[s.Lines.Count - 1] : string.Empty, "Name");
                        break;
                    case "Item":
                        what = "use item " + Attr(s.Lines.Count > 0 ? s.Lines[s.Lines.Count - 1] : string.Empty, "ItemName");
                        break;
                    case "Holocom":
                        what = "answer the holocom";
                        break;
                    case "Walk":
                        // a counted task needs an action; a step that ticked on arrival is a "go to" step
                        what = s.Comment.IndexOf(": step ", StringComparison.Ordinal) < 0
                            ?"!! nothing caught for this count - the bot would only walk to where it ticked"
                            : "walk there (it ticked when you arrived)";
                        break;
                    default:
                        continue; // walks between steps, skipped steps
                }
                n++;
                trace.Add(n.ToString(CultureInfo.InvariantCulture) + ". " + s.Comment + "  ->  " + what);
            }
        }

        private static string Build(IList<RecEvent> events, string profileName, string fileName, string recordingName, List<string> trace)
        {
            // The shuttle can land you before its cast is noted ("jump while casting Heroic Transport"): put the cast
            // first, so the hop is seen as the mission item that moved you.
            var ordered = new List<RecEvent>(events);
            for (int i = 0; i + 1 < ordered.Count; i++)
            {
                RecEvent j = ordered[i];
                if (j.Kind != EvKind.Jump)
                    continue;
                for (int k = i + 1; k < ordered.Count && ordered[k].Elapsed - j.Elapsed <= 3; k++)
                {
                    RecEvent c = ordered[k];
                    if ((c.Kind == EvKind.Cast && c.Option.StartsWith("Heroic Transport", StringComparison.Ordinal)) ||
                        (c.Kind == EvKind.QuestItem && c.Option.StartsWith("Heroic Transport", StringComparison.Ordinal)))
                    {
                        ordered.RemoveAt(k);
                        ordered.Insert(i, c);
                        i++;
                        break;
                    }
                }
            }
            events = ordered;
            // lifts and taxis are ridden by the navigator: clicks on them are not object uses
            var transport = new HashSet<string>(StringComparer.Ordinal);
            foreach (RecEvent e in events)
            {
                if ((e.Kind == EvKind.ElevatorOpen || e.Kind == EvKind.ElevatorRide || e.Kind == EvKind.TaxiOpen || e.Kind == EvKind.TaxiStart || e.Kind == EvKind.TaxiEnd) &&
                    e.Target != null && e.Target.Name.Length > 0)
                    transport.Add(e.Target.Name);
            }

            // every named object you used anywhere in the recording (for a cast the game did not tie to an object)
            var allUses = new List<Use>();
            foreach (RecEvent e in events)
            {
                if ((e.Kind == EvKind.ObjectClick || e.Kind == EvKind.Cast) && e.Target != null && e.Target.Kind == ObjKind.Placeable &&
                    e.Target.Name.Length > 0 && !e.Target.Name.StartsWith("#", StringComparison.Ordinal) && !transport.Contains(e.Target.Name))
                    allUses.Add(new Use { Name = e.Target.Name, Pos = e.Target.HasPos ? e.Target.Pos : e.Pos, At = e.Elapsed });
            }

            // NPCs you clicked in places far apart (your companion follows you): never the cause of a quest tick
            var followers = new HashSet<string>(StringComparer.Ordinal);
            var npcSpots = new Dictionary<string, Vec3>(StringComparer.Ordinal);
            foreach (RecEvent e in events)
            {
                if (e.Kind != EvKind.NpcClick || e.Target == null || e.Target.Name.Length == 0)
                    continue;
                Vec3 at = e.Target.HasPos ? e.Target.Pos : e.Pos;
                Vec3 first;
                if (!npcSpots.TryGetValue(e.Target.Name, out first))
                    npcSpots[e.Target.Name] = at;
                else if (first.DistanceTo(at) > 25f)
                    followers.Add(e.Target.Name);
            }
            var hiddenQuests = new HashSet<ulong>(); // the game's own helper quests (marked hidden at least once)
            foreach (RecEvent e in events)
                if (e.QuestHidden && e.QuestId != 0)
                    hiddenQuests.Add(e.QuestId);
            var questTitles = new Dictionary<ulong, string>();
            foreach (RecEvent e in events)
                if (e.QuestId != 0 && !string.IsNullOrEmpty(e.QuestName) && !questTitles.ContainsKey(e.QuestId))
                    questTitles[e.QuestId] = e.QuestName;
            var killedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (RecEvent e in events)
                if (e.Kind == EvKind.Kill && e.Target != null && e.Target.Name.Length > 0)
                    killedNames.Add(e.Target.Name);

            var steps = new List<Step>();
            var markers = new List<KeyValuePair<string, Vec3>>();
            var uses = new List<Use>();
            var consumed = new Dictionary<string, double>(StringComparer.Ordinal);
            var seen = new Dictionary<string, Vec3>(StringComparer.Ordinal);
            var kills = new List<Kill>();
            var allKills = new List<Kill>(); // every kill in the recording (more spots for each fight)
            ObjRef talk = null;
            double talkAt = -1e9;
            double boardAt = -1e9; // when you last opened a mission board
            ObjRef lastClicked = null; // the last object or NPC you clicked (not lifts or taxis)
            double lastClickedAt = -1e9;
            ObjRef questWindow = null; // the NPC whose quest window you last opened
            double questWindowAt = -1e9;
            double holoAt = -1e9;
            string item = null;
            double itemAt = -1e9;
            double rideAt = -1e9;
            double fightAt = -1e9;
            double castEndAt = -1e9;
            Step pendingRide = null;
            Vec3 rideFrom = default(Vec3);
            ObjRef pendingLift = null;
            string pendingDest = string.Empty;
            double liftOpenAt = -1e9;
            Vec3 itemPos = default(Vec3);
            string itemOn = null; // the NPC you used the item on, if you had one targeted
            string castItem = null; // a cast on nothing (maybe a mission item) and where it ended
            double castItemAt = -1e9;
            Vec3 castItemPos = default(Vec3);
            string npcClickName = null;
            Vec3 npcClickPos = default(Vec3);
            double npcClickAt = -1e9;
            Step lastWalk = null;
            var finalAt = new Dictionary<ulong, double>();
            double lastWalkAt = -1e9;
            int dark = 0, light = 0;
            var vendors = new Dictionary<string, Vec3>(StringComparer.Ordinal);
            double lastProgressAt = -1e9;
            ulong lastProgressQuest = 0;
            EvKind lastProgressKind = EvKind.Start;
            var stepText = new Dictionary<ulong, string>();
            var picked = new List<ulong>();
            var weeklies = new HashSet<ulong>(); // a weekly can be picked up again (3 times a week): never loop just for it
            var knownItems = new List<string>(); // every mission item you used in this recording
            var itemForTask = new Dictionary<string, string>(StringComparer.Ordinal); // quest/step/task -> the item you did it with
            var scanner = new Dictionary<string, List<Vec3>>(StringComparer.Ordinal); // same-kind objects near your clicks
            var itemOrder = new List<Use>(); // mission items used since the last tick, in order
            var completedOrder = new List<ulong>();
            double bareCastAt = -1e9;
            Vec3 bareCastPos = default(Vec3);

            bool haveTrail = false;
            Vec3 lastTrail = default(Vec3);
            var convos = new List<Convo>(); // every conversation and the replies you picked
            Convo convo = null;
            double prevT = 0;
            foreach (RecEvent e in events)
            {
                double t = e.Elapsed;
                foreach (Step made in steps)
                    if (made.At < -1e8)
                        made.At = prevT; // when each step was made (to find the conversation it came from)
                prevT = t;
                if ((e.Kind == EvKind.ObjectClick || e.Kind == EvKind.NpcClick) && e.Target != null && e.Target.Name.Length > 0 && !transport.Contains(e.Target.Name))
                {
                    lastClicked = e.Target;
                    lastClickedAt = t;
                }
                switch (e.Kind)
                {
                    case EvKind.ConvoStart:
                        convo = e.Note == How.HolocomCall || e.Target == null ? null : new Convo { Partner = e.Target.Name, Start = t, End = t + 600 };
                        if (convo != null)
                            convos.Add(convo);
                        break;
                    case EvKind.ConvoChoice:
                        if (convo != null)
                        {
                            convo.Picks.Add(e.OptionGuessed ? "?" : e.Option);
                            convo.Wheels.Add(e.Choices ?? new List<string>());
                        }
                        break;
                    case EvKind.ConvoEnd:
                        if (convo != null)
                            convo.End = t;
                        convo = null;
                        break;
                }
                if (pendingRide != null && !e.Pos.IsZero && e.Pos.DistanceTo(rideFrom) >= 3f)
                {
                    pendingRide.Gate = SideOf(rideFrom, e.Pos);
                    if (pendingDest.Length > 0 && pendingLift.HasPos)
                    {
                        pendingRide.Comment = "Elevator: " + pendingLift.Name + " to " + pendingDest;
                        pendingRide.Lines[0] = "<UseTransport TransporterName=\"" + A(pendingLift.Name) + "\" Type=\"Wonkavator\" Option=\"" + A(pendingDest) +
                                               "\" XYZ=\"" + X(pendingLift.Pos) + "\" />";
                    }
                    else
                    {
                        // the floor is not known: walk to where you came out and leave the lift to BuddyCron
                        pendingRide.Comment = "Elevator: " + pendingLift.Name + "";
                        pendingRide.Lines[0] = "<MoveTo QuestId=\"1\" Name=\"Off the " + A(pendingLift.Name) + "\" XYZ=\"" + X(e.Pos) + "\" />";
                    }
                    pendingRide = null;
                }
                // Follow your footsteps: a walk point every WalkSpacing metres; BuddyCron finds the way in between.
                if ((e.Kind == EvKind.ElevatorRide || e.Kind == EvKind.Loading || e.Kind == EvKind.Jump || e.Kind == EvKind.AreaChange) && !e.Pos.IsZero)
                {
                    haveTrail = true;
                    lastTrail = e.Pos; // the ride step takes you there; footsteps go on from the arrival
                }
                else if (e.Kind != EvKind.Start && e.Kind != EvKind.QuestLog && e.Kind != EvKind.Stop && !e.Pos.IsZero)
                {
                    if (!haveTrail)
                    {
                        haveTrail = true;
                        lastTrail = e.Pos;
                    }
                    else if (WalkSpacing > 0f && lastTrail.DistanceTo(e.Pos) >= WalkSpacing)
                    {
                        markers.Add(new KeyValuePair<string, Vec3>("Your path", e.Pos));
                        lastTrail = e.Pos;
                    }
                }
                switch (e.Kind)
                {
                    case EvKind.Marker:
                        if (e.Note.StartsWith(UndoNote, StringComparison.Ordinal))
                        {
                            // ignore your last action, whatever it was: an object click, a mission item, or an NPC click
                            double useAt = uses.Count > 0 ? uses[uses.Count - 1].At : -1e9;
                            double itemUseAt = item != null ? itemAt : -1e9;
                            double npcAt = npcClickName != null ? npcClickAt : -1e9;
                            if (useAt >= itemUseAt && useAt >= npcAt && uses.Count > 0)
                                uses.RemoveAt(uses.Count - 1);
                            else if (itemUseAt >= npcAt && item != null)
                            {
                                item = null;
                                if (itemOrder.Count > 0)
                                    itemOrder.RemoveAt(itemOrder.Count - 1);
                            }
                            else if (npcClickName != null)
                                npcClickName = null;
                            break;
                        }
                        if (e.Note.StartsWith(PauseNote, StringComparison.Ordinal) || e.Note.StartsWith(WaitNote, StringComparison.Ordinal))
                        {
                            // your pause / wait hotkey: its own step right here, run when the next step is still to do
                            FlushRoute(steps, markers);
                            var spot = new Step { Kind = "Route", Comment = e.Note.StartsWith(PauseNote, StringComparison.Ordinal) ? "Your pause here" : "Your wait here" };
                            spot.Lines.Add("<MoveTo QuestId=\"1\" Name=\"" + A(spot.Comment) + "\" XYZ=\"" + X(e.Pos) + "\" />");
                            spot.Lines.Add(e.Note.StartsWith(PauseNote, StringComparison.Ordinal)
                                ? "<UserDialog Title=\"Quest Recorder\" Message=\"You paused here when you recorded: do this part by hand, then press Continue.\" />"
                                : "<WaitTimer QuestId=\"1\" WaitTime=\"10\" />");
                            steps.Add(spot);
                            break;
                        }
                        markers.Add(new KeyValuePair<string, Vec3>(e.Note, e.Pos));
                        break;
                    case EvKind.ObjectClick:
                    case EvKind.Cast:
                    {
                        // "Using Rune of Passion..." is the cast of the mission item "Rune of Passion" (older recordings
                        // didn't tie it to the item): a mission item use, in your order
                        string usedItem = e.Kind == EvKind.Cast ? ItemFromCast(e.Option) : null;
                        if (usedItem != null)
                        {
                            if (!knownItems.Contains(usedItem))
                                knownItems.Add(usedItem);
                            if (itemOrder.Count == 0 || itemOrder[itemOrder.Count - 1].Name != usedItem || t - itemOrder[itemOrder.Count - 1].At > 5)
                                itemOrder.Add(new Use { Name = usedItem, Pos = e.Pos, At = t });
                            item = usedItem;
                            itemAt = t;
                            itemPos = e.Pos;
                            itemOn = null;
                            break;
                        }
                        ObjRef o = e.Target;
                        if (e.Kind == EvKind.Cast && (o == null || o.Name.Length == 0 || o.Name.StartsWith("#", StringComparison.Ordinal)) && !e.Pos.IsZero)
                        {
                            // a cast the game did not tie to an object (CZ-198 Biotoxin 1, Kolto 1): you stood right at it
                            bareCastAt = t;
                            bareCastPos = e.Pos;
                            // a cast on nothing that moves you far right after is a mission item too.
                            if (e.Option.Length > 0 && e.Option != "Loot" && !e.Option.EndsWith("...", StringComparison.Ordinal) && !IsTravelAbility(e.Option))
                            {
                                castItem = e.Option;
                                castItemAt = t;
                                castItemPos = e.Pos;
                            }
                        }
                        if (o == null || o.Kind != ObjKind.Placeable || o.Name.Length == 0 || o.Name.StartsWith("#", StringComparison.Ordinal) || transport.Contains(o.Name))
                            break;
                        Vec3 p = o.HasPos ? o.Pos : e.Pos;
                        string key = X(p);
                        seen[o.Name] = p;
                        double when;
                        if (e.Kind == EvKind.ObjectClick && consumed.TryGetValue(key, out when) && t - when <= 30)
                            break; // a second click on an object already used (no new cast on it)
                        if (e.Kind == EvKind.ObjectClick && t - castEndAt <= 1.5 && uses.Count > 0 && uses[uses.Count - 1].Name != o.Name)
                            break; // a stray click right as a cast on another object ended (CZ-198 Kolto after the last pod)
                        if (e.Kind == EvKind.Cast)
                            castEndAt = t;
                        if (uses.Count > 0 && X(uses[uses.Count - 1].Pos) == key)
                        {
                            uses[uses.Count - 1].At = t; // the click and its cast are one use
                            if (e.Kind == EvKind.Cast)
                                uses[uses.Count - 1].Cast = true;
                            break;
                        }
                        uses.Add(new Use { Name = o.Name, Pos = p, At = t, Cast = e.Kind == EvKind.Cast, Stand = e.Pos });
                        break;
                    }
                    case EvKind.ConvoStart:
                        if (e.Note == How.HolocomCall)
                            holoAt = t;
                        else if (e.Target != null && e.Target.Name.Length > 0)
                        {
                            talk = e.Target;
                            talkAt = t;
                        }
                        break;
                    case EvKind.BoardOpen:
                        boardAt = t;
                        break;
                    case EvKind.Spot:
                        // the scanner: another object like the one you clicked, nearby
                        if (e.Target != null && e.Target.HasPos && e.Target.Name.Length > 0)
                        {
                            List<Vec3> sl;
                            if (!scanner.TryGetValue(e.Target.Name, out sl))
                                scanner[e.Target.Name] = sl = new List<Vec3>();
                            if (sl.Count < 40)
                                sl.Add(e.Target.Pos);
                        }
                        break;
                    case EvKind.QuestItem:
                        if (e.Option.Length > 0)
                        {
                            if (!knownItems.Contains(e.Option))
                                knownItems.Add(e.Option);
                            if (itemOrder.Count == 0 || itemOrder[itemOrder.Count - 1].Name != e.Option || t - itemOrder[itemOrder.Count - 1].At > 5)
                                itemOrder.Add(new Use { Name = e.Option, Pos = e.Pos, At = t }); // (its cast may have been noted already)
                            item = e.Option;
                            itemAt = t;
                            itemPos = e.Pos;
                            itemOn = e.Target != null && e.Target.Kind != ObjKind.Placeable && e.Target.Name.Length > 0 && !string.Equals(e.Target.Toughness, "companion", StringComparison.OrdinalIgnoreCase) ? e.Target.Name : null;
                        }
                        break;
                    case EvKind.Loading:
                        // handled with the jump / area change it causes (below)
                        if (e.HasFrom && item != null && t - itemAt <= 30)
                            goto case EvKind.Jump;
                        break;
                    case EvKind.NpcClick:
                        if (e.Target != null && e.Target.Name.Length > 0 && !string.Equals(e.Target.Toughness, "companion", StringComparison.OrdinalIgnoreCase))
                        {
                            npcClickName = e.Target.Name;
                            npcClickPos = e.Target.HasPos ? e.Target.Pos : e.Pos;
                            npcClickAt = t;
                        }
                        break;
                    case EvKind.ConvoChoice:
                        if (!e.OptionGuessed && e.Option.EndsWith(" dark", StringComparison.Ordinal))
                            dark++;
                        else if (!e.OptionGuessed && e.Option.EndsWith(" light", StringComparison.Ordinal))
                            light++;
                        break;
                    case EvKind.ElevatorOpen:
                    case EvKind.ElevatorRide:
                    {
                        // Take the elevator you took.
                        ObjRef w = e.Target;
                        string dest = e.Kind == EvKind.ElevatorRide ? e.Option : string.Empty;
                        if (dest.Length == 0 && w != null && w.Destinations != null && w.Destinations.Length == 2 && w.Floor >= 1 && w.Floor <= 2)
                            dest = w.Destinations[2 - w.Floor]; // two floors: the other one
                        rideAt = t;
                        if (e.Kind == EvKind.ElevatorRide && w != null && pendingLift != null && w.Name == pendingLift.Name && t - liftOpenAt <= 60)
                        {
                            // the window you opened, now ridden: that step already covers it (just take the floor if new)
                            if (pendingRide != null && dest.Length > 0 && pendingDest.Length == 0)
                                pendingDest = dest;
                            break;
                        }
                        liftOpenAt = t;
                        if (w != null && w.Name.Length > 0)
                        {
                            // walk points so far come first
                            if (markers.Count > 0)
                            {
                                var before = new Step { Kind = "Route", Comment = "Your route" };
                                AddRoute(before, markers);

                                steps.Add(before);
                                markers.Clear();
                            }
                            // The ride is its own step, not tied to one quest step.
                            var ride = new Step { Kind = "Ride", Comment = "Elevator: " + w.Name };
                            ride.Lines.Add(string.Empty); // written when you come out (below)
                            steps.Add(ride);
                            pendingRide = ride;
                            pendingLift = w;
                            pendingDest = dest;
                            rideFrom = e.HasStand ? e.Stand : (e.Kind == EvKind.ElevatorOpen ? e.Pos : e.FromPos);
                        }
                        break;
                    }
                    case EvKind.TaxiStart:
                    case EvKind.TaxiEnd:
                    case EvKind.ShipTravel:
                        rideAt = t;
                        break;
                    case EvKind.AreaChange:
                    case EvKind.Jump:
                        // A mission item that moved you.
                        if ((item == null || t - itemAt > 30) && castItem != null && t - castItemAt <= 3 && e.Kind == EvKind.Jump)
                        {
                            item = castItem;
                            itemAt = castItemAt;
                            itemPos = castItemPos;
                        }
                        castItem = null;
                        // (a heroic shuttle from the Fleet goes through a loading screen to the planet: that's a hop too)
                        bool otherArea = e.Kind == EvKind.AreaChange && !e.Pos.IsZero;
                        if (item != null && t - itemAt <= 30 && (otherArea || (e.HasFrom && e.FromPos.DistanceTo(e.Pos) >= 30f)) && t - rideAt > 5)
                        {
                            FlushRoute(steps, markers);
                            // Runs from wherever you are.
                            // "Away" = further than half the hop you made (a short 50 m shuttle hop, like Saving Face after
                            // Personal Challenge, would otherwise count as already there and get walked).
                            float hopLen = otherArea || !e.HasFrom || e.FromPos.IsZero ? 200f : e.FromPos.DistanceTo(e.Pos);
                            float awayR = Math.Max(15f, Math.Min(100f, hopLen / 2f));
                            string away = Fmt.Format("(((Me.Position.X - ({0})) ** 2 + (Me.Position.Z - ({1})) ** 2) &gt; {2})",
                                                     Fmt.F3(e.Pos.X), Fmt.F3(e.Pos.Z), ((int)(awayR * awayR)).ToString(CultureInfo.InvariantCulture));
                            // An old recording only knew the cast "Heroic Transport".
                            string useName = item == "Heroic Transport" ? HeroicMark : item;
                            string pyName = useName.Replace("\\", "\\\\").Replace("'", "\\'");
                            string have = "(any(i.Name == '" + pyName + "' for i in Me.QuestItems) or any(i.Name == '" + pyName + "' for i in Me.Backpack))";
                            string shortHave = "(any(i.Name == 'Heroic Transport' for i in Me.QuestItems) or any(i.Name == 'Heroic Transport' for i in Me.Backpack))";
                            bool heroic = useName.StartsWith("Heroic Transport: ", StringComparison.Ordinal);
                            // the whole step (waits too) only runs when a shuttle can actually be used from here
                            var hop = new Step { Kind = "Ride", Comment = "Mission item: " + useName, Gate = away + " and " + A(heroic ? "(" + have + " or " + shortHave + ")" : have) };
                            string where = "<Hotspots><Hotspot Name=\"Where you used it\" XYZ=\"" + X(itemPos.IsZero ? e.FromPos : itemPos) + "\" /></Hotspots>";
                            string cond = away + " and " + A(have);
                            hop.Lines.Add("<If Condition=\"" + cond + "\">");
                            hop.Lines.Add("<UseItem ItemName=\"" + A(useName) + "\" Condition=\"" + cond + "\" WaitTime=\"3000\" Range=\"100000\">" + where + "</UseItem>");
                            hop.Lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"5\" />");
                            hop.Lines.Add("</If>");
                            if (heroic)
                            {
                                // The Korriban way: UseItem "Heroic Transport" took the shuttle there.
                                string shortCond = away + " and " + A(shortHave);
                                hop.Lines.Add("<If Condition=\"" + shortCond + "\">");
                                hop.Lines.Add("<UseItem ItemName=\"Heroic Transport\" Condition=\"" + shortCond + "\" WaitTime=\"3000\" Range=\"100000\">" + where + "</UseItem>");
                                hop.Lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"5\" />");
                                hop.Lines.Add("</If>");
                            }                            steps.Add(hop);
                            item = null;
                            rideAt = t;
                            haveTrail = true;
                            lastTrail = e.Pos;
                            break;
                        }
                        // a phase door / doorway you went through.
                        if (t - rideAt > 20 && e.HasFrom && !e.Note.StartsWith(How.Revived, StringComparison.Ordinal))
                        {
                            markers.Add(new KeyValuePair<string, Vec3>("Before the " + (e.Kind == EvKind.AreaChange ? "area change" : "door / phase"), e.FromPos));
                            markers.Add(new KeyValuePair<string, Vec3>("Came out here", e.Pos));
                        }
                        break;
                    case EvKind.CombatStart:
                        fightAt = t;
                        break;
                    case EvKind.Kill:
                        if (e.Note.Length == 0 && e.Target != null)
                        {
                            var k = new Kill { Name = e.Target.Name, Pos = e.Target.HasPos ? e.Target.Pos : e.Pos, At = t };
                            kills.Add(k);
                            allKills.Add(k);
                            if (lastWalk != null && t - lastWalkAt <= 2 && steps.Contains(lastWalk))
                            {
                                // the count that ticked just before was this kill: fight it there instead of walking
                                lastWalk.Kind = "Grind";
                                lastWalk.Kills = new List<Kill> { k };
                                lastWalk.Lines.RemoveAll(l => l.StartsWith("<MoveTo", StringComparison.Ordinal) && l.Contains("Where it ticked"));
                            }
                            lastWalk = null;
                        }
                        break;
                    case EvKind.QuestAdded:
                    {
                        uses.Clear(); // the click on the giver / board was the pick-up
                        if (e.QuestHidden || e.QuestId == 0)
                            break;
                        string q = Fmt.Hex(e.QuestId);
                        picked.Add(e.QuestId);
                        // a weekly comes back (3 times a week): only "not in my log", like the Oricon weekly. Dailies and heroics: not in my log and not done.
                        bool repeat = Regex.IsMatch(e.QuestName ?? string.Empty, @"^\[(WEEKLY|DAILY|HEROIC|AREA)\b", RegexOptions.IgnoreCase);
                        bool isWeekly = repeat && (e.QuestName ?? string.Empty).StartsWith("[WEEKLY", StringComparison.OrdinalIgnoreCase);
                        var s = new Step { Kind = "Pickup", Gate = isWeekly ? "(not HasQuest(" + q + "))" : "(not HasQuest(" + q + ")) and (not IsQuestComplete(" + q + "))", Comment = "Pick up " + e.QuestName };
                        if (isWeekly)
                            weeklies.Add(e.QuestId);
                        // put down to the mission board, but you were talking to someone after opening it: they gave it
                        // (End of Torvix comes from General Konya, not the Black Hole Mission Terminal)
                        // the giver is whatever you clicked right before it landed in your log
                        ObjRef giver = lastClicked != null && t - lastClickedAt <= 60 ? lastClicked : e.Target;
                        if (giver != null && giver.Name.Length > 0)
                        {
                            Vec3 gp = giver.HasPos ? giver.Pos : e.Pos;
                            // dailies / weeklies / heroics: the giver stops offering them once done today, and BuddyCron's
                            // PickUpQuest would click it forever - PickUpIfOffered gives up after 3 tries.
                                s.Lines.Add("<" + (repeat ? "PickUpIfOffered" : "PickUpQuest") + " QuestName=\"" + A(e.QuestName) + "\" QuestId=\"" + q + "\"" + (giver.Kind == ObjKind.Placeable ? " SearchRadius=\"20\"" : string.Empty) +
                                            " UseRange=\"20\" GiverName=\"" + A(giver.Name) + "\" XYZ=\"" + X(gp) + "\" />");
                        }
                        else
                        {
                            s.Lines.Add("<!-- " + Fmt.XmlComment(e.QuestName + " - comes by itself, no giver.") + " -->");
                        }
                        steps.Add(s);
                        break;
                    }
                    case EvKind.QuestTask:
                    case EvKind.QuestStep:
                    {
                        if (e.QuestHidden || e.QuestId == 0)
                            break;
                        if (e.Kind == EvKind.QuestTask && e.Text.StartsWith("Complete ", StringComparison.OrdinalIgnoreCase))
                            break; // a weekly's "Complete X" ticks by itself
                        string q = Fmt.Hex(e.QuestId);
                        bool isCount = e.Kind == EvKind.QuestTask && e.Max > 0;
                        int task = e.TaskIndex >= 0 ? e.TaskIndex : 0;
                        string gate, what;
                        if (isCount)
                        {
                            gate = Fmt.Format("HasQuest({0}) and not IsTaskValueGreaterThan({0}, {1}, {2}, {3})", q, e.Step, task, e.Count > 0 ? e.Count - 1 : 0);
                            what = Fmt.Format("{0}: {1} ({2}/{3})", e.QuestName, e.Text, e.Count, e.Max);
                        }
                        else
                        {
                            ulong ob = e.HasOldStep ? e.OldBranch : e.Branch;
                            ulong os = e.HasOldStep ? e.OldStep : e.Step;
                            gate = Fmt.Format("HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})", q, ob, os);
                            what = Fmt.Format("{0}: step {2} done{3}", e.QuestName, ob, os, e.Text.Length > 0 ? " (next: " + e.Text + ")" : string.Empty);
                        }
                        // the same moment: another quest counting the same action, or this quest's task finishing its step
                        if (e.Kind == EvKind.QuestStep || (isCount && e.Count >= e.Max))
                            finalAt[e.QuestId] = t; // this tick finished the step / count
                        bool sameMoment = t - lastProgressAt <= 1.0;
                        if (sameMoment && (lastProgressQuest != e.QuestId || lastProgressKind == EvKind.QuestTask)) // another quest, or a second count / the step of this quest ticking for the same action (Section X Hyperbiology ticks two counts per sample)
                        {
                            // One kill counting for two quests (Control the Battlefield + Asset Liquidation): the fight
                            // keeps going until this quest's count is reached too.
                            Step fight = steps.Count > 0 ? steps[steps.Count - 1] : null;
                            if (fight != null && fight.Kind == "Grind" && fight.Quest != e.QuestId && kills.Count + (fight.Kills != null ? 1 : 0) > 0)
                            {
                                if (fight.Also == null)
                                    fight.Also = new Dictionary<ulong, string>();
                                fight.Also[e.QuestId] = gate;
                                string also = e.QuestName + ": " + (isCount ? e.Text : "step done");
                                if (fight.Comment.IndexOf(" + " + e.QuestName, StringComparison.Ordinal) < 0)
                                    fight.Comment += " + " + also;
                            }
                            else if (fight != null && fight.Kind == "Grind" && fight.Quest == e.QuestId && e.Kind == EvKind.QuestStep)
                            {
                                // the last kill finished the step: fight until the step is done, not one short of it
                                fight.Gate = gate;
                            }
                            lastProgressAt = t;
                            lastProgressQuest = e.QuestId;
                            lastProgressKind = e.Kind;
                            if (e.Text.Length > 0)
                                stepText[e.QuestId] = e.Text;
                            break;
                        }
                        lastProgressAt = t;
                        lastProgressQuest = e.QuestId;
                        lastProgressKind = e.Kind;
                        List<Use> orderNow = itemOrder; // mission items used since the last tick, in your order
                        itemOrder = new List<Use>();
                        // a step change carries the NEXT step's text: match objects against the step that just finished
                        string doneText;
                        if (e.Kind != EvKind.QuestStep || !stepText.TryGetValue(e.QuestId, out doneText))
                            doneText = e.Text;
                        if (e.Text.Length > 0)
                            stepText[e.QuestId] = e.Text;
                        // How an object is used, like the tested profiles.
                        string useIds = null;
                        if (isCount && e.Count >= e.Max)
                            useIds = Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\" TaskId=\"{3}\"", q, e.Branch, e.Step, task);
                        else if (!isCount)
                            useIds = Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\"", q, e.HasOldStep ? e.OldBranch : e.Branch, e.HasOldStep ? e.OldStep : e.Step);

                        var lines = new List<string>();
                        if (markers.Count > 0)
                        {
                            // Walk points are their own step.
                            var route = new Step { Kind = "Route", Comment = "Your route" };
                            AddRoute(route, markers);
                            steps.Add(route);
                            markers.Clear();
                        }

                        // what you clicked since the last progress, in your order, at the exact spots: that's the step
                        // (no guessing which object the task means from its name)
                        List<string> ow = Words(doneText);
                        var recent = uses.FindAll(u => t - u.At <= (isCount ? 30 : 180)); // a puzzle can take a few minutes
                        var named = new List<Use>();
                        bool keepRest = false;
                        List<Use> take = recent;
                        // a kill right before the tick and no object the objective names.
                        var spawners = new List<Use>();
                        if (named.Count == 0 && kills.Count > 0 && t - kills[kills.Count - 1].At <= 3 && (take.Count == 0 || t - take[take.Count - 1].At > 3))
                        {
                            // ...or an object you really used.
                            take = take.FindAll(u => kills.Exists(k => k.Pos.DistanceTo(u.Pos) <= (u.Cast ? 3f : 1f)) ||
                                                     (u.Cast && u.At <= fightAt + 1 && fightAt - u.At <= 20));
                            foreach (Use u in take)
                                if (!kills.Exists(k => k.Pos.DistanceTo(u.Pos) <= 3f))
                                    spawners.Add(u);
                            // one count, one object: the one the mob that just died came out of (pod 1 -> droid 1)
                            if (isCount && take.Count > 1)
                            {
                                Vec3 kp = kills[kills.Count - 1].Pos;
                                take.Sort((a, b) => a.Pos.DistanceTo(kp).CompareTo(b.Pos.DistanceTo(kp)));
                                keepRest = true;
                                take = new List<Use> { take[0] };
                            }
                        }
                        // A step finished after several objects.
                        if (!isCount && take.Count > 1)
                        {
                            var seq = new List<Use>();
                            foreach (Use u in take)
                                if (seq.Count == 0 || X(seq[seq.Count - 1].Pos) != X(u.Pos))
                                    seq.Add(u);
                            for (int si = 0; si < seq.Count - 1; si++)
                                if (!spawners.Contains(seq[si]))
                                    spawners.Add(seq[si]);
                            foreach (Use u in take)
                                if (!seq.Contains(u))
                                    uses.Remove(u);
                            take = seq;
                        }
                        // A puzzle: 6+ clicks in this one step on the same object (one spot, like the End of Torvix
                        // pipeline controls): the bot can't solve it, so it walks to that exact object and pauses for you.
                        // Decided by where you clicked, never by the object's name.
                        if (!isCount && take.Count >= 6 && take.TrueForAll(u => u.Pos.DistanceTo(take[0].Pos) <= 2f))
                        {
                            Use at = take[take.Count - 1];
                            lines.Add("<MoveTo QuestId=\"1\" Name=\"" + A(at.Name) + "\" XYZ=\"" + X(at.Stand.IsZero ? at.Pos : at.Stand) + "\" />");
                            lines.Add("<UserDialog Title=\"Quest Recorder\" Message=\"" + A("Do the " + at.Name + " by hand, then press Continue.") + "\" />");
                            foreach (Use u in take)
                            {
                                consumed[X(u.Pos)] = t;
                                uses.Remove(u);
                            }
                            AddStep(steps, "Use", gate, what + " (do it by hand)", lines, e.QuestId);
                            break;
                        }
                        // Every use in the order you did it, tied to the quest so it can never loop.
                        string countIds = isCount
                            ? Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\" TaskId=\"{3}\" TaskValue=\"{4}\"", q, e.Branch, e.Step, task, e.Count > 0 ? e.Count - 1 : 0)
                            : useIds;
                        foreach (Use u in take)
                        {
                            if (spawners.Contains(u))
                            {
                                // the object only starts the fight.
                                if (!u.Stand.IsZero && u.Stand.DistanceTo(u.Pos) > 1.5f && u.Stand.DistanceTo(u.Pos) < 10f)
                                    lines.Add("<MoveTo QuestId=\"1\" Name=\"Stand for " + A(u.Name) + "\" XYZ=\"" + X(u.Stand) + "\" />");
                                lines.Add("<Interact QuestId=\"" + q + "\" Name=\"" + A(u.Name) + "\" XYZ=\"" + X(u.Pos) + "\" />");
                                consumed[X(u.Pos)] = t;
                                uses.Remove(u);
                                continue;
                            }
                            lines.AddRange(UseAt(u.Name, u.Pos, u.Stand, countIds));
                            consumed[X(u.Pos)] = t;
                            uses.Remove(u);
                        }
                        if (named.Count == 0 && !keepRest)
                            uses.Clear();

                        bool killCause = kills.Count > 0 && t - kills[kills.Count - 1].At <= 3;
                        // an object use that ended within 3 s of the tick did it, even if a mob died at the same moment
                        if (take.Count > 0 && t - take[take.Count - 1].At <= 3)
                            killCause = false;
                        if (killCause)
                        {
                            // a boss step keeps every mob from the whole fight, in the order you killed them.
                            double from = Math.Min(fightAt, t - 3);
                            var use = kills.FindAll(k => isCount || k.At >= from - 1);
                            Step prev = steps.Count > 0 ? steps[steps.Count - 1] : null;
                            if (isCount && lines.Count == 0 && prev != null && prev.Kind == "Grind" && prev.Quest == e.QuestId && prev.Task == e.TaskIndex)
                            {
                                prev.Gate = gate; // the same kill count going on: its stop point moves up
                                prev.Comment = what;
                                prev.Kills.AddRange(use);
                            }
                            else
                            {
                                var g = new Step { Kind = "Grind", Gate = gate, Comment = what, Quest = e.QuestId, Task = e.TaskIndex, Kills = use };
                                g.Lines.AddRange(lines);
                                steps.Add(g);
                            }
                            kills.Clear();
                            break;
                        }
                        // kept for a count you got by looting what the mobs dropped (the tick comes after the loot, not the kill)
                        List<Kill> lootKills = kills.FindAll(k => t - k.At <= 20);
                        kills.Clear();
                        if (item != null && t - itemAt <= 20)
                        {
                            // a quest item you used right before (UseItem fires it at the spot you used it)
                            string itemIds = isCount
                                ? Fmt.Format(" BranchId=\"{0}\" StepId=\"{1}\" TaskId=\"{2}\" TaskValue=\"{3}\"", e.Branch, e.Step, task, e.Count > 0 ? e.Count - 1 : 0) // done as soon as this count goes up
                                : Fmt.Format(" BranchId=\"{0}\" StepId=\"{1}\"", e.HasOldStep ? e.OldBranch : e.Branch, e.HasOldStep ? e.OldStep : e.Step);
                            Vec3 usedAt = itemPos.IsZero ? e.Pos : itemPos;
                            // used on an NPC (a scanner on a spy): on the nearest one of that name around the spot, like the Korriban ID Scanner (1.5 m, radius 200)
                            string onNpc = itemOn != null ? " Range=\"15\" Radius=\"200\" InCombat=\"False\"><Npcs><Npc>" + A(itemOn) + "</Npc></Npcs>" : ">";
                            // several items used one after another with nothing ticking in between (the Sith runes at the
                            // altar): each one once, in your order, then the last one as usual
                            var earlierUses = orderNow.FindAll(u => t - u.At <= 120 && !u.Name.StartsWith("Heroic Transport", StringComparison.Ordinal));
                            if (earlierUses.Count > 0)
                                earlierUses.RemoveAt(earlierUses.Count - 1); // the last one is the UseItem below
                            if (earlierUses.Exists(u => u.Name != item))
                            {
                                Vec3 lastSpot = default(Vec3);
                                foreach (Use u in earlierUses)
                                {
                                    if (lastSpot.IsZero || lastSpot.DistanceTo(u.Pos) > 5f)
                                    {
                                        lines.Add("<MoveTo QuestId=\"" + q + "\" Name=\"Where you used " + A(u.Name) + "\" XYZ=\"" + X(u.Pos) + "\" />");
                                        lastSpot = u.Pos;
                                    }
                                    lines.Add("<UseMissionItemOnce ItemName=\"" + A(u.Name) + "\" />");
                                }
                                // the last one once too: if the order went wrong, the whole row is tried again next round
                                // instead of pressing one rune forever
                                lines.Add("<UseMissionItemOnce ItemName=\"" + A(item) + "\" />");
                                lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"3\" />");
                            }
                            else
                                lines.Add("<UseItem QuestId=\"" + q + "\"" + itemIds + " ItemName=\"" + A(item) + "\" WaitTime=\"3000\" XYZ=\"" + X(usedAt) + "\"" + onNpc +
                                          "<Hotspots><Hotspot Name=\"Where you used it\" XYZ=\"" + X(usedAt) + "\" /></Hotspots></UseItem>");
                            itemForTask[q + "/" + e.Step + "/" + task] = item; // this task is done with this item
                            item = null;
                            AddStep(steps, "Item", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (take.Count > 0)
                        {
                            AddStep(steps, "Use", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (t - holoAt <= 20)
                        {
                            lines.Add("<AnswerHolocomCall QuestName=\"" + A(e.QuestName) + "\" QuestId=\"" + q + "\" />");
                            holoAt = -1e9;
                            AddStep(steps, "Holocom", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (talk != null && t - talkAt <= 20)
                        {
                            lines.Add("<" + (talk.Kind == ObjKind.Placeable ? "Interact" : "TalkTo") + " QuestId=\"" + q + "\" Name=\"" + A(talk.Name) + "\" WaitTime=\"5\" XYZ=\"" +
                                      X(talk.HasPos ? talk.Pos : e.Pos) + "\" />");
                            talk = null;
                            AddStep(steps, "Talk", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (isCount && lootKills.Count > 0)
                        {
                            // Story quests "Collect 6 datapads" style: you killed, then looted, then it ticked.
                            var lg = new Step { Kind = "Grind", Gate = gate, Comment = what + " (kill and loot)", Quest = e.QuestId, Task = e.TaskIndex, Kills = lootKills };
                            lg.Lines.AddRange(lines);
                            steps.Add(lg);
                            break;
                        }
                        if (isCount && npcClickName != null && t - npcClickAt <= 6 && killedNames.Contains(npcClickName))
                        {
                            // you looted the body of a mob you had killed.
                            var lk = new Step { Kind = "Grind", Gate = gate, Comment = what + " (kill and loot)", Quest = e.QuestId, Task = e.TaskIndex,
                                                Kills = new List<Kill> { new Kill { Name = npcClickName, Pos = npcClickPos, At = t } } };
                            lk.Lines.AddRange(lines);
                            steps.Add(lk);
                            npcClickName = null;
                            break;
                        }
                        // a count that ticked with no click of yours, where you did this same task's earlier counts with a
                        // mission item: the item again (what you did, not a guess from names)
                        string sameItem;
                        if (isCount && itemForTask.TryGetValue(q + "/" + e.Step + "/" + task, out sameItem))
                        {
                            lines.Add("<UseItem QuestId=\"" + q + "\"" + Fmt.Format(" BranchId=\"{0}\" StepId=\"{1}\" TaskId=\"{2}\" TaskValue=\"{3}\"", e.Branch, e.Step, task, e.Count > 0 ? e.Count - 1 : 0) +
                                      " ItemName=\"" + A(sameItem) + "\" WaitTime=\"3000\" XYZ=\"" + X(e.Pos) + "\"><Hotspots><Hotspot Name=\"Where it ticked\" XYZ=\"" + X(e.Pos) + "\" /></Hotspots></UseItem>");
                            AddStep(steps, "Item", gate, what + " (your " + sameItem + ")", lines, e.QuestId);
                            break;
                        }
                        // (no guessing from names: a tick with no click of yours just walks to where it ticked)
                        if (npcClickName != null && t - npcClickAt <= 30 && !killedNames.Contains(npcClickName) && !followers.Contains(npcClickName))
                        {
                            // you clicked an NPC (no conversation) and the count went up a bit later (Black Hole 'Identify.
                            lines.Add("<TalkTo QuestId=\"" + q + "\" Name=\"" + A(npcClickName) + "\" WaitTime=\"5\" XYZ=\"" + X(npcClickPos) + "\" />");
                            npcClickName = null;
                            AddStep(steps, "Talk", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (hiddenQuests.Contains(e.QuestId) && !isCount)
                            break; // a hidden helper quest moving on by itself (the game's own bookkeeping): nothing to walk to
                        lines.Add("<MoveTo QuestId=\"" + q + "\" Name=\"Where it ticked\" XYZ=\"" + X(e.Pos) + "\" />");
                        AddStep(steps, "Walk", gate, what, lines, e.QuestId);
                        if (isCount)
                        {
                            // the count can tick a moment BEFORE the game reports the kill.
                            lastWalk = steps[steps.Count - 1];
                            lastWalkAt = t;
                        }
                        break;
                    }
                    case EvKind.QuestRemoved:
                    {
                        // picked up and gone again without any progress.
                        ulong gone = e.QuestId;
                        if (gone != 0 && !steps.Exists(st => st.Quest == gone && st.Kind != "Pickup"))
                        {
                            steps.RemoveAll(st => st.Kind == "Pickup" && st.Gate != null && st.Gate.Contains(Fmt.Hex(gone)));
                            picked.Remove(gone);
                        }
                        break;
                    }
                    case EvKind.QuestCompleted:
                    {
                        if (e.QuestHidden || e.QuestId == 0)
                            break;
                        string q = Fmt.Hex(e.QuestId);
                        // Finished by your last kill or use, with no count tick in between.
                        double fin;
                        bool ticked = finalAt.TryGetValue(e.QuestId, out fin) && t - fin <= 3.0; // a finishing tick ~1.2 s before the reward window
                        if (!ticked && !(talk != null && t - talkAt <= 20))
                        {
                            string doneGate = "HasQuest(" + q + ") and not IsQuestComplete(" + q + ")";
                            Use lastUse = uses.Count > 0 && t - uses[uses.Count - 1].At <= 3 ? uses[uses.Count - 1] : null;
                            if (lastUse == null && uses.Count > 0)
                            {
                                // the last thing you clicked before the reward came up (no kill after it)
                                Use lastClick = uses.FindLast(u => t - u.At <= 60);
                                if (lastClick != null && (kills.Count == 0 || kills[kills.Count - 1].At < lastClick.At))
                                    lastUse = lastClick;
                            }
                            if (kills.Count > 0 && t - kills[kills.Count - 1].At <= 3 && (lastUse == null || lastUse.At < kills[kills.Count - 1].At))
                            {
                                double from = Math.Min(fightAt, t - 3);
                                var g = new Step { Kind = "Grind", Gate = doneGate, Comment = e.QuestName + ": last fight", Quest = e.QuestId,
                                                   Kills = kills.FindAll(k => k.At >= from - 1) };
                                // the thing you clicked that brought the boss out (Kregg's Locker draws Kregg out)
                                Use spawner = uses.FindLast(u => u.At <= fightAt + 1 && fightAt - u.At <= 15);
                                if (spawner != null)
                                {
                                    g.Lines.AddRange(UseAt(spawner.Name, spawner.Pos, spawner.Stand, " QuestId=\"" + q + "\""));
                                    g.Comment = e.QuestName + ": click " + spawner.Name + ", then the last fight";
                                    uses.Remove(spawner);
                                }
                                steps.Add(g);
                                kills.Clear();
                            }
                            else if (lastUse != null)
                            {
                                var u = new Step { Kind = "Use", Gate = doneGate, Comment = e.QuestName + ": last click", Quest = e.QuestId };
                                u.Lines.AddRange(UseAt(lastUse.Name, lastUse.Pos, lastUse.Stand, " QuestId=\"" + q + "\""));
                                steps.Add(u);
                                uses.Remove(lastUse);
                            }
                        }
                        var s = new Step { Kind = "Complete", Quest = e.QuestId, Comment = "Reward: " + e.QuestName };
                        // Handed in to someone you were talking to.
                        ObjRef by = null;
                        if (talk != null && t - talkAt <= 20)
                            by = talk;
                        else if (e.Target != null && e.Target.Name.Length > 0 && e.Note.IndexOf("clicked", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 e.Note.IndexOf("talk", StringComparison.OrdinalIgnoreCase) >= 0)
                            by = e.Target;
                        if (by != null)
                        {
                            s.Gate = "HasQuest(" + q + ") and not IsQuestComplete(" + q + ")";
                            s.Quest = e.QuestId;
                            s.Lines.Add("<TurnInQuest QuestName=\"" + A(e.QuestName) + "\" QuestId=\"" + q + "\"" + (by.Kind == ObjKind.Placeable ? " SearchRadius=\"20\" UseRange=\"20\"" : string.Empty) +
                                        " TurnInName=\"" + A(by.Name) + "\" XYZ=\"" + X(by.HasPos ? by.Pos : e.Pos) + "\" />");
                        }
                        // BuddyCron only takes a reward that is already up.
                        if (s.Gate == null)
                            s.Gate = "HasQuest(" + q + ") and not IsQuestComplete(" + q + ")";
                        s.Lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"5\" />");
                        s.Lines.Add("<CompleteQuest QuestId=\"" + q + "\" />");
                        steps.Add(s);
                        if (!completedOrder.Contains(e.QuestId))
                            completedOrder.Add(e.QuestId);
                        break;
                    }
                    case EvKind.InteractionOpen:
                        if (e.Target != null && e.Target.Name.Length > 0 && e.Option.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            questWindow = e.Target;
                            questWindowAt = t;
                        }
                        // a vendor you used: the profile's Vendors list, so the bot repairs / sells where you did
                        if (e.Option == "Vendor" && e.Target != null && e.Target.Name.Length > 0 && e.Target.HasPos && !vendors.ContainsKey(e.Target.Name))
                            vendors[e.Target.Name] = e.Target.Pos;
                        break;
                }
            }

            if (pendingRide != null)
                steps.Remove(pendingRide); // the recording ended before you came out of the lift
            foreach (Step made in steps)
                if (made.At < -1e8)
                    made.At = prevT;
            ApplyReplies(steps, convos, dark >= light ? "dark" : "light");
            // The game's own count for each task: if you stopped (or a tick got lost) before the end, the last step of
            // that task still runs until the task is done (19/20 when you stopped -> goes on to 20/20).
            var maxFor = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (RecEvent ev in events)
                if (ev.Kind == EvKind.QuestTask && ev.Max > 0 && !ev.QuestHidden)
                    maxFor[Fmt.Hex(ev.QuestId) + ", " + ev.Step + ", " + Math.Max(0, ev.TaskIndex)] = ev.Max;
            var lastOfTask = new Dictionary<string, Step>(StringComparer.Ordinal);
            foreach (Step s in steps)
            {
                if (s.Gate == null)
                    continue;
                foreach (Match m in Regex.Matches(s.Gate, @"IsTaskValueGreaterThan\((0x[0-9A-F]+, \d+, \d+), (\d+)\)"))
                    lastOfTask[m.Groups[1].Value] = s;
            }
            foreach (KeyValuePair<string, Step> kv in lastOfTask)
            {
                ulong max;
                if (!maxFor.TryGetValue(kv.Key, out max))
                    continue;
                Step s = kv.Value;
                // a later step already finishes this quest step: nothing to stretch
                string[] k3 = kv.Key.Split(new[] { ", " }, StringSplitOptions.None);
                string finish = @"IsBranchStepComplete\(" + Regex.Escape(k3[0]) + @", \d+, " + k3[1] + @"\)";
                int at = steps.IndexOf(s);
                if (steps.FindIndex(at + 1, n => n.Gate != null && Regex.IsMatch(n.Gate, finish)) >= 0)
                    continue;
                string full = "IsTaskValueGreaterThan(" + kv.Key + ", " + (max - 1) + ")";
                s.Gate = Regex.Replace(s.Gate, @"IsTaskValueGreaterThan\(" + Regex.Escape(kv.Key) + @", \d+\)", full);
                if (s.Also != null)
                    foreach (ulong k in new List<ulong>(s.Also.Keys))
                        s.Also[k] = Regex.Replace(s.Also[k], @"IsTaskValueGreaterThan\(" + Regex.Escape(kv.Key) + @", \d+\)", full);
            }
            KeepBestOfRepeats(steps);
            MergeCountClicks(steps, scanner);
            // a fight roams every spot you killed those mobs at near it, not one spot where it camps the respawn
            foreach (Step s in steps)
            {
                if (s.Kind != "Grind" || s.Kills == null || s.Kills.Count == 0)
                    continue;
                var names = new HashSet<string>(s.Kills.ConvertAll(k => k.Name));
                var own = new List<Kill>(s.Kills);
                foreach (Kill k in allKills)
                    if (names.Contains(k.Name) && !s.Kills.Contains(k) && own.Exists(o => o.Pos.DistanceTo(k.Pos) <= 60f))
                        s.Kills.Add(new Kill { Name = k.Name, Pos = k.Pos, At = k.At });
            }
            if (trace != null)
            {
                FillTrace(steps, trace);
                return string.Empty;
            }
            // Old recordings: name each Heroic Transport after the heroic you did next.
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind != "Ride" || !steps[i].Comment.Contains(HeroicMark))
                    continue;
                string mission = null;
                for (int j = i + 1; j < steps.Count && mission == null; j++)
                {
                    string title;
                    if (steps[j].Quest != 0 && questTitles.TryGetValue(steps[j].Quest, out title) && title.StartsWith("[HEROIC", StringComparison.OrdinalIgnoreCase))
                    {
                        int close = title.IndexOf(']');
                        mission = (close >= 0 ? title.Substring(close + 1) : title).Trim();
                    }
                }
                string full = mission != null && mission.Length > 0 ? "Heroic Transport: " + mission : "Heroic Transport";
                steps[i].Comment = steps[i].Comment.Replace(HeroicMark, full);
                if (steps[i].Gate != null)
                    steps[i].Gate = steps[i].Gate.Replace("'" + HeroicMark + "'", "'" + A(full.Replace("\\", "\\\\").Replace("'", "\\'")) + "'");
                for (int k = 0; k < steps[i].Lines.Count; k++)
                    steps[i].Lines[k] = steps[i].Lines[k].Replace("'" + HeroicMark + "'", "'" + A(full.Replace("\\", "\\\\").Replace("'", "\\'")) + "'")
                                                         .Replace(HeroicMark, A(full));
            }
            picked.RemoveAll(w => weeklies.Contains(w));
            return Render(steps, picked, profileName, fileName, recordingName, dark > light ? "Dark" : (light > dark ? "Light" : null), vendors, completedOrder);
        }

        /// <summary>Walk points collected so far become a route step before what comes next.</summary>
        private static void FlushRoute(List<Step> steps, List<KeyValuePair<string, Vec3>> markers)
        {
            if (markers.Count == 0)
                return;
            var route = new Step { Kind = "Route", Comment = "Your route" };
            AddRoute(route, markers);
            steps.Add(route);
            markers.Clear();
        }

        /// <summary>Writes walk points as route lines (lift rides become UseTransport).</summary>
        private static void AddRoute(Step route, List<KeyValuePair<string, Vec3>> markers)
        {
            foreach (KeyValuePair<string, Vec3> m in markers)
            {
                if (m.Key.StartsWith(RideKey, StringComparison.Ordinal))
                {
                    string[] wd = m.Key.Substring(RideKey.Length).Split('|');
                    route.Lines.Add("<UseTransport TransporterName=\"" + wd[0] + "\" Type=\"Wonkavator\" Option=\"" + wd[1] + "\" XYZ=\"" + X(m.Value) + "\" />");
                }
                else
                    route.Lines.Add("<MoveTo QuestId=\"1\" Name=\"" + A(m.Key) + "\" XYZ=\"" + X(m.Value) + "\" />");
            }
        }

        private static string LinkedUse(string name, Vec3 pos, string ids)
        {
            return LinkedUse(name, pos, ids, 20);
        }

        // BlacklistDuration 5: a use a fight cut short is tried again a few seconds later instead of after 30.
        private static string LinkedUse(string name, Vec3 pos, string ids, int useRange)
        {
            return "<UseObject ObjectName=\"" + A(name) + "\"" + ids + " UseRange=\"" + useRange.ToString(CultureInfo.InvariantCulture) +
                   "\" IgnoreLOS=\"True\" WaitTime=\"3000\" BlacklistDuration=\"5\" XYZ=\"" + X(pos) + "\" />";
        }

        /// <summary>One object use.</summary>
        private static List<string> UseAt(string name, Vec3 pos, Vec3 stand, string ids)
        {
            var l = new List<string>();
            float d = stand.IsZero ? 0f : stand.DistanceTo(pos);
            if (!stand.IsZero && (d > 1.5f || Math.Abs(stand.Y - pos.Y) > 1f) && d < 10f)
            {
                l.Add("<MoveTo QuestId=\"1\" Name=\"Stand for " + A(name) + "\" XYZ=\"" + X(stand) + "\" />");
                l.Add(LinkedUse(name, pos, ids, Math.Max(20, (int)Math.Ceiling((d + 1f) * 10f))));
            }
            else
                l.Add(LinkedUse(name, pos, ids, 20));
            return l;
        }

        private static string OneUse(string name, Vec3 pos)
        {
            return "<UseObject ObjectName=\"" + A(name) + "\" QuestId=\"1\" IgnoreQuestRelation=\"True\" UseRange=\"20\" IgnoreLOS=\"True\" WaitTime=\"3000\" XYZ=\"" + X(pos) + "\" />";
        }

        /// <summary>
        /// "Click 4 of these" the way the Oricon profiles do it: one UseObject with every spot you clicked one at, so the
        /// bot uses whichever is there (one exact spot breaks when that one has been used or hasn't respawned).
        /// </summary>
        private static void MergeCountClicks(List<Step> steps, Dictionary<string, List<Vec3>> scanner)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                Step s = steps[i];
                string name = SingleUseObject(s);
                if (name == null || s.Gate.IndexOf("IsTaskValueGreaterThan", StringComparison.Ordinal) < 0)
                    continue; // a run starts with a count
                // the run of steps for the same quest clicking the same kind of object
                var run = new List<Step> { s };
                for (int j = i + 1; j < steps.Count; j++)
                {
                    Step n = steps[j];
                    if (n.Kind == "Route")
                        continue;
                    if (n.Quest != s.Quest || SingleUseObject(n) != name)
                        break;
                    run.Add(n);
                    if (n.Gate.IndexOf("IsBranchStepComplete", StringComparison.Ordinal) >= 0)
                        break; // the click that finished the step ends the run
                }
                List<Vec3> scanned;
                if (!scanner.TryGetValue(name, out scanned))
                    scanned = new List<Vec3>();
                if (run.Count < 2 && scanned.Count == 0)
                    continue;
                Step last = run[run.Count - 1];
                var spots = new List<string>();
                var seenSpots = new List<Vec3>();
                foreach (Step r in run)
                {
                    string xyz = Attr(r.Lines[0], "XYZ");
                    Vec3 p;
                    if (!TryVec(xyz, out p) || seenSpots.Exists(v => v.DistanceTo(p) <= 2f))
                        continue;
                    seenSpots.Add(p);
                    spots.Add("<Hotspot Name=\"" + A(name) + "\" XYZ=\"" + xyz + "\" />");
                }
                // the scanner's extra spots of the same kind near where you clicked
                var clicked = new List<Vec3>(seenSpots);
                foreach (Vec3 p in scanned)
                {
                    if (seenSpots.Exists(v => v.DistanceTo(p) <= 2f) || !clicked.Exists(v => v.DistanceTo(p) <= 80f))
                        continue;
                    seenSpots.Add(p);
                    spots.Add("<Hotspot Name=\"" + A(name) + " (scanner)\" XYZ=\"" + X(p) + "\" />");
                }
                if (spots.Count < 2)
                    continue;
                string line = last.Lines[0].TrimEnd();
                if (line.EndsWith("/>", StringComparison.Ordinal))
                    line = line.Substring(0, line.Length - 2).TrimEnd();
                line = Regex.Replace(line, " Radius=\"[^\"]*\"", string.Empty) + " Radius=\"50\">";
                last.Lines[0] = line + "<Hotspots>" + string.Concat(spots.ToArray()) + "</Hotspots></UseObject>";
                last.Comment = last.Comment + " - any " + name + " (" + spots.Count + " spots)";
                foreach (Step r in run)
                    if (r != last)
                        steps.Remove(r);
                i = steps.IndexOf(last);
            }
        }

        /// <summary>The object name when the step is one plain UseObject for a count, else null.</summary>
        private static string SingleUseObject(Step s)
        {
            if (s.Kind != "Use" || s.Gate == null || s.Lines.Count != 1 || (s.Gate.IndexOf("IsTaskValueGreaterThan", StringComparison.Ordinal) < 0 && s.Gate.IndexOf("IsBranchStepComplete", StringComparison.Ordinal) < 0))
                return null;
            string l = s.Lines[0];
            if (!l.StartsWith("<UseObject", StringComparison.Ordinal) || !l.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                return null;
            string n = Attr(l, "ObjectName");
            return n.Length > 0 ? n : null;
        }

        private static bool TryVec(string xyz, out Vec3 v)
        {
            v = default(Vec3);
            string[] p = (xyz ?? string.Empty).Split(',');
            float x, y, z;
            if (p.Length != 3 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return false;
            v = new Vec3(x, y, z);
            return true;
        }

        /// <summary>
        /// A step done twice (a carried-on recording, or a daily done again) is written once: in the place it first
        /// came, with the best way you did it. One that only walks to where it ticked loses to one that does something;
        /// otherwise the newest wins (newer recordings catch more).
        /// </summary>
        private static void KeepBestOfRepeats(List<Step> steps)
        {
            var first = new Dictionary<string, Step>(StringComparer.Ordinal);
            var drop = new HashSet<Step>();
            foreach (Step s in steps)
            {
                if (s.Gate == null || s.Kind == "Route" || s.Kind == "Ride")
                    continue;
                string key = s.Comment + "|" + s.Gate;
                Step keep;
                if (!first.TryGetValue(key, out keep))
                {
                    first[key] = s;
                    continue;
                }
                drop.Add(s);
                bool keepDoesSomething = keep.Kind != "Walk";
                bool thisDoesSomething = s.Kind != "Walk";
                if (thisDoesSomething || !keepDoesSomething)
                {
                    keep.Kind = s.Kind;
                    keep.Lines.Clear();
                    keep.Lines.AddRange(s.Lines);
                    keep.Kills = s.Kills;
                    keep.Task = s.Task;
                }
            }
            if (drop.Count > 0)
                steps.RemoveAll(drop.Contains);
        }

        private static void AddStep(List<Step> steps, string kind, string gate, string what, List<string> lines, ulong quest)
        {
            var s = new Step { Kind = kind, Gate = gate, Comment = what, Quest = quest };
            s.Lines.AddRange(lines);
            steps.Add(s);
        }

        private static string Render(List<Step> steps, List<ulong> picked, string profileName, string fileName, string recordingName, string side,
                                     Dictionary<string, Vec3> vendors, List<ulong> completedOrder)
        {
            var sb = new StringBuilder();
            const string nl = "\r\n";
            sb.Append("<Profile>").Append(nl);
            sb.Append("\t<!-- Made by Quest Recorder ").Append(QuestRecorderPlugin.Patch).Append(" - LethToxic. Recording: ").Append(Fmt.XmlComment(recordingName)).Append(" -->").Append(nl);
            string by = Fmt.OneLine(CreditName ?? string.Empty).Trim();
            if (by.Length > 0)
                sb.Append("\t<!-- Recorded by ").Append(Fmt.XmlComment(by.Length > 60 ? by.Substring(0, 60) : by)).Append(" -->").Append(nl);
            sb.Append("\t<Name>").Append(Fmt.XmlAttr(profileName)).Append("</Name>").Append(nl);
            if (side != null)
                sb.Append("\t<!-- You mostly picked ").Append(side.ToLowerInvariant()).Append(" replies. -->").Append(nl)
                  .Append("\t<ForceAlignment Type=\"").Append(side).Append("\" AutoSkip=\"True\" />").Append(nl);
            if (vendors.Count > 0)
            {
                sb.Append("\t<Vendors>").Append(nl);
                foreach (KeyValuePair<string, Vec3> v in vendors)
                    sb.Append("\t\t<Hotspot Name=\"").Append(A(v.Key)).Append("\" XYZ=\"").Append(X(v.Value)).Append("\" />").Append(nl);
                sb.Append("\t</Vendors>").Append(nl);
            }
            sb.Append(nl);
            sb.Append("\t<Questing>").Append(nl);
            // Route steps: walk them only while the step they lead to is still to do.
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind == "Ride")
                {
                    // ride while on the near side and any quest with a step after the ride is unfinished
                    var open = new List<string>();
                    var qs = new HashSet<ulong>();
                    for (int j = i + 1; j < steps.Count; j++)
                    {
                        ulong q = steps[j].Quest;
                        if (q != 0 && qs.Add(q))
                            open.Add(Fmt.Format("(HasQuest({0}) and not IsQuestComplete({0}))", Fmt.Hex(q)));
                    }
                    if (open.Count == 0)
                    {
                        // a mission item hop at the very end (Heroic Transport from the Fleet, then you stopped): it only
                        // runs while you have the item, and the item goes away with its quest - keep it
                        if (string.IsNullOrEmpty(steps[i].Gate) || !steps[i].Comment.StartsWith("Mission item:", StringComparison.Ordinal))
                            steps[i].Kind = "Skip";
                    }
                    else
                        steps[i].Gate = string.IsNullOrEmpty(steps[i].Gate) ? "(" + string.Join(" or ", open) + ")" : steps[i].Gate + " and (" + string.Join(" or ", open) + ")";
                }
            }
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind != "Route")
                    continue;
                string next = null;
                for (int j = i + 1; j < steps.Count && next == null; j++)
                {
                    Step n = steps[j];
                    if (n.Gate != null && n.Kind != "Pickup" && n.Kind != "Route" && n.Kind != "Skip")
                        next = n.Gate;
                }
                steps[i].Gate = next;
                if (next == null)
                    steps[i].Kind = "Skip";
            }
            foreach (Step s in steps)
            {
                // nothing to do in it (e.g. a lift ride at the very end of the recording): leave it out - an empty step stops BuddyCron loading the profile
                if ((s.Kills == null || s.Kills.Count == 0) && s.Lines.TrueForAll(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith("<!--", StringComparison.Ordinal)))
                    continue;
                if (s.Kind == "Skip")
                    continue;
                sb.Append("\t\t<!-- ").Append(Fmt.XmlComment(s.Comment)).Append(" -->").Append(nl);
                if (s.Kind == "Grind")
                {
                    var anchors = new List<Kill>();
                    var mobs = new List<string>();
                    foreach (Kill k in s.Kills)
                    {
                        if (!mobs.Contains(k.Name))
                            mobs.Add(k.Name);
                        // every spot you killed at (two kills within 2 m are one spot)
                        bool near = false;
                        foreach (Kill a in anchors)
                        {
                            if (a.Pos.DistanceTo(k.Pos) <= 2f)
                            {
                                near = true;
                                break;
                            }
                        }
                        if (!near)
                            anchors.Add(k);
                    }
                    string fightGate = s.Gate;
                    if (s.Also != null && s.Also.Count > 0)
                    {
                        fightGate = "(" + s.Gate + ")";
                        foreach (string g in s.Also.Values)
                            fightGate += " or (" + g + ")";
                    }
                    sb.Append("\t\t<If Condition=\"").Append(fightGate).Append("\">").Append(nl);
                    foreach (string l in s.Lines)
                        sb.Append("\t\t\t").Append(l).Append(nl);
                    sb.Append("\t\t\t<Grind While=\"").Append(fightGate).Append("\">").Append(nl);
                    sb.Append("\t\t\t\t<GrindArea Name=\"").Append(A(s.Comment)).Append("\">").Append(nl);
                    sb.Append("\t\t\t\t\t<MinLevel>1</MinLevel>").Append(nl).Append("\t\t\t\t\t<MaxLevel>100</MaxLevel>").Append(nl).Append("\t\t\t\t\t<Mobs>");
                    foreach (string m in mobs)
                        sb.Append("<Mob>").Append(A(m)).Append("</Mob>");
                    sb.Append("</Mobs>").Append(nl).Append("\t\t\t\t\t<Hotspots>").Append(nl);
                    foreach (Kill a in anchors)
                        sb.Append("\t\t\t\t\t\t<Hotspot Name=\"").Append(A(a.Name)).Append("\" XYZ=\"").Append(X(a.Pos)).Append("\" />").Append(nl);
                    sb.Append("\t\t\t\t\t</Hotspots>").Append(nl).Append("\t\t\t\t</GrindArea>").Append(nl).Append("\t\t\t</Grind>").Append(nl).Append("\t\t</If>").Append(nl);
                }
                else if (s.Gate != null)
                {
                    sb.Append("\t\t<If Condition=\"").Append(s.Gate).Append("\">").Append(nl);
                    foreach (string l in s.Lines)
                        sb.Append("\t\t\t").Append(l).Append(nl);
                    sb.Append("\t\t</If>").Append(nl);
                }
                else
                {
                    foreach (string l in s.Lines)
                        sb.Append("\t\t").Append(l).Append(nl);
                }
            }
            if (completedOrder.Count > 0)
            {
                // last check: any reward window still open (a fight can hold one back; the weekly's comes last)
                sb.Append("\t\t<!-- Grab any rewards still waiting -->").Append(nl);
                sb.Append("\t\t<WaitTimer QuestId=\"1\" WaitTime=\"5\" />").Append(nl);
                foreach (ulong q in completedOrder)
                    sb.Append("\t\t<CompleteQuest QuestId=\"").Append(Fmt.Hex(q)).Append("\" />").Append(nl);
            }
            if (picked.Count > 0 && fileName.Length > 0)
            {
                // a use that did not register leaves its quest unfinished: go round again (finished steps are skipped)
                var conds = new List<string>();
                foreach (ulong q in picked)
                {
                    string h = Fmt.Hex(q);
                    string c = "(HasQuest(" + h + ") and not IsQuestComplete(" + h + "))";
                    if (!conds.Contains(c))
                        conds.Add(c);
                }
                sb.Append("\t\t<!-- Not finished? Go round again (done steps get skipped) -->").Append(nl);
                sb.Append("\t\t<If Condition=\"").Append(string.Join(" or ", conds)).Append("\">").Append(nl);
                sb.Append("\t\t\t<WaitTimer QuestId=\"1\" WaitTime=\"3\" />").Append(nl);
                sb.Append("\t\t\t<LoadProfile Path=\"").Append(A(fileName)).Append("\" />").Append(nl);
                sb.Append("\t\t</If>").Append(nl);
            }
            sb.Append("\t</Questing>").Append(nl).Append("</Profile>").Append(nl);
            return sb.ToString();
        }
    }
}
