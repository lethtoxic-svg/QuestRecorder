// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;

namespace QuestRecorder
{
    /// <summary>
    /// The mission steps written the way a good hand-made profile writes them: BuddyCron's own steps (PickUpQuest, Grind on
    /// a GrindArea, UseObject, UseItem, TalkTo, CompleteQuest, MoveTo), each inside an If that is true while that step is still
    /// to do, and each step ending when the game says it is done (no time limits, no give-ups). What the recording saw fills
    /// them in: the game's own names for what the task counts, every spot, where you stood, how far you stood, whether you
    /// were in a fight. Only three jobs stay code, written by the plain pass as before: a mission board (the board read), a row
    /// of mission items used once each in order, and a special-bar ability.
    /// </summary>
    internal static class CoreWrite
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The GrindAreas the last <see cref="Blocks"/> wrote (for the profile's header).</summary>
        [ThreadStatic] private static StringBuilder _areas;
        [ThreadStatic] private static HashSet<string> _areaNames;
        /// <summary>Missions the recording saw on more than one branch (their steps are asked by branch).</summary>
        [ThreadStatic] private static HashSet<ulong> _branched;
        /// <summary>Where each transport put you, with the steps you did there.</summary>
        [ThreadStatic] private static List<Site> _sites;
        /// <summary>What the block being made also needs to be true (null = nothing): not at another open mission's site.</summary>
        [ThreadStatic] private static string _extra;

        public static string AreasXml
        {
            get { return _areas != null ? _areas.ToString() : string.Empty; }
        }

        /// <summary>No GrindAreas yet (a new profile).</summary>
        public static void Reset()
        {
            _areas = null;
            _areaNames = null;
        }

        /// <summary>Names already used by the header's other GrindAreas (so a name is never there twice).</summary>
        public static bool AreaNameTaken(string name)
        {
            return _areaNames != null && _areaNames.Contains(name);
        }

        // ------------------------------------------------------------------ gates

        private static string H(ulong q)
        {
            return Fmt.Hex(q);
        }

        private static string U(ulong v)
        {
            return Fmt.UInt(v);
        }

        /// <summary>
        /// True while the step is still to do: the mission is in the log and not past that step. For a task: on an earlier
        /// step (it comes next), or on that step with the task not done. (The game's own IsTaskComplete is true for every
        /// step but the one the mission is on, so it is only asked once the mission has come to that step.)
        /// </summary>
        public static string Todo(CoreStep s)
        {
            string q = H(s.Quest);
            string g = "HasQuest(" + q + ") and not " + StepDone(s, s.Step);
            if (s.Task < 0)
                return g;
            string task = TaskOpen(s);
            if (s.Step <= 1)
                return g + " and " + task;
            return g + " and (not " + StepDone(s, s.Step - 1) + " or " + task + ")";
        }

        /// <summary>
        /// The game's "past that step": IsStepComplete, or for a mission the recording saw on more than one branch
        /// IsBranchStepComplete (true once the mission is on another branch, so a step of one branch never holds the bot
        /// after the mission went on by another).
        /// </summary>
        private static string StepDone(CoreStep s, ulong step)
        {
            string q = H(s.Quest);
            if (Branched(s))
                return "IsBranchStepComplete(" + q + ", " + U(s.Branch) + ", " + U(step) + ")";
            return "IsStepComplete(" + q + ", " + U(step) + ")";
        }

        private static bool Branched(CoreStep s)
        {
            return _branched != null && s.Branch > 0 && _branched.Contains(s.Quest);
        }

        private static string TaskOpen(CoreStep s)
        {
            string q = H(s.Quest);
            return Partial(s) ? "not IsTaskValueGreaterThan(" + q + ", " + U(s.Step) + ", " + s.Task.ToString(Inv) + ", " + U(s.CountTo - 1) + ")"
                              : "not IsTaskComplete(" + q + ", " + U(s.Step) + ", " + s.Task.ToString(Inv) + ")";
        }

        /// <summary>True while the mission is on that step and the task is not done (only once it has come to that step).</summary>
        public static string OnTask(CoreStep s)
        {
            // (IsTaskComplete is true off that step; on a branched mission also on that branch)
            return Branched(s) ? "not " + StepDone(s, s.Step) + " and " + TaskOpen(s) : TaskOpen(s);
        }

        /// <summary>BranchId on a tag of a branched mission (the tag's own "step done" then asks by branch).</summary>
        private static string BranchAttr(CoreStep s)
        {
            return Branched(s) ? " BranchId=\"" + U(s.Branch) + "\"" : string.Empty;
        }

        /// <summary>The step's count stops short of the task's own (an earlier part of a task done in parts).</summary>
        private static bool Partial(CoreStep s)
        {
            return s.Task >= 0 && s.Max > 0 && s.CountTo > 0 && s.CountTo < s.Max;
        }

        private static string Open(ulong q)
        {
            return "HasQuest(" + H(q) + ") and not IsQuestComplete(" + H(q) + ")";
        }

        // ------------------------------------------------------------------ the blocks

        public static List<CoreRender.Block> Blocks(CoreFacts c, List<CoreStep> steps)
        {
            _areas = new StringBuilder();
            _areaNames = new HashSet<string>(StringComparer.Ordinal);
            _branched = new HashSet<ulong>();
            var firstBranch = new Dictionary<ulong, ulong>();
            foreach (CoreStep x in steps)
            {
                if (x.Quest == 0 || x.Branch == 0)
                    continue;
                ulong fb;
                if (!firstBranch.TryGetValue(x.Quest, out fb))
                    firstBranch[x.Quest] = x.Branch;
                else if (fb != x.Branch)
                    _branched.Add(x.Quest);
            }
            CoreRender.Stretch(steps, c);
            MergeHiddenKills(c, steps);
            _sites = Sites(c, steps);
            // the clicks that opened a mission board (a board pick-up's own): never a mission step's action
            var boardClicks = new List<Fact>();
            foreach (CoreStep x in steps)
                if (x.Kind == StepKind.Pickup)
                    foreach (Fact f in x.Causes)
                        if (f.Kind == FactKind.Board && f.Target != null)
                            boardClicks.Add(f);
            var names = SpecNames(c);
            var blocks = new List<CoreRender.Block>();
            int no = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                CoreStep s = steps[i];
                if (s.Quest == 0)
                    continue; // nothing to gate it on (a transport no mission could be tied to: the travel builder's own hop is kept)
                if (s.Kind == StepKind.Walk && s.Task >= 0 && steps.Exists(n => n != s && n.Kind != StepKind.Walk && n.Kind != StepKind.Reward && n.Quest == s.Quest &&
                                                                              n.Branch == s.Branch && n.Step == s.Step && n.Task == s.Task))
                    continue; // a count nothing was tied to, of a task another step does by what you did: that step's tag does it to the end
                if ((s.Kind == StepKind.Use || s.Kind == StepKind.Collect) && s.Causes.Count > 0 &&
                    s.Causes.TrueForAll(f => f.Kind == FactKind.UseObject && f.Target != null &&
                                             boardClicks.Exists(bc => bc.Target.Name == f.Target.Name && Math.Abs(bc.At - f.At) <= 2)))
                    continue; // only the mission board you opened for a pick-up (the pick-up does it)
                // a row of mission items used once each on one step (runes): one block, the items in your order
                int last = i;
                if (s.Kind == StepKind.Item && s.Task < 0)
                    while (last + 1 < steps.Count && steps[last + 1].Kind == StepKind.Item && steps[last + 1].Quest == s.Quest &&
                           steps[last + 1].Step == s.Step && steps[last + 1].Task < 0)
                        last++;
                Site held = HeldBy(s);
                _extra = held != null ? "not (" + Busy(held) + ")" : null;
                CoreRender.Block b;
                try
                {
                    b = last > i ? Row(c, steps, i, last, names) : Make(c, steps, i, names);
                }
                finally
                {
                    _extra = null;
                }
                i = last;
                if (b == null)
                    continue;
                b.Group = GroupOf(s);
                if (s.Kind != StepKind.Reward)
                    b.No = ++no;
                blocks.Add(b);
            }
            return blocks;
        }

