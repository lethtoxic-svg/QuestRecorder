// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Text;
using Reborn.Utilities.XmlEngine;

namespace QuestRecorder
{
    internal sealed class DraftInfo
    {
        public string Place = string.Empty;
        public DateTime Started;
        public DateTime Ended;
        public string BaseName = string.Empty;
        public bool Final;
        public bool Truncated;
    }

    /// <summary>Which profile tags this BuddyCron knows (its XmlEngine element map).</summary>
    internal sealed class TagCheckResult
    {
        public bool Ran;
        public string Problem = string.Empty;
        public HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal);
    }

    internal static class TagCheck
    {
        private static TagCheckResult _cached;

        /// <summary>The last successful check, or null.</summary>
        public static TagCheckResult Cached
        {
            get { return _cached; }
        }

        /// <summary>Builds BuddyCron's element map.</summary>
        public static TagCheckResult Run()
        {
            TagCheckResult c = _cached;
            if (c != null)
                return c;
            var r = new TagCheckResult();
            try
            {
                var engine = new XmlEngine(null, false);
                Dictionary<string, Type> map = engine.TypeMap;
                if (map == null || map.Count == 0)
                {
                    r.Problem = "BuddyCron's tag list was empty";
                }
                else
                {
                    foreach (string k in map.Keys)
                        r.Known.Add(k);
                    r.Ran = true;
                    _cached = r;
                }
            }
            catch (Exception ex)
            {
                r.Problem = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }
    }

    /// <summary>Turns the event list into a DRAFT questing profile in the sample-profile tag syntax.</summary>
    internal sealed class DraftBuilder
    {
        // ------------------------------------------------------------------ model

        private enum ActKind
        {
            Crumb,
            Marker,
            Taxi,
            Elevator,
            Jump,
            Ship,
            Loading,
            Area,
            Click,
            NpcClick,
            Talk,
            Holocom,
            Item,
            Cast,
            Kill,
            Died,
            Note,
            Loot,
        }

        private sealed class Act
        {
            public ActKind Kind;
            public RecEvent Ev;
            public double At;
            public double EndAt = double.NaN;
            public bool Used;
            public bool Consumed;
            public string Text = string.Empty;
        }

        private enum CauseKind
        {
            None,
            Talk,
            Holocom,
            Click,
            NpcClick,
            Item,
            Cast,
            Kills,
        }

        private sealed class Cause
        {
            public CauseKind Kind;
            public Act Act;
            public List<RecEvent> Kills;
        }

        private sealed class Line
        {
            public string Text;
            public ulong Area;
            public string AreaName = string.Empty;
            /// <summary>When set, <see cref="Text"/> is an If and these lines go inside it.</summary>
            public List<Line> Inner;
            /// <summary>This line moves you to another area: always write its block with area Ifs.</summary>
            public bool ForceArea;
        }

        private sealed class Block
        {
            public bool IsStep;
            public ulong Branch;
            public ulong Step;
            public string Comment = string.Empty;
            public readonly List<Line> Lines = new List<Line>();
            public readonly HashSet<string> Grinds = new HashSet<string>(StringComparer.Ordinal);
        }

        private enum ItemKind
        {
            QuestGroup,
            Pickup,
            Note,
            Free,
        }

        private sealed class Item
        {
            public ItemKind Kind;
            public ulong QuestId;
            public string Comment = string.Empty;
            public readonly List<Block> Blocks = new List<Block>();
            public readonly List<Line> Lines = new List<Line>();
        }

        private sealed class GrindDef
        {
            public string Key = string.Empty;
            public string Name = string.Empty;
            public ulong QuestId;
            public ulong Branch;
            public ulong Step;
            public int Task;
            public readonly List<RecEvent> Kills = new List<RecEvent>();
            public readonly HashSet<int> Seqs = new HashSet<int>();
        }

        private sealed class VendorDef
        {
            public string Name = string.Empty;
            public Vec3 Pos;
            public string AreaName = string.Empty;
        }

        // ------------------------------------------------------------------ state

        private const string Q1 = "1";

        private readonly IList<RecEvent> _source;
        private readonly DraftInfo _info;
        private readonly TagCheckResult _tags;
        private readonly string _stand;
        private readonly bool _standFellBack;
        private readonly BreadcrumbsInDraft _crumbs;
        private readonly int _wait;

        private readonly List<Act> _pending = new List<Act>();
        private readonly List<Item> _items = new List<Item>();
        private readonly List<GrindDef> _grinds = new List<GrindDef>();
        private readonly HashSet<string> _grindNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<VendorDef> _vendors = new List<VendorDef>();
        private readonly Dictionary<string, string> _stepText = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _usedTags = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, string> _questNames = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _questFqns = new Dictionary<ulong, string>();

        private readonly List<Act> _recentKills = new List<Act>();

        // Reply picks on alignment wheels: the side you mostly picked switches ForceAlignment on.
        private int _darkPicks;
        private int _lightPicks;
        private Act _talk;
        private Act _holo;
        private Cause _lastCause;
        private double _lastCauseAt = -1e9;
        private int _checkFailures;
        private int _questTriggers;

        private DraftBuilder(IList<RecEvent> events, DraftInfo info, RrSettings cfg, TagCheckResult tags)
        {
            _source = events;
            _info = info;
            _tags = tags;
            _crumbs = cfg.DraftBreadcrumbs;
            _wait = cfg.DraftWaitTime;
            bool wantNoCombat = cfg.DraftStandTag == StandTag.NoCombatMoveTo;
            bool known = tags == null || !tags.Ran || tags.Known.Contains("NoCombatMoveTo");
            _stand = wantNoCombat && known ? "NoCombatMoveTo" : "MoveTo";
            _standFellBack = wantNoCombat && !known;
        }

        public static string Build(IList<RecEvent> events, DraftInfo info, RrSettings cfg, TagCheckResult tags)
        {
            var b = new DraftBuilder(events, info, cfg, tags);
            b.Walk();
            return b.Render();
        }

        // ------------------------------------------------------------------ walk the events

        private void Walk()
        {
            foreach (RecEvent e in _source)
            {
                if (e.QuestId == 0)
                    continue;
                if (e.QuestName.Length > 0 && !_questNames.ContainsKey(e.QuestId))
                    _questNames[e.QuestId] = e.QuestName;
                if (e.QuestFqn.Length > 0 && !_questFqns.ContainsKey(e.QuestId))
                    _questFqns[e.QuestId] = e.QuestFqn;
            }
            foreach (RecEvent e in Reorder(_source))
            {
                switch (e.Kind)
                {
                    case EvKind.Breadcrumb:
                        Add(ActKind.Crumb, e);
                        break;
                    case EvKind.Marker:
                        Add(ActKind.Marker, e);
                        break;
                    case EvKind.TaxiOpen:
                    case EvKind.ElevatorOpen:
                    case EvKind.BoardOpen:
                        ConsumeClick(e);
                        break;
                    case EvKind.InteractionOpen:
                        ConsumeClick(e);
                        if (e.Option == "Vendor")
                            AddVendor(e);
                        break;
                    case EvKind.TaxiEnd:
                        Add(ActKind.Taxi, e);
                        break;
                    case EvKind.ElevatorRide:
                        Add(ActKind.Elevator, e);
                        break;
                    case EvKind.Jump:
                        Add(ActKind.Jump, e);
                        break;
                    case EvKind.ShipTravel:
                        Add(ActKind.Ship, e);
                        break;
                    case EvKind.Loading:
                        Add(ActKind.Loading, e);
                        break;
                    case EvKind.AreaChange:
                        Add(ActKind.Area, e);
                        break;
                    case EvKind.ObjectClick:
                        Add(ActKind.Click, e);
                        break;
                    case EvKind.NpcClick:
                        Add(ActKind.NpcClick, e);
                        break;
                    case EvKind.ConvoStart:
                        ConsumeClick(e);
                        if (e.Note == How.HolocomCall)
                            _holo = Add(ActKind.Holocom, e);
                        else
                            _talk = Add(ActKind.Talk, e);
                        break;
                    case EvKind.ConvoEnd:
                        if (e.Note == How.HolocomCall)
                        {
                            if (_holo != null && double.IsNaN(_holo.EndAt))
                                _holo.EndAt = e.Elapsed;
                        }
                        else if (_talk != null && double.IsNaN(_talk.EndAt))
                        {
                            _talk.EndAt = e.Elapsed;
                        }
                        break;
                    case EvKind.ConvoChoice:
                        if (!e.OptionGuessed && e.Option.EndsWith(" dark", StringComparison.Ordinal))
                            _darkPicks++;
                        else if (!e.OptionGuessed && e.Option.EndsWith(" light", StringComparison.Ordinal))
                            _lightPicks++;
                        // only wheels where your pick is not the bot's dark auto-pick need attention.
                        if (e.CheckFailed)
                            AddNote(e, "[!] Reply wheel" + (e.Target != null ? " with " + e.Target.DisplayName : string.Empty) +
                                       ": you picked reply " + e.Option + " (" + e.Note + "). If that reply matters for the quest, " +
                                       "this talk needs a hand-picked reply instead of the dark auto-pick.");
                        break;
                    case EvKind.QuestItem:
                        Add(ActKind.Item, e);
                        break;
                    case EvKind.Cast:
                        if (e.Target != null) // a cast next to an object: maybe that object was used
                            Add(ActKind.Cast, e);
                        else if (e.Option.StartsWith("Loot", StringComparison.OrdinalIgnoreCase))
                            Add(ActKind.Loot, e); // looting what you killed: a collect-from-mobs task
                        break;
                    case EvKind.Kill:
                        if (e.Note.Length == 0) // tagged by another player: no credit, not a hotspot
                        {
                            Act k = Add(ActKind.Kill, e);
                            _recentKills.Add(k);
                            while (_recentKills.Count > 0 && e.Elapsed - _recentKills[0].At > 60)
                                _recentKills.RemoveAt(0);
                        }
                        break;
                    case EvKind.QuestLog:
                        if (e.Quests != null)
                        {
                            foreach (QuestLine ql in e.Quests)
                            {
                                if (ql.Text.Length > 0)
                                    _stepText[StepKey(ql.QuestId, ql.Branch, ql.Step)] = ql.Text;
                            }
                        }
                        break;
                    case EvKind.Died:
                        Add(ActKind.Died, e);
                        break;
                    case EvKind.QuestAdded:
                        if (!e.QuestHidden)
                            Pickup(e);
                        break;
                    case EvKind.QuestStep:
                        if (!e.QuestHidden)
                            StepTrigger(e, e.OldBranch, e.OldStep, -1);
                        break;
                    case EvKind.QuestTask:
                        if (!e.QuestHidden)
                            StepTrigger(e, e.Branch, e.Step, e.TaskIndex);
                        break;
                    case EvKind.QuestCompleted:
                        if (!e.QuestHidden)
                            TurnIn(e);
                        break;
                    case EvKind.QuestAbandoned:
                        if (!e.QuestHidden)
                            AddNote(e, "You abandoned " + QuestText(e) + " here.");
                        break;
                    case EvKind.QuestRemoved:
                        if (!e.QuestHidden)
                            AddNote(e, "[?] " + QuestText(e) + " left the quest log here (completed or abandoned: the game did not say).");
                        break;
                    case EvKind.CharacterChange:
                        FlushFree("Recorded before the character change");
                        TopNote("[?] Character changed here (" + e.Note + "). What follows was recorded on that character.");
                        ResetTransient();
                        break;
                    case EvKind.Resumed:
                        FlushFree("Recorded before the bot ran");
                        TopNote("[?] The bot ran here (" + Fmt.Duration(e.DurationMs / 1000.0) + "). What it did is not in this draft.");
                        ResetTransient();
                        break;
                }
            }
            FlushFree("Recorded after the last quest update (not tied to a quest)");
        }

        /// <summary>Events of one look share a time.</summary>
        private static List<RecEvent> Reorder(IList<RecEvent> events)
        {
            var result = new List<RecEvent>(events.Count);
            int i = 0;
            while (i < events.Count)
            {
                int j = i + 1;
                while (j < events.Count && events[j].Time == events[i].Time)
                    j++;
                var slots = new List<int>();
                var quests = new List<RecEvent>();
                for (int k = i; k < j; k++)
                {
                    result.Add(events[k]);
                    if (IsQuestTrigger(events[k].Kind))
                    {
                        slots.Add(result.Count - 1);
                        quests.Add(events[k]);
                    }
                }
                if (quests.Count > 1)
                {
                    var sorted = new List<RecEvent>(quests.Count);
                    foreach (RecEvent q in quests)
                    {
                        if (q.Kind != EvKind.QuestAdded)
                            sorted.Add(q);
                    }
                    foreach (RecEvent q in quests)
                    {
                        if (q.Kind == EvKind.QuestAdded)
                            sorted.Add(q);
                    }
                    for (int k = 0; k < slots.Count; k++)
                        result[slots[k]] = sorted[k];
                }
                i = j;
            }
            return result;
        }

        private static bool IsQuestTrigger(EvKind k)
        {
            return k == EvKind.QuestAdded || k == EvKind.QuestStep || k == EvKind.QuestTask || k == EvKind.QuestCompleted;
        }

        private Act Add(ActKind kind, RecEvent e)
        {
            var a = new Act { Kind = kind, Ev = e, At = e.Elapsed };
            _pending.Add(a);
            return a;
        }

        private void AddNote(RecEvent e, string text)
        {
            Act a = Add(ActKind.Note, e);
            a.Text = text;
        }

        private void ResetTransient()
        {
            _recentKills.Clear();
            _talk = null;
            _holo = null;
            _lastCause = null;
            _lastCauseAt = -1e9;
        }

        /// <summary>A click that only opened a taxi map, an elevator, a board, a vendor or a conversation is not a step of its own.</summary>
        private void ConsumeClick(RecEvent e)
        {
            ulong id = e.Target != null ? e.Target.NodeId : 0;
            Act loose = null;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Act a = _pending[i];
                if (e.Elapsed - a.At > 10)
                    break;
                if ((a.Kind != ActKind.Click && a.Kind != ActKind.NpcClick) || a.Used || a.Consumed)
                    continue;
                if (id != 0 && a.Ev.Target != null && a.Ev.Target.NodeId == id)
                {
                    a.Consumed = true;
                    return;
                }
                if (loose == null && id == 0 && e.Elapsed - a.At <= 3)
                    loose = a; // the window did not say which object: take the click right before it
            }
            if (loose != null)
                loose.Consumed = true;
        }

        private void AddVendor(RecEvent e)
        {
            ObjRef t = e.Target;
            if (t == null || t.Name.Length == 0)
                return;
            Vec3 p = t.HasPos ? t.Pos : e.Pos;
            foreach (VendorDef v in _vendors)
            {
                if (v.Name == t.Name && v.Pos.DistanceTo(p) < 5f)
                    return;
            }
            _vendors.Add(new VendorDef { Name = t.Name, Pos = p, AreaName = e.AreaName });
        }

        // ------------------------------------------------------------------ triggers

        private void Pickup(RecEvent e)
        {
            _questTriggers++;
            RememberStepText(e);
            ObjRef giver = e.Target;
            if (giver == null)
            {
                string why = e.Note == How.Holocom
                    ? "came in a holocom call here. If BuddyCron has to answer it, add <AnswerHolocomCall QuestName=\"" +
                      Fmt.XmlAttr(QName(e)) + "\" QuestId=\"" + Fmt.Hex(e.QuestId) + "\" /> where it belongs."
                    : "was added to your log here without a giver (area entry, item or another quest?). No PickUpQuest written.";
                AddNote(e, QuestText(e) + " " + why);
                return;
            }

            Act related = RelatedAct(e, giver);
            List<Act> taken = TakeUpTo(related, related != null);
            if (related != null)
                related.Used = true;
            var item = new Item { Kind = ItemKind.Pickup, QuestId = e.QuestId };
            item.Comment = "Pickup " + QuestText(e) + (e.Note.Length > 0 ? " (" + e.Note + ")" : string.Empty);
            Travel(taken, item.Lines, Q1, false);

            if (giver.How.IndexOf("guess", StringComparison.OrdinalIgnoreCase) >= 0)
                item.Lines.Add(Comment("[?] giver: " + giver.How));
            if (e.Option.Length > 0)
                item.Lines.Add(Comment("[?] taken from mission board \"" + e.Option + "\": check GiverName and Type"));
            var sb = new StringBuilder();
            sb.Append("<PickUpQuest QuestName=\"").Append(Fmt.XmlAttr(QName(e))).Append("\" QuestId=\"").Append(Fmt.Hex(e.QuestId)).Append('"');
            if (giver.Kind == ObjKind.Placeable)
                sb.Append(" Type=\"Placeable\"");
            sb.Append(" GiverName=\"").Append(Fmt.XmlAttr(giver.Name)).Append("\" XYZ=\"").Append(Fmt.Xyz(PosOf(giver, e))).Append("\" />");
            item.Lines.Add(Tag("PickUpQuest", sb.ToString(), e));
            _items.Add(item);
        }

        private void TurnIn(RecEvent e)
        {
            _questTriggers++;
            var block = new Block { IsStep = false };
            ObjRef t = e.Target;
            if (t != null)
            {
                Act related = RelatedAct(e, t);
                List<Act> taken = TakeUpTo(related, related != null);
                if (related != null)
                    related.Used = true;
                Travel(taken, block.Lines, Fmt.Hex(e.QuestId), false);
                if (t.How.IndexOf("guess", StringComparison.OrdinalIgnoreCase) >= 0)
                    block.Lines.Add(Comment("[?] turn-in: " + t.How));
                var sb = new StringBuilder();
                sb.Append("<TurnInQuest QuestName=\"").Append(Fmt.XmlAttr(QName(e))).Append("\" QuestId=\"").Append(Fmt.Hex(e.QuestId)).Append('"');
                if (t.Kind == ObjKind.Placeable)
                    sb.Append(" Type=\"Placeable\"");
                sb.Append(" TurnInName=\"").Append(Fmt.XmlAttr(t.Name)).Append("\" XYZ=\"").Append(Fmt.Xyz(PosOf(t, e))).Append("\" />");
                block.Lines.Add(Tag("TurnInQuest", sb.ToString(), e));
            }
            else if (e.Note == How.Holocom)
            {
                block.Lines.Add(Tag("AnswerHolocomCall", Holocom(e), e));
            }
            else
            {
                block.Lines.Add(Comment("[?] completed here without a turn-in NPC or object (remote completion?)"));
            }
            block.Lines.Add(Tag("CompleteQuest", "<CompleteQuest QuestName=\"" + Fmt.XmlAttr(QName(e)) + "\" QuestId=\"" +
                                                 Fmt.Hex(e.QuestId) + "\" />", e));
            Group(e).Blocks.Add(block);
        }

        private void StepTrigger(RecEvent e, ulong branch, ulong step, int task)
        {
            // Weekly / area tasks such as "Complete Staff Cuts" tick by themselves when another quest finishes.
            if (e.Kind == EvKind.QuestTask && e.Text.StartsWith("Complete ", StringComparison.OrdinalIgnoreCase))
                return;
            _questTriggers++;
            if (e.CheckFailed)
                _checkFailures++;
            string q = Fmt.Hex(e.QuestId);
            Cause c = FindCause(e);
            bool reused = ReferenceEquals(c, _lastCause);
            if (c.Act != null)
                c.Act.Used = true; // before the travel lines, so the cause is not written twice
            if (c.Kind == CauseKind.Cast && c.Act.Ev.Target != null)
                UseClicksOn(c.Act.Ev.Target.NodeId, c.Act);

            Block block = null;
            if (c.Kind == CauseKind.Kills)
            {
                GrindDef going = FindGrind(e.QuestId, branch, step, task);
                if (going != null)
                    block = ContinuingGrindBlock(e.QuestId, branch, step, going.Key);
            }
            if (block == null)
                block = StepBlock(Group(e), branch, step);
            if (block.Comment.Length == 0)
            {
                string text;
                block.Comment = Fmt.Format("Branch {0} step {1}", branch, step);
                if (_stepText.TryGetValue(StepKey(e.QuestId, branch, step), out text) && text.Length > 0)
                    block.Comment += ": \"" + text + "\"";
            }
            if (e.CheckFailed)
                block.Lines.Add(Comment("[?] " + e.Check + ". The numbers below may be off."));

            GrindDef grind = null;
            bool grindHere = false;
            if (c.Kind == CauseKind.Kills)
            {
                grind = FindGrind(e.QuestId, branch, step, task);
                grindHere = grind != null && block.Grinds.Contains(grind.Key);
            }

            if (!reused)
            {
                List<Act> taken = TakeUpTo(c.Act, c.Kind != CauseKind.None);
                Travel(taken, block.Lines, q, grindHere);
            }

            string ids = Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\"", q, branch, step);
            string taskAttr = task >= 0 ? " TaskId=\"" + task.ToString() + "\"" : string.Empty;
            string wait = " WaitTime=\"" + _wait.ToString() + "\"";
            switch (c.Kind)
            {
                case CauseKind.Talk:
                {
                    ObjRef t = c.Act.Ev.Target;
                    if (t == null)
                    {
                        block.Lines.Add(Comment(Fmt.Format("[?] branch {0} step {1} changed during a conversation, but the recorder could not tell with whom", branch, step)));
                        block.Lines.Add(Stand(q, "Conversation here", c.Act.Ev.Pos, c.Act.Ev));
                        break;
                    }
                    if (t.How.IndexOf("guess", StringComparison.OrdinalIgnoreCase) >= 0)
                        block.Lines.Add(Comment("[?] talk partner: " + t.How));
                    if (t.Kind == ObjKind.Placeable)
                        block.Lines.Add(Tag("Interact", "<Interact" + ids + " Name=\"" + Fmt.XmlAttr(t.Name) + "\"" + wait + " XYZ=\"" +
                                                        Fmt.Xyz(PosOf(t, c.Act.Ev)) + "\" />", c.Act.Ev));
                    else
                        block.Lines.Add(Tag("TalkTo", "<TalkTo" + ids + " Name=\"" + Fmt.XmlAttr(t.Name) + "\"" + wait + " XYZ=\"" +
                                                      Fmt.Xyz(PosOf(t, c.Act.Ev)) + "\" />", c.Act.Ev));
                    break;
                }
                case CauseKind.Holocom:
                    block.Lines.Add(Tag("AnswerHolocomCall", Holocom(e), c.Act.Ev));
                    break;
                case CauseKind.Click:
                {
                    ObjRef t = c.Act.Ev.Target;
                    block.Lines.Add(UseObjectLine(e, t != null ? t.Name : "?", PosOf(t, c.Act.Ev), ids, taskAttr, wait, step, task, c.Act.Ev));
                    break;
                }
                case CauseKind.Cast:
                {
                    RecEvent ce = c.Act.Ev;
                    ObjRef t = ce.Target;
                    if (!CastFromClick(t))
                        block.Lines.Add(Comment("[?] ObjectName inferred from your cast \"" + ce.Option + "\" (" + Fmt.Duration(ce.DurationMs / 1000.0) +
                                                ") right before the update: " + t.How + ". Check the name."));
                    block.Lines.Add(UseObjectLine(e, t.Name, PosOf(t, ce), ids, taskAttr, wait, step, task, ce));
                    break;
                }
                case CauseKind.NpcClick:
                {
                    ObjRef t = c.Act.Ev.Target;
                    block.Lines.Add(Comment("[?] you clicked this NPC (no conversation) right before the update"));
                    block.Lines.Add(Tag("Interact", "<Interact" + ids + taskAttr + " Name=\"" + Fmt.XmlAttr(t != null ? t.Name : "?") + "\"" + wait +
                                                    " XYZ=\"" + Fmt.Xyz(PosOf(t, c.Act.Ev)) + "\" />", c.Act.Ev));
                    break;
                }
                case CauseKind.Item:
                {
                    RecEvent ie = c.Act.Ev;
                    var sb = new StringBuilder();
                    sb.Append("<UseItem").Append(ids).Append(taskAttr).Append(" ItemName=\"").Append(Fmt.XmlAttr(ie.Option)).Append('"');
                    if (ie.Target != null && ie.Target.Name.Length > 0)
                        sb.Append(" OnObject=\"").Append(Fmt.XmlAttr(ie.Target.Name)).Append('"');
                    sb.Append(wait).Append(" XYZ=\"").Append(Fmt.Xyz(ie.Pos)).Append("\" />");
                    block.Lines.Add(Tag("UseItem", sb.ToString(), ie));
                    break;
                }
                case CauseKind.Kills:
                    GrindLine(e, block, branch, step, task, c, grind);
                    break;
                default:
                    block.Lines.Add(Comment(Fmt.Format("[?] branch {0} step {1}{2} was done here without a talk, click, quest item or kill right before: probably \"go to a place\"",
                        branch, step, task >= 0 ? " task " + task.ToString() : string.Empty)));
                    block.Lines.Add(Stand(q, Fmt.Format("Step {0}/{1} done here", branch, step), e.Pos, e));
                    break;
            }

            if (c.Kind != CauseKind.None)
            {
                _lastCause = c;
                _lastCauseAt = e.Elapsed;
            }
            if (e.Kind == EvKind.QuestStep)
                RememberStepText(e);
        }

        /// <summary>One object use.</summary>
        private Line UseObjectLine(RecEvent e, string name, Vec3 pos, string ids, string taskAttr, string wait, ulong step, int task, RecEvent at)
        {
            bool partial = e.Kind == EvKind.QuestTask && e.Max > 0 && e.Count > 0 && e.Count < e.Max;
            // UseRange 20 = 2 m and IgnoreLOS.
            const string reach = " UseRange=\"20\" IgnoreLOS=\"True\"";
            if (!partial)
                return Tag("UseObject", "<UseObject ObjectName=\"" + Fmt.XmlAttr(name) + "\"" + ids + taskAttr + reach + wait + " XYZ=\"" + Fmt.Xyz(pos) + "\" />", at);
            string q = Fmt.Hex(e.QuestId);
            Line one = Tag("UseObject", "<UseObject ObjectName=\"" + Fmt.XmlAttr(name) + "\" QuestId=\"1\" IgnoreQuestRelation=\"True\"" + reach + wait +
                                        " XYZ=\"" + Fmt.Xyz(pos) + "\" />", at);
            var gate = new Line
            {
                Text = Fmt.Format("<If Condition=\"HasQuest({0}) and not IsTaskValueGreaterThan({0}, {1}, {2}, {3})\">", q, step, task >= 0 ? task : 0, e.Count - 1),
                Area = at != null ? at.AreaId : 0,
                AreaName = at != null ? at.AreaName : string.Empty,
                Inner = new List<Line> { one },
            };
            return gate;
        }

        private void RememberStepText(RecEvent e)
        {
            if (e.HasStep && e.Text.Length > 0)
                _stepText[StepKey(e.QuestId, e.Branch, e.Step)] = e.Text;
        }

        private static string StepKey(ulong q, ulong b, ulong s)
        {
            return q.ToString("X16") + ":" + b.ToString() + ":" + s.ToString();
        }

        /// <summary>The act that gave or took the quest (the conversation, the click, the board terminal click).</summary>
        private Act RelatedAct(RecEvent e, ObjRef who)
        {
            if (e.Note == How.Offered || e.Note == How.Talking)
                return _talk;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Act a = _pending[i];
                if (e.Elapsed - a.At > 30)
                    break;
                if ((a.Kind == ActKind.Click || a.Kind == ActKind.NpcClick) && a.Ev.Target != null && a.Ev.Target.NodeId == who.NodeId)
                    return a;
            }
            return null;
        }

        /// <summary>What made the quest move: the conversation going on.</summary>
        private Cause FindCause(RecEvent e)
        {
            double t = e.Elapsed;
            if (_lastCause != null && t - _lastCauseAt <= 1.0)
                return _lastCause;

            Cause best = null;
            double bestAt = double.NegativeInfinity;
            int bestRank = int.MaxValue;
            Act weakCast = null;

            Consider(TalkCause(_talk, t, CauseKind.Talk), ref best, ref bestAt, ref bestRank, t);
            Consider(TalkCause(_holo, t, CauseKind.Holocom), ref best, ref bestAt, ref bestRank, t);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Act a = _pending[i];
                double age = t - a.At;
                if (age > 30)
                    break;
                if (a.Used || a.Consumed || age < 0)
                    continue;
                switch (a.Kind)
                {
                    case ActKind.Click:
                        if (age <= 20)
                            Consider(new Cause { Kind = CauseKind.Click, Act = a }, ref best, ref bestAt, ref bestRank, a.At);
                        break;
                    case ActKind.NpcClick:
                        if (age <= 20)
                        {
                            List<RecEvent> loot = LootKills(a);
                            if (loot != null)
                                Consider(new Cause { Kind = CauseKind.Kills, Act = a, Kills = loot }, ref best, ref bestAt, ref bestRank, a.At);
                            else
                                Consider(new Cause { Kind = CauseKind.NpcClick, Act = a }, ref best, ref bestAt, ref bestRank, a.At);
                        }
                        break;
                    case ActKind.Item:
                        if (age <= 20)
                            Consider(new Cause { Kind = CauseKind.Item, Act = a }, ref best, ref bestAt, ref bestRank, a.At);
                        break;
                    case ActKind.Cast:
                        if (age <= 20)
                        {
                            // A cast at an object usable for a quest.
                            if (StrongCast(a))
                                Consider(new Cause { Kind = CauseKind.Cast, Act = a }, ref best, ref bestAt, ref bestRank, a.At);
                            else if (weakCast == null)
                                weakCast = a;
                        }
                        break;
                    case ActKind.Kill:
                        if (age <= 10)
                            Consider(new Cause { Kind = CauseKind.Kills, Act = a, Kills = KillsBefore(a.At, 10, t) }, ref best, ref bestAt, ref bestRank, a.At);
                        break;
                    case ActKind.Loot:
                        // A count that goes up right after you loot: the item came from the mobs you killed in the last minute.
                        if (age <= 20)
                        {
                            List<RecEvent> looted = KillsBefore(a.At, 60, t);
                            if (looted.Count == 0)
                            {
                                // kills already credited to another quest's count can still be where this item dropped
                                foreach (Act k in _recentKills)
                                    if (k.At >= a.At - 60 && k.At <= a.At)
                                        looted.Add(k.Ev);
                            }
                            if (looted.Count > 0)
                                Consider(new Cause { Kind = CauseKind.Kills, Act = a, Kills = looted }, ref best, ref bestAt, ref bestRank, a.At);
                        }
                        break;
                }
            }
            if (best == null && weakCast != null)
                best = new Cause { Kind = CauseKind.Cast, Act = weakCast };
            // A quest object you finished using a few seconds earlier beats a mob that happened to die in between.
            if (best != null && best.Kind == CauseKind.Kills)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    Act a = _pending[i];
                    double age = t - a.At; // a cast event is written when the cast ends
                    if (t - a.At > 30)
                        break;
                    if (a.Used || a.Consumed || a.Kind != ActKind.Cast || !StrongCast(a))
                        continue;
                    if (age >= -1 && age <= 8)
                    {
                        best = new Cause { Kind = CauseKind.Cast, Act = a };
                        break;
                    }
                }
            }
            return best ?? new Cause { Kind = CauseKind.None };
        }

        private static bool StrongCast(Act a)
        {
            ObjRef t = a.Ev.Target;
            return t != null && (t.UsableForQuest || t.How.StartsWith("you clicked", StringComparison.Ordinal));
        }

        /// <summary>The cast's object came from a click (exact), not from "nearest object" (inferred).</summary>
        private static bool CastFromClick(ObjRef t)
        {
            return t != null && t.How.StartsWith("you clicked", StringComparison.Ordinal);
        }

        /// <summary>Marks clicks on <paramref name="nodeId"/> shortly before <paramref name="cast"/> as used (the cast says it all).</summary>
        private void UseClicksOn(ulong nodeId, Act cast)
        {
            if (nodeId == 0)
                return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Act a = _pending[i];
                if (a.At > cast.At)
                    continue;
                if (cast.At - a.At > 30)
                    break;
                if ((a.Kind == ActKind.Click || a.Kind == ActKind.NpcClick) && a.Ev.Target != null && a.Ev.Target.NodeId == nodeId)
                    a.Used = true;
            }
        }

        private static Cause TalkCause(Act talk, double t, CauseKind kind)
        {
            if (talk == null || talk.At > t)
                return null;
            bool active = double.IsNaN(talk.EndAt);
            double limit = talk.Used ? 5 : 20;
            if (!active && t - talk.EndAt > limit)
                return null;
            return new Cause { Kind = kind, Act = talk };
        }

        private static void Consider(Cause c, ref Cause best, ref double bestAt, ref int bestRank, double at)
        {
            if (c == null)
                return;
            if ((c.Kind == CauseKind.Talk || c.Kind == CauseKind.Holocom) && double.IsNaN(c.Act.EndAt))
                at = double.PositiveInfinity; // a conversation still going on is the cause
            else if (c.Kind == CauseKind.Talk || c.Kind == CauseKind.Holocom)
                at = c.Act.EndAt;
            int rank = Rank(c.Kind);
            if (at > bestAt || (at == bestAt && rank < bestRank))
            {
                best = c;
                bestAt = at;
                bestRank = rank;
            }
        }

        private static int Rank(CauseKind k)
        {
            switch (k)
            {
                case CauseKind.Talk:
                    return 0;
                case CauseKind.Holocom:
                    return 1;
                case CauseKind.Item:
                    return 2;
                case CauseKind.Cast:
                    return 3;
                case CauseKind.Click:
                    return 4;
                case CauseKind.Kills:
                    return 5;
                default:
                    return 6;
            }
        }

        private List<RecEvent> KillsBefore(double at, double window, double until)
        {
            var list = new List<RecEvent>();
            foreach (Act a in _pending)
            {
                if (a.Kind == ActKind.Kill && !a.Used && a.At >= at - window && a.At <= until)
                    list.Add(a.Ev);
            }
            return list;
        }

        /// <summary>A click on a mob you just killed is looting: the kills are the cause (collect-from-mobs task).</summary>
        private List<RecEvent> LootKills(Act click)
        {
            ObjRef t = click.Ev.Target;
            if (t == null || t.NodeId == 0)
                return null;
            bool looted = false;
            foreach (Act a in _recentKills)
            {
                if (a.Ev.Target != null && a.Ev.Target.NodeId == t.NodeId && click.At - a.At <= 60 && click.At >= a.At)
                {
                    looted = true;
                    break;
                }
            }
            if (!looted)
                return null;
            var list = new List<RecEvent>();
            foreach (Act a in _recentKills)
            {
                if (a.At >= click.At - 30 && a.At <= click.At)
                    list.Add(a.Ev);
            }
            return list;
        }

        /// <summary>Removes and returns the pending acts up to and including <paramref name="upTo"/>.</summary>
        private List<Act> TakeUpTo(Act upTo, bool onlyIfPending)
        {
            int idx;
            if (upTo == null)
            {
                if (onlyIfPending)
                    return new List<Act>();
                idx = _pending.Count - 1;
            }
            else
            {
                idx = _pending.IndexOf(upTo);
                if (idx < 0)
                    return new List<Act>(); // already written with an earlier update
            }
            if (idx < 0)
                return new List<Act>();
            List<Act> taken = _pending.GetRange(0, idx + 1);
            _pending.RemoveRange(0, idx + 1);
            return taken;
        }

        // ------------------------------------------------------------------ grinds

        private GrindDef FindGrind(ulong q, ulong b, ulong s, int task)
        {
            string key = GrindKey(q, b, s, task);
            GrindDef any = null;
            foreach (GrindDef g in _grinds)
            {
                if (g.Key == key)
                    return g;
                if (task < 0 && g.QuestId == q && g.Branch == b && g.Step == s)
                    any = g;
            }
            return any;
        }

        /// <summary>The block that already grinds key for this quest, when nothing but other grinds.</summary>
        private Block ContinuingGrindBlock(ulong q, ulong b, ulong s, string key)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                Item it = _items[i];
                if (it.Kind != ItemKind.QuestGroup)
                    return null;
                if (it.QuestId == q)
                {
                    if (it.Blocks.Count == 0)
                        return null;
                    Block last = it.Blocks[it.Blocks.Count - 1];
                    return last.IsStep && last.Branch == b && last.Step == s && last.Grinds.Contains(key) ? last : null;
                }
                foreach (Block blk in it.Blocks)
                {
                    if (!blk.IsStep || blk.Grinds.Count == 0)
                        return null;
                    foreach (Line l in blk.Lines)
                    {
                        if (!l.Text.StartsWith("<Grind ", StringComparison.Ordinal) && !l.Text.StartsWith("<!--", StringComparison.Ordinal))
                            return null;
                    }
                }
            }
            return null;
        }

        private static string GrindKey(ulong q, ulong b, ulong s, int task)
        {
            return StepKey(q, b, s) + ":" + task.ToString();
        }

        private void GrindLine(RecEvent e, Block block, ulong branch, ulong step, int task, Cause c, GrindDef grind)
        {
            bool multiTask = task >= 0 && e.TaskTotal > 1;
            if (grind == null)
            {
                grind = new GrindDef
                {
                    Key = GrindKey(e.QuestId, branch, step, task),
                    QuestId = e.QuestId,
                    Branch = branch,
                    Step = step,
                    Task = task,
                };
                string baseName = Fmt.OneLine(QName(e).Length > 0 ? QName(e) : Fmt.Hex(e.QuestId)) + " " + branch.ToString() + "-" + step.ToString() +
                                  (multiTask ? " task " + task.ToString() : string.Empty);
                string name = baseName;
                for (int n = 2; _grindNames.Contains(name); n++)
                    name = baseName + " " + n.ToString();
                _grindNames.Add(name);
                grind.Name = name;
                _grinds.Add(grind);
            }
            if (c.Kills != null)
            {
                foreach (RecEvent k in c.Kills)
                {
                    if (grind.Seqs.Add(k.Seq))
                        grind.Kills.Add(k);
                }
            }
            string q = Fmt.Hex(e.QuestId);
            // A count you only got part of the way here (e.g.
            bool partial = e.Kind == EvKind.QuestTask && e.Max > 0 && e.Count > 0 && e.Count < e.Max;
            string partialCond = partial
                ? Fmt.Format("not IsTaskValueGreaterThan({0}, {1}, {2}, {3})", q, step, task >= 0 ? task : 0, e.Count - 1)
                : null;
            if (block.Grinds.Contains(grind.Key))
            {
                // this block already grinds there: the new kills add hotspots, and its stop point moves up to the new count
                Dictionary<string, Line> mine;
                Line existing;
                if (_grindLineOf.TryGetValue(block, out mine) && mine.TryGetValue(grind.Key, out existing))
                {
                    string full = grind.Task >= 0 && multiTask
                        ? Fmt.Format("not IsTaskComplete({0}, {1}, {2})", q, step, grind.Task)
                        : Fmt.Format("not IsBranchStepComplete({0}, {1}, {2})", q, branch, step);
                    existing.Text = "<Grind GrindRef=\"" + Fmt.XmlAttr(grind.Name) + "\" While=\"" + (partialCond ?? full) + "\" />";
                }
                return;
            }

            string cond;
            if (partialCond != null)
            {
                cond = partialCond;
            }
            else if (grind.Task >= 0 && multiTask)
            {
                cond = Fmt.Format("not IsTaskComplete({0}, {1}, {2})", q, step, grind.Task);
            }
            else
            {
                cond = Fmt.Format("not IsBranchStepComplete({0}, {1}, {2})", q, branch, step);
                if (task >= 0 && e.TaskTotal < 0)
                    block.Lines.Add(Comment(Fmt.Format("[?] the number of tasks of this step was not read. If it has more than one, use While=\"not IsTaskComplete({0}, {1}, {2})\"", q, step, task)));
            }
            RecEvent last = c.Kills != null && c.Kills.Count > 0 ? c.Kills[c.Kills.Count - 1] : e;
            Line grindLine = Tag("Grind", "<Grind GrindRef=\"" + Fmt.XmlAttr(grind.Name) + "\" While=\"" + cond + "\" />", last);
            block.Lines.Add(grindLine);
            block.Grinds.Add(grind.Key);
            Dictionary<string, Line> lines;
            if (!_grindLineOf.TryGetValue(block, out lines))
            {
                lines = new Dictionary<string, Line>(StringComparer.Ordinal);
                _grindLineOf[block] = lines;
            }
            lines[grind.Key] = grindLine;
        }

        /// <summary>The Grind line each block wrote per grind area, so a later count in the same block can move its stop point.</summary>
        private readonly Dictionary<Block, Dictionary<string, Line>> _grindLineOf = new Dictionary<Block, Dictionary<string, Line>>();

        // ------------------------------------------------------------------ travel lines

        private void Travel(List<Act> acts, List<Line> lines, string questId, bool insideGrind)
        {
            var crumbs = new List<Act>();
            for (int i = 0; i < acts.Count; i++)
            {
                Act a = acts[i];
                if (a.Kind == ActKind.Crumb)
                {
                    if (!insideGrind)
                        crumbs.Add(a);
                    continue;
                }
                FlushCrumbs(crumbs, lines, questId);
                if (a.Used || a.Consumed)
                    continue;
                RecEvent e = a.Ev;
                switch (a.Kind)
                {
                    case ActKind.Marker:
                        lines.Add(Stand(questId, e.Note, e.Pos, e));
                        break;
                    case ActKind.Taxi:
                    {
                        string from = e.Target != null && e.Target.Name.Length > 0 ? e.Target.Name : string.Empty;
                        Vec3 stand = e.HasStand ? e.Stand : (e.Target != null && e.Target.HasPos ? e.Target.Pos : e.Pos);
                        if (from.Length == 0)
                            lines.Add(Comment("[?] the taxi terminal you started from was not identified: fill in TransporterName"));
                        // Option must be the name the taxi MAP shows, which can differ from the terminal object's name.
                        string landed = e.Target2 != null && e.Target2.Name.Length > 0 ? ", next to the terminal object \"" + e.Target2.Name + "\"" : string.Empty;
                        string offered = e.Choices != null && e.Choices.Count > 0 ? " Names on the taxi map: " + string.Join(" | ", e.Choices) + "." : string.Empty;
                        if (e.Option.Length == 0)
                            lines.Add(Comment("[?] the taxi-map name of the terminal you landed at was not read: fill in Option (you landed at " +
                                              Fmt.Xyz(e.Pos) + landed + ")." + offered));
                        else if (e.OptionGuessed)
                            lines.Add(Comment("[?] Option \"" + e.Option + "\" is a guess (" + e.Note + ")." + offered));
                        var inner = new List<Line>();
                        inner.Add(Stand(questId, from.Length > 0 ? from : "Taxi", stand, e));
                        inner.Add(Tag("UseTransport", "<UseTransport Type=\"Taxi\" TransporterName=\"" + Fmt.XmlAttr(from.Length > 0 ? from : "?") +
                                                      "\" Option=\"" + Fmt.XmlAttr(e.Option.Length > 0 ? e.Option : "?") + "\" XYZ=\"" + Fmt.Xyz(stand) + "\" />", e));
                        AddGated(lines, inner, stand, e.Pos, false, "taxi ride");
                        break;
                    }
                    case ActKind.Elevator:
                    {
                        string name = e.Target != null && e.Target.Name.Length > 0 ? e.Target.Name : "?";
                        Vec3 stand = e.HasStand ? e.Stand : (e.HasFrom ? e.FromPos : e.Pos);
                        Line standLine = Stand(questId, name != "?" ? name : "Elevator", stand, e);
                        ulong area = e.HasFrom ? e.FromArea : e.AreaId;
                        string areaName = e.HasFrom ? e.FromAreaName : e.AreaName;
                        standLine.Area = area;
                        standLine.AreaName = areaName;
                        if (!(e.HasFrom && e.FromArea != e.AreaId))
                        {
                            // A lift inside the same area.
                            lines.Add(Comment("Lift \"" + name + "\"" + (e.Option.Length > 0 ? " to \"" + e.Option + "\"" : string.Empty) +
                                              ": the navigator rides it by itself, so this just walks to where you came out."));
                            lines.Add(Stand(questId, "Where the " + (name != "?" ? name : "lift") + " brought you", e.Pos, e));
                            break;
                        }
                        if (e.Option.Length == 0)
                        {
                            string floors = e.Target != null && e.Target.Destinations != null && e.Target.Destinations.Length > 0
                                ? string.Join(" | ", e.Target.Destinations)
                                : "not read";
                            lines.Add(Comment("[?] the floor you chose was not seen: fill in Option. Floors: " + floors + ". You arrived at " + Fmt.Xyz(e.Pos)));
                        }
                        Line ride = Tag("UseTransport", "<UseTransport Type=\"Wonkavator\" TransporterName=\"" + Fmt.XmlAttr(name) + "\" Option=\"" +
                                                        Fmt.XmlAttr(e.Option.Length > 0 ? e.Option : "?") + "\" XYZ=\"" + Fmt.Xyz(stand) + "\" />", e);
                        ride.Area = area;
                        ride.AreaName = areaName;
                        var inner = new List<Line> { standLine, ride };
                        if (e.HasFrom && e.FromArea != e.AreaId)
                        {
                            // the ride leads into another area: the area If is the gate
                            standLine.ForceArea = true;
                            ride.ForceArea = true;
                            lines.AddRange(inner);
                        }
                        else
                        {
                            AddGated(lines, inner, stand, e.Pos, true, "elevator ride");
                        }
                        break;
                    }
                    case ActKind.Jump:
                    {
                        float dist = e.HasFrom ? e.Pos.DistanceTo(e.FromPos) : 0f;
                        if (e.Note.StartsWith(How.Revived, StringComparison.Ordinal))
                        {
                            lines.Add(Comment("You were revived and moved " + Fmt.F1(dist) + " m here."));
                            break;
                        }
                        Act click = ClickBefore(acts, i, 15);
                        if (click != null)
                        {
                            lines.Add(Comment(Fmt.Format("Position jump of {0} m right after clicking \"{1}\" (door, shuttle or phase). Now at {2}.",
                                Fmt.F1(dist), click.Ev.Target != null ? click.Ev.Target.DisplayName : "?", Fmt.Xyz(e.Pos))));
                            break;
                        }
                        Line before = Stand(questId, "Before the jump", e.HasFrom ? e.FromPos : e.Pos, e);
                        if (e.HasFrom)
                        {
                            before.Area = e.FromArea;
                            before.AreaName = e.FromAreaName;
                        }
                        lines.Add(Comment(Fmt.Format("[?] position jump of {0} m here ({1}). After it you were at {2}. Add the door, phase or transport by hand.",
                            Fmt.F1(dist), e.Note, Fmt.Xyz(e.Pos))));
                        if (e.HasFrom)
                            AddGated(lines, new List<Line> { before }, e.FromPos, e.Pos, false, "jump");
                        else
                            lines.Add(before);
                        break;
                    }
                    case ActKind.Ship:
                        if (e.Option.Length > 0)
                        {
                            lines.Add(Comment("[?] check that SpaceTravel knows this planet name"));
                            lines.Add(Tag("SpaceTravel", "<SpaceTravel Planet=\"" + Fmt.XmlAttr(e.Option) + "\" />", e));
                        }
                        else
                        {
                            lines.Add(Comment("[?] the ship travelled here, but the destination was not read: add <SpaceTravel Planet=\"...\" />"));
                        }
                        break;
                    case ActKind.Loading:
                    {
                        string from = e.HasFrom && e.FromArea != e.AreaId ? "\"" + e.FromAreaName + "\" -> " : string.Empty;
                        string extra = e.Details != null && e.Details.Count > 0 ? " " + string.Join(", ", e.Details) : string.Empty;
                        lines.Add(Comment("Loading screen (" + Fmt.Duration(e.DurationMs / 1000.0) + "): " + from + "\"" + e.AreaName + "\" " +
                                          Fmt.Hex(e.AreaId) + extra));
                        break;
                    }
                    case ActKind.Area:
                        if (!HasNear(acts, i, ActKind.Loading, 2))
                            lines.Add(Comment("Area changed: \"" + e.FromAreaName + "\" -> \"" + e.AreaName + "\" " + Fmt.Hex(e.AreaId)));
                        break;
                    case ActKind.Click:
                    {
                        ObjRef t = e.Target;
                        string name = t != null ? t.Name : "?";
                        string tag = "<Interact" + (questId != Q1 ? " QuestId=\"" + questId + "\"" : string.Empty) + " Name=\"" + Fmt.XmlAttr(name) +
                                     "\" WaitTime=\"" + _wait.ToString() + "\" XYZ=\"" + Fmt.Xyz(PosOf(t, e)) + "\" />";
                        Act moved = MoveAfter(acts, i, 15);
                        if (moved != null)
                        {
                            AddMoverLine(lines, Tag("Interact", tag, e), e, moved, "door or phase click");
                        }
                        else
                        {
                            lines.Add(Comment("[?] you clicked \"" + name + "\" here and no quest update followed. Uncomment if it is needed (door, phase, bind point): " + tag));
                        }
                        break;
                    }
                    case ActKind.NpcClick:
                    {
                        Act moved = MoveAfter(acts, i, 15);
                        if (moved != null && e.Target != null)
                        {
                            AddMoverLine(lines, Tag("Interact", "<Interact" + (questId != Q1 ? " QuestId=\"" + questId + "\"" : string.Empty) + " Name=\"" +
                                                                Fmt.XmlAttr(e.Target.Name) + "\" WaitTime=\"" + _wait.ToString() + "\" XYZ=\"" +
                                                                Fmt.Xyz(PosOf(e.Target, e)) + "\" />", e), e, moved, "door or phase click");
                        }
                        break;
                    }
                    case ActKind.Talk:
                        if (e.Target != null)
                            lines.Add(Comment("You talked to \"" + e.Target.DisplayName + "\" at " + Fmt.Xyz(PosOf(e.Target, e)) + " here; no quest update followed."));
                        break;
                    case ActKind.Holocom:
                        lines.Add(Comment("Holocom call here; no quest update followed."));
                        break;
                    case ActKind.Item:
                        lines.Add(Comment("[?] you used \"" + e.Option + "\" here and no quest update followed: <UseItem ItemName=\"" + Fmt.XmlAttr(e.Option) +
                                          "\" WaitTime=\"" + _wait.ToString() + "\" XYZ=\"" + Fmt.Xyz(e.Pos) + "\" />"));
                        break;
                    case ActKind.Cast:
                        if (e.Target != null && e.Target.UsableForQuest)
                            lines.Add(Comment("[?] you cast \"" + e.Option + "\" next to \"" + e.Target.DisplayName + "\" (usable for a quest) at " +
                                              Fmt.Xyz(PosOf(e.Target, e)) + " and no quest update followed"));
                        break;
                    case ActKind.Died:
                        lines.Add(Comment("[?] You died here" + (e.Note.Length > 0 ? " (fighting " + e.Note + ")" : string.Empty) + "."));
                        break;
                    case ActKind.Note:
                        lines.Add(Comment(a.Text));
                        break;
                }
            }
            FlushCrumbs(crumbs, lines, questId);
        }

        private void FlushCrumbs(List<Act> crumbs, List<Line> lines, string questId)
        {
            if (crumbs.Count == 0)
                return;
            if (_crumbs != BreadcrumbsInDraft.Off)
            {
                const int Max = 8;
                var picks = new List<Act>();
                if (crumbs.Count <= Max)
                {
                    picks.AddRange(crumbs);
                }
                else
                {
                    int lastIdx = -1;
                    for (int k = 0; k < Max; k++)
                    {
                        int idx = (int)Math.Round(k * (crumbs.Count - 1) / (double)(Max - 1));
                        if (idx != lastIdx)
                            picks.Add(crumbs[idx]);
                        lastIdx = idx;
                    }
                }
                foreach (Act a in picks)
                {
                    Line l = Stand(questId, "Path", a.Ev.Pos, a.Ev);
                    if (_crumbs == BreadcrumbsInDraft.AsComments)
                        l.Text = "<!-- " + Fmt.XmlComment(l.Text) + " -->";
                    lines.Add(l);
                }
            }
            crumbs.Clear();
        }

        /// <summary>A click within <paramref name="seconds"/> before acts[i] that was not used for anything else.</summary>
        private static Act ClickBefore(List<Act> acts, int i, double seconds)
        {
            for (int k = i - 1; k >= 0; k--)
            {
                Act a = acts[k];
                if (acts[i].At - a.At > seconds)
                    break;
                if ((a.Kind == ActKind.Click || a.Kind == ActKind.NpcClick) && !a.Used && !a.Consumed)
                    return a;
            }
            return null;
        }

        /// <summary>The jump, loading screen or area change that follows acts[i] within <paramref name="seconds"/>, or null.</summary>
        private static Act MoveAfter(List<Act> acts, int i, double seconds)
        {
            for (int k = i + 1; k < acts.Count; k++)
            {
                Act a = acts[k];
                if (a.At - acts[i].At > seconds)
                    break;
                if (a.Kind == ActKind.Jump || a.Kind == ActKind.Loading || a.Kind == ActKind.Area)
                    return a;
            }
            return null;
        }

        /// <summary>A door / shuttle / phase line that moved you.</summary>
        private void AddMoverLine(List<Line> lines, Line line, RecEvent click, Act moved, string what)
        {
            if (moved.Kind != ActKind.Jump || !moved.Ev.HasFrom || moved.Ev.FromArea != moved.Ev.AreaId)
            {
                line.ForceArea = true;
                lines.Add(line);
                return;
            }
            AddGated(lines, new List<Line> { line }, click.Pos, moved.Ev.Pos, false, what);
        }

        /// <summary>Wraps inner (a stand and a ride) in an If on your position.</summary>
        private void AddGated(List<Line> lines, List<Line> inner, Vec3 before, Vec3 after, bool preferY, string what)
        {
            string gate = PositionGate(before, after, preferY);
            if (gate == null)
            {
                lines.Add(Comment("[?] add a position gate (an If on Me.Position) around this " + what + ", so a restart after it does not do it again"));
                lines.AddRange(inner);
                return;
            }
            lines.Add(Comment("[?] position gate for this " + what + " (half-way between where you were before and after it): it must be true " +
                              "wherever you can be before it and false after it. Check it."));
            lines.Add(new Line
            {
                Text = "<If Condition=\"" + Fmt.XmlAttr(gate) + "\">",
                Inner = inner,
                Area = inner[0].Area,
                AreaName = inner[0].AreaName,
            });
        }

        /// <summary>"(Me.Position.X &lt; 12.3)".</summary>
        private static string PositionGate(Vec3 before, Vec3 after, bool preferY)
        {
            float ax = Math.Abs(after.X - before.X);
            float ay = Math.Abs(after.Y - before.Y);
            float az = Math.Abs(after.Z - before.Z);
            string axis;
            float a;
            float b;
            if (ay >= 3f && (preferY || ay * 4f >= Math.Max(ax, az)))
            {
                axis = "Y";
                a = before.Y;
                b = after.Y;
            }
            else if (ax >= az && ax >= 6f)
            {
                axis = "X";
                a = before.X;
                b = after.X;
            }
            else if (az >= 6f)
            {
                axis = "Z";
                a = before.Z;
                b = after.Z;
            }
            else
            {
                return null;
            }
            double mid = (a + (double)b) / 2.0;
            return "(Me.Position." + axis + (a < b ? " < " : " > ") + Fmt.F1(mid) + ")";
        }

        private static bool HasNear(List<Act> acts, int i, ActKind kind, double seconds)
        {
            for (int k = 0; k < acts.Count; k++)
            {
                if (k != i && acts[k].Kind == kind && Math.Abs(acts[k].At - acts[i].At) <= seconds)
                    return true;
            }
            return false;
        }

        private void FlushFree(string title)
        {
            if (_pending.Count == 0)
                return;
            List<Act> taken = TakeUpTo(null, false);
            var item = new Item { Kind = ItemKind.Free, Comment = title };
            Travel(taken, item.Lines, Q1, false);
            if (item.Lines.Count > 0)
                _items.Add(item);
        }

        private void TopNote(string text)
        {
            _items.Add(new Item { Kind = ItemKind.Note, Comment = text });
        }

        // ------------------------------------------------------------------ grouping

        /// <summary>The quest's outer If: the last item when it is the same quest, else a new one.</summary>
        private Item Group(RecEvent e)
        {
            if (_items.Count > 0)
            {
                Item last = _items[_items.Count - 1];
                if (last.Kind == ItemKind.QuestGroup && last.QuestId == e.QuestId)
                    return last;
            }
            var g = new Item { Kind = ItemKind.QuestGroup, QuestId = e.QuestId };
            g.Comment = QuestText(e) + (QFqn(e).Length > 0 ? " " + QFqn(e) : string.Empty);
            _items.Add(g);
            return g;
        }

        private static Block StepBlock(Item group, ulong branch, ulong step)
        {
            if (group.Blocks.Count > 0)
            {
                Block last = group.Blocks[group.Blocks.Count - 1];
                if (last.IsStep && last.Branch == branch && last.Step == step)
                    return last;
            }
            var b = new Block { IsStep = true, Branch = branch, Step = step };
            group.Blocks.Add(b);
            return b;
        }

        // ------------------------------------------------------------------ line helpers

        private Line Stand(string questId, string name, Vec3 pos, RecEvent e)
        {
            string n = Fmt.OneLine(name);
            string text = "<" + _stand + " QuestId=\"" + questId + "\"" + (n.Length > 0 ? " Name=\"" + Fmt.XmlAttr(n) + "\"" : string.Empty) +
                          " XYZ=\"" + Fmt.Xyz(pos) + "\" />";
            return Tag(_stand, text, e);
        }

        private Line Tag(string tag, string text, RecEvent e)
        {
            _usedTags.Add(tag);
            return new Line { Text = text, Area = e != null ? e.AreaId : 0, AreaName = e != null ? e.AreaName : string.Empty };
        }

        private static Line Comment(string text)
        {
            return new Line { Text = "<!-- " + Fmt.XmlComment(text) + " -->", Area = 0 };
        }

        private string Holocom(RecEvent e)
        {
            return "<AnswerHolocomCall QuestName=\"" + Fmt.XmlAttr(QName(e)) + "\" QuestId=\"" + Fmt.Hex(e.QuestId) + "\" />";
        }

        private static Vec3 PosOf(ObjRef o, RecEvent e)
        {
            if (o != null && o.HasPos)
                return o.Pos;
            return e.Pos;
        }

        private string QuestText(RecEvent e)
        {
            string n = QName(e);
            return "\"" + (n.Length > 0 ? n : "(title not read)") + "\" " + Fmt.Hex(e.QuestId);
        }

        /// <summary>The quest's title: the event's own, else the one any other event of this recording has.</summary>
        private string QName(RecEvent e)
        {
            if (e.QuestName.Length > 0)
                return e.QuestName;
            string n;
            return _questNames.TryGetValue(e.QuestId, out n) ? n : string.Empty;
        }

        private string QFqn(RecEvent e)
        {
            if (e.QuestFqn.Length > 0)
                return e.QuestFqn;
            string n;
            return _questFqns.TryGetValue(e.QuestId, out n) ? n : string.Empty;
        }

        // ------------------------------------------------------------------ render

        private string Render()
        {
            var body = new StringBuilder();
            foreach (Item it in _items)
                RenderItem(body, it);

            var grinds = new StringBuilder();
            foreach (GrindDef g in _grinds)
                RenderGrind(grinds, g);

            var sb = new StringBuilder();
            NL(sb, 0, "<Profile xmlns=\"http://tempuri.org/ProfileSchema.xsd\">");
            NL(sb, 1, "<!-- DRAFT made by Quest Recorder " + QuestRecorderPlugin.Patch +
                      ". After you have checked it, replace this line with the \"LethToxic created.\" line. -->");
            foreach (string h in HeaderNotes())
                NL(sb, 1, "<!-- " + Fmt.XmlComment(h) + " -->");
            NL(sb, 0, string.Empty);
            NL(sb, 1, "<Name>" + Text("[DRAFT] " + (_info.Place.Length > 0 ? _info.Place : "Recording") + " " +
                                      _info.Started.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)) + "</Name>");
            if (_darkPicks > _lightPicks)
            {
                NL(sb, 1, "<!-- You picked dark replies " + _darkPicks.ToString() + " times and light " + _lightPicks.ToString() + " times: dark side on. -->");
                NL(sb, 1, "<ForceAlignment Type=\"Dark\" AutoSkip=\"True\" />");
            }
            else if (_lightPicks > _darkPicks)
            {
                NL(sb, 1, "<!-- You picked light replies " + _lightPicks.ToString() + " times and dark " + _darkPicks.ToString() + " times: light side on. -->");
                NL(sb, 1, "<ForceAlignment Type=\"Light\" AutoSkip=\"True\" />");
            }
            else
            {
                NL(sb, 1, "<!-- <ForceAlignment Type=\"Dark\" AutoSkip=\"True\" /> -->");
            }
            NL(sb, 1, "<Vendors>");
            foreach (VendorDef v in _vendors)
            {
                NL(sb, 2, "<Vendor Name=\"" + Fmt.XmlAttr(v.Name) + "\" XYZ=\"" + Fmt.Xyz(v.Pos) + "\" />");
                if (v.AreaName.Length > 0)
                    NL(sb, 2, "<!-- " + Fmt.XmlComment(v.AreaName) + " -->");
            }
            NL(sb, 1, "</Vendors>");
            NL(sb, 1, "<GrindAreas>");
            sb.Append(grinds);
            NL(sb, 1, "</GrindAreas>");
            NL(sb, 1, "<Questing>");
            sb.Append(body);
            NL(sb, 1, "</Questing>");
            NL(sb, 0, "</Profile>");
            return sb.ToString();
        }

        private List<string> HeaderNotes()
        {
            var notes = new List<string>();
            notes.Add(Fmt.Format("Recorded {0} to {1} ({2}){3}. Timeline: {4}.txt and {4}.csv.",
                _info.Started.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                _info.Ended.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                Fmt.Duration((_info.Ended - _info.Started).TotalSeconds),
                _info.Place.Length > 0 ? " on " + _info.Place : string.Empty, _info.BaseName));
            notes.Add("This is a skeleton, not a finished profile. Every [?] marks a guess: read it, fix it or delete it before you run this.");
            notes.Add("Ids and positions are what the game showed and names are the game's text; a [?] marks a name the recorder had to work out. " +
                      "BranchId/StepId are the game's QuestState numbers; TaskId is the 0-based task index.");
            if (!_info.Final)
                notes.Add("Not final: the recording was still running when this was written. It is rewritten about every minute and when you stop.");
            if (_info.Truncated)
                notes.Add("[?] The recording had more than " + RecordingSession.MaxEventsInMemory.ToString() + " events; this draft covers only the first ones.");
            if (_checkFailures > 0)
                notes.Add(Fmt.Format("WARNING: {0} live {1} of IsBranchStepComplete / IsTaskValueGreaterThan disagreed with the recorded numbers. Look for \"live check\" below and in the .txt.",
                    _checkFailures, _checkFailures == 1 ? "check" : "checks"));
            if (_questTriggers == 0)
                notes.Add("[?] No quest update was seen while recording, so there are no quest blocks: only the path.");

            if (_tags == null)
            {
                notes.Add(_info.Final ? "Tag check: not run (BuddyCron was closing)." : "Tag check: runs when you stop the recording.");
            }
            else if (!_tags.Ran)
            {
                notes.Add("Tag check: could not run (" + _tags.Problem + ").");
            }
            else
            {
                var missing = new List<string>();
                foreach (string t in _usedTags)
                {
                    if (!_tags.Known.Contains(t))
                        missing.Add(t);
                }
                missing.Sort(StringComparer.Ordinal);
                if (missing.Count == 0)
                    notes.Add("Tag check: every tag used here is known to this BuddyCron.");
                else
                    notes.Add("[?] Tag check: this BuddyCron does not know " + string.Join(", ", missing) + ". Check those lines.");
            }
            if (_standFellBack)
                notes.Add("This BuddyCron does not know NoCombatMoveTo, so the stands use MoveTo.");
            return notes;
        }

        private void RenderItem(StringBuilder sb, Item it)
        {
            switch (it.Kind)
            {
                case ItemKind.Note:
                    NL(sb, 2, "<!-- " + Fmt.XmlComment(it.Comment) + " -->");
                    return;
                case ItemKind.Free:
                    NL(sb, 2, "<!-- " + Fmt.XmlComment(it.Comment) + " -->");
                    RenderLines(sb, 2, it.Lines);
                    return;
                case ItemKind.Pickup:
                {
                    string q = Fmt.Hex(it.QuestId);
                    NL(sb, 2, "<!-- " + Fmt.XmlComment(it.Comment) + " -->");
                    string qn;
                    _questNames.TryGetValue(it.QuestId, out qn);
                    qn = qn ?? string.Empty;
                    bool repeatable = qn.IndexOf("[DAILY]", StringComparison.OrdinalIgnoreCase) >= 0 || qn.IndexOf("[WEEKLY]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      qn.IndexOf("[HEROIC", StringComparison.OrdinalIgnoreCase) >= 0 || it.Comment.IndexOf("mission board", StringComparison.OrdinalIgnoreCase) >= 0;
                    // A weekly comes back (3 times a week): only "not in my log", like the Oricon weekly.
                    // Dailies, heroics and everything else: not in my log and not done.
                    bool weekly = repeatable && qn.IndexOf("[WEEKLY]", StringComparison.OrdinalIgnoreCase) >= 0;
                    NL(sb, 2, weekly
                        ? "<If Condition=\"(not HasQuest(" + q + "))\">"
                        : "<If Condition=\"((not HasQuest(" + q + ")) and (not IsQuestComplete(" + q + ")))\">");
                    RenderLines(sb, 3, it.Lines);
                    NL(sb, 2, "</If>");
                    return;
                }
                default:
                {
                    string q = Fmt.Hex(it.QuestId);
                    NL(sb, 2, "<!-- " + Fmt.XmlComment(it.Comment) + " -->");
                    NL(sb, 2, "<If Condition=\"((HasQuest(" + q + ")) and (not IsQuestComplete(" + q + ")))\">");
                    foreach (Block b in it.Blocks)
                    {
                        if (b.IsStep)
                        {
                            if (b.Comment.Length > 0)
                                NL(sb, 3, "<!-- " + Fmt.XmlComment(b.Comment) + " -->");
                            NL(sb, 3, Fmt.Format("<If Condition=\"HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})\">", q, b.Branch, b.Step));
                            RenderLines(sb, 4, b.Lines);
                            NL(sb, 3, "</If>");
                        }
                        else
                        {
                            RenderLines(sb, 3, b.Lines);
                        }
                    }
                    NL(sb, 2, "</If>");
                    return;
                }
            }
        }

        /// <summary>Lines in one area are written as they are.</summary>
        private static void RenderLines(StringBuilder sb, int depth, List<Line> lines)
        {
            var areas = new HashSet<ulong>();
            bool force = false;
            foreach (Line l in lines)
            {
                if (l.Area != 0)
                    areas.Add(l.Area);
                if (Forces(l))
                    force = true;
            }
            if (areas.Count == 0 || (areas.Count == 1 && !force))
            {
                foreach (Line l in lines)
                    Write(sb, depth, l);
                return;
            }

            int i = 0;
            while (i < lines.Count)
            {
                // the run's area: the first line with an area from here on (comments join the run they precede)
                ulong area = 0;
                string areaName = string.Empty;
                for (int k = i; k < lines.Count; k++)
                {
                    if (lines[k].Area != 0)
                    {
                        area = lines[k].Area;
                        areaName = lines[k].AreaName;
                        break;
                    }
                }
                if (area == 0)
                {
                    for (; i < lines.Count; i++)
                        Write(sb, depth, lines[i]);
                    return;
                }
                NL(sb, depth, "<If Condition=\"(Core.Player.AreaId == " + Fmt.Hex(area) + ")\">");
                if (areaName.Length > 0)
                    NL(sb, depth + 1, "<!-- " + Fmt.XmlComment(areaName) + " -->");
                bool seenArea = false;
                while (i < lines.Count)
                {
                    Line l = lines[i];
                    if (l.Area != 0 && l.Area != area)
                        break;
                    if (l.Area == 0 && seenArea && NextAreaDiffers(lines, i, area))
                        break;
                    if (l.Area == area)
                        seenArea = true;
                    Write(sb, depth + 1, l);
                    i++;
                }
                NL(sb, depth, "</If>");
            }
        }

        private static bool Forces(Line l)
        {
            if (l.ForceArea)
                return true;
            if (l.Inner != null)
            {
                foreach (Line i in l.Inner)
                {
                    if (Forces(i))
                        return true;
                }
            }
            return false;
        }

        /// <summary>One line, or a gate If with its lines inside.</summary>
        private static void Write(StringBuilder sb, int depth, Line l)
        {
            NL(sb, depth, l.Text);
            if (l.Inner == null)
                return;
            foreach (Line i in l.Inner)
                Write(sb, depth + 1, i);
            NL(sb, depth, "</If>");
        }

        /// <summary>True when the next line with an area belongs to another area (so this comment goes with that run).</summary>
        private static bool NextAreaDiffers(List<Line> lines, int i, ulong area)
        {
            for (int k = i; k < lines.Count; k++)
            {
                if (lines[k].Area != 0)
                    return lines[k].Area != area;
            }
            return false;
        }

        private void RenderGrind(StringBuilder sb, GrindDef g)
        {
            var mobs = new List<string>();
            foreach (RecEvent k in g.Kills)
            {
                string n = k.Target != null ? k.Target.Name : string.Empty;
                if (n.Length > 0 && !mobs.Contains(n))
                    mobs.Add(n);
            }
            NL(sb, 2, "<GrindArea Name=\"" + Fmt.XmlAttr(g.Name) + "\">");
            NL(sb, 3, "<!-- " + Fmt.XmlComment(Fmt.Format("[?] from {0} {1} seen right before the quest count went up ({2}, branch {3} step {4}{5}). Check the mob names.",
                g.Kills.Count, g.Kills.Count == 1 ? "kill" : "kills", Fmt.Hex(g.QuestId), g.Branch, g.Step,
                g.Task >= 0 ? " task " + g.Task.ToString() : string.Empty)) + " -->");
            NL(sb, 3, "<MinLevel>1</MinLevel>");
            NL(sb, 3, "<MaxLevel>81</MaxLevel>");
            NL(sb, 3, "<Mobs>");
            foreach (string m in mobs)
                NL(sb, 4, "<Mob>" + Text(m) + "</Mob>");
            NL(sb, 3, "</Mobs>");
            NL(sb, 3, "<Hotspots>");
            foreach (KeyValuePair<string, Vec3> h in Hotspots(g.Kills))
                NL(sb, 4, "<Hotspot Name=\"" + Fmt.XmlAttr(h.Key) + "\" XYZ=\"" + Fmt.Xyz(h.Value) + "\" />");
            NL(sb, 3, "</Hotspots>");
            NL(sb, 2, "</GrindArea>");
        }

        /// <summary>Kill positions grouped within 40 m; at most 6 spots, the busiest first-seen ones.</summary>
        private static List<KeyValuePair<string, Vec3>> Hotspots(List<RecEvent> kills)
        {
            var sums = new List<double[]>();
            var counts = new List<int>();
            var names = new List<Dictionary<string, int>>();
            var anchors = new List<Vec3>();
            foreach (RecEvent k in kills)
            {
                Vec3 p = k.Target != null && k.Target.HasPos ? k.Target.Pos : k.Pos;
                int best = -1;
                // Each spot is anchored on a real kill and takes the kills within 6 m of it.
                for (int c = 0; c < anchors.Count; c++)
                {
                    if (anchors[c].DistanceTo(p) <= 6f)
                    {
                        best = c;
                        break;
                    }
                }
                if (best < 0)
                {
                    sums.Add(new double[3]);
                    counts.Add(0);
                    names.Add(new Dictionary<string, int>(StringComparer.Ordinal));
                    anchors.Add(p);
                    best = sums.Count - 1;
                }
                sums[best][0] += p.X;
                sums[best][1] += p.Y;
                sums[best][2] += p.Z;
                counts[best]++;
                string n = k.Target != null && k.Target.Name.Length > 0 ? k.Target.Name : "Kills";
                int have;
                names[best].TryGetValue(n, out have);
                names[best][n] = have + 1;
            }

            var order = new List<int>();
            for (int c = 0; c < sums.Count; c++)
                order.Add(c);
            order.Sort((a, b) => counts[b] != counts[a] ? counts[b].CompareTo(counts[a]) : a.CompareTo(b));
            if (order.Count > 6)
                order.RemoveRange(6, order.Count - 6);
            order.Sort();

            var result = new List<KeyValuePair<string, Vec3>>();
            foreach (int c in order)
            {
                string top = "Kills";
                int topCount = -1;
                foreach (KeyValuePair<string, int> kv in names[c])
                {
                    if (kv.Value > topCount)
                    {
                        top = kv.Key;
                        topCount = kv.Value;
                    }
                }
                result.Add(new KeyValuePair<string, Vec3>(top, anchors[c])); // a spot where a kill really happened
            }
            return result;
        }

        /// <summary>XML element text (escapes &amp;, &lt; and &gt;).</summary>
        private static string Text(string s)
        {
            return Fmt.XmlAttr(s).Replace("&quot;", "\"");
        }

        private static void NL(StringBuilder sb, int depth, string text)
        {
            if (text.Length > 0)
                sb.Append('\t', depth).Append(text);
            sb.Append("\r\n");
        }
    }
}
