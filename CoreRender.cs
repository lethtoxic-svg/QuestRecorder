using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuestRecorder
{
    /// <summary>
    /// B6-B8 of the new core: each mission step as one entry of the step runner's table (QrSteps), one hook that runs it,
    /// and four gated copies of that hook in the Questing list. Rewards stay BuddyCron's own CompleteQuest; a puzzle
    /// stays a pause for you to do it by hand.
    /// </summary>
    internal static class CoreRender
    {
        /// <summary>One step as profile text, with what the merge with the travel steps needs to know.</summary>
        internal sealed class Block
        {
            public CoreStep Step;
            /// <summary>The runner's step number (0 = no runner step, native lines only).</summary>
            public int No;
            /// <summary>The step's gate: true while it is still to do (null = none).</summary>
            public string Gate;
            public string Label = string.Empty;
            /// <summary>The runner table entry ("new QrStepDef { ... }"); null = none.</summary>
            public string Def;
            /// <summary>Has an in-fight hook too (an item used once the mob's health is down).</summary>
            public bool Fight;
            /// <summary>The runner's lines (the gated copies), before any native lines.</summary>
            public readonly List<string> Run = new List<string>();
            /// <summary>Native lines after the runner's (a reward, a puzzle pause).</summary>
            public readonly List<string> After = new List<string>();
            public double At;
            public ulong Quest;
            /// <summary>Who you talked to for it (replies are matched by this name), or empty.</summary>
            public string Talk = string.Empty;
            public Vec3 TalkAt;
            /// <summary>A pick-up from a mission board (the board pick-up from the travel builder is used when it has one).</summary>
            public bool Board;
            /// <summary>What the recorder knows about how it found the step (shown in the review list, never in the profile).</summary>
            public readonly List<string> Unsure = new List<string>();
            /// <summary>Needs a look (yellow in the review list): tied by a guess, nothing tied, or done by hand.</summary>
            public bool Yellow;
            /// <summary>What the bot does for it, in plain words (the task tracker's line; never in the profile).</summary>
            public string What = string.Empty;
            /// <summary>Of missions whose transports share one name (0 = none): their steps go round again, as the game picks the transport.</summary>
            public int Group;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const int Copies = 4;

        // ------------------------------------------------------------------ B6: gates

        /// <summary>True while the task of that step has not reached n (and the quest is on that step).</summary>
        public static string TaskGate(ulong q, ulong step, int task, ulong n)
        {
            return "HasQuest(" + Fmt.Hex(q) + ") and not IsTaskValueGreaterThan(" + Fmt.Hex(q) + ", " + Fmt.UInt(step) + ", " + task.ToString(Inv) + ", " + Fmt.UInt(n > 0 ? n - 1 : 0) + ")";
        }

        /// <summary>The gate of a step: its task's count below n, or (no task counted) the quest still on that step.</summary>
        public static string Gate(CoreStep s, ulong n)
        {
            return s.Task >= 0 ? TaskGate(s.Quest, s.Step, s.Task, n) : OnStepGate(s.Quest, s.Step);
        }

        /// <summary>True while the quest is on that step (the game says a task value is "greater" when the quest is not on the step).</summary>
        public static string OnStepGate(ulong q, ulong step)
        {
            return "HasQuest(" + Fmt.Hex(q) + ") and not IsTaskValueGreaterThan(" + Fmt.Hex(q) + ", " + Fmt.UInt(step) + ", 0, 999999)";
        }

        public static string OpenGate(ulong q)
        {
            return "HasQuest(" + Fmt.Hex(q) + ") and not IsQuestComplete(" + Fmt.Hex(q) + ")";
        }

        // ------------------------------------------------------------------ B7: the blocks

        /// <summary>Every step of the new core as a block, numbered in order. Steps with no mission to gate them are left out.</summary>
        public static List<Block> Blocks(CoreFacts c, List<CoreStep> steps)
        {
            Stretch(steps, c);
            var blocks = new List<Block>();
            int no = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                CoreStep s = steps[i];
                if (s.Quest == 0)
                    continue; // nothing to gate it on (a transport no mission could be tied to: the travel builder's own hop is kept)
                if (s.Kind == StepKind.Walk && s.Task >= 0 && steps.FindIndex(i + 1, n => n.Kind != StepKind.Walk && n.Quest == s.Quest && n.Branch == s.Branch &&
                                                                                   n.Step == s.Step && n.Task == s.Task) >= 0)
                    continue; // a count nothing was tied to, of a task a later step does by what you did: that step does it
                Block b = Make(c, steps, i, ref no);
                if (b != null)
                    blocks.Add(b);
            }
            return blocks;
        }

        /// <summary>
        /// You stopped before a task was done (19 of 20): its last step goes on to the game's full count, so the profile
        /// finishes it. (A step that completed already runs to the full count.) Not when the game ended that mission itself
        /// as another one completed (a heroic's bonus): you didn't stop, so the bot does as many as you did.
        /// </summary>
        internal static void Stretch(List<CoreStep> steps, CoreFacts c = null)
        {
            var last = new Dictionary<string, CoreStep>(StringComparer.Ordinal);
            foreach (CoreStep s in steps)
                if (s.Task >= 0 && s.Quest != 0)
                    last[Fmt.Hex(s.Quest) + "/" + s.Branch + "/" + s.Step + "/" + s.Task] = s;
            foreach (CoreStep s in last.Values)
                if (s.Max > s.CountTo && s.CountTo > 0)
                {
                    ulong with = c != null ? c.EndedWith(s.Quest) : 0;
                    if (with != 0)
                    {
                        string by = CoreSteps.QuestName(c, with);
                        s.Doubt("the game ended this mission when " + (by.Length > 0 ? by : "another mission") + " was done, at " + s.CountTo + " of " +
                                s.Max + ": the bot does " + s.CountTo + ", as you did");
                        continue;
                    }
                    s.Doubt("you stopped at " + s.CountTo + " of " + s.Max + ": this step goes on to " + s.Max);
                    s.CountTo = s.Max;
                }
        }

        private static Block Make(CoreFacts c, List<CoreStep> steps, int at, ref int no)
        {
            CoreStep s = steps[at];
            var b = new Block { Step = s, At = s.At, Quest = s.Quest };
            b.Unsure.AddRange(s.Unsure);
            b.Yellow = s.Rank >= 3;
            string text = TaskText(c, s);
            string on = TargetName(s);
            // (an action done for a step whose count was not seen: which object or NPC it was, the step's text can name another task)
            if (s.Task < 0 && text.Length > 0 && on.Length > 0 && text.IndexOf(on, StringComparison.OrdinalIgnoreCase) < 0 &&
                s.Kind != StepKind.Walk && s.Kind != StepKind.Hop && s.Kind != StepKind.Reward && s.Kind != StepKind.HandIn && s.Kind != StepKind.Pickup)
                text += " (" + on + ")";
            string count = s.CountTo > 1 ? " (" + s.CountTo + ")" : string.Empty;
            var d = new Def();
            d.Quest = s.Quest;
            d.Branch = s.Branch;
            d.Step = s.Step;
            d.Task = s.Task;
            d.CountTo = s.Task >= 0 ? Math.Max(1, s.CountTo) : 0;
            double took = Math.Max(0, s.End - s.At);
            switch (s.Kind)
            {
                case StepKind.Pickup:
                {
                    b.Label = "Pick up " + Name(s);
                    // only "do I have it?": the game's "already complete" has read true for missions the giver offered
                    // again (heroics, weeklies, a new character); the runner gives a giver that doesn't offer it one look
                    b.Gate = "not HasQuest(" + Fmt.Hex(s.Quest) + ")";
                    Fact cause = s.Causes.Count > 0 ? s.Causes[0] : null;
                    b.Board = cause != null && cause.Kind == FactKind.Board;
                    if (cause != null && cause.Kind == FactKind.Talk && cause.Target != null)
                    {
                        b.Talk = cause.Target.Name;
                        b.TalkAt = cause.Target.HasPos ? cause.Target.Pos : cause.Me;
                    }
                    d.Kind = "QrPickup";
                    d.Branch = 0;
                    d.Step = 0;
                    d.Task = 0;
                    d.CountTo = 0;
                    Targets(s, d, true);
                    d.MaxTries = 3;
                    d.WallCap = 90;
                    break;
                }
                case StepKind.Kill:
                case StepKind.KillLoot:
                case StepKind.Collect:
                    b.Label = text.Length > 0 ? text + count : (s.Kind == StepKind.Collect ? "Collect" : "Kill") + (on.Length > 0 ? " " + on : string.Empty) + count;
                    b.Gate = Gate(s, d.CountTo);
                    d.Kind = s.Kind == StepKind.Collect ? "QrCollect" : "QrKill";
                    d.Loot = s.Kind == StepKind.KillLoot || s.Loot;
                    Targets(s, d, true);
                    d.MaxTries = 12;
                    d.WaitAtSpot = 10;
                    d.WallCap = Clamp(2 * took + 300, 600, 1800);
                    d.NoProgressCap = NoProgress(s);
                    if (d.Specs.Count == 0 && d.Nodes.Count == 0)
                        Look(b, d.Names.Count > 0 ? "no type id for what this step counts: the bot looks for it by name at your spots" : "no type id for what this step counts: the bot can only wait at your spots");
                    break;
                case StepKind.Use:
                    b.Label = text.Length > 0 ? text + count : "Use" + (on.Length > 0 ? " " + on : string.Empty) + count;
                    b.Gate = Gate(s, d.CountTo);
                    d.Kind = "QrUse";
                    Targets(s, d, true);
                    d.MaxTries = (int)Math.Min(40, 4 + 2 * d.CountTo);
                    d.WallCap = Clamp(2 * took + 240, 300, 1500);
                    d.NoProgressCap = NoProgress(s);
                    if (d.Specs.Count == 0 && d.Nodes.Count == 0)
                        Look(b, d.Names.Count > 0 ? "no type id for the object: the bot looks for it by name at your spot" : "no type id for the object: the bot clicks an object the game marks usable for a mission at your spot");
                    break;
                case StepKind.Talk:
                {
                    b.Label = text.Length > 0 ? text : "Talk" + (on.Length > 0 ? " to " + on : string.Empty);
                    b.Gate = Gate(s, d.CountTo);
                    Fact cause = s.Causes.Find(f => f.Kind == FactKind.Talk && f.Target != null);
                    if (cause != null)
                    {
                        b.Talk = cause.Target.Name;
                        b.TalkAt = cause.Target.HasPos ? cause.Target.Pos : cause.Me;
                    }
                    d.Kind = "QrTalk";
                    Targets(s, d, true);
                    d.MaxTries = 3;
                    d.WallCap = 120;
                    break;
                }
                case StepKind.Item:
                case StepKind.Ability:
                    b.Label = text.Length > 0 ? text + count : (s.Item.Length > 0 ? "Use " + s.Item : "Use " + s.Cast) + count;
                    b.Gate = Gate(s, d.CountTo);
                    d.Kind = s.Kind == StepKind.Ability ? "QrAbility" : "QrItem";
                    d.Item = s.Item;
                    d.Cast = s.Cast;
                    ItemTargets(s, d);
                    d.DeadOnly = s.DeadOnly;
                    d.InFight = s.InFight;
                    d.HealthAt = HealthAt(s);
                    if (d.HealthAt > 0f)
                        d.InFight = true;
                    d.MaxTries = (int)Math.Min(30, 3 + 2 * d.CountTo);
                    d.WallCap = Clamp(2 * took + 240, 300, 1500);
                    d.NoProgressCap = NoProgress(s);
                    if (s.Kind == StepKind.Ability && s.Cast.Length == 0)
                        Look(b, "the combat log gave no name for this ability: the bot can't cast it");
                    break;
                case StepKind.Walk:
                    b.Label = text.Length > 0 ? text : ByItself(s, text) ? "Wait for the game to move it on" : "Go on where it ticked";
                    b.Gate = Gate(s, d.CountTo);
                    d.Kind = "QrWalkWait";
                    Spots(s.Spots, d);
                    d.WaitAtSpot = 60;
                    d.WallCap = 300;
                    if (ByItself(s, text))
                    {
                        // (a step with no objective the game moved on by itself a few seconds after it came: a short wait)
                        d.WaitAtSpot = 20;
                        b.Yellow = false;
                    }
                    else if (Arrived(s))
                        b.Yellow = false; // (getting there was the step: the bot does what you did)
                    else
                        Look(b, "nothing you did was tied to this step: the bot walks to where it ticked and waits there");
                    break;
                case StepKind.Holocom:
                    if (s.Task >= 0)
                    {
                        b.Label = text.Length > 0 ? text : "Answer the call";
                        b.Gate = Gate(s, d.CountTo);
                    }
                    else
                    {
                        // the call ends the mission: done once it leaves your log or its reward waits
                        b.Label = "Answer the call: " + Name(s);
                        b.Gate = OpenGate(s.Quest);
                        d.Branch = 0;
                        d.Step = 0;
                        d.CountTo = 0;
                        Reward(b, s.Quest, 5);
                    }
                    d.Kind = "QrHolocom";
                    d.MaxTries = 3;
                    d.WallCap = 300;
                    break;
                case StepKind.HandIn:
                {
                    b.Label = "Hand in " + Name(s);
                    b.Gate = s.Step != 0 ? OnStepGate(s.Quest, s.Step) : OpenGate(s.Quest);
                    Fact cause = s.Causes.Find(f => f.Target != null);
                    if (cause != null && cause.Kind == FactKind.Talk)
                    {
                        b.Talk = cause.Target.Name;
                        b.TalkAt = cause.Target.HasPos ? cause.Target.Pos : cause.Me;
                    }
                    d.Kind = "QrHandIn";
                    d.Branch = 0;
                    d.Step = 0;
                    d.Task = 0;
                    d.CountTo = 0;
                    Targets(s, d, true);
                    d.MaxTries = 3;
                    d.WallCap = 120;
                    Reward(b, s.Quest, 5);
                    break;
                }
                case StepKind.Reward:
                    // nothing to walk to: BuddyCron's own CompleteQuest (done at once when no reward waits)
                    b.Label = "Reward: " + Name(s);
                    b.What = "take the reward";
                    Reward(b, s.Quest, 3);
                    return b;
                case StepKind.Hop:
                {
                    ulong onStep;
                    if (!StepAt(c, s.Quest, s.At, out onStep))
                        return null; // the mission wasn't in your log when you used it: the travel builder's hop is kept
                    b.Label = "Use " + s.Item;
                    b.Gate = HopGate(steps, at, onStep);
                    d.Kind = "QrHop";
                    d.Step = onStep;
                    d.Branch = 0;
                    d.Task = -1;
                    d.CountTo = 0;
                    d.Item = s.Item;
                    d.Cast = s.Causes.Count > 0 ? s.Causes[0].Cast : string.Empty;
                    if (!s.Landing.IsZero)
                    {
                        d.Spots.Add(s.Landing);
                        // (it put you in another area: being in that area is landed - positions of two areas don't compare)
                        if (s.LandArea != 0 && s.Area != 0 && s.LandArea != s.Area)
                            d.Area = s.LandArea;
                        // landed = within half the hop of where it put you (never as far as where you used it)
                        Vec3 from = s.Spots.Count > 0 ? s.Spots[0] : s.Landing;
                        d.Range = (float)Math.Min(100.0, Math.Max(0.5, from.DistanceTo(s.Landing) / 2.0));
                    }
                    else
                        Look(b, "where it took you was not seen: the bot uses it once");
                    d.MaxTries = 3;
                    d.WallCap = 120;
                    break;
                }
                case StepKind.Puzzle:
                {
                    // walk there (bounded), then a pause for you: the order is the game's own puzzle
                    b.Label = text.Length > 0 ? text : "Puzzle";
                    b.Gate = Gate(s, d.CountTo);
                    d.Kind = "QrWalk";
                    d.Task = -1;
                    d.CountTo = 0;
                    Spots(s.Spots, d);
                    d.WallCap = 300;
                    b.Yellow = true;
                    string what = s.Causes.Count > 0 && s.Causes[0].Target != null && s.Causes[0].Target.Name.Length > 0 ? s.Causes[0].Target.Name : "the controls";
                    b.After.Add("<If Condition=\"" + Fmt.XmlAttr(b.Gate) + "\">");
                    b.After.Add("\t<UserDialog Title=\"Quest Recorder\" Message=\"" + Fmt.XmlAttr("Do the puzzle at " + what + " by hand, then press Continue.") + "\" />");
                    b.After.Add("</If>");
                    break;
                }
                default:
                    return null;
            }
            // (one of the actions you did before the step changed, with another after it: done after one go, as you did it -
            // the later one moves the mission on)
            if (s.Task < 0 && Action(s.Kind) && steps.FindIndex(at + 1, n => n.Quest == s.Quest && n.Branch == s.Branch && n.Step == s.Step && n.Task < 0 && Action(n.Kind)) >= 0)
            {
                d.Once = true;
                if (d.Kind == "QrKill" || d.Kind == "QrCollect")
                    d.MaxTries = Math.Min(d.MaxTries, 3);
            }
            b.What = What(s, d, on, text);
            b.No = ++no;
            d.No = b.No;
            d.Label = b.Label;
            b.Fight = d.InFight;
            b.Def = d.Text();
            Copy(b);
            return b;
        }

        /// <summary>What the bot does for the step, in plain words (the task tracker's line). "!!" = a count nothing was caught for.</summary>
        private static string What(CoreStep s, Def d, string on, string text)
        {
            string who = on.Length > 0 ? on : (d.Names.Count > 0 ? d.Names[0] : string.Empty);
            string w;
            switch (d.Kind)
            {
                case "QrPickup":
                    w = "pick up" + (who.Length > 0 ? " from " + who : string.Empty);
                    break;
                case "QrKill":
                    w = "kill " + (who.Length > 0 ? who : "what it counts") + (d.Loot ? " and loot it" : string.Empty);
                    break;
                case "QrCollect":
                    w = "kill " + (who.Length > 0 ? who : "what it counts") + " and take what it drops";
                    break;
                case "QrUse":
                    w = "use " + (who.Length > 0 ? who : "the mission object at your spot");
                    break;
                case "QrTalk":
                    w = "talk to " + (who.Length > 0 ? who : "who you talked to");
                    break;
                case "QrItem":
                    w = "use item " + (d.Item.Length > 0 ? d.Item : CastName(d.Cast)) + (who.Length > 0 ? " on " + who : string.Empty);
                    break;
                case "QrAbility":
                    w = "use " + (d.Cast.Length > 0 ? CastName(d.Cast) : "the mission ability") + (who.Length > 0 ? " on " + who : string.Empty);
                    break;
                case "QrWalkWait":
                    w = s.Task >= 0 ? "!! nothing you did was caught for this count - the bot only walks to where it ticked and waits"
                        : ByItself(s, text) ? "wait a moment (the game moved it on by itself)"
                        : Arrived(s) ? "walk there (it ticked when you got there)" : "walk to where it ticked and wait there";
                    break;
                case "QrHolocom":
                    w = "answer the holocom";
                    break;
                case "QrHandIn":
                    w = "hand in" + (who.Length > 0 ? " to " + who : string.Empty);
                    break;
                case "QrHop":
                    w = "use " + d.Item + " (it takes you there)";
                    break;
                case "QrWalk":
                    w = "walk there, then you do the puzzle by hand";
                    break;
                default:
                    w = "do it";
                    break;
            }
            return d.Once ? w + " (once)" : w;
        }

        /// <summary>
        /// A step (no count) that changed where you went on to, well after and far from your last action: getting there was
        /// the step (a "travel to"), not something the recorder missed.
        /// </summary>
        internal static bool Arrived(CoreStep s)
        {
            return s.Kind == StepKind.Walk && s.Task < 0 && s.Spots.Count > 0 &&
                   s.Unsure.Exists(u => u.StartsWith("it changed where you went after your last action", StringComparison.Ordinal));
        }

        /// <summary>
        /// A step with no objective text (no count) that the game moved on by itself within 15 s of coming to it, with
        /// nothing you did seen: a stage the game passes through (mission "phases" do this).
        /// </summary>
        internal static bool ByItself(CoreStep s, string text)
        {
            return s.Kind == StepKind.Walk && s.Task < 0 && text.Length == 0 && s.Came > double.MinValue && s.At - s.Came >= 0 && s.At - s.Came < 15;
        }

        /// <summary>A cast bar's name without "Using" and the dots ("Using Serum..." = "Serum").</summary>
        internal static string CastName(string cast)
        {
            string b = (cast ?? string.Empty).Trim().TrimEnd('.', '\u2026', ' ');
            return b.StartsWith("Using ", StringComparison.Ordinal) ? b.Substring(6) : b;
        }

        internal static bool Action(StepKind k)
        {
            return k == StepKind.Kill || k == StepKind.KillLoot || k == StepKind.Collect || k == StepKind.Use || k == StepKind.Talk ||
                   k == StepKind.Item || k == StepKind.Ability;
        }

        private static void Look(Block b, string why)
        {
            if (!b.Unsure.Contains(why))
                b.Unsure.Add(why);
            b.Yellow = true;
        }

        /// <summary>The reward: taken right here while the quest is finishing or its reward window waits.</summary>
        private static void Reward(Block b, ulong q, int wait)
        {
            string h = Fmt.Hex(q);
            b.After.Add("<If Condition=\"" + Fmt.XmlAttr("(" + OpenGate(q) + ") or " + RewardWaitingText(h)) + "\">");
            b.After.Add("\t<WaitTimer QuestId=\"1\" WaitTime=\"" + wait.ToString(Inv) + "\" />");
            b.After.Add("\t<CompleteQuest QuestId=\"" + h + "\" />");
            b.After.Add("</If>");
        }

        /// <summary>The same test the travel builder writes for "a reward window for this quest is waiting".</summary>
        internal static Func<string, string> RewardWaiting;

        internal static string RewardWaitingText(string h)
        {
            return RewardWaiting != null ? RewardWaiting(h) : "False";
        }

        /// <summary>The four gated copies of the step's hook (and its in-fight hook), then one last take-out outside them.</summary>
        private static void Copy(Block b)
        {
            string g = Fmt.XmlAttr(b.Gate);
            string h = "Qr" + b.No.ToString(Inv), f = h + "f";
            for (int k = 0; k < Copies; k++)
            {
                b.Run.Add("<If Condition=\"" + g + "\">");
                if (b.Fight)
                    b.Run.Add("\t<Hook Name=\"" + f + "\" Operation=\"I\" Index=\"0\" Location=\"PreCombatLogic\" />");
                b.Run.Add("\t<Hook Name=\"" + h + "\" Operation=\"I\" Index=\"0\" Location=\"TreeStart\" />");
                b.Run.Add("\t<WaitTimer QuestId=\"1\" WaitTime=\"1\" />");
                b.Run.Add("\t<Hook Name=\"" + h + "\" Operation=\"R\" Location=\"TreeStart\" />");
                if (b.Fight)
                    b.Run.Add("\t<Hook Name=\"" + f + "\" Operation=\"R\" Location=\"PreCombatLogic\" />");
                b.Run.Add("</If>");
            }
            b.Run.Add("<Hook Name=\"" + h + "\" Operation=\"R\" Location=\"TreeStart\" />");
            if (b.Fight)
                b.Run.Add("<Hook Name=\"" + f + "\" Operation=\"R\" Location=\"PreCombatLogic\" />");
        }

        /// <summary>
        /// A walk between the mission steps (to a board, off a lift, through a door) as a runner walk: a time limit, a
        /// give-up when the way can't be found, the stuck watchdog and the mount rule. Each copy only while you are not
        /// there yet (so the copies after it got there cost nothing). Not a mission step: never in the coverage line.
        /// </summary>
        public static Block Walk(int no, string name, Vec3 at)
        {
            var d = new Def { No = no, Kind = "QrWalk", Label = "Walk: " + name, Task = -1, WallCap = 300 };
            d.Spots.Add(at);
            var b = new Block { No = no, Label = d.Label, Def = d.Text() };
            string g = Fmt.XmlAttr("(((Me.Position.X - (" + F3(at.X) + ")) ** 2 + (Me.Position.Y - (" + F3(at.Y) + ")) ** 2 + (Me.Position.Z - (" + F3(at.Z) + ")) ** 2) > 0.36)");
            string h = "Qr" + no.ToString(Inv);
            for (int k = 0; k < Copies; k++)
            {
                b.Run.Add("<If Condition=\"" + g + "\">");
                b.Run.Add("\t<Hook Name=\"" + h + "\" Operation=\"I\" Index=\"0\" Location=\"TreeStart\" />");
                b.Run.Add("\t<WaitTimer QuestId=\"1\" WaitTime=\"1\" />");
                b.Run.Add("\t<Hook Name=\"" + h + "\" Operation=\"R\" Location=\"TreeStart\" />");
                b.Run.Add("</If>");
            }
            b.Run.Add("<Hook Name=\"" + h + "\" Operation=\"R\" Location=\"TreeStart\" />");
            return b;
        }

        private static string F3(float v)
        {
            return Math.Round(v, 3).ToString("0.###", Inv);
        }

        /// <summary>
        /// A transport runs while the mission is on the step it was on when you used it. When the next step of that mission
        /// is on the same step (you hopped to where it counts), it also stops once that step's task is done.
        /// </summary>
        private static string HopGate(List<CoreStep> steps, int at, ulong onStep)
        {
            CoreStep hop = steps[at];
            for (int j = at + 1; j < steps.Count; j++)
            {
                CoreStep n = steps[j];
                if (n.Quest != hop.Quest || n.Kind == StepKind.Hop)
                    continue;
                if (n.Step == onStep && n.Task >= 0)
                    return TaskGate(n.Quest, n.Step, n.Task, Math.Max(1, n.CountTo));
                break;
            }
            return OnStepGate(hop.Quest, onStep);
        }

        /// <summary>The step the mission was on at that moment (false: it was not in your log then).</summary>
        internal static bool StepAt(CoreFacts c, ulong q, double t, out ulong step)
        {
            step = 0;
            bool found = false;
            foreach (Track tr in c.Tracks)
            {
                if (tr.Quest != q || tr.At > t + 0.5)
                    continue;
                if (tr.Kind == TrackKind.Completed || tr.Kind == TrackKind.Removed)
                {
                    found = false;
                    continue;
                }
                if (tr.Step != 0 || tr.Kind == TrackKind.Step)
                {
                    step = tr.Step;
                    found = true;
                }
            }
            return found;
        }

        // ------------------------------------------------------------------ the step's targets and spots

        private static void Targets(CoreStep s, Def d, bool withAccepts)
        {
            foreach (ulong x in s.Specs)
                if (!d.Specs.Contains(x))
                    d.Specs.Add(x);
            if (withAccepts)
                foreach (ulong x in s.Accepts)
                    if (!d.Specs.Contains(x))
                        d.Specs.Add(x);
            foreach (ulong x in s.Nodes)
                if (!d.Nodes.Contains(x))
                    d.Nodes.Add(x);
            Spots(s.Spots, d);
            Names(s.Causes, d);
        }

        /// <summary>An item's target types and spots: the uses that counted first, in your order, then the tries at other spots.</summary>
        private static void ItemTargets(CoreStep s, Def d)
        {
            var uses = new List<Fact>(s.Causes);
            Fmt.StableSort(uses, (x, y) => x.At.CompareTo(y.At));
            var tries = new List<Fact>(s.Tries);
            Fmt.StableSort(tries, (x, y) => x.At.CompareTo(y.At));
            uses.AddRange(tries);
            var spots = new List<Vec3>();
            foreach (Fact f in uses)
            {
                Vec3 p = f.Target != null && f.Target.HasPos ? f.Target.Pos : f.Me;
                if (!p.IsZero && !spots.Exists(x => x.DistanceTo(p) < 0.05f))
                    spots.Add(p);
            }
            foreach (Vec3 p in s.Spots)
                if (!spots.Exists(x => x.DistanceTo(p) < 0.05f))
                    spots.Add(p);
            foreach (ulong x in s.Specs)
                if (!d.Specs.Contains(x))
                    d.Specs.Add(x);
            // (the task's own list only when the item went on something: a "use it here" item has no target to look for)
            if (s.Specs.Count > 0 || s.Nodes.Count > 0)
                foreach (ulong x in s.Accepts)
                    if (!d.Specs.Contains(x))
                        d.Specs.Add(x);
            foreach (ulong x in s.Nodes)
                if (!d.Nodes.Contains(x))
                    d.Nodes.Add(x);
            Spots(spots, d);
            if (s.Specs.Count > 0 || s.Nodes.Count > 0 || s.Causes.Exists(f => f.Target != null && f.Target.Spec == 0 && f.Target.Name.Length > 0))
                Names(s.Causes, d);
        }

        /// <summary>A recording with no type ids: the names of what the step's actions were done to, so the bot can still find them.</summary>
        private static void Names(List<Fact> causes, Def d)
        {
            if (d.Specs.Count > 0)
                return;
            foreach (Fact f in causes)
            {
                if (f.Target == null || f.Target.Kind == ObjKind.Player)
                    continue;
                string n = f.Target.Name;
                if (n.Length == 0 || n.StartsWith("#", StringComparison.Ordinal) || n.StartsWith("@", StringComparison.Ordinal) || d.Names.Contains(n))
                    continue;
                d.Names.Add(n);
                if (d.Names.Count >= 8)
                    break;
            }
        }

        private static void Spots(List<Vec3> from, Def d)
        {
            foreach (Vec3 p in from)
                if (!p.IsZero && !d.Spots.Exists(x => x.DistanceTo(p) < 0.05f))
                    d.Spots.Add(p);
            if (d.Spots.Count > 40)
                d.Spots.RemoveRange(40, d.Spots.Count - 40);
        }

        /// <summary>Health (percent) to bring the mob down to before the item goes on: the health it had when you used it, plus a margin.</summary>
        internal static float HealthAt(CoreStep s)
        {
            float h = -1f;
            foreach (Fact f in s.Causes)
                if (f.InFight && f.Target != null && !f.Target.Dead && f.Target.Health > 0f && f.Target.Health < 95f)
                    h = Math.Max(h, f.Target.Health);
            return h > 0f ? Math.Min(100f, (float)Math.Ceiling(h) + 5f) : 0f;
        }

        /// <summary>No count for this long and the step gives up: twice the longest wait you had between counts, at least 5 minutes, at most 15.</summary>
        private static double NoProgress(CoreStep s)
        {
            var times = s.Causes.ConvertAll(f => f.At);
            times.Sort();
            double gap = 0;
            for (int i = 1; i < times.Count; i++)
                gap = Math.Max(gap, times[i] - times[i - 1]);
            return Clamp(2 * gap, 300, 900);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return Math.Max(lo, Math.Min(hi, Math.Round(v)));
        }

        /// <summary>What the step's first action was done to ("Torvix Munition Cache"), or empty.</summary>
        internal static string TargetName(CoreStep s)
        {
            // (never you or another player: their names stay out of the profile)
            foreach (Fact f in s.Causes)
                if (f.Target != null && f.Target.Kind != ObjKind.Player && f.Target.Name.Length > 0 && !f.Target.Name.StartsWith("#", StringComparison.Ordinal) &&
                    !f.Target.Name.StartsWith("@", StringComparison.Ordinal))
                    return Fmt.OneLine(f.Target.Name);
            return string.Empty;
        }

        internal static string Name(CoreStep s)
        {
            return s.QuestName.Length > 0 ? s.QuestName : Fmt.Hex(s.Quest);
        }

        /// <summary>What the game's tracker says for that task ("Identify Torvix's Spies").</summary>
        internal static string TaskText(CoreFacts c, CoreStep s)
        {
            if (s.Task < 0)
            {
                // the objective the tracker showed once the quest came to that step
                foreach (Track t in c.Tracks)
                    if (t.Quest == s.Quest && t.Step == s.Step && t.Ev != null && t.Ev.Text.Length > 0 && (t.Kind == TrackKind.Added || t.Kind == TrackKind.Step))
                        return Fmt.OneLine(t.Ev.Text);
                foreach (Track t in c.Tracks)
                    if (t.Quest == s.Quest && t.Step == s.Step && t.Ev != null && t.Ev.Text.Length > 0 && t.Kind == TrackKind.Task)
                        return Fmt.OneLine(t.Ev.Text);
                return string.Empty;
            }
            foreach (Track t in c.Tracks)
                if (t.Quest == s.Quest && t.Step == s.Step && t.Task == s.Task && t.Ev != null && t.Ev.Text.Length > 0)
                    return Fmt.OneLine(t.Ev.Text);
            return string.Empty;
        }

        // ------------------------------------------------------------------ the runner's table and hooks

        /// <summary>One entry of the runner's table, as C#.</summary>
        private sealed class Def
        {
            public int No;
            public string Kind = "QrWalkWait";
            public string Label = string.Empty;
            public ulong Quest, Branch, Step, CountTo;
            public int Task = -1;
            public readonly List<ulong> Specs = new List<ulong>(), Nodes = new List<ulong>();
            public readonly List<Vec3> Spots = new List<Vec3>();
            public readonly List<string> Names = new List<string>();
            public ulong Ability, Template, Area;
            public string Item = string.Empty, Cast = string.Empty;
            public bool DeadOnly, InFight, Loot, Once;
            public float HealthAt, Range = 0.4f;
            public int MaxTries = 6;
            public double NoProgressCap = 300, WallCap = 900, WaitAtSpot = 8;

            public string Text()
            {
                var sb = new StringBuilder("new QrStepDef { No = ");
                sb.Append(No.ToString(Inv)).Append(", Kind = ").Append(Kind).Append(", Label = ").Append(Cs(Label));
                if (Quest != 0)
                    sb.Append(", Quest = ").Append(U(Quest));
                if (Branch != 0)
                    sb.Append(", Branch = ").Append(Fmt.UInt(Branch)).Append("UL");
                if (Step != 0)
                    sb.Append(", Step = ").Append(Fmt.UInt(Step)).Append("UL");
                sb.Append(", Task = ").Append(Task.ToString(Inv));
                if (CountTo != 0)
                    sb.Append(", CountTo = ").Append(Fmt.UInt(CountTo)).Append("UL");
                if (Specs.Count > 0)
                    sb.Append(", Specs = new ulong[] { ").Append(string.Join(", ", Specs.ConvertAll(U))).Append(" }");
                if (Nodes.Count > 0)
                    sb.Append(", Nodes = new ulong[] { ").Append(string.Join(", ", Nodes.ConvertAll(U))).Append(" }");
                if (Names.Count > 0)
                    sb.Append(", Names = new string[] { ").Append(string.Join(", ", Names.ConvertAll(Cs))).Append(" }");
                if (Spots.Count > 0)
                    sb.Append(", Spots = new System.Numerics.Vector3[] { ").Append(string.Join(", ", Spots.ConvertAll(V))).Append(" }");
                if (Ability != 0)
                    sb.Append(", Ability = ").Append(U(Ability));
                if (Template != 0)
                    sb.Append(", Template = ").Append(U(Template));
                if (Area != 0)
                    sb.Append(", Area = ").Append(U(Area));
                if (Item.Length > 0)
                    sb.Append(", Item = ").Append(Cs(Item));
                if (Cast.Length > 0)
                    sb.Append(", Cast = ").Append(Cs(Cast));
                if (DeadOnly)
                    sb.Append(", DeadOnly = true");
                if (InFight)
                    sb.Append(", InFight = true");
                if (Loot)
                    sb.Append(", Loot = true");
                if (Once)
                    sb.Append(", Once = true");
                if (HealthAt > 0f)
                    sb.Append(", HealthAt = ").Append(F(HealthAt));
                if (Range != 0.4f)
                    sb.Append(", Range = ").Append(F(Range));
                if (MaxTries != 6)
                    sb.Append(", MaxTries = ").Append(MaxTries.ToString(Inv));
                if (NoProgressCap != 300)
                    sb.Append(", NoProgressCap = ").Append(NoProgressCap.ToString("0", Inv));
                if (WallCap != 900)
                    sb.Append(", WallCap = ").Append(WallCap.ToString("0", Inv));
                if (WaitAtSpot != 8)
                    sb.Append(", WaitAtSpot = ").Append(WaitAtSpot.ToString("0", Inv));
                return sb.Append(" }").ToString();
            }
        }

        private static string U(ulong v)
        {
            return "0x" + v.ToString("X", Inv) + "UL";
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                v = 0f;
            return Math.Round(v, 3).ToString("0.###", Inv) + "f";
        }

        private static string V(Vec3 p)
        {
            return "new System.Numerics.Vector3(" + F(p.X) + ", " + F(p.Y) + ", " + F(p.Z) + ")";
        }

        /// <summary>A C# string literal (never the end of a CDATA block).</summary>
        internal static string Cs(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? string.Empty)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                    case '\n':
                    case '\t':
                        sb.Append(' ');
                        break;
                    case '>':
                        sb.Append("\\u003E"); // (so "]]>" can never close the CDATA block)
                        break;
                    default:
                        if (ch < ' ')
                            sb.Append(' ');
                        else
                            sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>The code chunks every profile with runner steps carries: the runner, its table, one hook per step (walks too).</summary>
        public static List<string> Chunks(List<Block> blocks, List<Block> walks = null)
        {
            const string nl = "\r\n";
            var chunks = new List<string>();
            var run = blocks.FindAll(b => b.No > 0);
            if (walks != null)
                run.AddRange(walks.FindAll(b => b.No > 0));
            if (run.Count == 0)
                return chunks;
            chunks.Add("\t\t<CodeChunk Name=\"QrRunner\" Type=\"Definition\"><![CDATA[" + nl + CoreRunner.Text.Replace("\r\n", "\n").Replace("\n", nl) + nl + "\t\t]]></CodeChunk>");
            var sb = new StringBuilder();
            sb.Append("\t\t<CodeChunk Name=\"QrSteps\" Type=\"Definition\"><![CDATA[").Append(nl);
            sb.Append("static QrStepDef[] QrSteps()").Append(nl).Append("{").Append(nl).Append("\treturn new QrStepDef[] {").Append(nl);
            foreach (Block b in run)
                sb.Append("\t\t").Append(b.Def).Append(',').Append(nl);
            sb.Append("\t};").Append(nl).Append("}").Append(nl).Append("\t\t]]></CodeChunk>");
            chunks.Add(sb.ToString());
            foreach (Block b in run)
            {
                string n = b.No.ToString(Inv);
                chunks.Add("\t\t<CodeChunk Name=\"Qr" + n + "\" Type=\"Hook\"><![CDATA[" + nl + "\t\t\treturn QrRun(" + n + ");" + nl + "\t\t]]></CodeChunk>");
                if (b.Fight)
                    chunks.Add("\t\t<CodeChunk Name=\"Qr" + n + "f\" Type=\"Hook\"><![CDATA[" + nl + "\t\t\treturn QrFight(" + n + ");" + nl + "\t\t]]></CodeChunk>");
            }
            return chunks;
        }

        // ------------------------------------------------------------------ B8: the coverage line

        /// <summary>
        /// "26 mission steps: 9 tied to what you did by the game's own ids, 14 by the one action you did for them, 3 need a look."
        /// (Pick-ups, hand-ins and rewards are tied by the mission's own id and not counted.)
        /// </summary>
        public static string Coverage(List<Block> blocks)
        {
            int all = 0, ids = 0, only = 0, look = 0;
            foreach (Block b in blocks)
            {
                CoreStep s = b.Step;
                if (s.Kind == StepKind.Reward || s.Kind == StepKind.Pickup || s.Kind == StepKind.HandIn || (s.Kind == StepKind.Holocom && s.Task < 0))
                    continue;
                all++;
                if (b.Yellow)
                    look++;
                else if (s.Rank == 1)
                    ids++;
                else
                    only++;
            }
            if (all == 0)
                return string.Empty;
            return all + " mission steps: " + ids + " tied to what you did by the game's own ids, " + only + " by the one action you did for them, " +
                   (look == 0 ? "none need a look." : look + (look == 1 ? " needs a look." : " need a look."));
        }
    }
}