        private static CoreRender.Block Make(CoreFacts c, List<CoreStep> steps, int at, Dictionary<ulong, List<string>> names)
        {
            CoreStep s = steps[at];
            var b = new CoreRender.Block { Step = s, At = s.At, Quest = s.Quest };
            b.Unsure.AddRange(s.Unsure);
            b.Yellow = s.Rank >= 3;
            string text = CoreRender.TaskText(c, s);
            // (an action step with no count of its own, whose objective text is another task's that this profile does as
            // its own step: the text is not this step's - Republic's Most Wanted showed "Defeat Chopper" when it came to
            // the step Burnout's kill finished; the step is named after what you did, "Kill Burnout")
            if (s.Task < 0 && text.Length > 0 && CoreRender.Action(s.Kind) &&
                steps.Exists(n => n != s && n.Quest == s.Quest && n.Branch == s.Branch && n.Step == s.Step && n.Task >= 0 &&
                                  string.Equals(CoreRender.TaskText(c, n), text, StringComparison.Ordinal)))
                text = string.Empty;
            string on = CoreRender.TargetName(s);
            if (s.Task < 0 && !s.Merged && text.Length > 0 && on.Length > 0 && text.IndexOf(on, StringComparison.OrdinalIgnoreCase) < 0 &&
                s.Kind != StepKind.Walk && s.Kind != StepKind.Hop && s.Kind != StepKind.Reward && s.Kind != StepKind.HandIn && s.Kind != StepKind.Pickup)
                text += " (" + on + ")";
            string count = s.CountTo > 1 ? " (" + s.CountTo + ")" : string.Empty;
            // one of the actions you did before the step changed, with another after it: done once, as you did it (the later
            // one moves the mission on)
            bool once = s.Task < 0 && CoreRender.Action(s.Kind) &&
                        steps.FindIndex(at + 1, n => n.Quest == s.Quest && n.Branch == s.Branch && n.Step == s.Step && n.Task < 0 && CoreRender.Action(n.Kind)) >= 0;
            // more than one task on this step: tags name their task (else one would go on once its own task is done)
            bool multi = s.Task >= 0 && (c.Tracks.Exists(t => t.Quest == s.Quest && t.Step == s.Step && t.Task >= 0 && t.Task != s.Task) ||
                                         steps.Exists(n => n != s && n.Quest == s.Quest && n.Branch == s.Branch && n.Step == s.Step && n.Task != s.Task &&
                                                           n.Kind != StepKind.Walk && n.Kind != StepKind.Reward));
            string todo = Todo(s);
            // (in the area you did it; a fight's While keeps only the area: a fight that runs on past a site's edge goes on)
            string here = Here(s, todo);
            string hereWhile = InArea(s, todo);
            var run = b.Run;
            switch (s.Kind)
            {
                case StepKind.Pickup:
                {
                    b.Label = "Pick up " + CoreRender.Name(s);
                    Fact cause = s.Causes.Count > 0 ? s.Causes[0] : null;
                    b.Board = cause != null && cause.Kind == FactKind.Board;
                    if (cause == null || cause.Target == null || cause.Target.Name.Length == 0)
                    {
                        Look(b, "no giver was seen for this mission: the bot can't pick it up");
                        b.Gate = Here(s, "not HasQuest(" + H(s.Quest) + ")");
                        return b; // nothing written: the travel builder's own pick-up (if any) is kept
                    }
                    string giver = cause.Target.Name;
                    Vec3 gp = cause.Target.HasPos ? cause.Target.Pos : cause.Me;
                    if (b.Board)
                    {
                        // a mission board: the board read (the plain pass turns this into BuddyCron's TakeFromBoard hook,
                        // which takes it when the board lists it and otherwise moves straight on)
                        b.Gate = Here(s, "not HasQuest(" + H(s.Quest) + ")");
                        run.Add(If(b.Gate));
                        Stand(run, giver, cause.Me, gp);
                        run.Add("\t<PickUpQuest QuestId=\"" + H(s.Quest) + "\" QuestName=\"" + A(CoreRender.Name(s)) + "\" GiverName=\"" + A(giver) +
                                "\" GiverType=\"Placeable\" SearchRadius=\"5\" XYZ=\"" + X(gp) + "\" />");
                        run.Add("</If>");
                    }
                    else
                    {
                        // an NPC (a conversation or its quest window): walk to where you stood (the right one of two with the same
                        // name), then BuddyCron's PickUpQuest
                        b.Talk = giver;
                        b.TalkAt = gp;
                        b.Gate = Here(s, "not HasQuest(" + H(s.Quest) + ") and not IsQuestComplete(" + H(s.Quest) + ")");
                        run.Add(If(b.Gate));
                        Stand(run, giver, cause.Me, gp);
                        run.Add("\t<PickUpQuest QuestId=\"" + H(s.Quest) + "\" QuestName=\"" + A(CoreRender.Name(s)) + "\" GiverName=\"" + A(giver) +
                                "\" UseRange=\"4\" XYZ=\"" + X(gp) + "\" />");
                        run.Add("</If>");
                    }
                    b.What = "pick up from " + giver;
                    break;
                }
                case StepKind.Kill:
                case StepKind.KillLoot:
                {
                    b.Label = text.Length > 0 ? text + count : "Kill" + (on.Length > 0 ? " " + on : string.Empty) + count;
                    b.Gate = here;
                    List<string> mobs = TargetNames(s, names, true, false);
                    List<Vec3> fightSpots = new List<Vec3>(s.Spots);
                    if (s.Task < 0)
                        Waves(c, steps, at, mobs, fightSpots);
                    if (once)
                    {
                        // one fight before the step's last action: go where you fought; BuddyCron fights what is there (the next
                        // step's own tag clears what stands at its spot)
                        b.Gate = here;
                        run.Add(If(here));
                        if (s.Spots.Count > 0)
                            run.Add("\t<MoveTo QuestId=\"" + H(s.Quest) + "\" Name=\"" + A(mobs.Count > 0 ? mobs[0] : "Where you fought") + "\" XYZ=\"" + X(s.Spots[0]) + "\" />");
                        run.Add("</If>");
                        b.What = "go where you fought " + (mobs.Count > 0 ? mobs[0] : "it");
                        break;
                    }
                    string area = Area(b.Label, s, mobs, fightSpots);
                    run.Add(If(here));
                    // (kill and loot: LootFor names the mobs whose bodies the bot opens itself when your bags are full)
                    run.Add("\t<Grind GrindRef=\"" + A(area) + "\" While=\"" + A(hereWhile) + "\"" +
                            (s.Kind == StepKind.KillLoot || s.Loot ? LootFor(mobs) : string.Empty) + " />");
                    run.Add("</If>");
                    if (mobs.Count == 0)
                        Look(b, "no name for what this step counts: the bot walks your fight spots and fights what attacks it");
                    b.What = "fight " + Join(mobs) + " at your " + Math.Max(1, Hotspots(fightSpots, 1f, 25).Count) + " spots" + (s.Kind == StepKind.KillLoot || s.Loot ? " (BuddyCron loots them)" : string.Empty);
                    break;
                }
                case StepKind.Collect:
                case StepKind.Use:
                {
                    b.Label = text.Length > 0 ? text + count : (s.Kind == StepKind.Collect ? "Collect" : "Use") + (on.Length > 0 ? " " + on : string.Empty) + count;
                    b.Gate = Here(s, multi || Partial(s) ? OnTask(s) : todo);
                    Fact first = s.Causes.Find(f => f.Kind == FactKind.UseObject && f.Target != null && f.Target.Name.Length > 0);
                    // for a click done once: the last click with a cast bar (the one the game acted on), else the last click
                    Fact did = null;
                    foreach (Fact f in Sorted(s.Causes))
                        if (f.Kind == FactKind.UseObject && f.Target != null && GoodName(f.Target) && (f.Cast.Length > 0 || did == null || did.Cast.Length == 0))
                            did = f;
                    Vec3 exit;
                    if (s.Kind == StepKind.Use && first != null && Lift(c, first, out exit))
                    {
                        // the step was a lift ride: walk to where you came out (BuddyCron's navigator rides the lift)
                        run.Add(If(b.Gate));
                        run.Add("\t<MoveTo QuestId=\"" + H(s.Quest) + "\" Name=\"Off the " + A(first.Target.Name) + "\" XYZ=\"" + X(exit) + "\" />");
                        // then on to where it moved on, when you walked on after the ride (Black Hole Glow, 5 Nov: "Return to
                        // Mission Terminal" ended at the terminal, well past the lift - the bot would wait at the lift)
                        Vec3 moved = s.End > first.At ? c.PosAt(s.End) : default(Vec3);
                        bool walkOn = !moved.IsZero && moved.DistanceTo(exit) >= 3f;
                        if (walkOn)
                            run.Add("\t<MoveTo QuestId=\"" + H(s.Quest) + "\" Name=\"Where it moved on\" XYZ=\"" + X(moved) + "\" />");
                        run.Add("</If>");
                        b.What = "ride the " + first.Target.Name + " to where you came out" + (walkOn ? ", then walk to where it moved on" : string.Empty);
                        break;
                    }
                    List<string> objects = ObjectNames(s, names);
                    List<string> mobs = MobNames(s, names);
                    if (s.Kind == StepKind.Collect && mobs.Count > 0 && objects.Count > 0)
                    {
                        // a count fed by kills and clicks, where a clicked thing is another mission's own object or a lift
                        // you rode: only tied here by time (no combat log), it is not what this task counts (CZ-198's Defeat
                        // Czerka Forces with the Kolto Containers of What's Yours Is Mine and Elevator 1 between the kills)
                        // (only a stray: most of the count came from kills and loot, and that object from a quarter of it at most;
                        // the Glow's pipeline controls, three of its four, stay)
                        int fought = s.Causes.Count(f => f.Kind == FactKind.Kill || f.Kind == FactKind.Loot);
                        int all = fought + s.Causes.Count(f => f.Kind == FactKind.UseObject);
                        var notHere = new List<string>();
                        foreach (Fact f in s.Causes)
                        {
                            Vec3 ex;
                            if (f.Kind != FactKind.UseObject || f.Target == null || notHere.Contains(f.Target.Name) || fought * 2 <= all)
                                continue;
                            string n = f.Target.Name;
                            if (s.Causes.Count(g => g.Kind == FactKind.UseObject && g.Target != null && g.Target.Name == n) * 4 <= all &&
                                (OtherTaskObject(steps, at, n) || Lift(c, f, out ex)))
                                notHere.Add(n);
                        }
                        objects.RemoveAll(notHere.Contains);
                        if (objects.Count == 0)
                            s.Causes.RemoveAll(f => f.Kind == FactKind.UseObject && f.Target != null && notHere.Contains(f.Target.Name));
                    }
                    if (once && did != null)
                    {
                        // used once before the step's last action: walk to where you stood, then click it once
                        run.Add(If(b.Gate));
                        run.Add("\t<ClickObject Name=\"" + A(did.Target.Name) + "\" XYZ=\"" + X(did.Target.HasPos ? did.Target.Pos : did.Me) +
                                "\" Stand=\"" + X(did.Me) + "\" />");
                        run.Add("</If>");
                        b.What = "click " + did.Target.Name + " once";
                        break;
                    }
                    if (objects.Count == 0)
                    {
                        if (mobs.Count > 0)
                        {
                            // (counted by what you looted only)
                            string ga = Area(b.Label, s, mobs, s.Spots);
                            run.Add(If(here));
                            run.Add("\t<Grind GrindRef=\"" + A(ga) + "\" While=\"" + A(hereWhile) + "\"" + LootFor(mobs) + " />");
                            run.Add("</If>");
                            b.Gate = here;
                            b.What = "fight " + Join(mobs) + " (BuddyCron loots them)";
                            break;
                        }
                        Look(b, "no name for the object: the bot walks to your spot only");
                        run.Add(If(b.Gate));
                        if (s.Spots.Count > 0)
                            run.Add("\t<MoveTo QuestId=\"" + H(s.Quest) + "\" Name=\"Where you used it\" XYZ=\"" + X(s.Spots[0]) + "\" />");
                        run.Add("</If>");
                        break;
                    }
                    // the objects' spots first (in your order), then where you fought for the same count (BuddyCron fights what
                    // stands at a spot before it uses anything there, and loots what it kills)
                    var spots = new List<Vec3>();
                    foreach (Fact f in Sorted(s.Causes))
                        if (f.Kind == FactKind.UseObject)
                            AddSpot(spots, f.Target != null && f.Target.HasPos ? f.Target.Pos : f.Me);
                    foreach (Vec3 p in s.Spots)
                        AddSpot(spots, p);
                    bool one = spots.Count == 1 && objects.Count == 1;
                    run.Add(If(b.Gate));
                    if (one && first != null)
                        Stand(run, first.Target.Name, first.Me, spots[0]);
                    var sb = new StringBuilder("\t<UseObject QuestId=\"");
                    sb.Append(H(s.Quest)).Append('"').Append(BranchAttr(s)).Append(" StepId=\"").Append(U(s.Step)).Append('"');
                    if (multi || Partial(s))
                        sb.Append(" TaskId=\"").Append(s.Task.ToString(Inv)).Append('"');
                    if (Partial(s))
                        sb.Append(" TaskValue=\"").Append(U(s.CountTo - 1)).Append('"');
                    if (objects.Count == 1)
                        sb.Append(" ObjectName=\"").Append(A(objects[0])).Append('"');
                    sb.Append(" XYZ=\"").Append(X(spots[0])).Append("\" Radius=\"").Append(one ? "10" : "30").Append("\" UseRange=\"4\" WaitTime=\"500\"");
                    // two objects of that name at one spot (Korriban's Red Engine, the upper and lower machine): the game can
                    // tie the mission to the one that isn't usable, so the bot takes the usable one of the name there
                    bool twin = one && first != null && Twin(c, first.Target);
                    // an object that opened a conversation as you used it (Coruscant's Sealed Case): the game ties the talk to the
                    // mission, not the object, so UseObject would stand next to it and never use it
                    bool talks = !twin && s.Causes.Exists(f => f.Kind == FactKind.UseObject && f.OpensTalk && f.Target != null && objects.Contains(f.Target.Name));
                    if (twin)
                        sb.Append(" IgnoreQuestRelation=\"True\" IgnoreLOS=\"True\"");
                    else if (talks)
                        sb.Append(" IgnoreQuestRelation=\"True\"");
                    sb.Append('>');
                    if (objects.Count > 1)
                    {
                        sb.Append("<Placeables>");
                        foreach (string n in objects)
                            sb.Append("<Placeable>").Append(A(n)).Append("</Placeable>");
                        sb.Append("</Placeables>");
                    }
                    if (!one)
                    {
                        sb.Append("<Hotspots>");
                        foreach (Vec3 p in Hotspots(spots, 0.5f, 40))
                            sb.Append("<Hotspot XYZ=\"").Append(X(p)).Append("\" />");
                        sb.Append("</Hotspots>");
                    }
                    sb.Append("</UseObject>");
                    run.Add(sb.ToString());
                    run.Add("</If>");
                    b.What = "use " + Join(objects) + (one ? string.Empty : " at your " + spots.Count + " spots") + (mobs.Count > 0 ? ", fighting " + Join(mobs) + " there" : string.Empty) +
                             (twin ? " (the usable one of the two there)" : string.Empty) + (talks ? " (it opens a talk)" : string.Empty);
                    break;
                }
                case StepKind.Talk:
                {
                    b.Label = text.Length > 0 ? text : "Talk" + (on.Length > 0 ? " to " + on : string.Empty);
                    b.Gate = Here(s, multi ? OnTask(s) : todo);
                    Fact cause = s.Causes.Find(f => f.Kind == FactKind.Talk && f.Target != null && f.Target.Name.Length > 0);
                    if (cause == null)
                    {
                        Look(b, "nobody to talk to was seen");
                        return null;
                    }
                    Vec3 tp = cause.Target.HasPos ? cause.Target.Pos : cause.Me;
                    b.Talk = cause.Target.Name;
                    b.TalkAt = tp;
                    run.Add(If(b.Gate));
                    Stand(run, cause.Target.Name, cause.Me, tp);
                    run.Add("\t<TalkTo QuestId=\"" + H(s.Quest) + "\" Name=\"" + A(cause.Target.Name) + "\" UseRange=\"4\" WaitTime=\"5\" XYZ=\"" + X(tp) + "\" />");
                    run.Add("</If>");
                    b.What = "talk to " + cause.Target.Name;
                    break;
                }
                case StepKind.Item:
                {
                    b.Label = text.Length > 0 ? text + count : "Use " + (s.Item.Length > 0 ? s.Item : s.Cast) + count;
                    b.Gate = Here(s, multi || Partial(s) ? OnTask(s) : todo);
                    if (s.Item.Length == 0)
                    {
                        Look(b, "the mission item's name was not seen");
                        return null;
                    }
                    List<Fact> scan = BinocCause(s);
                    if (scan != null)
                    {
                        BinocLine(s, scan, true, run, b);
                        break;
                    }
                    if (once)
                    {
                        // used once before the step's last action: the plain pass's one-use (it waits for the cast to end)
                        run.Add(If(b.Gate));
                        OnceLines(c, s, names, run, true);
                        run.Add("</If>");
                        b.What = "use " + s.Item + " once";
                        break;
                    }
                    ItemLine(c, s, names, multi, run, b);
                    break;
                }
                case StepKind.Ability:
                {
                    b.Label = text.Length > 0 ? text + count : "Use " + s.Cast + count;
                    b.Gate = Here(s, multi || Partial(s) ? OnTask(s) : todo);
                    if (s.Cast.Length == 0)
                    {
                        Look(b, "the combat log gave no name for this ability: the bot can't use it");
                        return null;
                    }
                    List<Fact> scanA = BinocCause(s);
                    if (scanA != null)
                    {
                        BinocLine(s, scanA, false, run, b);
                        break;
                    }
                    // a special-bar ability: the plain pass's ability step (code: it casts from the bar where you used it)
                    Fact f = s.Causes.Count > 0 ? s.Causes[0] : null;
                    Vec3 where = f != null ? f.Me : (s.Spots.Count > 0 ? s.Spots[0] : default(Vec3));
                    string npc = f != null && f.Target != null && f.Target.Kind == ObjKind.Npc && GoodName(f.Target) ? f.Target.Name : string.Empty;
                    run.Add(If(b.Gate));
                    run.Add("\t<UseQuestAbility AbilityName=\"" + A(CoreRender.CastName(s.Cast)) + "\"" + (npc.Length > 0 ? " Npc=\"" + A(npc) + "\"" : string.Empty) +
                            (s.InFight ? " InCombat=\"True\"" : string.Empty) + (where.IsZero ? string.Empty : " XYZ=\"" + X(where) + "\"") + " />");
                    run.Add("</If>");
                    b.What = "use the " + CoreRender.CastName(s.Cast) + " ability";
                    break;
                }
                case StepKind.Walk:
                {
                    b.Label = text.Length > 0 ? text : CoreRender.ByItself(s, text) ? "Wait for the game to move it on" : "Go on where it ticked";
                    b.Gate = here;
                    if (CoreRender.ByItself(s, text))
                        b.Yellow = false; // (a stage the game passes through by itself: the next step's tag waits for it)
                    else if (CoreRender.Arrived(s))
                        b.Yellow = false; // (getting there was the step)
                    else
                        Look(b, "nothing you did was tied to this step: the bot walks to where it ticked");
                    if (s.Spots.Count == 0)
                        return null;
                    run.Add(If(here));
                    run.Add("\t<MoveTo QuestId=\"" + H(s.Quest) + "\" Name=\"Where it moved on\" XYZ=\"" + X(s.Spots[0]) + "\" />");
                    run.Add("</If>");
                    b.What = "walk to where it moved on";
                    break;
                }
                case StepKind.Holocom:
                {
                    b.Label = text.Length > 0 ? text : "Answer the call";
                    b.Gate = s.Task >= 0 || s.Step > 0 ? todo : Open(s.Quest);
                    run.Add(If(b.Gate));
                    run.Add("\t<AnswerHolocomCall QuestId=\"" + H(s.Quest) + "\" />");
                    run.Add("</If>");
                    b.What = "answer the holocom call";
                    break;
                }
                case StepKind.HandIn:
                {
                    b.Label = "Hand in " + CoreRender.Name(s);
                    b.Gate = Here(s, Open(s.Quest));
                    Fact cause = s.Causes.Find(f => f.Target != null && f.Target.Name.Length > 0);
                    if (cause == null)
                    {
                        Reward(b, s.Quest);
                        return b;
                    }
                    Vec3 tp = cause.Target.HasPos ? cause.Target.Pos : cause.Me;
                    run.Add(If(b.Gate));
                    Stand(run, cause.Target.Name, cause.Me, tp);
                    run.Add("\t<TurnInQuest QuestId=\"" + H(s.Quest) + "\" QuestName=\"" + A(CoreRender.Name(s)) + "\" TurnInName=\"" + A(cause.Target.Name) +
                            "\" UseRange=\"4\" XYZ=\"" + X(tp) + "\" />");
                    run.Add("</If>");
                    if (cause.Kind == FactKind.Talk)
                    {
                        b.Talk = cause.Target.Name;
                        b.TalkAt = tp;
                    }
                    Reward(b, s.Quest);
                    b.What = "hand in to " + cause.Target.Name;
                    break;
                }
                case StepKind.Reward:
                {
                    b.Label = "Reward: " + CoreRender.Name(s);
                    b.What = "take the reward";
                    // how long the reward window took after the mission's last step when you did it (the Glow's ~35 s after
                    // you walked out)
                    // (from where that step ended, not where it began: Ancient Guardians' boss fight began two minutes
                    // before its kill, and the bot waited three minutes for a reward that comes with the kill)
                    double last = double.MinValue;
                    foreach (CoreStep o in steps)
                    {
                        if (o == s || o.Quest != s.Quest || o.Kind == StepKind.Reward || o.At > s.At)
                            continue;
                        double ended = o.End > o.At && o.End <= s.At ? o.End : o.At;
                        last = Math.Max(last, ended);
                    }
                    // (done the moment another mission was: a weekly that counts it, or the mission that weekly counts -
                    // its window comes with that one's)
                    bool together = c.Tracks.Exists(t => t.Quest != s.Quest && (t.Kind == TrackKind.Completed || t.Kind == TrackKind.Reward) &&
                                                         Math.Abs(t.At - s.At) <= 1.0);
                    Reward(b, s.Quest, together || last <= double.MinValue ? 4.0 : Math.Max(4.0, Math.Min(120.0, s.At - last)));
                    return b;
                }
                case StepKind.Hop:
                    return Hop(c, steps, at, b);
                case StepKind.Puzzle:
                {
                    // walk to where you stood, then a pause for you: the order is the game's own puzzle
                    b.Label = text.Length > 0 ? text : "Puzzle";
                    b.Gate = here;
                    b.Yellow = true;
                    Fact f = s.Causes.Count > 0 ? s.Causes[0] : null;
                    string what = f != null && f.Target != null && f.Target.Name.Length > 0 ? f.Target.Name : "the controls";
                    run.Add(If(here));
                    if (f != null && !f.Me.IsZero)
                        run.Add("\t<MoveTo QuestId=\"" + H(s.Quest) + "\" Name=\"" + A(what) + "\" XYZ=\"" + X(f.Me) + "\" />");
                    run.Add("\t<UserDialog Title=\"Quest Recorder\" Message=\"" + A("Do the puzzle at " + what + " by hand, then press Continue.") +
                            "\" IsBotContinueAllowed=\"True\" />");
                    run.Add("</If>");
                    b.What = "you do the puzzle, then press Continue";
                    break;
                }
                default:
                    return null;
            }
            b.Gate = Fmt.XmlAttr(b.Gate);
            return b;
        }

