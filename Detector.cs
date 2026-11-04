// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Text;
using BuddyCron;

namespace QuestRecorder
{
    /// <summary>Compares two snapshots and writes edge-triggered events (something started, ended or changed).</summary>
    internal sealed class Detector
    {
        private const long TalkWindowMs = 15000;
        private const long ClickWindowMs = 15000;
        private const long BoardWindowMs = 10000;

        private readonly RecordingSession _session;
        private readonly GameReader _reader;

        private bool _questLogPending;
        private int _questLogWait;
        private Snap _questLogSnap;
        private string _questLogTitle = "Quest log at the start";

        private ObjRef _convoPartner;
        private ObjRef _questWindow; // the NPC whose quest window you last opened
        private long _questWindowTick = long.MinValue / 2;
        private bool _convoComlink;
        private long _convoStartTick;
        private long _convoEndTick = long.MinValue / 2;
        private ulong _offered;

        // The choice wheel waiting for your pick.
        private List<ReplyOpt> _wheel;
        private long _wheelNode = -1;
        private Snap _wheelSnap;
        private int _wheels;
        private int _wheelsDiffering;

        private ObjRef _lastClick;
        private long _lastClickTick = long.MinValue / 2;
        private readonly HashSet<ulong> _spotted = new HashSet<ulong>(); // objects the scanner already noted
        private ulong _lastNpcClickId;
        private long _lastNpcClickTick = long.MinValue / 2;

        private ObjRef _taxiSource;
        private bool _haveTaxiStand;
        private Vec3 _taxiStand;

        private BoardInfo _board;
        private long _boardCloseTick = long.MinValue / 2;

        private long _combatStartTick;
        private int _combatKills;
        private string _lastCombatMobs = string.Empty;

        private bool _haveCrumb;
        private Vec3 _lastCrumb;

        private ulong _castSpec;
        private string _castName = string.Empty;
        private long _castStartTick;
        private Vec3 _castPos;
        private ulong _castArea;
        private bool _castInCombat;
        private bool _castIsQuestItem;
        private ObjRef _castTarget;
        private string _lastCastName = string.Empty;
        private long _lastCastEndTick = long.MinValue / 2;

        private readonly HashSet<ulong> _rewardSeen = new HashSet<ulong>();
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private int _markers;

        public Detector(RecordingSession session, GameReader reader)
        {
            _session = session;
            _reader = reader;
        }

        private static RrSettings Cfg
        {
            get { return RrSettings.Instance; }
        }

        // ------------------------------------------------------------------ public entry points

        public void Begin(Snap first)
        {
            RecEvent e = New(EvKind.Start, first);
            e.Note = Fmt.Format("{0}, level {1}", Name(first.PlayerName), first.Level);
            Emit(e);
            _haveCrumb = true;
            _lastCrumb = first.Pos;
            QueueQuestLog(first, "Quest log at the start");
            TryQuestLog();
        }

        public void Process(Snap prev, Snap cur, bool afterLoading, long gapMs, bool afterBot)
        {
            if (prev == null)
            {
                Begin(cur);
                return;
            }

            if (cur.PlayerId != prev.PlayerId)
            {
                RecEvent e = New(EvKind.CharacterChange, cur);
                e.Note = Name(prev.PlayerName) + " -> " + Name(cur.PlayerName) + Fmt.Format(" (level {0})", cur.Level);
                SetFrom(e, prev);
                Emit(e);
                ResetTransient();
                _lastCrumb = cur.Pos;
                QueueQuestLog(cur, "Quest log of " + Name(cur.PlayerName));
                TryQuestLog();
                return;
            }

            if (afterBot)
            {
                RecEvent e = New(EvKind.Resumed, cur);
                e.DurationMs = gapMs;
                e.Note = "the bot ran for " + Fmt.Duration(gapMs / 1000.0) + "; what it did is not part of this recording";
                SetFrom(e, prev);
                Emit(e);
                ResetTransient();
                _lastCrumb = cur.Pos;
                QueueQuestLog(cur, "Quest log after the bot ran");
                TryQuestLog();
                return;
            }

            TryQuestLog();
            Clicks(cur);
            OtherCommand(cur);
            Casts(prev, cur);
            Movement(prev, cur, afterLoading, gapMs);
            ConversationStart(prev, cur);
            ReplyWheel(cur);
            Interaction(prev, cur);
            BoardOpen(cur);
            CombatAndKills(prev, cur);
            Quests(prev, cur);
            Endings(prev, cur);
            Breadcrumb(cur);
        }

