// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuestRecorder
{
    /// <summary>What you did, as one fact.</summary>
    internal enum FactKind
    {
        Kill,
        Loot,
        UseObject,
        Talk,
        Item,
        Ability,
        Board,
        /// <summary>A mission item that moved you far (a transport): its press, where you stood and where you landed.</summary>
        Hop,
    }

    /// <summary>
    /// One thing you did, put together from every row and log line that saw it: a kill (the recorder's row and the
    /// log's death line), a click on an object (the click, the cast that started, the cast that ended, the log's
    /// activation), a mission item use (the log's activation, the item leaving your bags), and so on.
    /// </summary>
    internal sealed class Fact
    {
        public FactKind Kind;
        /// <summary>Seconds from the recording's start (the log's exact time when the log saw it).</summary>
        public double At;
        /// <summary>When it was done (a cast's end); the same as <see cref="At"/> when instant.</summary>
        public double End;
        /// <summary>The look it ended in (rows with one time are one look of the recorder).</summary>
        public int Look;
        /// <summary>The look it began in.</summary>
        public int FirstLook;
        /// <summary>The recording row it came from; 0 = the combat log alone saw it.</summary>
        public int Seq;
        public Vec3 Me;
        public ulong Area;
        /// <summary>What it was done to (type id, node, anchor, where); null = nothing or not known.</summary>
        public ObjRef Target;
        /// <summary>The mission item used (its name as the recording has it).</summary>
        public string Item = string.Empty;
        /// <summary>The cast's or ability's name ("Accessing", "Using Serum...").</summary>
        public string Cast = string.Empty;
        /// <summary>The combat log's own id for that ability (0 = no log line).</summary>
        public ulong LogAbility;
        public bool InFight;
        public bool LogOnly;
        public bool ByCompanion;
        public bool Interrupted;
        /// <summary>How many times you clicked it (an object use: the clicks on that one object close together).</summary>
        public int Clicks = 1;
        internal bool SawClick;
        /// <summary>Where a hop landed you, and the area it put you in.</summary>
        public Vec3 Landing;
        public ulong LandArea;
        /// <summary>When the first cast of an object use ended (its effect); -1 = no cast end was seen.</summary>
        public double CastDone = -1;
        /// <summary>A talk where a conversation opened (not a bare click on the NPC).</summary>
        public bool Convo;
        /// <summary>A click on it that did the step without a conversation: your mission item left your bags on it as the step changed.</summary>
        public bool Did;
        public readonly List<string> Notes = new List<string>();

        public ulong Spec
        {
            get { return Target != null ? Target.Spec : 0; }
        }

        public ulong Node
        {
            get { return Target != null ? Target.NodeId : 0; }
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Fmt.F1(At)).Append(' ').Append(Kind);
            if (Item.Length > 0)
                sb.Append(" item=\"").Append(Item).Append('"');
            if (Cast.Length > 0)
                sb.Append(" cast=\"").Append(Cast).Append('"');
            if (LogAbility != 0)
                sb.Append(" log=").Append(LogAbility.ToString(CultureInfo.InvariantCulture));
            if (Target != null)
                sb.Append(" on ").Append(Target.KindText).Append(" \"").Append(Target.Name).Append("\" node=").Append(Fmt.Hex(Target.NodeId))
                  .Append(" spec=").Append(Fmt.Hex(Target.Spec)).Append(Target.Anchor != 0 ? " anchor=" + Fmt.Hex(Target.Anchor) : string.Empty);
            if (InFight)
                sb.Append(" in-fight");
            if (LogOnly)
                sb.Append(" log-only");
            if (ByCompanion)
                sb.Append(" companion");
            if (Interrupted)
                sb.Append(" interrupted");
            sb.Append(" seq=").Append(Seq).Append(" look=").Append(Look);
            foreach (string n in Notes)
                sb.Append(" [").Append(n).Append(']');
            return sb.ToString();
        }
    }

    internal enum TrackKind
    {
        Added,
        Task,
        Step,
        Completed,
        Removed,
        Reward,
        Holocom,
    }

    /// <summary>One change the mission tracker showed: what counted.</summary>
    internal sealed class Track
    {
        public TrackKind Kind;
        public RecEvent Ev;
        public double At;
        public int Look;
        public ulong Quest;
        public ulong Branch;
        public ulong Step;
        public ulong OldBranch;
        public ulong OldStep;
        public int Task = -1;
        public ulong Count;
        public ulong Max;
        public List<ulong> Targets = new List<ulong>();

        public override string ToString()
        {
            return Fmt.F1(At) + " " + Kind + " " + Fmt.Hex(Quest) + " b" + Branch + " s" + Step +
                   (Kind == TrackKind.Step ? " (from b" + OldBranch + " s" + OldStep + ")" : string.Empty) +
                   (Task >= 0 ? " task " + Task + " " + Count + "/" + Max : string.Empty) + " seq=" + (Ev != null ? Ev.Seq : 0) + " look=" + Look;
        }
    }

    /// <summary>
    /// B1 and B2 of the new core: every row of the recording and every line of the combat log that covers it, turned
    /// into one list of facts (what you did) and one list of tracker changes (what counted). Ids first: the log's
    /// instance number is the recording's node id, a kill gets its type id from any row that saw that node, a log
    /// activation and a recorder row of the same ability within 1.5 s are one action.
    /// </summary>
    internal sealed class CoreFacts
    {
        public List<RecEvent> Events;
        public int[] LookOf;
        public double[] AtOf;
        public DateTime Start;
        public DateTime End;
        /// <summary>The fullest description of each node any row had (type id, anchor, where, marker).</summary>
        public readonly Dictionary<ulong, ObjRef> Nodes = new Dictionary<ulong, ObjRef>();
        public readonly List<LogLine> Log = new List<LogLine>();
        public readonly List<string> LogFiles = new List<string>();
        /// <summary>Seconds of the recording the combat log covers (from, to).</summary>
        public double LogFrom = double.MaxValue, LogTo = double.MinValue;
        public readonly List<Fact> Facts = new List<Fact>();
        public readonly List<Track> Tracks = new List<Track>();
        /// <summary>The mission items the recording named.</summary>
        public readonly HashSet<string> MissionItems = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Log ability ids of your class kit, mounts and the like: never a mission action.</summary>
        public readonly HashSet<ulong> Kit = new HashSet<ulong>();
        /// <summary>Your fights as the log had them (seconds from, to).</summary>
        public readonly List<KeyValuePair<double, double>> Fights = new List<KeyValuePair<double, double>>();
        public readonly List<string> Notes = new List<string>();
        /// <summary>Your companions' node ids (from the combat log): never a mission's NPC.</summary>
        public readonly HashSet<ulong> Companions = new HashSet<ulong>();

        private readonly HashSet<string> _castPrefixes = new HashSet<string>(StringComparer.Ordinal) { "Using " };
        private readonly HashSet<string> _mounts = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<double, LogEnt>> _myTarget = new List<KeyValuePair<double, LogEnt>>();
        private readonly Dictionary<ulong, LogLine> _deaths = new Dictionary<ulong, LogLine>();
        private readonly List<LogLine> _acts = new List<LogLine>();
        private readonly HashSet<LogLine> _usedActs = new HashSet<LogLine>();
        /// <summary>Item rows that are a hop's press, or rows written while its cast ran (not uses).</summary>
        private readonly HashSet<int> _hopRows = new HashSet<int>();
        /// <summary>A mission item whose cast the log names otherwise (the Anti-rad Injector's "Hypermatter Adrenal").</summary>
        private readonly Dictionary<string, string> _itemCasts = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool HasLog
        {
            get { return Log.Count > 0; }
        }

        /// <summary>The combat log covers this moment of the recording.</summary>
        public bool LogCovers(double at)
        {
            return HasLog && at >= LogFrom - 5 && at <= LogTo + 5;
        }

        public double Sec(DateTime t)
        {
            return (t - Start).TotalSeconds;
        }

        public static CoreFacts Build(List<RecEvent> events, IEnumerable<string> logFolders)
        {
            var c = new CoreFacts { Events = events };
            c.Looks();
            c.CollectNodes();
            c.CollectNames();
            if (logFolders != null)
                c.ReadLog(logFolders);
            c.FindKit();
            c.FindHops();
            c.FromRows();
            c.FromLog();
            // a click on the NPC an item went on (to pick it, or a click right after that opened nothing) is not a talk
            foreach (Fact u in c.Facts.FindAll(x => x.Kind == FactKind.Item && x.Target != null && !x.LogOnly))
                c.DropClickOn(u);
            c.Facts.Sort((a, b) => a.At.CompareTo(b.At));
            c.CollectTracks();
            return c;
        }

        // ------------------------------------------------------------------ looks and nodes

        private void Looks()
        {
            int n = Events.Count;
            LookOf = new int[n];
            AtOf = new double[n];
            Start = DateTime.MinValue;
            foreach (RecEvent e in Events)
                if (e.Time != default(DateTime))
                {
                    Start = e.Time.AddSeconds(-e.Elapsed);
                    break;
                }
            int look = 0;
            for (int i = 0; i < n; i++)
            {
                RecEvent e = Events[i];
                if (i > 0 && (e.Time != Events[i - 1].Time || e.Time == default(DateTime) && e.Elapsed != Events[i - 1].Elapsed))
                    look++;
                LookOf[i] = look;
                AtOf[i] = e.Time != default(DateTime) && Start != DateTime.MinValue ? Sec(e.Time) : e.Elapsed;
            }
            End = n > 0 && Events[n - 1].Time != default(DateTime) ? Events[n - 1].Time : Start;
        }

        private void CollectNodes()
        {
            foreach (RecEvent e in Events)
            {
                ObjRef o = e.Target;
                if (o == null || o.NodeId == 0)
                    continue;
                ObjRef best;
                if (!Nodes.TryGetValue(o.NodeId, out best))
                {
                    Nodes[o.NodeId] = o.Clone(null);
                    continue;
                }
                if (best.Spec == 0 && o.Spec != 0)
                    best.Spec = o.Spec;
                if (best.Anchor == 0 && o.Anchor != 0)
                    best.Anchor = o.Anchor;
                if (!best.HasPos && o.HasPos)
                {
                    best.HasPos = true;
                    best.Pos = o.Pos;
                }
                if (best.Indication.Length == 0 && o.Indication.Length > 0)
                    best.Indication = o.Indication;
                best.UsableForQuest |= o.UsableForQuest;
                if (best.UseRange <= 0 && o.UseRange > 0)
                    best.UseRange = o.UseRange;
                if (best.Toughness.Length == 0 && o.Toughness.Length > 0)
                    best.Toughness = o.Toughness;
            }
        }

        /// <summary>The node's description with its type id filled in from every row that saw it.</summary>
        public ObjRef Full(ObjRef o)
        {
            if (o == null)
                return null;
            ObjRef c = o.Clone(null);
            ObjRef best;
            if (o.NodeId != 0 && Nodes.TryGetValue(o.NodeId, out best))
            {
                if (c.Spec == 0)
                    c.Spec = best.Spec;
                if (c.Anchor == 0)
                    c.Anchor = best.Anchor;
                if (!c.HasPos && best.HasPos)
                {
                    c.HasPos = true;
                    c.Pos = best.Pos;
                }
                if (c.Toughness.Length == 0)
                    c.Toughness = best.Toughness;
            }
            return c;
        }

        /// <summary>Cast names the recorder saw on an object ("Searching...", "Looting"): a click on an object.</summary>
        private readonly HashSet<string> _objectCasts = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A cast bar the recorder saw on an object you clicked ("Accessing", "Disabling..."), not an ability the combat log named.</summary>
        public bool IsObjectCastName(string name)
        {
            return name != null && name.Length > 0 && _objectCasts.Contains(name);
        }

        private void CollectNames()
        {
            foreach (RecEvent e in Events)
            {
                if ((e.Kind == EvKind.Cast || e.Kind == EvKind.CastStart) && e.Target != null && e.Target.Kind == ObjKind.Placeable && e.Option.Length > 0 && e.Option != "?")
                    _objectCasts.Add(e.Option);
                if (e.Kind == EvKind.QuestItem && e.Option.Length > 0)
                    MissionItems.Add(e.Option);
                if (e.Kind == EvKind.Mounted && e.Option.Length > 0)
                    _mounts.Add(e.Option);
            }
        }

        /// <summary>
        /// The mission item a cast or log ability names: its own name, or the cast-bar text "Using Serum..." for the one
        /// item whose name starts with "Serum". Two items fit: none (no guess; <see cref="ItemsOf"/> lists them).
        /// </summary>
        public string ItemOf(string ability)
        {
            List<string> l = ItemsOf(ability);
            return l.Count == 1 ? l[0] : null;
        }

        /// <summary>Every mission item a cast or log ability's name could mean (none, one, or several that fit).</summary>
        public List<string> ItemsOf(string ability)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(ability))
                return list;
            if (MissionItems.Contains(ability))
            {
                list.Add(ability);
                return list;
            }
            string named;
            if (_itemCasts.TryGetValue(ability, out named))
            {
                list.Add(named);
                return list;
            }
            string t = ability.TrimEnd('.', ' ', '\u2026');
            foreach (string head in _castPrefixes)
            {
                if (!t.StartsWith(head, StringComparison.Ordinal) || t.Length - head.Length < 3)
                    continue;
                string bare = t.Substring(head.Length);
                foreach (string v in MissionItems)
                    if ((v == bare || v.StartsWith(bare + " ", StringComparison.OrdinalIgnoreCase)) && !list.Contains(v))
                        list.Add(v);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // ------------------------------------------------------------------ the combat log

        private void ReadLog(IEnumerable<string> folders)
        {
            if (Start == DateTime.MinValue)
                return;
            foreach (string f in CombatLog.FilesFor(Start, End, folders))
            {
                try
                {
                    List<LogLine> lines = CombatLog.Read(f, Start, End);
                    if (lines.Count == 0)
                        continue;
                    LogFiles.Add(f);
                    Log.AddRange(lines);
                }
                catch (Exception ex)
                {
                    Notes.Add("could not read the combat log " + System.IO.Path.GetFileName(f) + ": " + ex.Message);
                }
            }
            if (Log.Count == 0)
            {
                Notes.Add("no combat log covers this recording (turn combat logging on in the game's settings for the best profile)");
                return;
            }
            Log.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Line.CompareTo(b.Line));
            LogFrom = Math.Max(0, Sec(Log[0].Time));
            LogTo = Sec(Log[Log.Count - 1].Time);
            double fightFrom = -1;
            foreach (LogLine l in Log)
            {
                double t = Sec(l.Time);
                if (l.Src.Who == LogWho.Companion && l.Src.Instance != 0)
                    Companions.Add(l.Src.Instance);
                if (l.KindId != CombatLog.KEvent)
                    continue;
                if (l.Src.Who == LogWho.Me)
                {
                    switch (l.WhatId)
                    {
                        case CombatLog.EnterCombat:
                            fightFrom = t;
                            break;
                        case CombatLog.ExitCombat:
                            if (fightFrom >= 0)
                                Fights.Add(new KeyValuePair<double, double>(fightFrom, t));
                            fightFrom = -1;
                            break;
                        case CombatLog.TargetSet:
                            _myTarget.Add(new KeyValuePair<double, LogEnt>(t, l.Tgt));
                            break;
                        case CombatLog.TargetCleared:
                            _myTarget.Add(new KeyValuePair<double, LogEnt>(t, null));
                            break;
                        case CombatLog.AbilityActivate:
                            _acts.Add(l);
                            break;
                    }
                }
                if (l.WhatId == CombatLog.Death && l.Tgt.Who == LogWho.Npc && l.Tgt.Instance != 0 && !_deaths.ContainsKey(l.Tgt.Instance))
                    _deaths[l.Tgt.Instance] = l;
            }
            if (fightFrom >= 0)
                Fights.Add(new KeyValuePair<double, double>(fightFrom, LogTo));
        }

        public bool InFightAt(double t)
        {
            foreach (KeyValuePair<double, double> f in Fights)
                if (t >= f.Key - 0.5 && t <= f.Value + 0.5)
                    return true;
            return false;
        }

        /// <summary>Your target at that moment as the log had it (null = none).</summary>
        private LogEnt TargetAt(double t)
        {
            LogEnt cur = null;
            foreach (KeyValuePair<double, LogEnt> p in _myTarget)
            {
                if (p.Key > t + 0.05)
                    break;
                cur = p.Value;
            }
            return cur;
        }

        /// <summary>
        /// Your class kit, mounts and travel: every ability that did damage or healing, that you used 3 or more times in
        /// fights, or 5 or more times in all, or that mounted you. A mission item's ability never counts as kit.
        /// </summary>
        private void FindKit()
        {
            if (!HasLog)
                return;
            var total = new Dictionary<ulong, int>();
            var inFight = new Dictionary<ulong, int>();
            var names = new Dictionary<ulong, string>();
            foreach (LogLine a in _acts)
            {
                int n;
                total[a.AbilityId] = total.TryGetValue(a.AbilityId, out n) ? n + 1 : 1;
                if (InFightAt(Sec(a.Time)))
                    inFight[a.AbilityId] = inFight.TryGetValue(a.AbilityId, out n) ? n + 1 : 1;
                names[a.AbilityId] = a.Ability;
            }
            var hits = new HashSet<ulong>();
            foreach (LogLine l in Log)
                if (l.Src.Who == LogWho.Me && l.KindId == CombatLog.KApplyEffect && (l.WhatId == CombatLog.Damage || l.WhatId == CombatLog.Heal) && l.AbilityId != 0)
                    hits.Add(l.AbilityId);
            foreach (KeyValuePair<ulong, int> p in total)
            {
                string name = names[p.Key];
                if (ItemsOf(name).Count > 0 || _objectCasts.Contains(name))
                    continue;
                int f;
                inFight.TryGetValue(p.Key, out f);
                if (hits.Contains(p.Key) || f >= 3 || p.Value >= 5 || _mounts.Contains(name))
                    Kit.Add(p.Key);
            }
        }

        /// <summary>The log activation of this cast or item: the same name (or the item the name means) within the window.</summary>
        private LogLine MatchAct(string cast, string item, double from, double to)
        {
            if (!HasLog)
                return null;
            LogLine best = null;
            double bestD = double.MaxValue;
            foreach (LogLine a in _acts)
            {
                if (_usedActs.Contains(a))
                    continue;
                double t = Sec(a.Time);
                if (t < from - 1.5 || t > to + 1.5)
                    continue;
                bool same = (cast.Length > 0 && cast != "?" && a.Ability == cast) || (item.Length > 0 && ItemsOf(a.Ability).Contains(item));
                if (!same)
                    continue;
                double d = Math.Abs(t - from);
                if (d < bestD)
                {
                    bestD = d;
                    best = a;
                }
            }
            if (best != null)
                _usedActs.Add(best);
            return best;
        }

        /// <summary>Who an activation went on: the first effect of that ability you put on a mob, else your target.</summary>
        private LogEnt OnWhom(LogLine act, double castSecs)
        {
            double t = Sec(act.Time);
            foreach (LogLine l in Log)
            {
                double u = Sec(l.Time);
                if (u < t - 0.01)
                    continue;
                if (u > t + castSecs + 1.0)
                    break;
                if (l.Src.Who == LogWho.Me && l.AbilityId == act.AbilityId && l.KindId == CombatLog.KApplyEffect && l.Tgt.Who == LogWho.Npc && l.Tgt.Instance != 0)
                    return l.Tgt;
            }
            LogEnt cur = TargetAt(t);
            return cur != null && cur.Who == LogWho.Npc ? cur : null;
        }

        /// <summary>A log side as an object reference, filled in from the recording's rows of that node.</summary>
        private ObjRef RefOf(LogEnt e)
        {
            if (e == null || e.Instance == 0)
                return null;
            ObjRef best;
            ObjRef o = Nodes.TryGetValue(e.Instance, out best) ? best.Clone("the combat log") : new ObjRef { NodeId = e.Instance, Kind = ObjKind.Npc, How = "the combat log" };
            if (o.Name.Length == 0)
                o.Name = e.Name;
            if (e.HasPos)
            {
                o.HasPos = true;
                o.Pos = e.Pos;
            }
            if (e.HpMax > 0)
                o.Health = 100f * e.Hp / e.HpMax;
            o.Dead = e.Hp == 0;
            return o;
        }

        // ------------------------------------------------------------------ facts from the recording's rows

        private Fact NewFact(FactKind k, int i)
        {
            RecEvent e = Events[i];
            var f = new Fact { Kind = k, At = AtOf[i], End = AtOf[i], Look = LookOf[i], FirstLook = LookOf[i], Seq = e.Seq, Me = e.Pos, Area = e.AreaId };
            f.InFight = HasLog && LogCovers(f.At) ? InFightAt(f.At) : InCombatRows(i);
            return f;
        }

        /// <summary>Without a log: between a CombatStart and its CombatEnd row.</summary>
        private bool InCombatRows(int i)
        {
            for (int j = i; j >= 0 && AtOf[i] - AtOf[j] < 600; j--)
            {
                if (Events[j].Kind == EvKind.CombatEnd && j < i)
                    return false;
                if (Events[j].Kind == EvKind.CombatStart)
                    return true;
            }
            return false;
        }

        private void FromRows()
        {
            var open = new Dictionary<ulong, Fact>(); // object uses still collecting their click, cast start and cast
            var loots = new Dictionary<ulong, Fact>();
            for (int i = 0; i < Events.Count; i++)
            {
                RecEvent e = Events[i];
                double t = AtOf[i];
                switch (e.Kind)
                {
                    case EvKind.Kill:
                    {
                        if (e.Target == null)
                            break;
                        Fact f = NewFact(FactKind.Kill, i);
                        f.Target = Full(e.Target);
                        f.InFight = true;
                        LogLine d;
                        if (e.Target.NodeId != 0 && _deaths.TryGetValue(e.Target.NodeId, out d))
                        {
                            f.At = f.End = Math.Min(t, Sec(d.Time));
                            f.ByCompanion = d.Src.Who == LogWho.Companion;
                            if (d.Tgt.HasPos)
                            {
                                f.Target.HasPos = true;
                                f.Target.Pos = d.Tgt.Pos;
                            }
                        }
                        if (f.Target.Spec == 0)
                            f.Notes.Add("no type id for this mob in the recording");
                        Facts.Add(f);
                        break;
                    }
                    case EvKind.Loot:
                    {
                        if (e.Target == null)
                            break;
                        LootRow(i, loots);
                        break;
                    }
                    case EvKind.ObjectClick:
                    case EvKind.CastStart:
                    case EvKind.Cast:
                    {
                        if (e.Target == null || e.Target.NodeId == 0 || e.Target.Kind == ObjKind.Player)
                        {
                            if (e.Kind == EvKind.Cast)
                                CastNoTarget(i);
                            break;
                        }
                        if (e.Target.Kind == ObjKind.Npc && e.Kind == EvKind.ObjectClick)
                            break;
                        Fact f;
                        if (open.TryGetValue(e.Target.NodeId, out f) && t - f.End < 12)
                        {
                            if (e.Kind == EvKind.ObjectClick && f.SawClick)
                                f.Clicks++;
                            f.SawClick |= e.Kind == EvKind.ObjectClick;
                            f.End = t;
                            f.Look = LookOf[i];
                            if (e.Kind != EvKind.ObjectClick && e.Option.Length > 0 && e.Option != "?" && (f.Cast.Length == 0 || f.Cast == "?"))
                                f.Cast = e.Option;
                            if (e.Kind == EvKind.Cast && e.DurationMs > 0 && f.At > t - e.DurationMs / 1000.0)
                                f.At = t - e.DurationMs / 1000.0;
                            if (e.Kind == EvKind.Cast && f.CastDone < 0)
                                f.CastDone = t;
                            break;
                        }
                        if (e.Target.Kind == ObjKind.Npc && e.Target.Dead)
                            break; // a cast near a body: the loot rows have it
                        f = NewFact(FactKind.UseObject, i);
                        f.Target = Full(e.Target);
                        f.SawClick = e.Kind == EvKind.ObjectClick;
                        if (e.Kind != EvKind.ObjectClick && e.Option.Length > 0)
                            f.Cast = e.Option;
                        if (e.Kind == EvKind.Cast && e.DurationMs > 0)
                            f.At = t - e.DurationMs / 1000.0;
                        if (e.Kind == EvKind.Cast)
                            f.CastDone = t;
                        open[e.Target.NodeId] = f;
                        Facts.Add(f);
                        break;
                    }
                    case EvKind.NpcClick:
                    case EvKind.ConvoStart:
                    {
                        if (e.Target == null || e.Target.Kind != ObjKind.Npc || e.Target.Dead)
                            break;
                        if (e.Kind == EvKind.NpcClick && KilledBefore(i))
                        {
                            LootRow(i, loots); // a click on a body you killed (an old recording has no loot row)
                            break;
                        }
                        if (e.Kind == EvKind.NpcClick && AttackedAfter(i))
                            break; // clicked to attack it: the fight and the kill are the facts
                        Fact f;
                        if (open.TryGetValue(e.Target.NodeId, out f) && t - f.End < 30 && f.Kind == FactKind.Talk)
                        {
                            f.End = t;
                            f.Look = LookOf[i];
                            f.Convo |= e.Kind == EvKind.ConvoStart;
                            break;
                        }
                        f = NewFact(FactKind.Talk, i);
                        f.Target = Full(e.Target);
                        f.Convo = e.Kind == EvKind.ConvoStart;
                        open[e.Target.NodeId] = f;
                        Facts.Add(f);
                        break;
                    }
                    case EvKind.ConvoEnd:
                    {
                        if (e.Target == null)
                            break;
                        Fact f;
                        if (open.TryGetValue(e.Target.NodeId, out f) && f.Kind == FactKind.Talk)
                        {
                            f.End = t;
                            f.Look = LookOf[i];
                            f.Convo = true;
                        }
                        break;
                    }
                    case EvKind.BoardOpen:
                    {
                        Fact f = NewFact(FactKind.Board, i);
                        f.Target = Full(e.Target);
                        Facts.Add(f);
                        break;
                    }
                    case EvKind.QuestItem:
                        if (!_hopRows.Contains(i))
                            QuestItemRow(i);
                        break;
                }
            }
            // the log's exact time and ability id for each object use
            foreach (Fact f in Facts)
            {
                if (f.Kind != FactKind.UseObject || f.Cast.Length == 0)
                    continue;
                LogLine a = MatchAct(f.Cast, string.Empty, f.At, f.End);
                if (a == null)
                    continue;
                f.LogAbility = a.AbilityId;
                f.At = Math.Min(f.At, Sec(a.Time));
            }
        }

        // ------------------------------------------------------------------ hops (a mission item that moves you)

        /// <summary>
        /// A mission item used, then you were moved far a few seconds later (the recorder's jump row, or the next spot
        /// far from where you stood with no walking between): a transport. The first item row before the move is the
        /// press; other item rows while its cast ran are not uses (you cannot use two at once).
        /// </summary>
        private void FindHops()
        {
            for (int i = 0; i < Events.Count; i++)
            {
                RecEvent e = Events[i];
                if (e.Kind != EvKind.QuestItem || e.Option.Length == 0 || _hopRows.Contains(i) || e.Note.IndexOf("item gone", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (e.Note.IndexOf("bag item", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue; // an item from your bags (a medpac), not a mission's transport
                if (e.Pos.IsZero)
                    continue;
                int land = -1;
                for (int k = i + 1; k < Events.Count && AtOf[k] - AtOf[i] <= 9; k++)
                {
                    RecEvent r = Events[k];
                    if (r.Kind == EvKind.Jump || r.Kind == EvKind.AreaChange || r.Kind == EvKind.Loading)
                    {
                        if (AtOf[k] - AtOf[i] >= 3)
                            land = k;
                        break;
                    }
                    if (r.Pos.IsZero)
                        continue;
                    float d = r.Pos.DistanceTo(e.Pos);
                    if (d <= 1.5f)
                        continue; // still standing where you pressed it (the cast runs)
                    if (d > 8f && AtOf[k] - AtOf[i] >= 4)
                        land = k; // moved far at once, no walking between
                    break;
                }
                if (land < 0)
                    continue;
                var f = new Fact { Kind = FactKind.Hop, At = AtOf[i], End = AtOf[land], Look = LookOf[land], FirstLook = LookOf[i], Seq = e.Seq, Me = e.Pos, Area = e.AreaId };
                f.Item = e.Option;
                f.Landing = Events[land].Pos;
                f.LandArea = Events[land].AreaId;
                if (Events[land].Kind != EvKind.Jump)
                    f.Notes.Add("no jump row: the move after it is read as the landing");
                _hopRows.Add(i);
                for (int k = i + 1; k < land; k++)
                    if (Events[k].Kind == EvKind.QuestItem)
                        _hopRows.Add(k); // a row while the transport's cast ran
                Facts.Add(f);
            }
        }

        /// <summary>A loot of a body (clicks on one body within 5 s are one loot).</summary>
        private void LootRow(int i, Dictionary<ulong, Fact> loots)
        {
            RecEvent e = Events[i];
            double t = AtOf[i];
            Fact f;
            if (loots.TryGetValue(e.Target.NodeId, out f) && t - f.End < 5)
            {
                f.End = t;
                f.Look = LookOf[i];
                return;
            }
            f = NewFact(FactKind.Loot, i);
            f.Target = Full(e.Target);
            f.Target.Dead = true;
            loots[e.Target.NodeId] = f;
            Facts.Add(f);
        }

        /// <summary>That NPC was killed before this row (the recording's kill row, or its death in the log).</summary>
        private bool KilledBefore(int i)
        {
            ulong node = Events[i].Target.NodeId;
            if (node == 0)
                return false;
            for (int j = i - 1; j >= 0; j--)
                if (Events[j].Kind == EvKind.Kill && Events[j].Target != null && Events[j].Target.NodeId == node)
                    return true;
            LogLine d;
            return _deaths.TryGetValue(node, out d) && Sec(d.Time) <= AtOf[i];
        }

        /// <summary>You clicked an NPC and a fight with it began within 3 s (a mob, not someone to talk to).</summary>
        private bool AttackedAfter(int i)
        {
            ulong node = Events[i].Target.NodeId;
            for (int j = i + 1; j < Events.Count && AtOf[j] - AtOf[i] < 3; j++)
            {
                RecEvent e = Events[j];
                if (e.Kind == EvKind.ConvoStart)
                    return false;
                if (e.Kind == EvKind.CombatStart || (e.Kind == EvKind.Kill && e.Target != null && e.Target.NodeId == node))
                    return true;
            }
            if (HasLog && LogCovers(AtOf[i]))
            {
                LogLine d;
                return InFightAt(AtOf[i] + 2) || (_deaths.TryGetValue(node, out d) && Sec(d.Time) - AtOf[i] < 60);
            }
            return false;
        }

        /// <summary>A cast with no object: a mission item's cast ("Using Serum...") or your mount (not a fact).</summary>
        private void CastNoTarget(int i)
        {
            RecEvent e = Events[i];
            if (e.Option.Length == 0 || _mounts.Contains(e.Option))
                return;
            string item = ItemOf(e.Option);
            double t = AtOf[i];
            if (item == null && _objectCasts.Contains(e.Option))
            {
                double st = e.DurationMs > 0 ? t - e.DurationMs / 1000.0 : t;
                // (not when you had an object under the cursor as it began: that was a use of the object, "Accessing" a
                // device right after looting a body)
                ObjRef over = CommandTarget(st);
                bool onObject = over != null && over.Kind == ObjKind.Placeable;
                foreach (Fact g in Facts)
                    if (!onObject && g.Kind == FactKind.Loot && st - g.End < 3 && st >= g.At - 1)
                    {
                        g.End = t;
                        g.Look = LookOf[i];
                        return; // the loot cast of a body you just clicked
                    }
                // a click on an object the recorder saw only as its cast ("Looting" with no object row)
                double start = e.DurationMs > 0 ? t - e.DurationMs / 1000.0 : t;
                foreach (Fact g in Facts)
                    if (g.Kind == FactKind.UseObject && g.Target == null && start - g.End < 3 && start >= g.At - 1)
                    {
                        g.End = t;
                        g.Look = LookOf[i];
                        return; // the next cast of the same click ("Looting", then "Loot")
                    }
                Fact u = NewFact(FactKind.UseObject, i);
                u.At = start;
                u.Cast = e.Option;
                u.Notes.Add("a click on an object the recorder saw only by its cast (where you stood is kept)");
                LogLine la = MatchAct(e.Option, string.Empty, start, t);
                if (la != null)
                {
                    u.LogAbility = la.AbilityId;
                    u.At = Math.Min(u.At, Sec(la.Time));
                }
                ObjRef under = CommandTarget(u.At);
                if (under != null && under.Kind == ObjKind.Placeable)
                    u.Target = under;
                Facts.Add(u);
                return;
            }
            if (item == null)
                return;
            double from = e.DurationMs > 0 ? t - e.DurationMs / 1000.0 : t;
            foreach (Fact g in Facts)
                if (g.Kind == FactKind.Item && g.Item == item && Math.Abs(g.End - t) < 4)
                {
                    g.Cast = e.Option;
                    g.At = Math.Min(g.At, from);
                    return;
                }
            Fact f = NewFact(FactKind.Item, i);
            f.Item = item;
            f.Cast = e.Option;
            f.At = from;
            LogLine a = MatchAct(e.Option, item, from, t);
            if (a != null)
            {
                f.LogAbility = a.AbilityId;
                f.At = Sec(a.Time);
                f.FirstLook = Math.Min(f.FirstLook, LookAtTime(f.At)); // (it began when the log says, not when the row saw it)
                f.Target = RefOf(OnWhom(a, t - f.At));
            }
            else if (e.Target != null)
                f.Target = Full(e.Target);
            Facts.Add(f);
        }

        /// <summary>
        /// "Used quest item X": with the combat log, a use only when the log shows its activation; else the game took it
        /// (at a step change, a hand-in). Without a log, the 2.4 rules: right after a step change or a completion it is
        /// the game taking it; a repeat of a use within 4 s is that use.
        /// </summary>
        private void QuestItemRow(int i)
        {
            RecEvent e = Events[i];
            string item = e.Option;
            if (item.Length == 0)
                return;
            double t = AtOf[i];
            bool logHere = HasLog && LogCovers(t);
            foreach (Fact g in Facts)
                if (g.Kind == FactKind.Item && g.Item == item && t - g.End < 4 && t >= g.At - 1)
                {
                    // the same use seen again: not when it names another target, or the log has a use of its own for it
                    bool otherTarget = e.Target != null && g.Target != null && e.Target.NodeId != g.Target.NodeId;
                    if (otherTarget || (logHere && HasActFor(item, t - 8, t)))
                        break;
                    g.End = Math.Max(g.End, t);
                    return;
                }
            Fact f = NewFact(FactKind.Item, i);
            f.Item = item;
            bool checkedTarget = false;
            if (e.Target != null && !e.Target.Dead)
            {
                f.Target = Full(e.Target);
                // the recorder checked that the item works on it; an old row only says what you had selected
                checkedTarget = e.Target.How.IndexOf("the item works on it", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!checkedTarget && LeftOverTarget(i, f.Target))
                {
                    f.Notes.Add("the row named " + f.Target.Name + ", left selected from before (the use is at your spot)");
                    f.Target = null;
                }
            }
            if (HasLog && LogCovers(t))
            {
                LogLine a = MatchAct(string.Empty, item, t - 8, t);
                if (a == null)
                {
                    a = OtherNameAct(t);
                    if (a == null)
                        return; // no activation: the game took it from your bags
                    _itemCasts[a.Ability] = item;
                    f.Notes.Add("the log names this item's cast \"" + a.Ability + "\"");
                }
                f.LogAbility = a.AbilityId;
                f.Cast = a.Ability;
                f.At = Sec(a.Time);
                f.FirstLook = Math.Min(f.FirstLook, LookAtTime(f.At)); // (it began when the log says, not when the row saw it)
                // the log's target only when the recording knows that object (the effect can land on a hidden one)
                ObjRef hit = RefOf(OnWhom(a, t - f.At));
                if (hit != null && (f.Target == null || !checkedTarget) && (Known(hit.NodeId) || f.Target == null))
                {
                    if (!Known(hit.NodeId))
                        f.Notes.Add("the item's effect landed on something the recorder never saw (the use is at your spot)");
                    else
                        f.Target = hit;
                }
                if (!SelfInFight(f))
                    Facts.Add(f);
                return;
            }
            if (e.Note.IndexOf("item gone", StringComparison.OrdinalIgnoreCase) >= 0 && TrackerChangedJustBefore(i) && !HiddenChangeOnly(i))
            {
                // taken by the game at the step change (rule 2.4.3); the row names what it went on: a click on that just
                // before is what did the step ("Plant the Targeting Beacon" on the war droid), not a bare click
                if (e.Target != null && e.Target.NodeId != 0)
                {
                    Fact on = null;
                    foreach (Fact g in Facts)
                        if ((g.Kind == FactKind.Talk || g.Kind == FactKind.UseObject) && g.Target != null && g.Target.NodeId == e.Target.NodeId && g.At <= t && t - g.End <= 8)
                            on = g;
                    if (on != null && !on.Did)
                    {
                        on.Did = true;
                        on.Notes.Add("your mission item left your bags on it as the step changed");
                    }
                }
                return;
            }
            f.Notes.Add("no combat log: a use by the recorder's own reading");
            if (!SelfInFight(f))
                Facts.Add(f);
        }

        /// <summary>An item used on yourself (or another player) in a fight: a medpac or a stim, not a mission action.</summary>
        private static bool SelfInFight(Fact f)
        {
            return f.InFight && (f.Target == null || f.Target.Kind == ObjKind.Player);
        }

        /// <summary>
        /// Every step change in this look and the 2 before it is a hidden mission's (no objective text, such as an item's
        /// handler): that change is the item's effect, not a step taking the item.
        /// </summary>
        private bool HiddenChangeOnly(int i)
        {
            int look = LookOf[i];
            bool any = false;
            for (int j = i; j >= 0 && LookOf[j] >= look - 2; j--)
            {
                RecEvent e = Events[j];
                if (e.Kind != EvKind.QuestStep && e.Kind != EvKind.QuestCompleted && e.Kind != EvKind.QuestRemoved)
                    continue;
                if (!e.QuestHidden)
                    return false;
                any = true;
            }
            for (int j = i + 1; j < Events.Count && LookOf[j] == look; j++)
            {
                RecEvent e = Events[j];
                if (e.Kind != EvKind.QuestStep && e.Kind != EvKind.QuestCompleted && e.Kind != EvKind.QuestRemoved)
                    continue;
                if (!e.QuestHidden)
                    return false;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// An activation of yours the moment an item left your bags, under a name that is not the item's (the item's
        /// ability has its own name): not your kit, not an object's cast, not your mount.
        /// </summary>
        private LogLine OtherNameAct(double t)
        {
            LogLine best = null;
            double bestD = double.MaxValue;
            foreach (LogLine a in _acts)
            {
                double u = Sec(a.Time);
                if (u < t - 2 || u > t + 0.5 || _usedActs.Contains(a) || Kit.Contains(a.AbilityId) || a.Ability.Length == 0)
                    continue;
                if (_objectCasts.Contains(a.Ability) || _mounts.Contains(a.Ability) || ItemsOf(a.Ability).Count > 0)
                    continue;
                if (InUseWindow(u) || CastRowNear(a.Ability, u))
                    continue; // the cast of a body or object you clicked (a loot's "Identifying Item")
                double d = Math.Abs(t - u);
                if (d < bestD)
                {
                    bestD = d;
                    best = a;
                }
            }
            if (best != null)
                _usedActs.Add(best);
            return best;
        }

        /// <summary>Inside a click on an object or a body (its click to its cast's end, give or take 1.5 s).</summary>
        private bool InUseWindow(double t)
        {
            foreach (Fact f in Facts)
                if ((f.Kind == FactKind.UseObject || f.Kind == FactKind.Loot) && t >= f.At - 1.5 && t <= f.End + 1.5)
                    return true;
            return false;
        }

        /// <summary>The recorder rowed a cast of that name around then (an object's or a body's cast, not an item's).</summary>
        private bool CastRowNear(string name, double t)
        {
            for (int i = 0; i < Events.Count; i++)
            {
                if (AtOf[i] < t - 2)
                    continue;
                RecEvent e = Events[i];
                if (AtOf[i] > t + 30)
                    break;
                double start = e.DurationMs > 0 ? AtOf[i] - e.DurationMs / 1000.0 : AtOf[i];
                if ((e.Kind == EvKind.Cast || e.Kind == EvKind.CastStart) && e.Option == name && t >= start - 2 && t <= AtOf[i] + 2)
                    return true;
            }
            return false;
        }

        /// <summary>An activation of that item not yet given to a use (looking only).</summary>
        private bool HasActFor(string item, double from, double to)
        {
            foreach (LogLine a in _acts)
            {
                if (_usedActs.Contains(a))
                    continue;
                double u = Sec(a.Time);
                if (u >= from - 1.5 && u <= to + 1.5 && ItemsOf(a.Ability).Contains(item))
                    return true;
            }
            return false;
        }

        /// <summary>The recording has rows of this node with its type id.</summary>
        private bool Known(ulong node)
        {
            ObjRef o;
            return node != 0 && Nodes.TryGetValue(node, out o) && o.Spec != 0;
        }

        /// <summary>
        /// An old row's target (what you had selected) that was left over: you selected it to talk and did not select
        /// it again since, or an earlier use of the same item already named it.
        /// </summary>
        private bool LeftOverTarget(int i, ObjRef target)
        {
            double t = AtOf[i];
            bool talked = false;
            for (int j = i - 1; j >= 0; j--)
            {
                RecEvent e = Events[j];
                if (e.Target == null || e.Target.NodeId != target.NodeId)
                    continue;
                if (e.Kind == EvKind.QuestItem && e.Option == Events[i].Option && e.Note.IndexOf("item gone", StringComparison.OrdinalIgnoreCase) < 0)
                    return true; // an earlier use named it already
                if (e.Kind == EvKind.ConvoStart)
                    talked = true;
                if (e.Kind == EvKind.Target || e.Kind == EvKind.Command)
                {
                    if (t - AtOf[j] <= 10)
                        return false; // you selected it just before the use
                    return talked;
                }
            }
            return talked;
        }

        /// <summary>A quest step change or completion in this look or the 2 looks before it.</summary>
        private bool TrackerChangedJustBefore(int i)
        {
            int look = LookOf[i];
            for (int j = i; j >= 0 && LookOf[j] >= look - 2; j--)
                if (Events[j].Kind == EvKind.QuestStep || Events[j].Kind == EvKind.QuestCompleted || Events[j].Kind == EvKind.QuestRemoved)
                    return true;
            // (the same look: the recorder can write the item row before the step change it came with)
            for (int j = i + 1; j < Events.Count && LookOf[j] == look; j++)
                if (Events[j].Kind == EvKind.QuestStep || Events[j].Kind == EvKind.QuestCompleted || Events[j].Kind == EvKind.QuestRemoved)
                    return true;
            return false;
        }

        // ------------------------------------------------------------------ facts only the combat log saw

        private void FromLog()
        {
            if (!HasLog)
                return;
            // kills the recorder did not row (a companion's, one in a burst)
            var rowKills = new HashSet<ulong>();
            foreach (Fact f in Facts)
                if (f.Kind == FactKind.Kill && f.Node != 0)
                    rowKills.Add(f.Node);
            foreach (KeyValuePair<ulong, LogLine> p in _deaths)
            {
                LogLine d = p.Value;
                if (rowKills.Contains(p.Key) || (d.Src.Who != LogWho.Me && d.Src.Who != LogWho.Companion))
                    continue;
                double t = Sec(d.Time);
                if (t < 0 || t > Sec(End))
                    continue;
                var f = new Fact { Kind = FactKind.Kill, At = t, End = t, Look = LookAtTime(t), LogOnly = true, InFight = true, ByCompanion = d.Src.Who == LogWho.Companion };
                f.FirstLook = f.Look;
                f.Target = RefOf(d.Tgt);
                f.Me = d.Src.Who == LogWho.Me && d.Src.HasPos ? d.Src.Pos : PosAt(t);
                if (f.Target.Spec == 0)
                    f.Notes.Add("no type id for this mob in the recording");
                Facts.Add(f);
            }
            // activations of mission items and quest abilities the recorder did not row
            foreach (LogLine a in _acts)
            {
                if (_usedActs.Contains(a) || Kit.Contains(a.AbilityId))
                    continue;
                double t = Sec(a.Time);
                if (t < 0 || t > Sec(End))
                    continue;
                List<string> items = ItemsOf(a.Ability);
                if (items.Count == 0 && CoveredByUse(a, t))
                    continue;
                if (items.Count == 0 && JustAfterReward(t))
                    continue; // the game's own activation as a mission is rewarded, not yours
                if (items.Count == 0 && ReplayBuilder.IsTravelAbility(a.Ability ?? string.Empty))
                    continue; // the game's own travel (Quick Travel, Fleet Pass): the travel steps do it, it is no mission action
                var f = new Fact { Kind = items.Count > 0 ? FactKind.Item : FactKind.Ability, At = t, End = t, Look = LookAtTime(t), LogOnly = true, InFight = InFightAt(t) };
                f.FirstLook = f.Look;
                // the object under your mouse as you activated it (the recorder rows the command, not always the click)
                ObjRef under = CommandTarget(t);
                // a generic object cast ("Searching...", "Attacking...") the recorder saw on an object elsewhere: an object use
                bool objectCast = items.Count == 0 && (IsObjectCast(a.AbilityId, a.Ability) || (under != null && under.Kind == ObjKind.Placeable));
                if (objectCast)
                {
                    f.Kind = FactKind.UseObject;
                    f.Target = under != null && under.Kind == ObjKind.Placeable ? under : null;
                    f.Notes.Add(f.Target != null ? "the click is from the combat log and the object under your mouse" : "the combat log saw a click on an object the recorder missed (where you stood is kept)");
                }
                f.Item = items.Count == 1 ? items[0] : string.Empty;
                if (items.Count > 1)
                    f.Notes.Add("the cast fits more than one mission item: " + string.Join(" | ", items) + " (the profile finds it by the cast's name)");
                f.Cast = a.Ability;
                f.LogAbility = a.AbilityId;
                f.Me = a.Src.HasPos ? a.Src.Pos : PosAt(t);
                if (!objectCast)
                {
                    f.Target = RefOf(OnWhom(a, 0.5));
                    if (f.Target == null && under != null && under.Kind == ObjKind.Npc && !under.Dead)
                        f.Target = under;
                    if (f.Target != null && Companions.Contains(f.Target.NodeId))
                        f.Target = null;
                }
                f.Interrupted = Stopped(a);
                if (a.Ability.Length == 0 && f.Kind != FactKind.UseObject)
                    f.Notes.Add("the log gives no name for this ability");
                _usedActs.Add(a);
                Facts.Add(f);
                if (f.Target != null)
                    DropClickOn(f);
            }
            // a click on your companion is never a talk for a mission
            Facts.RemoveAll(f => f.Kind == FactKind.Talk && f.Target != null && (Companions.Contains(f.Target.NodeId) || f.Target.Toughness == "companion"));
            foreach (Fact f in Facts)
                if (f.LogAbility != 0 && !f.Interrupted)
                {
                    foreach (LogLine a in _acts)
                        if (a.AbilityId == f.LogAbility && Math.Abs(Sec(a.Time) - f.At) < 0.01)
                        {
                            f.Interrupted = Stopped(a);
                            break;
                        }
                }
        }

        /// <summary>Within 3 s after a mission completed or showed its reward.</summary>
        private bool JustAfterReward(double t)
        {
            for (int i = 0; i < Events.Count; i++)
            {
                if (AtOf[i] > t)
                    break;
                if ((Events[i].Kind == EvKind.QuestCompleted || Events[i].Kind == EvKind.RewardWindow) && t - AtOf[i] <= 3)
                    return true;
            }
            return false;
        }

        /// <summary>An ability the recorder saw as the cast of a click on an object (same log id).</summary>
        private bool IsObjectCast(ulong ability, string name)
        {
            if (name.Length > 0 && _objectCasts.Contains(name))
                return true;
            foreach (Fact f in Facts)
                if (f.Kind == FactKind.UseObject && !f.LogOnly && f.LogAbility == ability)
                    return true;
            return false;
        }

        /// <summary>The object or NPC under your mouse in a command row within a moment of t (null: none).</summary>
        private ObjRef CommandTarget(double t)
        {
            ObjRef best = null;
            double bestD = 1.6;
            for (int i = 0; i < Events.Count; i++)
            {
                if (AtOf[i] < t - 1.6)
                    continue;
                if (AtOf[i] > t + 1.1)
                    break;
                RecEvent e = Events[i];
                if (e.Kind != EvKind.Command || e.Target == null || e.Target.NodeId == 0)
                    continue;
                double d = Math.Abs(AtOf[i] - t);
                if (d < bestD)
                {
                    bestD = d;
                    best = Full(e.Target);
                }
            }
            return best;
        }

        /// <summary>A click on the NPC an item went on, just before it: the click picked the target, the item is the action.</summary>
        private void DropClickOn(Fact use)
        {
            Facts.RemoveAll(f => f.Kind == FactKind.Talk && f.Target != null && f.Target.NodeId == use.Target.NodeId
                && ((use.At - f.End < 6 && use.At >= f.At - 1) || (!f.Convo && f.At >= use.At && f.At - use.End < 4)));
        }

        /// <summary>You cancelled it or it was interrupted before you activated anything else.</summary>
        private bool Stopped(LogLine act)
        {
            double t = Sec(act.Time);
            foreach (LogLine l in Log)
            {
                double u = Sec(l.Time);
                if (u <= t)
                    continue;
                if (u > t + 15)
                    break;
                if (l.Src.Who != LogWho.Me || l.KindId != CombatLog.KEvent)
                    continue;
                if (l.WhatId == CombatLog.AbilityActivate && l.AbilityId != act.AbilityId)
                    return false;
                if ((l.WhatId == CombatLog.AbilityCancel || l.WhatId == CombatLog.AbilityInterrupt) && l.AbilityId == act.AbilityId)
                    return true;
            }
            return false;
        }

        /// <summary>The activation is the cast of an object use the rows already have (a generic "Accessing" on it).</summary>
        private bool CoveredByUse(LogLine a, double t)
        {
            foreach (Fact f in Facts)
                if ((f.Kind == FactKind.UseObject || f.Kind == FactKind.Loot) && t >= f.At - 1.5 && t <= f.End + 1.5)
                {
                    if (f.LogAbility == 0 && f.Kind == FactKind.UseObject)
                    {
                        f.LogAbility = a.AbilityId;
                        if (f.Cast.Length == 0 || f.Cast == "?")
                            f.Cast = a.Ability;
                    }
                    return true;
                }
            return false;
        }

        /// <summary>The look in which the recorder would have seen a moment (the first look at or after it).</summary>
        public int LookAtTime(double t)
        {
            for (int i = 0; i < Events.Count; i++)
                if (AtOf[i] >= t - 0.001)
                    return LookOf[i];
            return Events.Count > 0 ? LookOf[Events.Count - 1] : 0;
        }

        /// <summary>Where you were at a moment (the nearest row).</summary>
        public Vec3 PosAt(double t)
        {
            Vec3 best = default(Vec3);
            double bestD = double.MaxValue;
            for (int i = 0; i < Events.Count; i++)
            {
                double d = Math.Abs(AtOf[i] - t);
                if (d < bestD)
                {
                    bestD = d;
                    best = Events[i].Pos;
                }
                else if (AtOf[i] > t)
                    break;
            }
            return best;
        }

        // ------------------------------------------------------------------ B2: what counted

        private void CollectTracks()
        {
            for (int i = 0; i < Events.Count; i++)
            {
                RecEvent e = Events[i];
                TrackKind k;
                switch (e.Kind)
                {
                    case EvKind.QuestAdded:
                        k = TrackKind.Added;
                        break;
                    case EvKind.QuestTask:
                        k = TrackKind.Task;
                        break;
                    case EvKind.QuestStep:
                        k = TrackKind.Step;
                        break;
                    case EvKind.QuestCompleted:
                        k = TrackKind.Completed;
                        break;
                    case EvKind.QuestRemoved:
                    case EvKind.QuestAbandoned:
                        k = TrackKind.Removed;
                        break;
                    case EvKind.RewardWindow:
                        k = TrackKind.Reward;
                        break;
                    case EvKind.HolocomAnswered:
                        k = TrackKind.Holocom;
                        break;
                    default:
                        continue;
                }
                if (e.QuestId == 0)
                    continue;
                var tr = new Track
                {
                    Kind = k,
                    Ev = e,
                    At = AtOf[i],
                    Look = LookOf[i],
                    Quest = e.QuestId,
                    Branch = e.Branch,
                    Step = e.Step,
                    OldBranch = e.OldBranch,
                    OldStep = e.OldStep,
                    Task = e.TaskIndex,
                    Count = e.Count,
                    Max = e.Max,
                };
                tr.Targets.AddRange(e.TaskTargets);
                Tracks.Add(tr);
            }
        }
    }
}