        /// <summary>A row of mission items used once each on one step (runes): every one in your order, where you used it.</summary>
        private static CoreRender.Block Row(CoreFacts c, List<CoreStep> steps, int from, int to, Dictionary<ulong, List<string>> names)
        {
            CoreStep s = steps[from];
            var b = new CoreRender.Block { Step = s, At = s.At, Quest = s.Quest };
            var items = new List<string>();
            for (int i = from; i <= to; i++)
            {
                b.Unsure.AddRange(steps[i].Unsure);
                if (steps[i].Rank >= 3)
                    b.Yellow = true;
                if (steps[i].Item.Length > 0)
                    items.Add(steps[i].Item);
            }
            string text = CoreRender.TaskText(c, s);
            b.Label = text.Length > 0 ? text : "Use " + string.Join(", ", items.ToArray());
            b.Gate = Here(s, Todo(s));
            // another action after the row moves the step on: the row once; else the row again until the step moves on
            bool once = steps.FindIndex(to + 1, n => n.Quest == s.Quest && n.Branch == s.Branch && n.Step == s.Step && n.Task < 0 && CoreRender.Action(n.Kind)) >= 0;
            b.Run.Add(If(b.Gate));
            for (int i = from; i <= to; i++)
                if (steps[i].Item.Length > 0)
                    OnceLines(c, steps[i], names, b.Run, once);
            b.Run.Add("</If>");
            b.What = "use " + string.Join(", then ", items.ToArray()) + ", in that order";
            b.Gate = Fmt.XmlAttr(b.Gate);
            return b;
        }