        public void Marker(Snap cur, string note, string source)
        {
            _markers++;
            RecEvent e = New(EvKind.Marker, cur);
            string n = Fmt.OneLine(note);
            e.Note = n.Length > 0 ? n : "Marker " + _markers.ToString();
            e.Option = source ?? string.Empty;
            Emit(e);
        }

        public void Paused(Snap last, string why)
        {
            RecEvent e = New(EvKind.Paused, last);
            e.Note = why;
            Emit(e);
        }

        public void Warning(Snap cur, string text)
        {
            string t = Fmt.OneLine(text);
            if (_warned.Count > 50 || !_warned.Add(t))
                return;
            RecEvent e = New(EvKind.Warning, cur);
            e.Note = t;
            Emit(e);
        }

        public void End(Snap last, string why)
        {
            if (_castSpec != 0 && last != null)
                EndCast(last);
            if (_wheel != null && last != null)
                ResolveWheel(last);
            RecEvent e = New(EvKind.Stop, last);
            e.Note = why ?? string.Empty;
            if (_wheels > 0)
                e.AddDetail(Fmt.Format("reply wheels seen: {0}; picked differently from the bot's dark auto-pick: {1}", _wheels, _wheelsDiffering));
            Emit(e);
        }

        // ------------------------------------------------------------------ quest log listing

        private void QueueQuestLog(Snap s, string title)
        {
            _questLogPending = true;
            _questLogWait = 0;
            _questLogSnap = s;
            _questLogTitle = title;
        }

        /// <summary>The listing waits a few looks so the quest titles (read a few per look) are known.</summary>
        private void TryQuestLog()
        {
            if (!_questLogPending || _questLogSnap == null)
                return;
            _questLogWait++;
            bool allKnown = true;
            foreach (QSnap q in _questLogSnap.Quests)
            {
                QuestInfo qi = _reader.Quest(q.QuestId);
                if (!qi.Resolved && qi.Attempts < 3)
                {
                    allKnown = false;
                    break;
                }
            }
            if (!allKnown && _questLogWait < 15)
                return;
            _questLogPending = false;
            RecEvent e = New(EvKind.QuestLog, _questLogSnap);
            e.Note = _questLogTitle;
            e.Quests = new List<QuestLine>();
            foreach (QSnap q in _questLogSnap.Quests)
            {
                var line = new QuestLine();
                line.QuestId = q.QuestId;
                line.Name = _reader.Quest(q.QuestId).Name;
                line.Branch = q.Branch;
                line.Step = q.Step;
                line.TaskTotal = TaskTotal(q);
                line.Counts = CountsText(q);
                line.Hidden = q.Hidden;
                StepInfo si = _reader.Step(q.QuestId, q.Branch, q.Step);
                if (si.Resolved && si.Tasks.Count > 0)
                    line.Text = si.Tasks[0].Text;
                e.Quests.Add(line);
            }
            Emit(e);
        }

        // ------------------------------------------------------------------ clicks and casts

        private void Clicks(Snap cur)
        {
            ObjRef c = cur.Click;
            if (c == null)
                return;
            _lastClick = c;
            _lastClickTick = cur.Tick;
            if (c.Kind == ObjKind.Placeable)
            {
                RecEvent e = New(EvKind.ObjectClick, cur);
                e.Target = c;
                e.Option = cur.ClickCommand;
                Emit(e);
                if (cur.SameKind != null)
                    foreach (ObjRef same in cur.SameKind)
                    {
                        if (!_spotted.Add(same.NodeId))
                            continue; // already noted
                        RecEvent sp = New(EvKind.Spot, cur);
                        sp.Target = same;
                        Emit(sp);
                    }
            }
            else if (c.Kind == ObjKind.Npc && !cur.InCombat)
            {
                if (c.NodeId == _lastNpcClickId && cur.Tick - _lastNpcClickTick < 3000)
                    return;
                _lastNpcClickId = c.NodeId;
                _lastNpcClickTick = cur.Tick;
                RecEvent e = New(EvKind.NpcClick, cur);
                e.Target = c;
                e.Option = cur.ClickCommand;
                Emit(e);
            }
        }

        /// <summary>A key or mouse command (not a use) with the mouse over an object: recorded every time out of combat.</summary>
        private void OtherCommand(Snap cur)
        {
            if (string.IsNullOrEmpty(cur.OtherCommand))
                return;
            RecEvent e = New(EvKind.Command, cur);
            e.Option = cur.OtherCommand;
            e.Target = cur.OtherCommandTarget;
            e.Note = "a key or mouse command over it (not a use: right-click or the interact key are the ones that use things)";
            Emit(e);
        }

