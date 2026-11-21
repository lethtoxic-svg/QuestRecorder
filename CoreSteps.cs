// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuestRecorder
{
    internal enum StepKind
    {
        Pickup,
        Kill,
        KillLoot,
        Use,
        Collect,
        Talk,
        Item,
        Ability,
        Walk,
        HandIn,
        Reward,
        Holocom,
        Hop,
        /// <summary>Many clicks on objects at one spot for one step, in no fixed order: done by hand, then Continue.</summary>
        Puzzle,
    }

    /// <summary>One step of the profile, as data (rendered to text only at the very end).</summary>
    internal sealed class CoreStep
    {
        public StepKind Kind;
        public ulong Quest;
        public string QuestName = string.Empty;
        public ulong Branch;
        public ulong Step;
        public int Task = -1;
        public ulong CountTo;
        public ulong Max;
        public double At;
        public double End;
        public ulong Area;
        public readonly List<ulong> Specs = new List<ulong>();
        /// <summary>Every type the task counts by the game's own list (its counters' task targets), seen or not.</summary>
        public readonly List<ulong> Accepts = new List<ulong>();
        public readonly List<ulong> Nodes = new List<ulong>();
        public readonly List<ulong> Anchors = new List<ulong>();
        /// <summary>Where the targets were (or where you stood, for an action with no target), in your order.</summary>
        public readonly List<Vec3> Spots = new List<Vec3>();
        public readonly List<Fact> Causes = new List<Fact>();
        /// <summary>The same item or ability used before the use that counted, at other spots (kept: spots can change between runs).</summary>
        public readonly List<Fact> Tries = new List<Fact>();
        public string Item = string.Empty;
        /// <summary>The item's or ability's cast name as the log wrote it (finds the item when two names fit).</summary>
        public string Cast = string.Empty;
        public ulong LogAbility;
        public bool InFight;
        public bool DeadOnly;
        public bool Loot;
        public readonly List<string> Unsure = new List<string>();
        /// <summary>A hop's landing (where the transport put you) and the area of it.</summary>
        public Vec3 Landing;
        public ulong LandArea;
        /// <summary>How sure the cause is: 1 = the game's ids tie it, 2 = the only action in the window, 3 = weaker.</summary>
        public int Rank = 1;
        /// <summary>When the quest came to this step (a step nothing was seen for; MinValue = not known).</summary>
        public double Came = double.MinValue;

        public void AddTarget(ObjRef o, Vec3 me)
        {
            if (o == null)
            {
                AddSpot(me);
                return;
            }
            if (o.Spec != 0 && !Specs.Contains(o.Spec))
                Specs.Add(o.Spec);
            if (o.NodeId != 0 && !Nodes.Contains(o.NodeId))
                Nodes.Add(o.NodeId);
            if (o.Anchor != 0 && !Anchors.Contains(o.Anchor))
                Anchors.Add(o.Anchor);
            AddSpot(o.HasPos ? o.Pos : me);
        }

        public void AddSpot(Vec3 p)
        {
            if (p.IsZero)
                return;
            foreach (Vec3 s in Spots)
                if (s.DistanceTo(p) < 0.05f)
                    return;
            Spots.Add(p);
        }

        public void Doubt(string why)
        {
            if (!Unsure.Contains(why))
                Unsure.Add(why);
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case StepKind.Pickup:
                        return "Pick-up";
                    case StepKind.Use:
                        return "Use object";
                    case StepKind.HandIn:
                        return "Hand-in";
                    default:
                        return Kind.ToString();
                }
            }
        }
    }

    /// <summary>
    /// B3 and B4 of the new core: for every change the mission tracker showed, the action that caused it (ids first),
    /// then the changes of one task with one kind of cause put together as one step.
    /// </summary>
    internal static class CoreSteps
    {
        /// <summary>The longest a cause can come before its tick (the window also stops at the quest's previous change).</summary>
        private const double MaxBack = 180;

        public static List<CoreStep> Build(CoreFacts c)
        {
            var steps = new List<CoreStep>();
            var order = new List<ulong>();
            var byQuest = new Dictionary<ulong, List<Change>>();
            var tracksOf = new Dictionary<ulong, List<Track>>();
            foreach (Track t in c.Tracks)
            {
                List<Track> l;
                if (!tracksOf.TryGetValue(t.Quest, out l))
                {
                    tracksOf[t.Quest] = l = new List<Track>();
                    order.Add(t.Quest);
                }
                l.Add(t);
            }
            foreach (ulong q in order)
                byQuest[q] = Changes(tracksOf[q]);
            var ctx = new Ctx { C = c };
            // the looks in which a quest completed (another quest's count in that look follows from it)
            foreach (Track t in c.Tracks)
                if (t.Kind == TrackKind.Completed || t.Kind == TrackKind.Reward)
                    ctx.DoneLooks.Add(new KeyValuePair<int, ulong>(t.Look, t.Quest));
            // pass 1: every action the game's ids tie to one quest's count belongs to that quest
            ctx.DryRun = true;
            ctx.IdsOnly = true;
            foreach (ulong q in order)
                Quest(ctx, q, byQuest[q], tracksOf[q], new List<CoreStep>());
            ctx.IdsOnly = false;
            // pass 2: the cause of every change (no steps yet): each quest bids for the actions it would take, and each
            // action goes to the strongest tie, then to the first change it could have made; twice, so a quest that lost
            // an action looks again among the rest
            ctx.Bidding = true;
            for (int round = 0; round < 2; round++)
            {
                foreach (ulong q in order)
                    Quest(ctx, q, byQuest[q], tracksOf[q], new List<CoreStep>());
                ctx.Settle();
            }
            ctx.Bidding = false;
            // pass 3: the steps, each quest taking no action another quest's change was tied to
            ctx.DryRun = false;
            foreach (ulong q in order)
                Quest(ctx, q, byQuest[q], tracksOf[q], steps);
            Hops(c, steps);
            steps.Sort((x, y) => x.At != y.At ? x.At.CompareTo(y.At) : x.End.CompareTo(y.End));
            return steps;
        }

        /// <summary>
        /// Six or more clicks for one step on objects all within 2 m of each other (side-by-side controls), with no count
        /// to follow: the order is the game's own puzzle, so one pause at that spot, decided by where you clicked.
        /// </summary>
        private static bool Puzzle(Ctx ctx, ulong q, Change ch, double from, HashSet<Fact> used, Dictionary<string, CoreStep> open, List<CoreStep> steps, string qname)
        {
            var uses = new List<Fact>();
            foreach (Fact f in Window(ctx.C, ch, from))
                if (f.Kind == FactKind.UseObject && f.Target != null && f.Target.HasPos && !used.Contains(f) && !TakenByOther(ctx, q, f))
                    uses.Add(f);
            if (uses.Count == 0)
                return false;
            // the cluster of the last one clicked
            Fact last = uses[uses.Count - 1];
            var cluster = uses.FindAll(f => f.Target.Pos.DistanceTo(last.Target.Pos) <= 2f);
            int clicks = 0;
            Fact most = last;
            foreach (Fact f in cluster)
            {
                clicks += Math.Max(1, f.Clicks);
                if (f.Clicks > most.Clicks)
                    most = f;
            }
            var nodes = new HashSet<ulong>();
            foreach (Fact f in cluster)
                nodes.Add(f.Node);
            if (clicks < 6 || nodes.Count < 2)
                return false;
            CoreStep p = OpenFor(open, steps, ch, most, qname, -1, false);
            p.Kind = StepKind.Puzzle;
            p.At = cluster[0].At;
            p.Spots.Clear();
            foreach (Fact f in cluster)
            {
                used.Add(f);
                ctx.Claim(f, q, ch.At, 1);
                p.Causes.Add(f);
                if (f.Spec != 0 && !p.Specs.Contains(f.Spec))
                    p.Specs.Add(f.Spec);
                if (f.Node != 0 && !p.Nodes.Contains(f.Node))
                    p.Nodes.Add(f.Node);
                if (f.Target.Anchor != 0 && !p.Anchors.Contains(f.Target.Anchor))
                    p.Anchors.Add(f.Target.Anchor);
            }
            p.AddSpot(most.Target.Pos);
            p.End = Math.Max(p.End, ch.At);
            p.Rank = 2;
            p.Doubt(clicks + " clicks on " + nodes.Count + " objects at one spot in no fixed order: done by hand");
            return true;
        }

        // ------------------------------------------------------------------ hops

        /// <summary>
        /// Every transport you used is a step of the mission it belongs to: the mission whose end took the item from
        /// your bags (the game's own tie); else the mission the item names; else the first one to count after you landed.
        /// </summary>
        private static void Hops(CoreFacts c, List<CoreStep> steps)
        {
            foreach (Fact f in c.Facts)
            {
                if (f.Kind != FactKind.Hop)
                    continue;
                string how;
                ulong q = HopQuest(c, f, out how);
                var s = new CoreStep { Kind = StepKind.Hop, Quest = q, Item = f.Item, At = f.At, End = f.End, Area = f.Area, Landing = f.Landing, LandArea = f.LandArea, Rank = how == null ? 1 : 2 };
                s.QuestName = QuestName(c, q);
                s.Causes.Add(f);
                s.AddSpot(f.Me);
                if (how != null)
                    s.Doubt(how);
                foreach (string n in f.Notes)
                    s.Doubt(n);
                steps.Add(s);
            }
        }

        private static ulong HopQuest(CoreFacts c, Fact hop, out string how)
        {
            how = null;
            // the game took the item away when a mission ended: that mission's transport
            var ended = new List<ulong>();
            for (int i = 0; i < c.Events.Count; i++)
            {
                RecEvent e = c.Events[i];
                if (e.Kind != EvKind.QuestItem || e.Option != hop.Item || c.AtOf[i] < hop.End || e.Note.IndexOf("item gone", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                for (int j = 0; j < c.Events.Count; j++)
                {
                    RecEvent d = c.Events[j];
                    if ((d.Kind == EvKind.QuestCompleted || d.Kind == EvKind.QuestRemoved) && d.QuestId != 0 && Math.Abs(c.LookOf[j] - c.LookOf[i]) <= 1 && !ended.Contains(d.QuestId))
                        ended.Add(d.QuestId);
                }
                break;
            }
            ulong named = NamedQuest(c, hop.Item, ended.Count > 0 ? ended : null);
            if (ended.Count == 1)
                return ended[0];
            if (ended.Count > 1 && named != 0)
                return named;
            if (named != 0)
            {
                how = "tied by the item's name (no mission end took it from your bags)";
                return named;
            }
            if (ended.Count > 1)
            {
                how = "more than one mission ended as the item went: the first of them";
                return ended[0];
            }
            // the first mission to count after you landed
            foreach (Track t in c.Tracks)
                if (t.Kind == TrackKind.Task && t.At > hop.End && t.At - hop.End < 600)
                {
                    how = "tied by time: the first mission that counted after you landed";
                    return t.Quest;
                }
            how = "no mission could be tied to it";
            return 0;
        }

        /// <summary>The mission whose name (without its "[DAILY]"-style tag) is in the item's name; 0 = none or more than one.</summary>
        private static ulong NamedQuest(CoreFacts c, string item, List<ulong> among)
        {
            ulong found = 0;
            var seen = new HashSet<ulong>();
            foreach (Track t in c.Tracks)
            {
                if (t.Ev == null || !seen.Add(t.Quest) || (among != null && !among.Contains(t.Quest)))
                    continue;
                string n = t.Ev.QuestName.Trim();
                if (n.StartsWith("[", StringComparison.Ordinal) && n.IndexOf(']') > 0)
                    n = n.Substring(n.IndexOf(']') + 1).Trim();
                if (n.Length < 4 || item.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (found != 0 && found != t.Quest)
                    return 0;
                found = t.Quest;
            }
            return found;
        }

        private static string QuestName(CoreFacts c, ulong q)
        {
            foreach (Track t in c.Tracks)
                if (t.Quest == q && t.Ev != null && t.Ev.QuestName.Length > 0)
                    return t.Ev.QuestName;
            return string.Empty;
        }

        private sealed class Ctx
        {
            public CoreFacts C;
            public readonly List<KeyValuePair<int, ulong>> DoneLooks = new List<KeyValuePair<int, ulong>>();
            /// <summary>Facts the game's ids tie to a quest's count: no other quest takes them by time alone.</summary>
            public readonly Dictionary<Fact, ulong> Claimed = new Dictionary<Fact, ulong>();
            /// <summary>The run that only finds causes (pass 1 and 2).</summary>
            public bool DryRun;
            /// <summary>Pass 1: only causes the game's ids tie.</summary>
            public bool IdsOnly;
            /// <summary>Pass 2: every quest says which actions it would take; <see cref="Settle"/> gives each to the best one.</summary>
            public bool Bidding;
            private readonly Dictionary<Fact, Bid> bids = new Dictionary<Fact, Bid>();

            private struct Bid
            {
                public ulong Q;
                public double At;
                public int Strength;
            }

            /// <summary>
            /// This quest's change at <paramref name="at"/> takes the action. Strength: 3 the game's ids tie it, 2 the objective
            /// names it, 1 the time ties it, 0 another action of the step done before it. While bidding, the strongest tie
            /// wins, then the earliest change (an action moves the first mission it can, not one that moves later).
            /// </summary>
            public void Claim(Fact f, ulong q, double at, int strength)
            {
                if (!DryRun || f == null || Claimed.ContainsKey(f))
                    return;
                if (!Bidding)
                {
                    Claimed[f] = q;
                    return;
                }
                Bid b;
                if (!bids.TryGetValue(f, out b) || strength > b.Strength || (strength == b.Strength && at < b.At - 0.01))
                    bids[f] = new Bid { Q = q, At = at, Strength = strength };
            }

            public void Settle()
            {
                foreach (KeyValuePair<Fact, Bid> p in bids)
                    if (!Claimed.ContainsKey(p.Key))
                        Claimed[p.Key] = p.Value.Q;
                bids.Clear();
            }
        }

        private static int Strength(int rank, string why)
        {
            if (rank == 1)
                return 3;
            return why != null && why.StartsWith("tied by name", StringComparison.Ordinal) ? 2 : 1;
        }

        /// <summary>One change of one quest: a count (all the counters that moved in one look), a step change, the end.</summary>
        private sealed class Change
        {
            public TrackKind Kind;
            public Track Main;
            public ulong Branch;
            public ulong Step;
            public int Task = -1;
            public ulong Count;
            public ulong Max;
            public readonly List<ulong> Targets = new List<ulong>();
            /// <summary>The targets are another row's (a step change has none of its own: rule 2.4.6).</summary>
            public bool Borrowed;
            public double At;
            public int Look;
            public Vec3 Pos;
            public ulong Area;
            /// <summary>What the tracker said the finished step or task was ("Defeat CZ-5X Eradicator Droid"); empty = not shown.</summary>
            public string Goal = string.Empty;
            /// <summary>A count: what the other tasks of the same step say ("Destroy Kolto Containers" beside "Recover Biotoxin Containers").</summary>
            public readonly List<string> Others = new List<string>();
            /// <summary>A step's end: what the recorder had seen by the look the quest came to that step is not what finished it.</summary>
            public int NotBy = int.MinValue;
            /// <summary>A step's end: when the quest came to that step.</summary>
            public double Since = double.MinValue;
        }

        private static bool Visible(Track t)
        {
            return t.Ev != null && t.Ev.Text.Length > 0;
        }

        private static List<Change> Changes(List<Track> tracks)
        {
            var list = new List<Change>();
            var byTask = new Dictionary<string, List<ulong>>();
            var byStep = new Dictionary<string, List<ulong>>();
            var goals = new Dictionary<string, string>();
            // (every task's text of each step, for telling one task's actions from another's)
            var tasksOf = new Dictionary<string, Dictionary<int, string>>();
            foreach (Track t in tracks)
                if (t.Kind == TrackKind.Task && Visible(t))
                {
                    Dictionary<int, string> m;
                    if (!tasksOf.TryGetValue(t.Branch + "/" + t.Step, out m))
                        tasksOf[t.Branch + "/" + t.Step] = m = new Dictionary<int, string>();
                    if (!m.ContainsKey(t.Task))
                        m[t.Task] = Fmt.OneLine(t.Ev.Text);
                }
            foreach (Track t in tracks)
            {
                if (t.Kind == TrackKind.Reward || t.Kind == TrackKind.Removed || t.Kind == TrackKind.Holocom)
                    continue;
                if (t.Kind == TrackKind.Task && t.Count == 0)
                    continue; // a counter shown at 0 (a new step)
                ulong b = t.Kind == TrackKind.Step ? t.OldBranch : t.Branch;
                ulong s = t.Kind == TrackKind.Step ? t.OldStep : t.Step;
                string sk = b + "/" + s;
                string goal;
                if (t.Kind == TrackKind.Task)
                {
                    if (Visible(t))
                        goals[sk + "/" + t.Task] = Fmt.OneLine(t.Ev.Text);
                    if (!goals.TryGetValue(sk + "/" + t.Task, out goal))
                        goal = string.Empty;
                }
                else if (!goals.TryGetValue(sk, out goal))
                    goal = string.Empty;
                // (the objective shown once the quest came to a step: Added and Step rows carry the new step's)
                if ((t.Kind == TrackKind.Added || t.Kind == TrackKind.Step) && Visible(t))
                    goals[t.Branch + "/" + t.Step] = Fmt.OneLine(t.Ev.Text);
                Change last = list.Count > 0 ? list[list.Count - 1] : null;
                if (t.Kind == TrackKind.Task && last != null && last.Kind == TrackKind.Task && last.Look == t.Look && last.Branch == b && last.Step == s &&
                    Visible(t) != Visible(last.Main))
                {
                    // two counters of one step in one look are one action (a hidden part-counter ticks with the one you see)
                    foreach (ulong x in t.Targets)
                        if (!last.Targets.Contains(x))
                            last.Targets.Add(x);
                    if (t.Targets.Count > 0)
                        last.Borrowed = false;
                    if (Visible(t) && !Visible(last.Main))
                    {
                        last.Main = t;
                        last.Goal = goal;
                        last.Task = t.Task;
                        last.Count = t.Count;
                        last.Max = t.Max;
                    }
                    if (last.Targets.Count > 0)
                    {
                        byTask[sk + "/" + last.Task] = new List<ulong>(last.Targets);
                        byStep[sk] = new List<ulong>(last.Targets);
                    }
                    continue;
                }
                var ch = new Change
                {
                    Kind = t.Kind,
                    Main = t,
                    Branch = b,
                    Step = s,
                    Task = t.Kind == TrackKind.Task ? t.Task : -1,
                    Count = t.Count,
                    Max = t.Max,
                    At = t.At,
                    Look = t.Look,
                    Pos = t.Ev != null ? t.Ev.Pos : default(Vec3),
                    Area = t.Ev != null ? t.Ev.AreaId : 0,
                    Goal = goal,
                };
                ch.Targets.AddRange(t.Targets);
                Dictionary<int, string> sib;
                if (ch.Kind == TrackKind.Task && tasksOf.TryGetValue(sk, out sib))
                    foreach (KeyValuePair<int, string> o in sib)
                        if (o.Key != ch.Task && o.Value.Length > 0)
                            ch.Others.Add(o.Value);
                List<ulong> lt;
                if (ch.Targets.Count > 0)
                {
                    if (ch.Kind == TrackKind.Task)
                        byTask[sk + "/" + ch.Task] = new List<ulong>(ch.Targets);
                    byStep[sk] = new List<ulong>(ch.Targets);
                }
                else if (ch.Kind == TrackKind.Task ? byTask.TryGetValue(sk + "/" + ch.Task, out lt) : byStep.TryGetValue(sk, out lt))
                {
                    ch.Targets.AddRange(lt);
                    ch.Borrowed = true;
                }
                list.Add(ch);
            }
            return list;
        }

        // ------------------------------------------------------------------ one quest

        private static void Quest(Ctx ctx, ulong q, List<Change> changes, List<Track> tracks, List<CoreStep> steps)
        {
            CoreFacts c = ctx.C;
            var used = new HashSet<Fact>();
            string qname = tracks[0].Ev != null ? tracks[0].Ev.QuestName : string.Empty;
            var open = new Dictionary<string, CoreStep>(); // the step each task is building, by branch/step/task/kind
            var counts = new Dictionary<string, ulong>(); // the last count seen of each task, by branch/step/task
            double prev = double.MinValue;
            int came = int.MinValue; // the look the quest came to the step it is on
            double cameAt = double.MinValue;
            foreach (Change ch in changes)
            {
                // (a count shows a moment after its action: two kills close together can tick after the earlier tick)
                double from = Math.Max(prev - 3, ch.At - MaxBack);
                switch (ch.Kind)
                {
                    case TrackKind.Added:
                    {
                        // (the game's hidden helper missions come with their parent mission or the area: no board or NPC
                        // ever offers one, so no pick-up; their steps wait for them)
                        CoreStep p = tracks.Exists(t => t.Ev != null && t.Ev.QuestHidden) ? null : Pickup(c, ch.Main, used);
                        if (p != null)
                        {
                            p.QuestName = qname;
                            steps.Add(p);
                        }
                        open.Clear();
                        break;
                    }
                    case TrackKind.Task:
                    {
                        if (FromOtherCompletion(ctx, q, ch))
                            break; // counted when another mission completed in this look
                        int rank;
                        string why;
                        string tk = ch.Branch + "/" + ch.Step + "/" + ch.Task;
                        ulong was;
                        counts.TryGetValue(tk, out was);
                        counts[tk] = ch.Count;
                        Fact cause = Cause(ctx, q, ch, from, used, out rank, out why);
                        if (cause != null)
                            used.Add(cause);
                        ctx.Claim(cause, q, ch.At, Strength(rank, why));
                        CoreStep cur = OpenFor(open, steps, ch, cause, qname, ch.Task, true);
                        // (the count moved by more than one: the ticks between were not rowed, so their causes come before)
                        var more = new List<KeyValuePair<Fact, int>>();
                        for (ulong k = was + 1; cause != null && k < ch.Count; k++)
                        {
                            int r2;
                            Fact extra = NextLike(ctx, q, ch, from, used, cause, out r2);
                            if (extra == null)
                                break;
                            used.Add(extra);
                            ctx.Claim(extra, q, ch.At, r2 == 1 ? 3 : 1);
                            more.Insert(0, new KeyValuePair<Fact, int>(extra, r2));
                        }
                        foreach (KeyValuePair<Fact, int> m in more)
                            Add(c, cur, m.Key, ch, m.Value, m.Value == 1 ? null : "a count the recorder did not row: the same kind of action before it");
                        Tries(ctx, q, ch, from, used, cause, cur);
                        Add(c, cur, cause, ch, rank, why);
                        cur.CountTo = Math.Max(cur.CountTo, ch.Count);
                        cur.Max = Math.Max(cur.Max, ch.Max);
                        break;
                    }
                    case TrackKind.Step:
                    case TrackKind.Completed:
                    {
                        if (!Building(open, ch) && FromOtherCompletion(ctx, q, ch))
                        {
                            // (a chain: this step was "complete that other mission"; a weekly that completes with it)
                            open.Clear();
                            if (ch.Kind == TrackKind.Completed)
                                Ending(ctx, ch, tracks, ch.At - 3, used, steps, qname);
                            break;
                        }
                        // (the actions of a step start after the quest's previous change: no lag allowance here)
                        ch.NotBy = came;
                        ch.Since = cameAt;
                        StepDone(ctx, q, ch, Math.Max(prev, ch.At - MaxBack), used, open, steps, qname);
                        if (ch.Kind == TrackKind.Completed)
                            Ending(ctx, ch, tracks, Math.Max(prev, ch.At - MaxBack), used, steps, qname);
                        // the step is done: every task of it is closed
                        var keys = new List<string>(open.Keys);
                        foreach (string k in keys)
                            if (k.StartsWith(ch.Branch + "/" + ch.Step + "/", StringComparison.Ordinal))
                            {
                                CoreStep d = open[k];
                                d.CountTo = Math.Max(d.CountTo, d.Max);
                                open.Remove(k);
                            }
                        break;
                    }
                }
                prev = ch.At;
                if (ch.Kind == TrackKind.Added || ch.Kind == TrackKind.Step)
                {
                    came = ch.Look;
                    cameAt = ch.At;
                }
            }
        }

        /// <summary>The step this task's change goes into: the one it is building with a cause of the same kind, or a new one.</summary>
        private static CoreStep OpenFor(Dictionary<string, CoreStep> open, List<CoreStep> steps, Change ch, Fact cause, string qname, int task, bool collect)
        {
            StepKind kind = KindOf(cause);
            // (one counter fed by objects and loot is a collect; the actions of a step with no counter stay apart,
            // except more of the same type)
            string what = cause == null ? string.Empty : "/" + Fmt.Hex(cause.Spec) + (kind == StepKind.Item || kind == StepKind.Ability ? "/" + (cause.Item.Length > 0 ? cause.Item : cause.Cast) : string.Empty);
            string key = ch.Branch + "/" + ch.Step + "/" + task + "/" + (collect && IsCollect(kind) ? "collect" : kind + (collect ? string.Empty : what));
            CoreStep cur;
            if (open.TryGetValue(key, out cur))
            {
                if (cur.Kind != kind)
                    cur.Kind = Merge(cur.Kind, kind);
                return cur;
            }
            cur = new CoreStep { Kind = kind, Quest = ch.Main.Quest, QuestName = qname, Branch = ch.Branch, Step = ch.Step, Task = task, At = cause != null ? cause.At : ch.At, Area = ch.Area };
            open[key] = cur;
            steps.Add(cur);
            return cur;
        }

        private static bool Building(Dictionary<string, CoreStep> open, Change ch)
        {
            string prefix = ch.Branch + "/" + ch.Step + "/";
            foreach (string k in open.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>A count of this quest in the look another quest completed in, with no action of its own: it follows from that.</summary>
        private static bool FromOtherCompletion(Ctx ctx, ulong q, Change ch)
        {
            foreach (KeyValuePair<int, ulong> d in ctx.DoneLooks)
                if (d.Value != q && Math.Abs(d.Key - ch.Look) <= 1)
                {
                    if (ch.Targets.Count > 0 && !ch.Borrowed)
                        foreach (Fact f in Window(ctx.C, ch, ch.At - 10))
                            if (f.Spec != 0 && ch.Targets.Contains(f.Spec))
                                return false; // its own action, tied by ids
                    return true;
                }
            return false;
        }

        /// <summary>
        /// A step was done (the step changed, or the quest completed). The tick that finished it is often not in the
        /// recording (it shows the step change instead), so its cause joins the step its task was building; a step with
        /// no counts seen gets one step per action you did for it, in your order.
        /// </summary>
        private static void StepDone(Ctx ctx, ulong q, Change ch, double from, HashSet<Fact> used, Dictionary<string, CoreStep> open, List<CoreStep> steps, string qname)
        {
            CoreFacts c = ctx.C;
            string prefix = ch.Branch + "/" + ch.Step + "/";
            var building = new List<CoreStep>();
            foreach (KeyValuePair<string, CoreStep> p in open)
                if (p.Key.StartsWith(prefix, StringComparison.Ordinal))
                    building.Add(p.Value);
            // (the mission ended a moment after it came to its last step - a reward window, a "return to the dropbox" it
            // did on its own: nothing you did then made that)
            if (ch.Kind == TrackKind.Completed && building.Count == 0 && ch.At - ch.Since < 2.5)
                return;
            int rank;
            string why;
            Fact cause = Cause(ctx, q, ch, from, used, out rank, out why);
            ctx.Claim(cause, q, ch.At, Strength(rank, why));
            if (building.Count > 0)
            {
                if (cause == null)
                    return; // the last count's tick made the step: nothing more was needed
                used.Add(cause);
                StepKind kind = KindOf(cause);
                // the tick that finished a task still counting (the recording shows the step change instead of it):
                // that task's step; a task already full, or a target that task never counted, is another task's
                foreach (CoreStep b in building)
                    if (Compatible(b.Kind, kind) && ((b.Max > 0 && b.CountTo < b.Max) || (cause.Spec != 0 && b.Specs.Contains(cause.Spec))))
                    {
                        if (b.Kind != kind)
                            b.Kind = Merge(b.Kind, kind);
                        Tries(ctx, q, ch, from, used, cause, b);
                        Add(c, b, cause, ch, Math.Min(rank, 2), null);
                        return;
                    }
                CoreStep n = OpenFor(open, steps, ch, cause, qname, -1, false);
                Add(c, n, cause, ch, rank, why);
                return;
            }
            // (pass 1 claims only what the game's ids tie: a step with no counts is tied by time, after every quest's ids)
            if (ctx.IdsOnly)
                return;
            // many clicks on objects at one spot, no count: a puzzle, done by hand
            if (Puzzle(ctx, q, ch, from, used, open, steps, qname))
                return;
            // no count of this step was seen: every action you did for it, then the one that finished it
            List<Fact> acts = Actions(ctx, q, ch, from, used, cause);
            if (acts.Count == 0)
            {
                if (ch.Kind == TrackKind.Completed)
                    return; // the end itself (a hand-in, a reward) is the step
                CoreStep w = OpenFor(open, steps, ch, null, qname, -1, false);
                w.Came = ch.Since;
                Add(c, w, null, ch, 3, why ?? "nothing you did was seen before the step changed (a walk, a cutscene or an area)");
                return;
            }
            foreach (Fact f in acts)
            {
                used.Add(f);
                if (f != cause)
                    ctx.Claim(f, q, ch.At, 0);
                CoreStep n = OpenFor(open, steps, ch, f, qname, -1, false);
                Add(c, n, f, ch, f == cause ? rank : 2, f == cause ? why : "tied by time: done for this step before it changed");
            }
        }

        /// <summary>
        /// The actions of a step that showed no counts: object uses, mission items, talks and quest abilities in the
        /// window, a loot in the last 10 s, and the cause; in your order, one per kind of target.
        /// </summary>
        private static List<Fact> Actions(Ctx ctx, ulong q, Change ch, double from, HashSet<Fact> used, Fact cause)
        {
            var list = new List<Fact>();
            foreach (Fact f in Window(ctx.C, ch, from))
            {
                // (older than a minute: only a click the game acted on - a cast bar, like End of Torvix's Master Safety
                // Control before two minutes of waves - is still this step's)
                if (f.At < ch.At - 60 && !OldClick(ctx.C, f))
                    continue;
                if (used.Contains(f) || TakenByOther(ctx, q, f))
                    continue;
                bool strong = f.Kind == FactKind.UseObject || f.Kind == FactKind.Item || (f.Kind == FactKind.Ability && !f.InFight && f.Cast.Length > 0) ||
                              (f.Kind == FactKind.Talk && ch.Kind != TrackKind.Completed);
                bool lastLoot = f.Kind == FactKind.Loot && ch.At - f.At <= 10;
                if (!(strong || lastLoot || f == cause))
                    continue;
                // (one more click on an object this mission already used - the device the step before was done on - is not
                // a new action of this step)
                if (f != cause && f.Kind == FactKind.UseObject && f.Node != 0 && UsedNode(used, f.Node))
                    continue;
                bool dup = false;
                foreach (Fact g in list)
                    if (g.Kind == f.Kind && g.Spec != 0 && g.Spec == f.Spec && g.Item == f.Item)
                        dup = true;
                if (!dup)
                    list.Add(f);
            }
            if (cause != null && !list.Contains(cause))
                list.Add(cause);
            list.Sort((x, y) => x.At.CompareTo(y.At));
            return list;
        }

        private static bool UsedNode(HashSet<Fact> used, ulong node)
        {
            foreach (Fact g in used)
                if (g.Node == node)
                    return true;
            return false;
        }

        /// <summary>A kind of action that makes missions move (a bare click on an NPC moves no count).</summary>
        private static bool Moves(Fact f, Change ch)
        {
            switch (f.Kind)
            {
                case FactKind.Item:
                case FactKind.UseObject:
                case FactKind.Loot:
                case FactKind.Kill:
                    return true;
                case FactKind.Talk:
                    return f.Convo || f.Did || ch.Kind != TrackKind.Task;
                case FactKind.Ability:
                    return !f.InFight && f.Cast.Length > 0;
            }
            return false;
        }

        /// <summary>The objective has the whole name of what the action was done to in it ("Biotoxin Container" in "Recover Biotoxin Containers").</summary>
        private static bool NamedIn(string goal, Fact f)
        {
            if (goal.Length == 0 || f.Target == null || f.Target.Kind == ObjKind.Player)
                return false;
            string n = f.Target.Name.Trim();
            if (n.Length < 4 || n.StartsWith("#", StringComparison.Ordinal) || n.StartsWith("@", StringComparison.Ordinal))
                return false;
            // (word by word, so "Fa'athra Gangster" is in "Defeat Fa'athra's Gangsters" and "Biotoxin Container" in "Recover Biotoxin Containers")
            List<string> g = Words(goal), w = Words(n);
            if (w.Count == 0)
                return false;
            for (int i = 0; i + w.Count <= g.Count; i++)
            {
                int k = 0;
                while (k < w.Count && g[i + k] == w[k])
                    k++;
                if (k == w.Count)
                    return true;
            }
            return false;
        }

        /// <summary>The words of a text in lower case, with a possessive "'s" and a plural "s" taken off.</summary>
        private static List<string> Words(string text)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                char ch = i < text.Length ? text[i] : ' ';
                if (char.IsLetterOrDigit(ch) || ch == '\'' || ch == '\u2019' || ch == '-')
                {
                    sb.Append(char.ToLowerInvariant(ch == '\u2019' ? '\'' : ch));
                    continue;
                }
                if (sb.Length == 0)
                    continue;
                string word = sb.ToString().Trim('\'', '-');
                sb.Length = 0;
                if (word.EndsWith("'s", StringComparison.Ordinal))
                    word = word.Substring(0, word.Length - 2);
                if (word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal) && !word.EndsWith("ss", StringComparison.Ordinal))
                    word = word.Substring(0, word.Length - 1);
                if (word.Length > 0)
                    list.Add(word);
            }
            return list;
        }

        /// <summary>The facts that could have made this change: from the window's start to the change's look.</summary>
        private static List<Fact> Window(CoreFacts c, Change ch, double from)
        {
            var list = new List<Fact>();
            foreach (Fact f in c.Facts)
            {
                if (f.Kind == FactKind.Board || f.Interrupted)
                    continue;
                if (f.At < from - 0.01 || f.At > ch.At + 0.6 || f.FirstLook > ch.Look || f.Look <= ch.NotBy)
                    continue;
                if (f.Kind == FactKind.UseObject && f.CastDone > ch.At + 0.6)
                    continue; // its cast had not ended yet: it did nothing by then
                list.Add(f);
            }
            return list;
        }

        /// <summary>
        /// A click more than a minute before the step changed that is still this step's: an object the game says one of
        /// your tasks counts (the recorder's task scanner saw it); in a recording from before the scanner, an object you
        /// clicked once in the whole recording and the game ran a cast bar for (a console, a switch - not one of a row of
        /// storage pods, nor a kolto tank).
        /// </summary>
        private static bool OldClick(CoreFacts c, Fact f)
        {
            if (f.Kind != FactKind.UseObject || f.Target == null || f.Target.Name.Length == 0)
                return false;
            bool scanner = false;
            foreach (RecEvent e in c.Events)
            {
                if (e.Target == null || e.Target.How == null || e.Target.How.IndexOf("one of your tasks counts", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                scanner = true;
                if ((f.Target.Spec != 0 && e.Target.Spec == f.Target.Spec) || e.Target.Name == f.Target.Name)
                    return true;
            }
            return !scanner && c.IsObjectCastName(f.Cast) && OneOff(c, f);
        }

        /// <summary>
        /// An object you clicked once in the whole recording (a console, a switch): not one of a row you used for a count
        /// (storage pods, kolto containers), nor a tank you used now and then.
        /// </summary>
        private static bool OneOff(CoreFacts c, Fact f)
        {
            if (f.Target == null || f.Target.Name.Length == 0)
                return false;
            int n = 0;
            foreach (Fact g in c.Facts)
                if (g.Kind == FactKind.UseObject && g.Target != null && g.Target.Name == f.Target.Name)
                    n++;
            return n == 1;
        }

        /// <summary>Another quest's count is tied to this fact by the game's ids.</summary>
        private static bool TakenByOther(Ctx ctx, ulong q, Fact f)
        {
            ulong owner;
            return ctx.Claimed.TryGetValue(f, out owner) && owner != q;
        }

        /// <summary>
        /// The newest action before this one of the same kind that this count could also be from: a type the task
        /// counts (rank 1), or the same type or item as the one that counted (rank 2).
        /// </summary>
        private static Fact NextLike(Ctx ctx, ulong q, Change ch, double from, HashSet<Fact> used, Fact like, out int rank)
        {
            rank = 0;
            Fact best = null;
            foreach (Fact f in Window(ctx.C, ch, from))
            {
                if (f == like || used.Contains(f) || f.Kind != like.Kind || f.At > like.At || TakenByOther(ctx, q, f))
                    continue;
                int r;
                if (f.Spec != 0 && ch.Targets.Contains(f.Spec))
                    r = 1;
                else if (f.Kind == FactKind.Item || f.Kind == FactKind.Ability ? (f.Item.Length > 0 ? f.Item : f.Cast) == (like.Item.Length > 0 ? like.Item : like.Cast) : f.Spec != 0 && f.Spec == like.Spec)
                    r = 2;
                else
                    continue;
                if (best == null || f.At > best.At)
                {
                    best = f;
                    rank = r;
                }
            }
            return best;
        }

        /// <summary>
        /// The same mission item (or quest ability) used before the use that counted, with nothing counting in between:
        /// a try at another spot. Where it works can change between runs (spies, scans), so every spot is kept, in order.
        /// </summary>
        private static void Tries(Ctx ctx, ulong q, Change ch, double from, HashSet<Fact> used, Fact cause, CoreStep s)
        {
            if (cause == null || (cause.Kind != FactKind.Item && cause.Kind != FactKind.Ability))
                return;
            string key = cause.Item.Length > 0 ? cause.Item : cause.Cast;
            if (key.Length == 0)
                return;
            foreach (Fact f in Window(ctx.C, ch, from))
            {
                if (f == cause || used.Contains(f) || f.At >= cause.At || f.Kind != cause.Kind || TakenByOther(ctx, q, f))
                    continue;
                if ((f.Item.Length > 0 ? f.Item : f.Cast) != key)
                    continue;
                used.Add(f);
                ctx.Claim(f, q, ch.At, 0);
                s.Tries.Add(f);
                s.AddTarget(f.Target, f.Me);
            }
        }

        private static void Add(CoreFacts c, CoreStep s, Fact cause, Change ch, int rank, string why)
        {
            s.End = Math.Max(s.End, ch.At);
            if (ch.Kind == TrackKind.Task)
                foreach (ulong x in ch.Targets)
                    if (!s.Accepts.Contains(x))
                        s.Accepts.Add(x);
            s.Rank = Math.Max(s.Rank, rank);
            if (why != null)
                s.Doubt(why);
            if (cause == null)
            {
                s.AddSpot(ch.Pos);
                return;
            }
            s.Causes.Add(cause);
            s.InFight |= cause.InFight && cause.Kind != FactKind.Kill && cause.Kind != FactKind.Loot;
            foreach (string n in cause.Notes)
                s.Doubt(n);
            switch (cause.Kind)
            {
                case FactKind.Item:
                case FactKind.Ability:
                    if (s.Item.Length == 0)
                        s.Item = cause.Item;
                    if (s.Cast.Length == 0)
                        s.Cast = cause.Cast;
                    if (s.LogAbility == 0)
                        s.LogAbility = cause.LogAbility;
                    if (cause.Target != null && cause.Target.Dead)
                        s.DeadOnly = true;
                    s.AddTarget(cause.Target, cause.Me);
                    break;
                case FactKind.Loot:
                    s.Loot = true;
                    s.AddTarget(cause.Target, cause.Me);
                    break;
                default:
                    s.AddTarget(cause.Target, cause.Me);
                    break;
            }
        }

        // ------------------------------------------------------------------ B3: the cause of one change

        /// <summary>
        /// The action that made this change: rank 1 when the game's own ids tie it (its type id is one the task counts);
        /// rank 2 when the task names no ids and it is the newest action of a kind that makes missions move; rank 3 a
        /// guess (a kill with no ids, or ids named that nothing seen had). Null: nothing was seen.
        /// </summary>
        private static Fact Cause(Ctx ctx, ulong q, Change ch, double from, HashSet<Fact> used, out int rank, out string why)
        {
            rank = 0;
            why = null;
            var window = new List<Fact>();
            foreach (Fact f in Window(ctx.C, ch, from))
                if (!used.Contains(f))
                    window.Add(f);
            if (ch.Targets.Count > 0)
            {
                Fact best = null;
                foreach (Fact f in window)
                    if (f.Spec != 0 && ch.Targets.Contains(f.Spec) && (best == null || Better(f, best)))
                        best = f;
                if (best != null)
                {
                    rank = 1;
                    return best;
                }
            }
            if (ctx.IdsOnly)
                return null;
            // the tracker's objective names what one of your actions was done to ("Defeat CZ-5X Eradicator Droid"): the newest such
            Fact byName = null;
            foreach (Fact f in window)
                if (Moves(f, ch) && !TakenByOther(ctx, q, f) && NamedIn(ch.Goal, f) && (byName == null || f.At >= byName.At))
                    byName = f;
            if (byName != null)
            {
                rank = 2;
                why = "tied by name: the objective names what you did it to";
                return byName;
            }
            // no ids tie it: the newest mission action (item, object, talk, quest ability), then a loot, then a kill;
            // never one the game's ids tie to another quest's count
            Fact act = null, loot = null, kill = null;
            foreach (Fact f in window)
            {
                if (TakenByOther(ctx, q, f))
                    continue;
                // (what another task of this step names is that task's: a Kolto Container is not a Biotoxin Container)
                if (ch.Others.Exists(g => NamedIn(g, f)))
                    continue;
                switch (f.Kind)
                {
                    case FactKind.Item:
                    case FactKind.UseObject:
                    case FactKind.Talk:
                        // (a click on an NPC that opened nothing, just after an item or object use, is not the action)
                        if (f.Kind == FactKind.Talk && !f.Convo && !f.Did && act != null && (act.Kind == FactKind.Item || act.Kind == FactKind.UseObject) && f.At - act.End < 6)
                            break;
                        // (nor does a click on an NPC that opened no conversation move a count by itself)
                        if (f.Kind == FactKind.Talk && !f.Convo && !f.Did && ch.Kind == TrackKind.Task)
                            break;
                        if (act == null || f.At >= act.At)
                            act = f;
                        break;
                    case FactKind.Ability:
                        if (!f.InFight && f.Cast.Length > 0 && (act == null || f.At >= act.At))
                            act = f;
                        break;
                    case FactKind.Loot:
                        if (loot == null || f.At >= loot.At)
                            loot = f;
                        break;
                    case FactKind.Kill:
                        if (kill == null || f.At >= kill.At)
                            kill = f;
                        break;
                }
            }
            // (named ids that came from another row, a step change's: they may be another task's, so no weaker for it)
            bool named = ch.Targets.Count > 0 && !ch.Borrowed;
            if (named && act != null && act.Kind == FactKind.Item && (act.Target == null || act.Target.Spec == 0))
            {
                rank = 2;
                why = "tied by time: the mission item was used just before it counted";
                return act;
            }
            string tail = named ? " (no action seen had a type id this task counts)" : " (the mission names no ids here)";
            // (a step that changed the very moment of a kill, with nothing else done for 2 s before it: the kill did it - a boss
            // that drops the card; a count stays with the action before it, a droid let out of a pod counts with the pods)
            Fact other = loot != null && (act == null || loot.At > act.At) ? loot : act;
            if (ch.Kind != TrackKind.Task && kill != null && ch.At - kill.At <= 1.5 && (other == null || other.At < kill.At - 2))
            {
                rank = named ? 3 : 2;
                why = "tied by time" + tail + ": the kill the moment it changed";
                return kill;
            }
            // (a count that ticked the moment of a kill, your last other action a talk long before it: the kill counted, not a
            // talk from 20 s earlier; an object or item can let out what you then kill - a pod's droid counts with the pods)
            if (ch.Kind == TrackKind.Task && kill != null && ch.At - kill.At <= 1.5 && act != null && act.Kind == FactKind.Talk &&
                kill.At - act.End > 10 && (loot == null || loot.At < kill.At))
            {
                rank = named ? 3 : 2;
                why = "tied by time" + tail + ": the kill the moment it counted (your last other action was long before)";
                return kill;
            }
            // (you went on from it: more than 15 s later and more than about 30 m away it changed - getting there did it)
            Fact newest = other ?? kill;
            if (newest != null && ch.At - newest.End > 15 && !ch.Pos.IsZero && !newest.Me.IsZero && ch.Pos.DistanceTo(newest.Me) > 3f)
            {
                why = "it changed where you went after your last action (" + Fmt.F1(ch.At - newest.End) + " s and about " +
                      ((int)(ch.Pos.DistanceTo(newest.Me) * 10)).ToString(CultureInfo.InvariantCulture) + " m on): reaching that spot was the step";
                rank = 2;
                return null;
            }
            // (a loot newer than every other action: the loot is what counted)
            if (loot != null && (act == null || loot.At > act.At))
            {
                rank = named ? 3 : 2;
                why = "tied by time" + tail + ": the loot just before it";
                return loot;
            }
            if (act != null)
            {
                rank = named ? 3 : 2;
                why = "tied by time" + tail + ": the last action before it";
                return act;
            }
            if (kill != null)
            {
                rank = 3;
                why = "tied by time" + tail + ": the last kill before it";
                return kill;
            }
            why = "nothing you did was seen before it counted (a walk, a cutscene or an area)";
            rank = 3;
            return null;
        }

        /// <summary>Of two id matches: the newer one (an object use beats a kill at the same moment).</summary>
        private static bool Better(Fact f, Fact best)
        {
            if (Math.Abs(f.At - best.At) < 0.3)
                return Weight(f.Kind) > Weight(best.Kind);
            return f.At > best.At;
        }

        private static int Weight(FactKind k)
        {
            switch (k)
            {
                case FactKind.Item:
                    return 5;
                case FactKind.UseObject:
                    return 4;
                case FactKind.Loot:
                    return 3;
                case FactKind.Talk:
                    return 2;
                case FactKind.Kill:
                    return 1;
                default:
                    return 0;
            }
        }

        private static StepKind KindOf(Fact f)
        {
            if (f == null)
                return StepKind.Walk;
            switch (f.Kind)
            {
                case FactKind.Kill:
                    return StepKind.Kill;
                case FactKind.Loot:
                    return StepKind.KillLoot;
                case FactKind.UseObject:
                    return StepKind.Use;
                case FactKind.Talk:
                    return StepKind.Talk;
                case FactKind.Item:
                    return StepKind.Item;
                case FactKind.Ability:
                    return StepKind.Ability;
                default:
                    return StepKind.Walk;
            }
        }

        /// <summary>Two causes of one task's counts that make one step (objects and loot of one counter: a collect).</summary>
        private static bool Compatible(StepKind a, StepKind b)
        {
            if (a == b)
                return true;
            return IsCollect(a) && IsCollect(b);
        }

        private static bool IsCollect(StepKind k)
        {
            return k == StepKind.Use || k == StepKind.KillLoot || k == StepKind.Collect || k == StepKind.Kill;
        }

        private static StepKind Merge(StepKind a, StepKind b)
        {
            if (a == b)
                return a;
            if ((a == StepKind.Kill && b == StepKind.KillLoot) || (a == StepKind.KillLoot && b == StepKind.Kill))
                return StepKind.KillLoot;
            return StepKind.Collect;
        }

        // ------------------------------------------------------------------ pick-up and the end of a quest

        /// <summary>
        /// Where the quest came from: a mission board you had open (the newest one), an NPC you talked to, or nothing
        /// (granted by a loot, an area, another quest: no pick-up step).
        /// </summary>
        private static CoreStep Pickup(CoreFacts c, Track t, HashSet<Fact> used)
        {
            Fact board = null, talk = null, other = null;
            foreach (Fact f in c.Facts)
            {
                if (f.FirstLook > t.Look || f.At > t.At + 0.6 || f.At < t.At - 60)
                    continue;
                if (f.Kind == FactKind.Board)
                    board = f;
                else if (f.Kind == FactKind.Talk && t.At - f.End < 20)
                    talk = f;
                else if ((f.Kind == FactKind.Loot || f.Kind == FactKind.UseObject || f.Kind == FactKind.Kill || f.Kind == FactKind.Item) && t.Look - f.Look <= 1)
                    other = f;
            }
            var s = new CoreStep { Kind = StepKind.Pickup, Quest = t.Quest, Branch = t.Branch, Step = t.Step, At = t.At, End = t.At, Area = t.Ev != null ? t.Ev.AreaId : 0 };
            // a board open within the last minute and no conversation after it
            if (board != null && (talk == null || talk.At < board.At) && (other == null || other.At < board.At))
            {
                s.AddTarget(board.Target, board.Me);
                s.Causes.Add(board);
                return s;
            }
            if (talk != null && (other == null || other.At < talk.At))
            {
                s.AddTarget(talk.Target, talk.Me);
                s.Causes.Add(talk);
                used.Add(talk);
                return s;
            }
            return null; // granted on its own (a loot, an area, the last mission)
        }

        /// <summary>
        /// The quest completed: an NPC you talked to just before is a hand-in; a holocom call; otherwise the reward is
        /// taken where you stood (a "Return to the Mission Dropbox" step, a reward window, an object turn-in).
        /// </summary>
        private static void Ending(Ctx ctx, Change ch, List<Track> tracks, double from, HashSet<Fact> used, List<CoreStep> steps, string qname)
        {
            CoreFacts c = ctx.C;
            Fact talk = null;
            // (an end a moment after the quest came to its last step: only the conversation that brought that step can be
            // the hand-in, not a click begun since - no one walks to an NPC and hands in within 2 s)
            bool atOnce = ch.At - ch.Since < 2.5;
            foreach (Fact f in Window(c, ch, Math.Max(from, ch.At - 15)))
                if (f.Kind == FactKind.Talk && !used.Contains(f) && ch.At - f.End < 15 && !(atOnce && f.At >= ch.Since - 0.01))
                    talk = f;
            bool holocom = false;
            bool reward = false;
            foreach (Track t in tracks)
            {
                if (t.Kind == TrackKind.Holocom && Math.Abs(t.At - ch.At) < 30)
                    holocom = true;
                if (t.Kind == TrackKind.Reward && Math.Abs(t.Look - ch.Look) <= 2)
                    reward = true;
            }
            var s = new CoreStep { Quest = ch.Main.Quest, QuestName = qname, Branch = ch.Branch, Step = ch.Step, At = ch.At, End = ch.At, Area = ch.Area };
            if (talk != null)
            {
                s.Kind = StepKind.HandIn;
                s.AddTarget(talk.Target, talk.Me);
                s.Causes.Add(talk);
                used.Add(talk);
            }
            else if (holocom)
            {
                s.Kind = StepKind.Holocom;
                s.AddSpot(ch.Pos);
            }
            else
            {
                s.Kind = StepKind.Reward;
                s.AddSpot(ch.Pos);
                if (!reward)
                    s.Doubt("no reward window was seen: the mission completed on its own");
            }
            steps.Add(s);
        }

        // ------------------------------------------------------------------ a readable dump (tests)

        public static bool DumpCauses;

        public static string Dump(List<CoreStep> steps)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (CoreStep s in steps)
            {
                n++;
                sb.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(' ').Append(Fmt.F1(s.At).PadLeft(7)).Append(' ').Append(s.KindText.PadRight(9))
                  .Append(' ').Append(Fmt.Hex(s.Quest)).Append(" b").Append(s.Branch).Append(" s").Append(s.Step);
                if (s.Task >= 0)
                    sb.Append(" task ").Append(s.Task);
                if (s.CountTo > 0)
                    sb.Append(" to ").Append(s.CountTo).Append('/').Append(s.Max);
                if (s.Item.Length > 0)
                    sb.Append(" item=\"").Append(s.Item).Append('"');
                if (s.Cast.Length > 0)
                    sb.Append(" cast=\"").Append(s.Cast).Append('"');
                if (s.Specs.Count > 0)
                    sb.Append(" specs=").Append(string.Join(",", s.Specs.ConvertAll(Fmt.Hex)));
                if (s.Anchors.Count > 0)
                    sb.Append(" anchors=").Append(s.Anchors.Count);
                sb.Append(" spots=").Append(s.Spots.Count);
                if (s.InFight)
                    sb.Append(" in-fight");
                if (s.Loot)
                    sb.Append(" loot");
                sb.Append(" rank").Append(s.Rank);
                sb.Append("  \"").Append(s.QuestName).Append('"');
                foreach (string u in s.Unsure)
                    sb.Append("\n            ? ").Append(u);
                if (DumpCauses)
                    foreach (Fact f in s.Causes)
                        sb.Append("\n            = ").Append(f.ToString());
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