        /// <summary>One use of a mission item, in the plain pass's one-use form (walk to where you used it, then use it once).</summary>
        private static void OnceLines(CoreFacts c, CoreStep s, Dictionary<ulong, List<string>> names, List<string> run, bool once)
        {
            Fact f = Sorted(s.Causes).Find(x => x.Item.Length > 0) ?? (s.Causes.Count > 0 ? s.Causes[0] : null);
            Vec3 at = f != null ? f.Me : (s.Spots.Count > 0 ? s.Spots[0] : default(Vec3));
            string npc = f != null && f.Target != null && f.Target.Kind == ObjKind.Npc && GoodName(f.Target) ? f.Target.Name : string.Empty;
            if (!at.IsZero)
                run.Add("\t<MoveTo QuestId=\"1\" Name=\"Where you used " + A(s.Item) + "\" XYZ=\"" + X(at) + "\" />");
            var sb = new StringBuilder("\t<UseMissionItemOnce ItemName=\"").Append(A(s.Item)).Append('"');
            if (npc.Length > 0)
            {
                sb.Append(" Npc=\"").Append(A(npc)).Append('"');
                sb.Append(" Range=\"").Append(RangeM(s)).Append('"');
            }
            if (s.DeadOnly)
                sb.Append(" DeadOnly=\"True\"");
            if (InFight(c, s))
                sb.Append(" InCombat=\"True\"");
            float hp = CoreRender.HealthAt(s);
            if (hp > 0f && hp < 100f)
                sb.Append(" HealthPercent=\"").Append(((int)hp).ToString(Inv)).Append('"');
            sb.Append(once ? " Once=\"True\" />" : " />");
            run.Add(sb.ToString());
        }

        /// <summary>The binocular scans that did this step, in order (one a spot: the last there, one with where you aimed first), or null.</summary>
        private static List<Fact> BinocCause(CoreStep s)
        {
            var scans = new List<Fact>();
            foreach (Fact f in Sorted(s.Causes))
            {
                if (!f.Binoc)
                    continue;
                int same = scans.FindIndex(g => g.Me.DistanceTo(f.Me) < 0.3f && (g.Scanned == null || f.Scanned == null || g.Scanned.Name == f.Scanned.Name));
                if (same < 0)
                    scans.Add(f);
                else if (f.HasAim || !scans[same].HasAim)
                    scans[same] = f; // (scanned again from the same spot: the later one, unless only the first had its aim)
            }
            return scans.Count > 0 ? scans : null;
        }

        /// <summary>
        /// A binocular scan (Macrobinoculars, a mission Scanner): the plain pass's code step. The bot stands where you stood,
        /// uses it, and turns and tilts the camera to where you aimed (to the object the game said you scanned, when it is
        /// there), zooms in, and waits for the scan; if the mission doesn't move on, a pop-up asks you to scan it yourself.
        /// </summary>
        private static void BinocLine(CoreStep s, List<Fact> scans, bool item, List<string> run, CoreRender.Block b)
        {
            string what = item ? s.Item : CoreRender.CastName(s.Cast);
            run.Add(If(b.Gate));
            var targets = new List<string>();
            bool noAim = false, noSpot = false;
            foreach (Fact f in scans)
            {
                Vec3 where = !f.Me.IsZero ? f.Me : (s.Spots.Count > 0 ? s.Spots[0] : default(Vec3));
                if (where.IsZero)
                    noSpot = true;
                else
                    run.Add("\t<MoveTo QuestId=\"1\" Name=\"Where you used " + A(what) + "\" XYZ=\"" + X(where) + "\" />");
                var sb = new StringBuilder();
                sb.Append(item ? "\t<UseMissionItemOnce ItemName=\"" : "\t<UseQuestAbility AbilityName=\"").Append(A(what)).Append('"');
                sb.Append(" Binoculars=\"True\"");
                if (f.HasAim)
                    sb.Append(" AimYaw=\"").Append(Fmt.F3(f.AimYaw)).Append("\" AimPitch=\"").Append(Fmt.F3(f.AimPitch)).Append('"');
                ObjRef t = f.Scanned;
                if (t != null && t.HasPos && t.Name.Length > 0)
                {
                    sb.Append(" AimName=\"").Append(A(t.Name)).Append("\" AimAt=\"").Append(X(t.Pos)).Append('"');
                    if (!targets.Contains(t.Name))
                        targets.Add(t.Name);
                }
                else if (!f.HasAim)
                    noAim = true;
                if (s.InFight)
                    sb.Append(" InCombat=\"True\"");
                sb.Append(" />");
                run.Add(sb.ToString());
            }
            run.Add("</If>");
            b.What = "use " + what + (scans.Count > 1 ? " at " + scans.Count.ToString(Inv) + " spots" : string.Empty) + " and scan" +
                     (targets.Count > 0 ? " the " + Join(targets) : string.Empty) + " (binoculars: the bot turns and tilts the camera to where you aimed" +
                     (noAim ? ", which wasn't recorded" : string.Empty) + ")";
            if (noSpot)
                Look(b, "where you stood for the scan was not seen");
            if (noAim)
                Look(b, "where you aimed the binoculars was not recorded (recorded before Quest Recorder 1.4b): the bot uses it and zooms where it looks, then asks you to scan it yourself if that didn't do it");
            Look(b, "binocular scans: the bot's turning, tilting and zooming haven't been tested in the game yet. If the scan doesn't happen, a pop-up asks you to scan it yourself, then press Continue");
        }