        private void Casts(Snap prev, Snap cur)
        {
            if (cur.InstantItemName != null)
            {
                RecEvent ie = New(EvKind.QuestItem, cur);
                ie.Option = cur.InstantItemName;
                ie.Target = cur.CastTarget;
                Emit(ie);
            }
            bool newCast = cur.CastSpec != 0 && (!prev.IsCasting || prev.CastSpec != cur.CastSpec);
            if (_castSpec != 0 && (cur.CastSpec == 0 || newCast))
                EndCast(cur);
            if (newCast)
            {
                _castSpec = cur.CastSpec;
                _castName = cur.CastName ?? string.Empty;
                _castStartTick = cur.Tick;
                _castPos = cur.Pos;
                _castArea = cur.AreaId;
                _castInCombat = prev.InCombat || cur.InCombat;
                _castIsQuestItem = cur.QuestItemName != null;
                _castTarget = cur.QuestItemName == null ? cur.CastTarget : null;
                if (cur.QuestItemName != null)
                {
                    RecEvent e = New(EvKind.QuestItem, cur);
                    e.Option = cur.QuestItemName;
                    e.Target = cur.CastTarget;
                    Emit(e);
                }
            }
            else if (_castSpec != 0 && cur.InCombat)
            {
                _castInCombat = true;
            }
        }

        private void EndCast(Snap cur)
        {
            long ms = cur.Tick - _castStartTick;
            _lastCastName = _castName;
            _lastCastEndTick = cur.Tick;
            // Out-of-combat casts, plus casts on an object when a fight broke out during them.
            bool onObject = _castTarget != null && _castTarget.Kind == ObjKind.Placeable;
            if (!_castIsQuestItem && ms >= 300 && ((!_castInCombat && !cur.InCombat) || (!_castInCombat && onObject)))
            {
                RecEvent e = New(EvKind.Cast, cur);
                e.Pos = _castPos;
                e.AreaId = _castArea;
                e.AreaName = _reader.Area(_castArea).Label;
                e.Option = _castName;
                e.DurationMs = ms;
                e.Target = _castTarget;
                Emit(e);
            }
            _castSpec = 0;
            _castTarget = null;
        }

        // ------------------------------------------------------------------ movement

        private void Movement(Snap prev, Snap cur, bool afterLoading, long gapMs)
        {
            bool areaChanged = cur.AreaId != prev.AreaId;
            float dist = cur.Pos.DistanceTo(prev.Pos);
            double dt = Math.Max(0.05, (cur.Tick - prev.Tick) / 1000.0);
            bool moved = false;

            // The ride first: an airlock or shuttle door is what caused the loading screen that follows it.
            if (cur.WonkaArrival != null)
            {
                WonkaArrival w = cur.WonkaArrival;
                RecEvent e = New(EvKind.ElevatorRide, cur);
                e.Target = w.From.Wonka;
                e.HasStand = true;
                e.Stand = w.From.Stand;
                e.Option = w.Dest ?? string.Empty;
                e.Note = w.How;
                SetFrom(e, prev);
                Emit(e);
                moved = true;
            }
            if (afterLoading)
            {
                RecEvent e = New(EvKind.Loading, cur);
                e.DurationMs = gapMs;
                SetFrom(e, prev);
                e.Note = areaChanged ? "into another area" : "same area";
                if (cur.WonkaArrival != null)
                    e.AddDetail("after the " + cur.WonkaArrival.From.Wonka.DisplayName + " ride");
                else if (cur.Tick - _lastCastEndTick < 30000 && _lastCastName.Length > 0)
                    e.AddDetail("after casting " + _lastCastName);
                else if (_lastClick != null && cur.Tick - _lastClickTick < 60000 && gapMs < 120000)
                    e.AddDetail("after clicking " + _lastClick.DisplayName);
                Emit(e);
                moved = true;
            }
            if (areaChanged)
            {
                RecEvent e = New(EvKind.AreaChange, cur);
                SetFrom(e, prev);
                Emit(e);
                moved = true;
            }
            if (!moved && !cur.IsInTaxi && !prev.IsInTaxi && dist >= Cfg.JumpMeters && dist / dt > 25.0)
            {
                RecEvent e = New(EvKind.Jump, cur);
                SetFrom(e, prev);
                e.Note = JumpGuess(prev, cur);
                Emit(e);
                moved = true;
            }
            if (cur.TaxiArrival != null)
            {
                RecEvent e = New(EvKind.TaxiEnd, cur);
                e.Pos = cur.TaxiArrival.LandPos;
                e.Target = _taxiSource;
                if (_haveTaxiStand)
                {
                    e.HasStand = true;
                    e.Stand = _taxiStand;
                }
                TaxiArrival ta = cur.TaxiArrival;
                // Option is the taxi-map name (what UseTransport matches), not the world name of the terminal.
                e.Option = ta.MapName ?? string.Empty;
                e.OptionGuessed = e.Option.Length > 0 && !ta.MapNameSure;
                e.Note = ta.MapNameHow ?? string.Empty;
                e.Target2 = ta.Dest;
                e.Choices = ta.MapChoices;
                if (ta.Dest != null)
                    e.AddDetail("landed next to the terminal object \"" + ta.Dest.DisplayName + "\"" +
                                (ta.Dest.HasPos ? " at " + Fmt.Xyz(ta.Dest.Pos) : string.Empty));
                if (ta.MapChoices != null && ta.MapChoices.Count > 0)
                    e.AddDetail("taxi map destinations: " + string.Join(" | ", ta.MapChoices));
                Emit(e);
                _taxiSource = null;
                _haveTaxiStand = false;
                moved = true;
            }
            if (cur.ShipDest != null)
            {
                RecEvent e = New(EvKind.ShipTravel, cur);
                e.Option = cur.ShipDest;
                Emit(e);
            }
            if (moved)
            {
                _haveCrumb = true;
                _lastCrumb = cur.Pos;
            }
        }

        private string JumpGuess(Snap prev, Snap cur)
        {
            if (prev.IsDead && !cur.IsDead)
                return How.Revived + " (medical center?)";
            if (_castSpec != 0 && _castName.Length > 0)
                return "while casting " + _castName;
            if (cur.Tick - _lastCastEndTick < 20000 && _lastCastName.Length > 0)
                return "after casting " + _lastCastName;
            if (_lastClick != null && cur.Tick - _lastClickTick < ClickWindowMs)
                return "after clicking " + _lastClick.DisplayName + " (door, shuttle or phase?)";
            return "no click or cast just before (phase change, cut scene or teleport?)";
        }

        // ------------------------------------------------------------------ conversations, interactions, boards

        private void ConversationStart(Snap prev, Snap cur)
        {
            if (!cur.InConvo || prev.InConvo)
                return;
            _convoPartner = cur.ConvoPartner;
            _convoComlink = cur.ConvoIsComlink;
            _offered = cur.OfferedQuestId;
            _convoStartTick = cur.Tick;
            RecEvent e = New(EvKind.ConvoStart, cur);
            e.Target = _convoPartner;
            e.Option = cur.ConvoTypeName;
            if (_convoComlink)
                e.Note = How.HolocomCall;
            if (_offered != 0)
            {
                e.QuestId = _offered;
                e.QuestName = _reader.Quest(_offered).Name;
                e.AddDetail("the game says this NPC offers quest " + Fmt.Hex(_offered));
            }
            Emit(e);
        }

        /// <summary>Choice wheels: remember a wheel when it appears.</summary>
        private void ReplyWheel(Snap cur)
        {
            if (_wheel != null && (!cur.InConvo || (cur.ConvoNode >= 0 && cur.ConvoNode != _wheelNode)))
                ResolveWheel(cur);
            if (cur.Replies != null && cur.Replies.Count > 0 && cur.ReplyNode != _wheelNode)
            {
                _wheel = cur.Replies;
                _wheelNode = cur.ReplyNode;
                _wheelSnap = cur;
            }
        }

        private static ReplyOpt AutoPick(List<ReplyOpt> wheel, string align)
        {
            foreach (ReplyOpt o in wheel)
                if (o.Align == align)
                    return o;
            foreach (ReplyOpt o in wheel)
                if (o.Align == "neutral")
                    return o;
            return wheel.Count > 0 ? wheel[0] : null;
        }