        /// <summary>
        /// A mission item used until the step is done: BuddyCron's UseItem on the NPCs you used it on (every type the task
        /// counts), from as far as you stood, in a fight when you were (or the NPC fought you right after), once its health
        /// was down to where you used it; with no NPC, at each spot where you stood.
        /// </summary>
        private static void ItemLine(CoreFacts c, CoreStep s, Dictionary<ulong, List<string>> names, bool multi, List<string> run, CoreRender.Block b)
        {
            var npcs = new List<string>();
            bool onObject = false;
            foreach (Fact f in Sorted(s.Causes))
            {
                if (f.Target == null || !GoodName(f.Target))
                    continue;
                if (f.Target.Kind == ObjKind.Npc)
                {
                    if (!npcs.Contains(f.Target.Name))
                        npcs.Add(f.Target.Name);
                }
                else if (f.Target.Kind == ObjKind.Placeable)
                    onObject = true;
            }
            if (npcs.Count > 0)
                foreach (string n in TargetNames(s, names, true, true))
                    if (!npcs.Contains(n))
                        npcs.Add(n);
            var spots = new List<Vec3>();
            var uses = Sorted(s.Causes);
            uses.AddRange(Sorted(s.Tries));
            foreach (Fact f in uses)
                AddSpot(spots, npcs.Count > 0 && f.Target != null && f.Target.HasPos ? f.Target.Pos : f.Me);
            // (then every other spot where what it counts was seen: Torvix's spies stand apart)
            if (spots.Count == 0 || npcs.Count > 0)
                foreach (Vec3 p in s.Spots)
                    AddSpot(spots, p);
            if (spots.Count == 0)
            {
                Look(b, "where you used it was not seen");
                return;
            }
            bool fight = InFight(c, s);
            float hp = CoreRender.HealthAt(s);
            run.Add(If(b.Gate));
            var sb = new StringBuilder("\t<UseItem QuestId=\"");
            sb.Append(H(s.Quest)).Append('"').Append(BranchAttr(s)).Append(" StepId=\"").Append(U(s.Step)).Append('"');
            if (multi || Partial(s))
                sb.Append(" TaskId=\"").Append(s.Task.ToString(Inv)).Append('"');
            if (Partial(s))
                sb.Append(" TaskValue=\"").Append(U(s.CountTo - 1)).Append('"');
            sb.Append(" ItemName=\"").Append(A(s.Item)).Append('"');
            sb.Append(" XYZ=\"").Append(X(spots[0])).Append('"');
            if (npcs.Count > 0)
                sb.Append(" Range=\"").Append(RangeM(s)).Append("\" Radius=\"50\"");
            else
                sb.Append(" Range=\"3\"");
            if (fight)
                sb.Append(" InCombat=\"True\"");
            if (hp > 0f && hp < 100f)
                sb.Append(" HealthPercent=\"").Append(((int)hp).ToString(Inv)).Append('"');
            if (s.DeadOnly)
                sb.Append(" DeadOnly=\"True\"");
            sb.Append(" WaitTime=\"1000\">");
            if (npcs.Count > 0)
            {
                sb.Append("<Npcs>");
                foreach (string n in npcs)
                    sb.Append("<Npc>").Append(A(n)).Append("</Npc>");
                sb.Append("</Npcs>");
            }
            sb.Append("<Hotspots>");
            foreach (Vec3 p in Hotspots(spots, 0.3f, 40))
                sb.Append("<Hotspot XYZ=\"").Append(X(p)).Append("\" />");
            sb.Append("</Hotspots></UseItem>");
            run.Add(sb.ToString());
            run.Add("</If>");
            if (onObject && npcs.Count == 0)
                Look(b, "you used it on an object: BuddyCron's UseItem only targets characters, so it is used where you stood");
            b.What = "use " + s.Item + (npcs.Count > 0 ? " on " + Join(npcs) : " where you stood") + (fight ? " (in a fight too" + (hp > 0f && hp < 100f ? ", once its health is down to " + (int)hp + "%" : string.Empty) + ")" : string.Empty);
        }

        /// <summary>
        /// A mission item that moved you (a transport): used until you are at that mission's site (where it put you and where
        /// you did its steps there). When another mission's transport has the same name, BuddyCron's UseItem may press that
        /// one: it is used until you are at either site, and the steps then go by the site you are at.
        /// </summary>
        private static CoreRender.Block Hop(CoreFacts c, List<CoreStep> steps, int at, CoreRender.Block b)
        {
            CoreStep s = steps[at];
            ulong onStep;
            if (s.Item.Length == 0 || (!CoreRender.StepAt(c, s.Quest, s.At, out onStep) && !StepBefore(c, s.Quest, s.At, out onStep)))
                return null; // the mission wasn't in your log when you used it: the travel builder's hop is kept
            b.Label = "Use " + s.Item;
            string q = H(s.Quest);
            string have = "(any(i.Name == " + Fmt.PyStr(s.Item) + " for i in Me.QuestItems) or any(i.Name == " + Fmt.PyStr(s.Item) + " for i in Me.Backpack))";
            Site site = _sites != null ? _sites.Find(x => x.Hop == s) : null;
            string away;
            if (site == null)
            {
                Look(b, "where it took you was not seen: the bot uses it while the mission is still on that step");
                away = "True";
            }
            else
                away = "not " + AtSite(site);
            string gate = "HasQuest(" + q + ") and not IsStepComplete(" + q + ", " + U(onStep) + ") and " + away;
            if (site != null && site.Shared)
            {
                string busy = Busy(site);
                if (busy.Length > 0)
                    gate += " and not (" + busy + ")";
                b.Unsure.Add("another mission's transport is also called " + s.Item + ": BuddyCron uses the first one it finds, so the bot does whichever mission it lands at first, then the other");
            }
            gate += " and " + have;
            // (the step it was used on is the mission's step at that time, read from the log: asked without the branch)
            Vec3 used = s.Causes.Count > 0 && !s.Causes[0].Me.IsZero ? s.Causes[0].Me : (s.Spots.Count > 0 ? s.Spots[0] : default(Vec3));
            // You went somewhere on purpose to use it (more than 5 units from where you finished the step before): the bot
            // walks there first, without quick travel, when it is in that area. Otherwise it is used wherever the bot
            // stands, as you did.
            double useAt = s.Causes.Count > 0 ? s.Causes[0].At : s.At;
            double prev = -1;
            foreach (CoreStep o in steps)
                if (o != s && o.Kind != StepKind.Walk && o.At < useAt - 0.5 && o.At > prev)
                    prev = o.At;
            Vec3 then = prev >= 0 ? c.PosAt(prev) : default(Vec3);
            bool walk = !used.IsZero && !then.IsZero && then.DistanceTo(used) > 5f;
            // Where the bot presses it. BuddyCron's UseItem first walks toward its XYZ until it stands level with it (within
            // 4.5 m up or down; across, Range 100000 is any distance), and with no XYZ that is <0, 0, 0>: on the fleet 70 m
            // below the deck, so the bot walked off and never pressed it (Tatooine, 22 Nov). So it is where you used it, in
            // the area you used it in; in each other area of the recording (the bot started again there), where you stood
            // in that area nearest the moment you used it.
            Vec3 press = !used.IsZero ? used : then;
            if (press.IsZero)
                return null; // nowhere you stood is known: the travel builder's hop (which has where you used it) is kept
            var elsewhere = new List<KeyValuePair<ulong, int>>();
            if (s.Area != 0)
            {
                var nearest = new Dictionary<ulong, int>();
                for (int i = 0; i < c.Events.Count; i++)
                {
                    RecEvent e = c.Events[i];
                    if (e.AreaId == 0 || e.AreaId == s.Area || e.Pos.IsZero)
                        continue;
                    int k;
                    if (!nearest.TryGetValue(e.AreaId, out k) || Math.Abs(c.AtOf[i] - useAt) < Math.Abs(c.AtOf[k] - useAt))
                        nearest[e.AreaId] = i;
                }
                elsewhere.AddRange(nearest);
                elsewhere.Sort((x, y) => x.Value.CompareTo(y.Value));
            }
            b.Gate = gate;
            // (entered only when the transport is not on a long cooldown: started again after using it, the bot would
            // press it until it can be used, up to an hour. The UseItem's own condition has no such check: its cooldown
            // starting during the cast must not end it before it has taken you there.)
            b.Run.Add(If(gate + " and " + ReplayBuilder.NoLongCooldown(s.Item)));
            string use = "<UseItem ItemName=\"" + A(s.Item) + "\" Condition=\"" + A(gate) + "\" Range=\"100000\" WaitTime=\"3000\" XYZ=\"";
            string tab = s.Area != 0 ? "\t\t" : "\t";
            if (s.Area != 0)
                b.Run.Add("\t" + If("Core.Player.AreaId == " + H(s.Area)));
            if (walk)
                b.Run.Add(tab + "<MoveTo QuestId=\"1\" Name=\"Where you used " + A(s.Item) + "\" XYZ=\"" + X(used) + "\" />");
            // (pressed every tick until the condition is false, which is once you have landed)
            b.Run.Add(tab + use + X(press) + "\" />");
            if (s.Area != 0)
                b.Run.Add("\t</If>");
            foreach (KeyValuePair<ulong, int> o in elsewhere)
            {
                b.Run.Add("\t" + If("Core.Player.AreaId == " + H(o.Key)));
                b.Run.Add("\t\t" + use + X(c.Events[o.Value].Pos) + "\" />");
                b.Run.Add("\t</If>");
            }
            b.Run.Add("</If>");
            b.What = "use " + s.Item + (walk ? " where you used it" : " where the bot stands") + " until it has taken you there";
            b.Gate = Fmt.XmlAttr(b.Gate);
            return b;
        }