        private void ResolveWheel(Snap cur)
        {
            List<ReplyOpt> wheel = _wheel;
            Snap at = _wheelSnap ?? cur;
            _wheel = null;
            _wheelNode = -1;
            _wheelSnap = null;
            _wheels++;

            ReplyOpt picked = null;
            if (cur.InConvo)
            {
                foreach (ReplyOpt o in wheel)
                    if (o.Node == cur.ConvoNode)
                        picked = o;
            }
            ReplyOpt dark = AutoPick(wheel, "dark");
            ReplyOpt light = AutoPick(wheel, "light");

            RecEvent e = New(EvKind.ConvoChoice, at);
            e.Target = _convoPartner;
            e.Choices = new List<string>();
            foreach (ReplyOpt o in wheel)
                e.Choices.Add(o.ToString());
            e.Option = picked != null ? picked.ToString() : "?";
            e.OptionGuessed = picked == null;
            e.Note = "dark auto-pick: " + (dark != null ? dark.ToString() : "?") + ", light auto-pick: " + (light != null ? light.ToString() : "?");
            if (wheel.Count == 1)
                e.AddDetail("only one reply on this wheel: any auto-pick takes it");
            if (picked == null)
                e.AddDetail(cur.InConvo
                    ? Fmt.Format("the conversation went on to node {0}, which is none of the replies' nodes (the reply line was skipped too fast to see)", cur.ConvoNode)
                    : "the conversation ended right after this wheel, so the pick could not be seen");
            else if (dark != null && picked.Option != dark.Option)
            {
                e.CheckFailed = true;
                _wheelsDiffering++;
                e.AddDetail(Fmt.Format("YOU PICKED {0} BUT THE BOT'S DARK AUTO-PICK TAKES {1}: if this reply matters for the quest, the profile needs a hand-picked reply here",
                    picked.ToString(), dark.ToString()));
            }
            e.AddDetail(Fmt.Format("wheel node {0}; replies (number alignment node): {1}", at.ReplyNode, string.Join(", ", wheel.ConvertAll(o => o.ToString() + " node " + o.Node.ToString()))));
            Emit(e);
        }

        private void Interaction(Snap prev, Snap cur)
        {
            if (cur.Interaction == prev.Interaction)
                return;
            var it = (chrInteractionType)cur.Interaction;
            if (it == chrInteractionType.Taxi)
            {
                _taxiSource = cur.InteractionTarget;
                _haveTaxiStand = true;
                _taxiStand = cur.Pos;
                RecEvent e = New(EvKind.TaxiOpen, cur);
                e.Target = _taxiSource;
                e.HasStand = true;
                e.Stand = cur.Pos;
                Emit(e);
            }
            else if (it == chrInteractionType.Wonkavator)
            {
                RecEvent e = New(EvKind.ElevatorOpen, cur);
                e.Target = cur.InteractionTarget;
                e.HasStand = true;
                e.Stand = cur.Pos;
                Emit(e);
            }
            else if (it != chrInteractionType.None && it != chrInteractionType.Conversation && it != chrInteractionType.MissionBoard)
            {
                RecEvent e = New(EvKind.InteractionOpen, cur);
                e.Option = cur.InteractionName;
                e.Target = cur.InteractionTarget;
                Emit(e);
                // a quest window from an NPC (the Recon Droid's "LightweightQuest" gives Asset Liquidation)
                if (cur.InteractionTarget != null && (cur.InteractionName ?? string.Empty).IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _questWindow = cur.InteractionTarget;
                    _questWindowTick = cur.Tick;
                }
            }
        }

        private void BoardOpen(Snap cur)
        {
            if (cur.Board == null)
                return;
            _board = cur.Board;
            RecEvent e = New(EvKind.BoardOpen, cur);
            e.Option = _board.Name;
            e.Target = _board.Terminal;
            foreach (KeyValuePair<ulong, string> m in _board.Missions)
                e.AddDetail("mission " + Fmt.Hex(m.Key) + "  " + m.Value);
            Emit(e);
        }

        // ------------------------------------------------------------------ combat

        private void CombatAndKills(Snap prev, Snap cur)
        {
            if (cur.InCombat && !prev.InCombat)
            {
                _combatStartTick = cur.Tick;
                _combatKills = 0;
                RecEvent e = New(EvKind.CombatStart, cur);
                if (cur.CombatMobs != null && cur.CombatMobs.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (ObjRef m in cur.CombatMobs)
                    {
                        if (sb.Length > 0)
                            sb.Append(", ");
                        sb.Append(m.DisplayName);
                        if (m.Level > 0)
                            sb.Append(" (").Append(Fmt.UInt(m.Level)).Append(m.Toughness.Length > 0 ? " " + m.Toughness : string.Empty).Append(')');
                    }
                    e.Note = sb.ToString();
                    _lastCombatMobs = e.Note;
                }
                Emit(e);
            }
            if (cur.Kills != null)
            {
                foreach (KillInfo k in cur.Kills)
                {
                    _combatKills++;
                    RecEvent e = New(EvKind.Kill, cur);
                    e.Target = k.Mob;
                    if (k.TappedByOther)
                        e.Note = "tagged by another player (no credit for you)";
                    Emit(e);
                }
            }
        }

        // ------------------------------------------------------------------ quests

        private void Quests(Snap prev, Snap cur)
        {
            if (!cur.QuestsAvailable)
                return;
            var prevById = new Dictionary<ulong, QSnap>();
            foreach (QSnap q in prev.Quests)
                prevById[q.QuestId] = q;
            var curIds = new HashSet<ulong>();

            foreach (QSnap q in cur.Quests)
            {
                curIds.Add(q.QuestId);
                QSnap old;
                if (!prevById.TryGetValue(q.QuestId, out old))
                {
                    RecEvent e = New(EvKind.QuestAdded, cur);
                    FillQuest(e, q);
                    string how;
                    string board;
                    e.Target = Recent(cur, q.QuestId, true, out how, out board);
                    e.Note = how;
                    e.Option = board;
                    AddTaskLines(e, q);
                    Emit(e);
                    continue;
                }

                if (q.Holocom && !old.Holocom)
                {
                    RecEvent e = New(EvKind.HolocomWaiting, cur);
                    FillQuest(e, q);
                    Emit(e);
                }
                else if (!q.Holocom && old.Holocom)
                {
                    RecEvent e = New(EvKind.HolocomAnswered, cur);
                    FillQuest(e, q);
                    Emit(e);
                }

                if (old.Branch != q.Branch || old.Step != q.Step)
                {
                    RecEvent e = New(EvKind.QuestStep, cur);
                    FillQuest(e, q);
                    e.HasOldStep = true;
                    e.OldBranch = old.Branch;
                    e.OldStep = old.Step;
                    StepCheck c;
                    if (cur.StepChecks != null && cur.StepChecks.TryGetValue(q.QuestId, out c) && c.Ran)
                    {
                        e.Check = Fmt.Format(
                            "live check: IsBranchStepComplete({0}, {1}, {2}) = {3} (expected True), IsBranchStepComplete({0}, {4}, {5}) = {6} (expected False)",
                            Fmt.Hex(q.QuestId), old.Branch, old.Step, c.OldDone, q.Branch, q.Step, c.NewDone);
                        e.CheckFailed = !c.OldDone || c.NewDone;
                    }
                    AddTaskLines(e, q);
                    Emit(e);
                    continue;
                }

                int n = Math.Min(old.Counts.Length, q.Counts.Length);
                for (int i = 0; i < n; i++)
                {
                    if (q.Counts[i] <= old.Counts[i])
                        continue;
                    RecEvent e = New(EvKind.QuestTask, cur);
                    FillQuest(e, q);
                    e.TaskIndex = i;
                    e.Count = q.Counts[i];
                    StepInfo si = _reader.Step(q.QuestId, q.Branch, q.Step);
                    TaskInfo ti = si.Task(i);
                    if (ti != null)
                    {
                        e.TaskId = ti.TaskId;
                        e.Max = ti.Max;
                        e.Text = ti.Text;
                    }
                    bool gt;
                    if (cur.TaskChecks != null && cur.TaskChecks.TryGetValue(q.QuestId.ToString("X16") + ":" + i.ToString(), out gt))
                    {
                        e.Check = Fmt.Format("live check: IsTaskValueGreaterThan({0}, {1}, {2}, {3}) = {4} (expected True)",
                            Fmt.Hex(q.QuestId), q.Step, i, old.Counts[i], gt);
                        e.CheckFailed = !gt;
                    }
                    Emit(e);
                }
            }

            if (prev.QuestsAvailable)
            {
                foreach (ulong id in cur.PendingRewards)
                {
                    if (prev.PendingRewards.Contains(id))
                        continue;
                    _rewardSeen.Add(id);
                    RecEvent e = New(EvKind.RewardWindow, cur);
                    e.QuestId = id;
                    QuestInfo qi = _reader.Quest(id);
                    e.QuestName = qi.Name;
                    e.QuestFqn = qi.Fqn;
                    Emit(e);
                }
            }

            foreach (QSnap old in prev.Quests)
            {
                if (curIds.Contains(old.QuestId))
                    continue;
                RemovedInfo ri = null;
                if (cur.Removed != null)
                    cur.Removed.TryGetValue(old.QuestId, out ri);
                EvKind kind = EvKind.QuestRemoved;
                if (ri != null && ri.Known)
                    kind = ri.Completed ? EvKind.QuestCompleted : (ri.Abandoned ? EvKind.QuestAbandoned : EvKind.QuestRemoved);
                RecEvent e = New(kind, cur);
                FillQuest(e, old);
                if (kind == EvKind.QuestCompleted)
                {
                    string how;
                    string board;
                    e.Target = Recent(cur, old.QuestId, false, out how, out board);
                    e.Note = how;
                    if (_rewardSeen.Contains(old.QuestId))
                        e.Option = "reward window was shown";
                }
                Emit(e);
            }
        }