        /// <summary>
        /// The reward right after the mission's last step: waits in 4 s steps (about 1.5 times as long as the window took for
        /// you, at least ~16 s) while the mission is still open and no reward window has come, then BuddyCron's CompleteQuest
        /// (done at once when no reward waits; the end of the profile takes any reward that came later).
        /// </summary>
        private static void Reward(CoreRender.Block b, ulong q, double waited = 4.0)
        {
            string h = H(q);
            string waiting = CoreRender.RewardWaitingText(h);
            if (b.Gate == null)
                b.Gate = "(" + Open(q) + ") or " + waiting;
            b.After.Add(If("(" + Open(q) + ") or " + waiting));
            int most = (int)Math.Ceiling(Math.Max(16.0, waited * 1.5));
            // (not while you stand at another open mission's site: this one's reward can't be coming there)
            string still = Fmt.XmlAttr(Open(q) + " and not " + waiting + (_extra != null ? " and " + _extra : string.Empty));
            for (int sofar = 0; sofar < most; sofar += 4)
                b.After.Add("\t<If Condition=\"" + still + "\"><WaitTimer QuestId=\"1\" WaitTime=\"4\" /></If>");
            b.After.Add("\t<CompleteQuest QuestId=\"" + h + "\" />");
            b.After.Add("</If>");
            b.Gate = Fmt.XmlAttr(b.Gate);
        }

        /// <summary>
        /// The step a mission you already had when you started recording was on at a moment before its first change was
        /// seen: the step that first change left (or the step its first count was on).
        /// </summary>
        private static bool StepBefore(CoreFacts c, ulong q, double t, out ulong step)
        {
            step = 0;
            foreach (Track tr in c.Tracks)
            {
                if (tr.Quest != q)
                    continue;
                if (tr.At <= t + 0.5 || tr.Kind == TrackKind.Added)
                    return false; // (seen before the moment, or added after it: not one you already had)
                if (tr.Kind == TrackKind.Step && tr.OldStep != 0)
                    step = tr.OldStep;
                else if (tr.Kind == TrackKind.Task && tr.Step != 0)
                    step = tr.Step;
                return step != 0;
            }
            return false;
        }

        /// <summary>Short waits, each only while the condition still holds (it is written XML-escaped already).</summary>
        private static List<string> WaitWhile(string gate, double waited)
        {
            int most = (int)Math.Ceiling(Math.Min(waited * 1.5 + 10, 600));
            int each = Math.Max(4, (most + 3) / 4);
            var l = new List<string>();
            for (int sofar = 0; sofar < most; sofar += each)
                l.Add("<If Condition=\"" + gate + "\"><WaitTimer QuestId=\"1\" WaitTime=\"" + each.ToString(Inv) + "\" /></If>");
            return l;
        }

        // ------------------------------------------------------------------ helpers

        private static string If(string gate)
        {
            return "<If Condition=\"" + Fmt.XmlAttr(gate) + "\">";
        }

        private static string A(string s)
        {
            return Fmt.XmlAttr(s ?? string.Empty);
        }

        private static string X(Vec3 v)
        {
            return string.Format(Inv, "{0:0.###},{1:0.###},{2:0.###}", v.X, v.Y, v.Z);
        }

        private static string Join(List<string> l)
        {
            if (l.Count == 0)
                return "what attacks it";
            if (l.Count <= 3)
                return string.Join(", ", l.ToArray());
            return string.Join(", ", l.GetRange(0, 3).ToArray()) + " and " + (l.Count - 3) + " more";
        }

        private static void Look(CoreRender.Block b, string why)
        {
            if (!b.Unsure.Contains(why))
                b.Unsure.Add(why);
            b.Yellow = true;
        }

        /// <summary>
        /// The mobs of a kill-and-loot fight, for the plain pass's full-bags loot hook (taken off the Grind line there):
        /// BuddyCron loots nothing when your bags are full, though a mission item needs no room.
        /// </summary>
        private static string LootFor(List<string> mobs)
        {
            var l = mobs.FindAll(m => !string.IsNullOrEmpty(m) && m.IndexOf('|') < 0);
            return l.Count > 0 ? " LootFor=\"" + A(string.Join("|", l)) + "\"" : string.Empty;
        }

        /// <summary>Walk to where you stood for it first (the right one of two with one name; a spot that can be reached).</summary>
        private static void Stand(List<string> run, string name, Vec3 me, Vec3 target)
        {
            if (me.IsZero)
                return;
            if (!target.IsZero && me.DistanceTo(target) > 10f)
                return; // (not where you stood for it: you used it from far away)
            run.Add("\t<MoveTo QuestId=\"1\" Name=\"Stand for " + A(name) + "\" XYZ=\"" + X(me) + "\" />");
        }

        private static bool GoodName(ObjRef o)
        {
            return o != null && o.Kind != ObjKind.Player && o.Name.Length > 0 && !o.Name.StartsWith("#", StringComparison.Ordinal) &&
                   !o.Name.StartsWith("@", StringComparison.Ordinal);
        }

        /// <summary>Every name the recording saw for each type id (spawn spec).</summary>
        private static Dictionary<ulong, List<string>> SpecNames(CoreFacts c)
        {
            var d = new Dictionary<ulong, List<string>>();
            Action<ObjRef> add = o =>
            {
                if (o == null || o.Spec == 0 || !GoodName(o))
                    return;
                List<string> l;
                if (!d.TryGetValue(o.Spec, out l))
                    d[o.Spec] = l = new List<string>();
                if (!l.Contains(o.Name))
                    l.Add(o.Name);
            };
            foreach (ObjRef o in c.Nodes.Values)
                add(o);
            foreach (Fact f in c.Facts)
                add(f.Target);
            return d;
        }

        /// <summary>
        /// The names of what the step counts: what your actions were done to (in your order), then every other type the step
        /// and the task count that the recording saw a name for.
        /// </summary>
        private static List<string> TargetNames(CoreStep s, Dictionary<ulong, List<string>> names, bool withAccepts, bool npcsOnly)
        {
            var l = new List<string>();
            foreach (Fact f in Sorted(s.Causes))
                if (f.Target != null && GoodName(f.Target) && (!npcsOnly || f.Target.Kind == ObjKind.Npc) && f.Target.Kind != ObjKind.Placeable && !l.Contains(f.Target.Name))
                    l.Add(f.Target.Name);
            var specs = new List<ulong>(s.Specs);
            if (withAccepts)
                foreach (ulong x in s.Accepts)
                    if (!specs.Contains(x))
                        specs.Add(x);
            foreach (ulong x in specs)
            {
                List<string> n;
                if (names.TryGetValue(x, out n))
                    foreach (string m in n)
                        if (!l.Contains(m) && !IsObjectName(s, m))
                            l.Add(m);
            }
            return l;
        }

        private static bool IsObjectName(CoreStep s, string name)
        {
            foreach (Fact f in s.Causes)
                if (f.Target != null && f.Target.Kind == ObjKind.Placeable && f.Target.Name == name)
                    return true;
            return false;
        }

        /// <summary>The objects you clicked for the step (their names), then any other object type the task counts.</summary>
        private static List<string> ObjectNames(CoreStep s, Dictionary<ulong, List<string>> names)
        {
            var l = new List<string>();
            var objSpecs = new HashSet<ulong>();
            foreach (Fact f in Sorted(s.Causes))
                if (f.Kind == FactKind.UseObject && f.Target != null && GoodName(f.Target))
                {
                    if (!l.Contains(f.Target.Name))
                        l.Add(f.Target.Name);
                    if (f.Target.Spec != 0)
                        objSpecs.Add(f.Target.Spec);
                }
            return l;
        }

        /// <summary>The mobs you killed or looted for the same count.</summary>
        /// <summary>The object is what another mission's step used to move its own count (a later stage of the same mission
        /// may use the same object again, as the Glow's pipeline controls).</summary>
        private static bool OtherTaskObject(List<CoreStep> steps, int at, string name)
        {
            CoreStep s = steps[at];
            for (int i = 0; i < steps.Count; i++)
            {
                CoreStep o = steps[i];
                if (i == at || (o.Kind != StepKind.Use && o.Kind != StepKind.Collect) || o.Quest == s.Quest)
                    continue;
                if (o.Causes.Exists(f => f.Kind == FactKind.UseObject && f.Target != null && f.Target.Name == name) &&
                    !o.Causes.Exists(f => f.Kind == FactKind.Kill || f.Kind == FactKind.Loot))
                    return true;
            }
            return false;
        }

        private static List<string> MobNames(CoreStep s, Dictionary<ulong, List<string>> names)
        {
            var l = new List<string>();
            foreach (Fact f in Sorted(s.Causes))
                if ((f.Kind == FactKind.Kill || f.Kind == FactKind.Loot) && f.Target != null && GoodName(f.Target) && f.Target.Kind != ObjKind.Placeable && !l.Contains(f.Target.Name))
                    l.Add(f.Target.Name);
            return l;
        }