        private void FillQuest(RecEvent e, QSnap q)
        {
            QuestInfo qi = _reader.Quest(q.QuestId);
            e.QuestId = q.QuestId;
            e.QuestName = qi.Name;
            e.QuestFqn = qi.Fqn;
            e.QuestHidden = q.Hidden;
            e.HasStep = true;
            e.Branch = q.Branch;
            e.Step = q.Step;
            e.TaskTotal = TaskTotal(q);
        }

        private int TaskTotal(QSnap q)
        {
            StepInfo si = _reader.Step(q.QuestId, q.Branch, q.Step);
            if (si.Resolved)
                return si.Tasks.Count;
            return q.Counts.Length;
        }

        private string CountsText(QSnap q)
        {
            StepInfo si = _reader.Step(q.QuestId, q.Branch, q.Step);
            var sb = new StringBuilder();
            int n = Math.Max(q.Counts.Length, si.Tasks.Count);
            for (int i = 0; i < n; i++)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                TaskInfo ti = si.Task(i);
                string count = i < q.Counts.Length ? Fmt.UInt(q.Counts[i]) : "?";
                sb.Append("task ").Append(i).Append(' ').Append(count);
                if (ti != null && ti.Max > 0)
                    sb.Append('/').Append(Fmt.UInt(ti.Max));
                if (ti != null && ti.Text.Length > 0)
                    sb.Append(" \"").Append(ti.Text).Append('"');
            }
            return sb.ToString();
        }

        private void AddTaskLines(RecEvent e, QSnap q)
        {
            StepInfo si = _reader.Step(q.QuestId, q.Branch, q.Step);
            if (!si.Resolved)
                return;
            foreach (TaskInfo ti in si.Tasks)
            {
                string count = ti.Index < q.Counts.Length ? Fmt.UInt(q.Counts[ti.Index]) : "?";
                string line = Fmt.Format("task {0} (TaskId {1}): {2}{3}", ti.Index, ti.TaskId, count,
                    ti.Max > 0 ? "/" + Fmt.UInt(ti.Max) : string.Empty);
                if (ti.Text.Length > 0)
                    line += "  \"" + ti.Text + "\"";
                e.AddDetail(line);
                if (e.Text.Length == 0 && ti.Text.Length > 0)
                    e.Text = ti.Text;
            }
        }

        /// <summary>Who gave (or took) a quest: the NPC you talk to, the mission board, or what you just clicked.</summary>
        private ObjRef Recent(Snap cur, ulong questId, bool forPickup, out string how, out string board)
        {
            how = string.Empty;
            board = string.Empty;
            bool talkingNow = cur.InConvo;
            bool talkedRecently = talkingNow || cur.Tick - _convoEndTick <= TalkWindowMs;
            bool boardNow = forPickup && _board != null && cur.BoardOpen;
            bool boardRecently = forPickup && _board != null && (cur.BoardOpen || cur.Tick - _boardCloseTick <= BoardWindowMs);
            if (forPickup && _offered != 0 && _offered == questId && _convoPartner != null && talkedRecently)
            {
                how = How.Offered;
                return _convoPartner;
            }
            // A board that never listed this quest didn't give it: you were talking to someone (End of Torvix comes
            // from General Konya, not the Black Hole Mission Terminal you had just used).
            // a quest window you just opened on an NPC gave it
            if (forPickup && _questWindow != null && cur.Tick - _questWindowTick <= TalkWindowMs)
            {
                how = "from its quest window";
                return _questWindow;
            }
            bool boardHasIt = _board != null && (_board.Missions.Count == 0 || _board.Missions.Exists(m => m.Key == questId));
            if (!boardHasIt && talkedRecently && _convoPartner != null && !_convoComlink)
            {
                how = How.Talking;
                return _convoPartner;
            }
            // What is open right now wins over what ended a moment ago.
            if (boardNow)
                return Board(out how, out board);
            if (talkingNow && _convoComlink)
            {
                how = How.Holocom;
                return null;
            }
            if (talkingNow && _convoPartner != null)
            {
                how = How.Talking;
                return _convoPartner;
            }
            // Then whichever ended last: the conversation or the mission board.
            if (boardRecently && (!talkedRecently || _boardCloseTick >= _convoEndTick))
                return Board(out how, out board);
            if (talkedRecently && _convoComlink)
            {
                how = How.Holocom;
                return null;
            }
            if (talkedRecently && _convoPartner != null)
            {
                how = How.Talking;
                return _convoPartner;
            }
            if (boardRecently)
                return Board(out how, out board);
            if (_lastClick != null && cur.Tick - _lastClickTick <= ClickWindowMs)
            {
                how = How.Clicked;
                return _lastClick;
            }
            how = forPickup ? How.NoGiver : How.NoTurnIn;
            return null;
        }