        private static List<Fact> Sorted(List<Fact> facts)
        {
            var l = new List<Fact>(facts);
            Fmt.StableSort(l, (x, y) => x.At.CompareTo(y.At));
            return l;
        }

        private static void AddSpot(List<Vec3> spots, Vec3 p)
        {
            if (p.IsZero || float.IsNaN(p.X + p.Y + p.Z) || float.IsInfinity(p.X + p.Y + p.Z))
                return;
            foreach (Vec3 x in spots)
                if (x.DistanceTo(p) < 0.05f)
                    return;
            spots.Add(p);
        }

        /// <summary>Spots at least <paramref name="apart"/> units from each other (in your order), at most <paramref name="most"/>.</summary>
        private static List<Vec3> Hotspots(List<Vec3> from, float apart, int most)
        {
            var l = new List<Vec3>();
            foreach (Vec3 p in from)
            {
                if (p.IsZero)
                    continue;
                bool near = false;
                foreach (Vec3 x in l)
                    if (x.DistanceTo(p) < apart)
                    {
                        near = true;
                        break;
                    }
                if (!near)
                    l.Add(p);
                if (l.Count >= most)
                    break;
            }
            return l;
        }

        /// <summary>
        /// The fight's GrindArea in the header: the mobs the task counts and every spot you fought at (a spot covers 30 m
        /// around it, so the bot fights where you fought and not further).
        /// </summary>
        private static string Area(string label, CoreStep s, List<string> mobs, List<Vec3> spots)
        {
            string name = Fmt.OneLine(CoreRender.Name(s) + ": " + label);
            string baseName = name;
            for (int n = 2; !_areaNames.Add(name); n++)
                name = baseName + " (" + n + ")";
            const string nl = "\r\n";
            _areas.Append("\t\t<GrindArea Name=\"").Append(A(name)).Append("\">").Append(nl);
            _areas.Append("\t\t\t<MinLevel>1</MinLevel>").Append(nl).Append("\t\t\t<MaxLevel>100</MaxLevel>").Append(nl).Append("\t\t\t<Mobs>");
            foreach (string m in mobs)
                _areas.Append("<Mob>").Append(A(m)).Append("</Mob>");
            _areas.Append("</Mobs>").Append(nl).Append("\t\t\t<Hotspots>").Append(nl);
            foreach (Vec3 p in Hotspots(spots, 1f, 25))
                _areas.Append("\t\t\t\t<Hotspot XYZ=\"").Append(X(p)).Append("\" Radius=\"3\" />").Append(nl);
            _areas.Append("\t\t\t</Hotspots>").Append(nl).Append("\t\t</GrindArea>").Append(nl);
            return name;
        }

        /// <summary>
        /// A fight step with no counts (a boss, waves after a console): every NPC you killed while the mission was on that
        /// step, near where the step's own kill was (40 m), joins the fight, at the spots you stood when you killed them
        /// (End of Torvix: the droid waves after Master Safety Control, then the Annihilation Droid).
        /// </summary>
        private static void Waves(CoreFacts c, List<CoreStep> steps, int at, List<string> mobs, List<Vec3> spots)
        {
            CoreStep s = steps[at];
            if (s.Spots.Count == 0)
                return;
            // when the mission came to this step: the last action of its step before
            double from = double.MinValue;
            for (int i = 0; i < steps.Count; i++)
            {
                CoreStep p = steps[i];
                if (p == s || p.Quest != s.Quest || (p.Branch == s.Branch && p.Step == s.Step) || p.At >= s.At)
                    continue;
                from = Math.Max(from, p.At);
                foreach (Fact f in p.Causes)
                    if (f.At < s.At)
                        from = Math.Max(from, f.At);
            }
            if (from == double.MinValue)
                from = s.At - 180;
            from = Math.Max(from, s.At - 180);
            double to = s.At + 1;
            foreach (Fact f in s.Causes)
                to = Math.Max(to, f.At + 1);
            foreach (Fact f in c.Facts)
            {
                if (f.Kind != FactKind.Kill || f.At <= from || f.At > to || f.Target == null || f.Target.Kind != ObjKind.Npc || !GoodName(f.Target))
                    continue;
                Vec3 where = f.Target.HasPos ? f.Target.Pos : f.Me;
                bool near = false;
                foreach (Vec3 p in s.Spots)
                    if (!where.IsZero && where.DistanceTo(p) <= 4f)
                        near = true;
                if (!near)
                    continue;
                if (!mobs.Contains(f.Target.Name))
                    mobs.Add(f.Target.Name);
                AddSpot(spots, f.Me.IsZero ? where : f.Me);
            }
        }

        /// <summary>How far you stood from what you used it on (metres, rounded up, plus 1; at least 2).</summary>
        private static string RangeM(CoreStep s)
        {
            float most = 0f;
            foreach (Fact f in s.Causes)
                if (f.Target != null && f.Target.HasPos && !f.Me.IsZero)
                    most = Math.Max(most, f.Me.DistanceTo(f.Target.Pos));
            if (most <= 0f)
                return "15";
            int m = (int)Math.Ceiling(most * 10f - 0.01f) + 1;
            return Math.Max(2, Math.Min(300, m)).ToString(Inv);
        }