        private ObjRef Board(out string how, out string board)
        {
            board = _board.Name;
            how = How.Board;
            return _board.Terminal;
        }

        // ------------------------------------------------------------------ endings and small state changes

        private void Endings(Snap prev, Snap cur)
        {
            if (!cur.InConvo && prev.InConvo)
            {
                RecEvent e = New(EvKind.ConvoEnd, cur);
                e.Target = _convoPartner;
                e.DurationMs = cur.Tick - _convoStartTick;
                if (_convoComlink)
                    e.Note = How.HolocomCall;
                Emit(e);
                _convoEndTick = cur.Tick;
            }
            if (!cur.BoardOpen && prev.BoardOpen)
            {
                RecEvent e = New(EvKind.BoardClose, cur);
                e.Option = _board != null ? _board.Name : string.Empty;
                Emit(e);
                _boardCloseTick = cur.Tick;
            }
            if (!cur.InCombat && prev.InCombat)
            {
                RecEvent e = New(EvKind.CombatEnd, cur);
                e.DurationMs = cur.Tick - _combatStartTick;
                e.Note = _combatKills == 1 ? "1 kill" : _combatKills.ToString() + " kills";
                Emit(e);
            }
            if (cur.IsDead && !prev.IsDead)
            {
                RecEvent e = New(EvKind.Died, cur);
                if (prev.InCombat || cur.InCombat)
                    e.Note = _lastCombatMobs;
                Emit(e);
            }
            else if (!cur.IsDead && prev.IsDead)
            {
                Emit(New(EvKind.Revived, cur));
            }
            if (cur.IsMounted && !prev.IsMounted)
            {
                RecEvent e = New(EvKind.Mounted, cur);
                e.Option = cur.MountName ?? string.Empty;
                Emit(e);
            }
            else if (!cur.IsMounted && prev.IsMounted)
            {
                Emit(New(EvKind.Dismounted, cur));
            }
            if (cur.IsInTaxi && !prev.IsInTaxi)
            {
                RecEvent e = New(EvKind.TaxiStart, cur);
                e.Target = _taxiSource;
                Emit(e);
            }
        }

        private void Breadcrumb(Snap cur)
        {
            int every = Cfg.BreadcrumbMeters;
            if (!_haveCrumb)
            {
                _haveCrumb = true;
                _lastCrumb = cur.Pos;
                return;
            }
            if (cur.IsInTaxi || cur.IsDead)
            {
                _lastCrumb = cur.Pos;
                return;
            }
            if (every <= 0 || cur.Pos.DistanceTo(_lastCrumb) < every)
                return;
            _lastCrumb = cur.Pos;
            Emit(New(EvKind.Breadcrumb, cur));
        }

        private void ResetTransient()
        {
            _wheel = null;
            _wheelNode = -1;
            _wheelSnap = null;
            _convoPartner = null;
            _convoComlink = false;
            _offered = 0;
            _convoEndTick = long.MinValue / 2;
            _lastClick = null;
            _lastClickTick = long.MinValue / 2;
            _taxiSource = null;
            _haveTaxiStand = false;
            _board = null;
            _boardCloseTick = long.MinValue / 2;
            _castSpec = 0;
            _castTarget = null;
            _lastCastName = string.Empty;
            _lastCastEndTick = long.MinValue / 2;
        }

        // ------------------------------------------------------------------ helpers

        private RecEvent New(EvKind kind, Snap s)
        {
            var e = new RecEvent();
            e.Kind = kind;
            if (s != null)
            {
                e.Time = s.Time;
                e.Pos = s.Pos;
                e.Heading = s.Heading;
                e.AreaId = s.AreaId;
                e.AreaName = _reader.Area(s.AreaId).Label;
            }
            else
            {
                e.Time = DateTime.Now;
            }
            return e;
        }

        private void SetFrom(RecEvent e, Snap prev)
        {
            e.HasFrom = true;
            e.FromPos = prev.Pos;
            e.FromArea = prev.AreaId;
            e.FromAreaName = _reader.Area(prev.AreaId).Label;
        }

        private void Emit(RecEvent e)
        {
            e.Summary = EventText.Headline(e);
            _session.Add(e);
        }

        private static string Name(string n)
        {
            return string.IsNullOrEmpty(n) ? "(unknown character)" : n;
        }
    }
}