        /// <summary>
        /// Used in a fight: you were fighting when you used it, or what you used it on fought you or was killed soon after (the
        /// Hyperbeast's serum: injected, then killed).
        /// </summary>
        private static bool InFight(CoreFacts c, CoreStep s)
        {
            foreach (Fact f in s.Causes)
            {
                if (f.InFight || (f.Target != null && f.Target.Health > 0f && f.Target.Health < 95f))
                    return true;
                if (f.Target == null || f.Target.NodeId == 0)
                    continue;
                foreach (Fact k in c.Facts)
                    if (k.Kind == FactKind.Kill && k.Target != null && k.Target.NodeId == f.Target.NodeId && k.At >= f.At && k.At - f.At <= 120)
                        return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ where a step is done

        /// <summary>The area you did the step in: where its first action was, else where it ticked (0 = not known).</summary>
        internal static ulong AreaOf(CoreStep s)
        {
            foreach (Fact f in Sorted(s.Causes))
                if (f.Area != 0)
                    return f.Area;
            return s.Area;
        }

        /// <summary>The gate, only while you are in the area you did the step in (a spot of another map is a walk to nowhere).</summary>
        private static string InArea(CoreStep s, string gate)
        {
            ulong a = AreaOf(s);
            return a != 0 ? gate + " and Core.Player.AreaId == " + H(a) : gate;
        }

        /// <summary>
        /// The gate in the area you did the step in, and (a mission whose transport shares its name with another's) not
        /// while you stand at the other mission's site and it is still open.
        /// </summary>
        private static string Here(CoreStep s, string gate)
        {
            string g = InArea(s, gate);
            return _extra != null ? g + " and " + _extra : g;
        }

        /// <summary>
        /// Another object of the same name within 1 unit of it (10 m: Korriban's two Red Engines, one 0.86 above the other).
        /// Not a respawn of it: a used container comes back as a new object at exactly the same spot (CZ-198's Kolto
        /// Containers), with the same spawn id when the recording has one.
        /// </summary>
        private static bool Twin(CoreFacts c, ObjRef used)
        {
            if (used == null || !used.HasPos || used.Name.Length == 0)
                return false;
            foreach (ObjRef o in c.Nodes.Values)
            {
                if (o == null || o.NodeId == 0 || o.NodeId == used.NodeId || o.Name != used.Name || !o.HasPos)
                    continue;
                float d = o.Pos.DistanceTo(used.Pos);
                if (d > 1f || d < 0.05f || (o.Anchor != 0 && used.Anchor != 0 && o.Anchor == used.Anchor))
                    continue;
                return true;
            }
            return false;
        }

        private static bool IsKill(CoreStep s)
        {
            return s.Kind == StepKind.Kill || s.Kind == StepKind.KillLoot;
        }

        /// <summary>
        /// A step whose kills the game counts in hidden parts (waves with no line of their own on the tracker: The Hate
        /// Machine's "Slay the K'lor'slugs"): one fight for the whole step, every kind you killed for it at every spot you
        /// fought (the step's own fight then adds the rest you killed there while the mission was on it). Tasks with a line
        /// of their own ("Kill 5 Troopers", "Kill 3 Droids") stay apart.
        /// </summary>
        private static void MergeHiddenKills(CoreFacts c, List<CoreStep> steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                CoreStep s = steps[i];
                if (!IsKill(s) || s.Quest == 0)
                    continue;
                List<CoreStep> same = steps.FindAll(n => IsKill(n) && n.Quest == s.Quest && n.Branch == s.Branch && n.Step == s.Step);
                if (same.Count < 2 || !same.Exists(n => n.Task >= 0 && CoreRender.TaskText(c, n).Length == 0) ||
                    same.Exists(n => n.Task >= 0 && CoreRender.TaskText(c, n).Length > 0))
                    continue;
                var m = new CoreStep { Kind = same.Exists(n => n.Kind == StepKind.KillLoot) ? StepKind.KillLoot : StepKind.Kill, Quest = s.Quest, QuestName = s.QuestName,
                                       Branch = s.Branch, Step = s.Step, Task = -1, At = s.At, End = s.End, Area = s.Area, Rank = s.Rank, Came = s.Came, Merged = true };
                foreach (CoreStep n in same)
                {
                    m.At = Math.Min(m.At, n.At);
                    m.End = Math.Max(m.End, n.End);
                    m.Rank = Math.Min(m.Rank, n.Rank);
                    if (n.Came != double.MinValue && (m.Came == double.MinValue || n.Came < m.Came))
                        m.Came = n.Came;
                    m.InFight |= n.InFight;
                    m.Loot |= n.Loot;
                    m.DeadOnly |= n.DeadOnly;
                    foreach (Vec3 p in n.Spots)
                        m.AddSpot(p);
                    foreach (Fact f in n.Causes)
                        if (!m.Causes.Contains(f))
                            m.Causes.Add(f);
                    foreach (Fact f in n.Tries)
                        if (!m.Tries.Contains(f))
                            m.Tries.Add(f);
                    foreach (ulong x in n.Specs)
                        if (!m.Specs.Contains(x))
                            m.Specs.Add(x);
                    foreach (ulong x in n.Accepts)
                        if (!m.Accepts.Contains(x))
                            m.Accepts.Add(x);
                    foreach (ulong x in n.Nodes)
                        if (!m.Nodes.Contains(x))
                            m.Nodes.Add(x);
                    foreach (ulong x in n.Anchors)
                        if (!m.Anchors.Contains(x))
                            m.Anchors.Add(x);
                    foreach (string u in n.Unsure)
                        m.Doubt(u);
                }
                steps[i] = m;
                foreach (CoreStep n in same)
                    if (n != s)
                        steps.Remove(n);
            }
        }

        /// <summary>A mission's site: where its transport put you, and the spots of the steps you did there.</summary>
        private sealed class Site
        {
            public CoreStep Hop;
            public ulong Quest;
            public ulong Area;
            public readonly List<Vec3> Spots = new List<Vec3>();
            public readonly HashSet<CoreStep> Steps = new HashSet<CoreStep>();
            /// <summary>How near one of its spots counts as being there (units, X and Z).</summary>
            public double R = 10;
            /// <summary>Another mission's transport has the same name; the missions of one name share a group number.</summary>
            public bool Shared;
            public int Group;
        }

        /// <summary>
        /// Every transport's site: its landing and the spots of that mission's steps after it in the landing's area (until
        /// its next transport), and of the steps of missions with no transport of their own done there before the next
        /// transport you used. A site reaches half way to the nearest other site in that area, and half way to where a
        /// transport inside one area was pressed, 10 units (100 m) at most.
        /// </summary>
        private static List<Site> Sites(CoreFacts c, List<CoreStep> steps)
        {
            var sites = new List<Site>();
            foreach (CoreStep h in steps)
            {
                if (h.Kind != StepKind.Hop || h.Quest == 0 || h.Landing.IsZero)
                    continue;
                var x = new Site { Hop = h, Quest = h.Quest, Area = h.LandArea != 0 ? h.LandArea : h.Area };
                x.Spots.Add(h.Landing);
                double until = double.MaxValue;
                foreach (CoreStep n in steps)
                    if (n != h && n.Kind == StepKind.Hop && n.Quest == h.Quest && n.At > h.At && n.At < until)
                        until = n.At;
                foreach (CoreStep n in steps)
                {
                    if (n == h || n.Quest != h.Quest || n.Kind == StepKind.Hop || n.Kind == StepKind.Reward || n.At < h.End - 0.5 || n.At >= until)
                        continue;
                    ulong na = AreaOf(n);
                    if (na != 0 && x.Area != 0 && na != x.Area)
                        continue;
                    x.Steps.Add(n);
                    foreach (Vec3 p in n.Spots)
                        AddSpot(x.Spots, p);
                    foreach (Fact f in n.Causes)
                        AddSpot(x.Spots, f.Me);
                }
                // Missions with no transport of their own that you did there, before you used the next transport (a
                // heroic you reached on another one's transport, a bonus): their spots are the site too. Started again
                // anywhere you worked there (Hutta: Hostage Situation's basement, 17 m from where Steel Voice's transport
                // put you), the bot counts as there and doesn't press a transport it has just used.
                double leave = double.MaxValue;
                foreach (CoreStep n in steps)
                    if (n != h && n.Kind == StepKind.Hop && n.At > h.At && n.At < leave)
                        leave = n.At;
                foreach (CoreStep n in steps)
                {
                    if (n.Quest == 0 || n.Quest == h.Quest || n.Kind == StepKind.Hop || n.Kind == StepKind.Reward || n.Kind == StepKind.Walk ||
                        n.At < h.End - 0.5 || n.At >= leave || steps.Exists(o => o.Kind == StepKind.Hop && o.Quest == n.Quest))
                        continue;
                    ulong na = AreaOf(n);
                    if (na == 0 || x.Area == 0 || na != x.Area)
                        continue;
                    foreach (Vec3 p in n.Spots)
                        AddSpot(x.Spots, p);
                    foreach (Fact f in n.Causes)
                        AddSpot(x.Spots, f.Me);
                }
                sites.Add(x);
            }
            foreach (Site x in sites)
            {
                foreach (Site y in sites)
                    if (y.Quest != x.Quest && y.Area == x.Area)
                        x.R = Math.Min(x.R, Near(x.Spots, y.Spots) / 2.0);
                // a transport pressed in the same area: never "there" where it is pressed
                Vec3 press = x.Hop.Causes.Count > 0 ? x.Hop.Causes[0].Me : default(Vec3);
                if (!press.IsZero && x.Hop.Area == x.Area)
                    x.R = Math.Min(x.R, Near(x.Spots, new List<Vec3> { press }) / 2.0);
                x.R = Math.Max(1.0, x.R);
            }
            // transports of one name for more than one mission
            int group = 0;
            foreach (Site x in sites)
            {
                if (x.Group != 0)
                    continue;
                List<Site> same = sites.FindAll(y => y.Hop.Item == x.Hop.Item);
                if (!same.Exists(y => y.Quest != x.Quest))
                    continue;
                group++;
                foreach (Site y in same)
                {
                    y.Shared = true;
                    y.Group = group;
                }
            }
            return sites;
        }

        /// <summary>The least distance between two lists of spots (with height: Nar Shaddaa's heroics sit floor above floor).</summary>
        private static double Near(List<Vec3> a, List<Vec3> b)
        {
            double best = double.MaxValue;
            foreach (Vec3 p in a)
                foreach (Vec3 q in b)
                {
                    double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
                    best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                }
            return best;
        }

        /// <summary>True while you are at the site: in its area and near one of its spots (every spot, none left out).</summary>
        private static string AtSite(Site x)
        {
            var parts = new List<string>();
            string r2 = (x.R * x.R).ToString("0.###", Inv);
            foreach (Vec3 p in Hotspots(x.Spots, (float)(x.R / 2.0), int.MaxValue))
                parts.Add("((Me.Position.X - (" + Fmt.F3(p.X) + ")) ** 2 + (Me.Position.Y - (" + Fmt.F3(p.Y) + ")) ** 2 + (Me.Position.Z - (" + Fmt.F3(p.Z) + ")) ** 2) < " + r2);
            string near = "(" + string.Join(" or ", parts.ToArray()) + ")";
            return x.Area != 0 ? "(Core.Player.AreaId == " + H(x.Area) + " and " + near + ")" : near;
        }

        /// <summary>You stand at the site of another mission of the same transport name that is still open (or empty).</summary>
        private static string Busy(Site x)
        {
            var parts = new List<string>();
            foreach (Site y in _sites)
                if (y.Group == x.Group && y.Quest != x.Quest && x.Group != 0)
                    parts.Add("(" + AtSite(y) + " and " + Open(y.Quest) + ")");
            return string.Join(" or ", parts.ToArray());
        }

        /// <summary>The shared site a step is done at (its mission's reward too), or null.</summary>
        private static Site HeldBy(CoreStep s)
        {
            if (_sites == null || s.Kind == StepKind.Hop)
                return null;
            foreach (Site x in _sites)
                if (x.Shared && Busy(x).Length > 0 && (x.Steps.Contains(s) || (s.Kind == StepKind.Reward && s.Quest == x.Quest)))
                    return x;
            return null;
        }

        /// <summary>The transport group of a step's mission (its transports, its steps at the sites, its reward), or 0.</summary>
        private static int GroupOf(CoreStep s)
        {
            if (_sites == null)
                return 0;
            foreach (Site x in _sites)
                if (x.Shared && x.Quest == s.Quest && (x.Hop == s || x.Steps.Contains(s) || s.Kind == StepKind.Reward))
                    return x.Group;
            return 0;
        }

        /// <summary>
        /// A click on a lift (its window opened, then a ride): where you came out (the first spot 3 units or more from where you
        /// boarded, within a minute of the ride).
        /// </summary>
        private static bool Lift(CoreFacts c, Fact f, out Vec3 exit)
        {
            exit = default(Vec3);
            List<RecEvent> ev = c.Events;
            for (int i = 0; i < ev.Count; i++)
            {
                RecEvent e = ev[i];
                double t = c.AtOf[i];
                if (t < f.At - 2 || t > f.At + 15)
                    continue;
                if ((e.Kind != EvKind.ElevatorOpen && e.Kind != EvKind.ElevatorRide) || e.Target == null || e.Target.Name != f.Target.Name)
                    continue;
                Vec3 from = e.HasStand ? e.Stand : (!f.Me.IsZero ? f.Me : e.Pos);
                for (int j = i + 1; j < ev.Count && c.AtOf[j] - t <= 60; j++)
                {
                    RecEvent n = ev[j];
                    if (n.Pos.IsZero || n.Kind == EvKind.Loading)
                        continue;
                    if (n.Pos.DistanceTo(from) >= 3f)
                    {
                        exit = n.Pos;
                        return true;
                    }
                }
                return false;
            }
            return false;
        }
    }
}
