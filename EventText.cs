// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Text;

namespace QuestRecorder
{
    /// <summary>Fixed phrases the detector writes and the draft builder reads back (keep them in one place).</summary>
    internal static class How
    {
        public const string Offered = "the NPC you talked to offers this quest";
        public const string Holocom = "during a holocom call";
        public const string Talking = "you were talking to it";
        public const string Board = "mission board";
        public const string Clicked = "you clicked it just before";
        public const string NoGiver = "no giver seen (granted automatically?)";
        public const string NoTurnIn = "no NPC or object seen (completed without a turn-in?)";

        /// <summary>Note on ConvoStart / ConvoEnd events of a holocom (comlink) conversation.</summary>
        public const string HolocomCall = "holocom call";

        /// <summary>Start of the jump guess when you were revived.</summary>
        public const string Revived = "you were revived";
    }

    /// <summary>Turns events into the timeline (.txt) lines, the CSV rows and the one-line headline. Pure text.</summary>
    internal static class EventText
    {
        private const string Indent = "                          ";

        // ------------------------------------------------------------------ headline

        public static string Headline(RecEvent e)
        {
            try
            {
                return HeadlineCore(e);
            }
            catch (Exception ex)
            {
                return e.Kind.ToString() + " (text failed: " + ex.Message + ")";
            }
        }

        private static string HeadlineCore(RecEvent e)
        {
            switch (e.Kind)
            {
                case EvKind.Start:
                    return "Recording started" + Colon(e.Note);
                case EvKind.Stop:
                    return "Recording stopped" + Paren(e.Note);
                case EvKind.QuestLog:
                {
                    int n = e.Quests != null ? e.Quests.Count : 0;
                    return Fmt.Format("{0}: {1} {2}", e.Note, n, n == 1 ? "quest" : "quests");
                }
                case EvKind.QuestAdded:
                    return "Quest added: " + Quest(e) + StepText(e) + Giver(e, "from");
                case EvKind.QuestStep:
                    return Fmt.Format("Quest step: {0}: branch {1} step {2} -> branch {3} step {4}{5}", Quest(e),
                        e.OldBranch, e.OldStep, e.Branch, e.Step, e.CheckFailed ? "  [live check disagrees]" : string.Empty);
                case EvKind.QuestTask:
                {
                    string max = e.Max > 0 ? "/" + Fmt.UInt(e.Max) : string.Empty;
                    string text = e.Text.Length > 0 ? "  \"" + e.Text + "\"" : string.Empty;
                    return Fmt.Format("Quest task: {0}: branch {1} step {2} task {3} now {4}{5}{6}{7}", Quest(e), e.Branch,
                        e.Step, e.TaskIndex, e.Count, max, text, e.CheckFailed ? "  [live check disagrees]" : string.Empty);
                }
                case EvKind.QuestCompleted:
                    return "Quest completed: " + Quest(e) + Giver(e, "at") + (e.Option.Length > 0 ? " - " + e.Option : string.Empty);
                case EvKind.QuestAbandoned:
                    return "Quest abandoned: " + Quest(e);
                case EvKind.QuestRemoved:
                    return "Quest left the log: " + Quest(e) + " (completed or abandoned: the game did not say)";
                case EvKind.HolocomWaiting:
                    return "Holocom call waiting: " + Quest(e);
                case EvKind.HolocomAnswered:
                    return "Holocom call answered: " + Quest(e);
                case EvKind.RewardWindow:
                    return "Reward window: " + Quest(e);
                case EvKind.ConvoStart:
                    if (e.Note == How.HolocomCall)
                        return "Holocom call started";
                    return "Conversation started with " + (e.Target != null ? Short(e.Target) : "someone the recorder could not identify") +
                           (e.QuestId != 0 ? " (offers " + Quest(e) + ")" : string.Empty);
                case EvKind.ConvoEnd:
                    return (e.Note == How.HolocomCall ? "Holocom call ended" : "Conversation ended" +
                               (e.Target != null ? " with " + Short(e.Target) : string.Empty)) +
                           " after " + Fmt.Duration(e.DurationMs / 1000.0);
                case EvKind.ConvoChoice:
                    return "Reply wheel" + (e.Target != null ? " with " + Short(e.Target) : string.Empty) + ": " +
                           (e.Choices != null ? string.Join(" | ", e.Choices) : "?") + " -> you picked " +
                           (e.OptionGuessed ? "? (not seen)" : e.Option) +
                           (e.CheckFailed ? "  [!] NOT what the bot's dark auto-pick takes" : string.Empty) + Paren(e.Note);
                case EvKind.ObjectClick:
                    return "Clicked object " + Name(e.Target);
                case EvKind.NpcClick:
                    return "Clicked NPC " + Name(e.Target);
                case EvKind.QuestItem:
                    if (e.Note == "equipped")
                        return "Put on \"" + e.Option + "\"";
                    return (e.Note == "ability" ? "Used quest ability \"" : "Used quest item \"") + e.Option + "\"" + (e.Target != null ? " on " + Short(e.Target) : string.Empty);
                case EvKind.Cast:
                    return "Cast \"" + (e.Option.Length > 0 ? e.Option : "?") + "\" for " + Fmt.Duration(e.DurationMs / 1000.0) + " (started out of combat)" +
                           (e.Target != null ? " near " + Short(e.Target) : string.Empty);
                case EvKind.Spot:
                    return "Scanner: " + (e.Target != null && e.Target.Kind == ObjKind.Npc ? "a mob your mission counts, " + Short(e.Target) : "another " + (e.Target != null ? Short(e.Target) : "object")) + " nearby";
                case EvKind.Command:
                    return "Command \"" + e.Option + "\" with the mouse over " + (e.Target != null ? Short(e.Target) : "something") + Paren(e.Note);
                case EvKind.Target:
                    return "Targeted " + (e.Target != null ? Short(e.Target) : "something");
                case EvKind.Loot:
                    return "Looted " + (e.Target != null ? Short(e.Target) : "a body");
                case EvKind.CastStart:
                    return "Started \"" + (e.Option.Length > 0 ? e.Option : "?") + "\" on " + (e.Target != null ? Short(e.Target) : "something");
                case EvKind.BoardOpen:
                {
                    int n = e.Details != null ? e.Details.Count : 0;
                    return Fmt.Format("Mission board opened: \"{0}\" ({1} {2})", e.Option, n, n == 1 ? "mission" : "missions");
                }
                case EvKind.BoardClose:
                    return "Mission board closed" + (e.Option.Length > 0 ? ": \"" + e.Option + "\"" : string.Empty);
                case EvKind.InteractionOpen:
                    return "Window opened: " + e.Option + (e.Target != null ? " with " + Short(e.Target) : string.Empty);
                case EvKind.TaxiOpen:
                    return "Taxi map opened at " + (e.Target != null ? Short(e.Target) : "an unknown terminal");
                case EvKind.TaxiStart:
                    return "Taxi ride started" + (e.Target != null ? " from " + Short(e.Target) : string.Empty);
                case EvKind.TaxiEnd:
                    return "Taxi ride ended: " + (e.Target != null ? Name(e.Target) : "unknown terminal") + " -> " +
                           (e.Option.Length > 0 ? "\"" + e.Option + "\"" + (e.OptionGuessed ? " [?]" : string.Empty)
                               : (e.Target2 != null ? Name(e.Target2) + " (taxi-map name not read)" : "unknown terminal")) +
                           Paren(e.Note);
                case EvKind.ElevatorOpen:
                    return "Elevator window opened: " + Name(e.Target) +
                           (e.Target != null && e.Target.Floor > 0 ? " (now on floor " + e.Target.Floor.ToString() + ")" : string.Empty);
                case EvKind.ElevatorRide:
                    return "Elevator ride: " + Name(e.Target) + " to " + (e.Option.Length > 0 ? "\"" + e.Option + "\"" : "an unknown floor");
                case EvKind.CombatStart:
                    return "Combat started" + Colon(e.Note);
                case EvKind.CombatEnd:
                    return "Combat ended after " + Fmt.Duration(e.DurationMs / 1000.0) + Colon(e.Note);
                case EvKind.Kill:
                    return "Killed " + Name(e.Target) + Level(e.Target) + Paren(e.Note);
                case EvKind.Died:
                    return "You died" + (e.Note.Length > 0 ? " (fighting " + e.Note + ")" : string.Empty);
                case EvKind.Revived:
                    return "You were revived";
                case EvKind.Mounted:
                    return "Mounted" + (e.Option.Length > 0 ? " \"" + e.Option + "\"" : string.Empty);
                case EvKind.Dismounted:
                    return "Dismounted";
                case EvKind.AreaChange:
                    return Fmt.Format("Area changed: \"{0}\" {1} -> \"{2}\" {3}", e.FromAreaName, Fmt.Hex(e.FromArea), e.AreaName, Fmt.Hex(e.AreaId));
                case EvKind.Loading:
                    return "Loading screen for " + Fmt.Duration(e.DurationMs / 1000.0) + Paren(e.Note);
                case EvKind.Jump:
                    return "Position jump of " + Fmt.F1(e.HasFrom ? e.Pos.DistanceTo(e.FromPos) : 0) + " m" + Paren(e.Note);
                case EvKind.ShipTravel:
                    return "Ship travelled to " + (e.Option.Length > 0 ? "\"" + e.Option + "\"" : "an unknown destination");
                case EvKind.Marker:
                    return "Marker: \"" + e.Note + "\"" + Paren(e.Option);
                case EvKind.Breadcrumb:
                    return "Breadcrumb";
                case EvKind.CharacterChange:
                    return "Character changed: " + e.Note;
                case EvKind.Paused:
                    return "Recording paused: " + e.Note;
                case EvKind.Resumed:
                    return "Recording resumed: " + e.Note;
                case EvKind.Warning:
                    return "Warning: " + e.Note;
                default:
                    return e.Kind.ToString();
            }
        }

        private static string Quest(RecEvent e)
        {
            string name = e.QuestName.Length > 0 ? e.QuestName : "(title not read)";
            return "\"" + name + "\" " + Fmt.Hex(e.QuestId) + (e.QuestHidden ? " (hidden)" : string.Empty);
        }

        private static string StepText(RecEvent e)
        {
            return e.HasStep ? Fmt.Format(" at branch {0} step {1}", e.Branch, e.Step) : string.Empty;
        }

        private static string Giver(RecEvent e, string word)
        {
            string how = e.Note.Length > 0 ? " (" + e.Note + ")" : string.Empty;
            if (e.Target != null)
                return " " + word + " " + Short(e.Target) + how;
            return how;
        }

        private static string Short(ObjRef o)
        {
            if (o == null)
                return "(unknown)";
            return o.KindText + " \"" + o.DisplayName + "\"";
        }

        private static string Name(ObjRef o)
        {
            return o == null ? "(unknown)" : "\"" + o.DisplayName + "\"";
        }

        private static string Level(ObjRef o)
        {
            if (o == null || o.Level == 0)
                return string.Empty;
            return " (level " + Fmt.UInt(o.Level) + (o.Toughness.Length > 0 ? " " + o.Toughness : string.Empty) + ")";
        }

        private static string Colon(string s)
        {
            return string.IsNullOrEmpty(s) ? string.Empty : ": " + s;
        }

        private static string Paren(string s)
        {
            return string.IsNullOrEmpty(s) ? string.Empty : " (" + s + ")";
        }

        /// <summary>"QUEST STEP" for EvKind.QuestStep.</summary>
        public static string Label(EvKind k)
        {
            string n = k.ToString();
            var sb = new StringBuilder(n.Length + 4);
            for (int i = 0; i < n.Length; i++)
            {
                if (i > 0 && char.IsUpper(n[i]))
                    sb.Append(' ');
                sb.Append(char.ToUpperInvariant(n[i]));
            }
            return sb.ToString();
        }

        /// <summary>Full description of an object for the timeline.</summary>
        public static string Describe(ObjRef o)
        {
            if (o == null)
                return string.Empty;
            var sb = new StringBuilder();
            sb.Append(o.KindText).Append(" \"").Append(o.DisplayName).Append("\" id ").Append(Fmt.Hex(o.NodeId));
            if (o.HasPos)
                sb.Append(" at ").Append(Fmt.Xyz(o.Pos));
            if (o.Indication.Length > 0 && o.Indication != "None")
                sb.Append(", quest marker ").Append(o.Indication);
            if (o.Level > 0)
                sb.Append(", level ").Append(Fmt.UInt(o.Level)).Append(o.Toughness.Length > 0 ? " " + o.Toughness : string.Empty);
            if (o.UseRange > 0)
                sb.Append(", use range ").Append(Fmt.F1(o.UseRange));
            if (o.UsableForQuest)
                sb.Append(", usable for a quest");
            if (o.IsVendor)
                sb.Append(", vendor");
            if (o.IsTaxiTerminal)
                sb.Append(", taxi terminal");
            if (o.IsWonkavator)
            {
                sb.Append(", elevator");
                if (o.Floor > 0)
                    sb.Append(" on floor ").Append(o.Floor);
                if (o.Destinations != null && o.Destinations.Length > 0)
                    sb.Append(", floors: ").Append(string.Join(" | ", o.Destinations));
            }
            if (o.How.Length > 0)
                sb.Append(" - ").Append(o.How);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ timeline (.txt)

        public static List<string> TxtLines(RecEvent e)
        {
            var lines = new List<string>();
            lines.Add(Fmt.Format("{0} {1,-8} #{2,-5} {3,-17} {4}", Fmt.Clock(e.Time), Fmt.Elapsed(e.Elapsed), e.Seq,
                Label(e.Kind), e.Summary));
            if (e.Kind == EvKind.Breadcrumb)
            {
                lines[0] += "  " + Fmt.Xyz(e.Pos);
                return lines;
            }
            lines.Add(Indent + Fmt.Format("at {0}  heading {1} deg  area \"{2}\" {3}", Fmt.Xyz(e.Pos), Fmt.F1(e.Heading), e.AreaName,
                Fmt.Hex(e.AreaId)));
            if (e.HasFrom)
            {
                lines.Add(Indent + Fmt.Format("before: {0}  area \"{1}\" {2}  ({3} m away)", Fmt.Xyz(e.FromPos), e.FromAreaName,
                    Fmt.Hex(e.FromArea), Fmt.F1(e.Pos.DistanceTo(e.FromPos))));
            }
            if (e.HasStand)
                lines.Add(Indent + "you stood at " + Fmt.Xyz(e.Stand));
            if (e.Target != null)
                lines.Add(Indent + Describe(e.Target));
            if (e.QuestFqn.Length > 0 && e.Kind != EvKind.QuestTask)
                lines.Add(Indent + "quest " + e.QuestFqn);
            if (e.Kind == EvKind.QuestTask && e.TaskIndex >= 0)
                lines.Add(Indent + Fmt.Format("task index {0}, TaskId {1}{2}", e.TaskIndex, e.TaskId,
                    e.TaskTotal > 0 ? ", the step has " + e.TaskTotal.ToString() + (e.TaskTotal == 1 ? " task" : " tasks") : string.Empty));
            if ((e.Kind == EvKind.ObjectClick || e.Kind == EvKind.NpcClick) && e.Option.Length > 0)
                lines.Add(Indent + "command " + e.Option);
            if (e.Kind == EvKind.QuestAdded && e.Option.Length > 0)
                lines.Add(Indent + "mission board \"" + e.Option + "\"");
            if (e.Check.Length > 0)
                lines.Add(Indent + e.Check + (e.CheckFailed ? "   <-- DISAGREES, check this step by hand" : string.Empty));
            if (e.Details != null)
            {
                foreach (string d in e.Details)
                    lines.Add(Indent + d);
            }
            if (e.Quests != null)
            {
                if (e.Quests.Count == 0)
                    lines.Add(Indent + "(no active quests)");
                foreach (QuestLine q in e.Quests)
                {
                    lines.Add(Indent + Fmt.Format("{0} \"{1}\"  branch {2} step {3}{4}{5}", Fmt.Hex(q.QuestId),
                        q.Name.Length > 0 ? q.Name : "(title not read)", q.Branch, q.Step,
                        q.Counts.Length > 0 ? "  " + q.Counts : string.Empty, q.Hidden ? "  (hidden)" : string.Empty));
                }
            }
            return lines;
        }

        // ------------------------------------------------------------------ CSV

        public const string CsvHeader =
            "seq,time,elapsed_s,kind,x,y,z,heading_deg,area_id,area_name,quest_id,quest_name,quest_fqn,quest_hidden,branch,step," +
            "old_branch,old_step,task_index,task_id,count,max,objective,target_kind,target_name,target_id,target_x,target_y," +
            "target_z,target_marker,target_how,before_x,before_y,before_z,before_area_id,before_area_name,stand_x,stand_y," +
            "stand_z,option,note,live_check,live_check_failed,duration_ms,summary," +
            "target_dead,target_anchor,target_level,target_toughness,target_use_range,target_quest_usable,target_spec,task_targets,target_health";

        public static string CsvRow(RecEvent e)
        {
            var f = new List<string>(48);
            f.Add(e.Seq.ToString());
            f.Add(Fmt.Stamp(e.Time));
            f.Add(Fmt.F1(e.Elapsed));
            f.Add(e.Kind.ToString());
            f.Add(Fmt.F3(e.Pos.X));
            f.Add(Fmt.F3(e.Pos.Y));
            f.Add(Fmt.F3(e.Pos.Z));
            f.Add(Fmt.F3(e.Heading));
            f.Add(e.AreaId != 0 ? Fmt.Hex(e.AreaId) : string.Empty);
            f.Add(e.AreaName);
            f.Add(e.QuestId != 0 ? Fmt.Hex(e.QuestId) : string.Empty);
            f.Add(e.QuestName);
            f.Add(e.QuestFqn);
            f.Add(e.QuestId != 0 ? (e.QuestHidden ? "yes" : "no") : string.Empty);
            f.Add(e.HasStep ? Fmt.UInt(e.Branch) : string.Empty);
            f.Add(e.HasStep ? Fmt.UInt(e.Step) : string.Empty);
            f.Add(e.HasOldStep ? Fmt.UInt(e.OldBranch) : string.Empty);
            f.Add(e.HasOldStep ? Fmt.UInt(e.OldStep) : string.Empty);
            bool task = e.TaskIndex >= 0;
            f.Add(task ? e.TaskIndex.ToString() : string.Empty);
            f.Add(task ? Fmt.UInt(e.TaskId) : string.Empty);
            f.Add(task ? Fmt.UInt(e.Count) : string.Empty);
            f.Add(task && e.Max > 0 ? Fmt.UInt(e.Max) : string.Empty);
            f.Add(e.Text);
            ObjRef t = e.Target;
            f.Add(t != null ? t.KindText : string.Empty);
            f.Add(t != null ? t.Name : string.Empty);
            f.Add(t != null ? Fmt.Hex(t.NodeId) : string.Empty);
            f.Add(t != null && t.HasPos ? Fmt.F3(t.Pos.X) : string.Empty);
            f.Add(t != null && t.HasPos ? Fmt.F3(t.Pos.Y) : string.Empty);
            f.Add(t != null && t.HasPos ? Fmt.F3(t.Pos.Z) : string.Empty);
            f.Add(t != null && t.Indication != "None" ? t.Indication : string.Empty);
            f.Add(t != null ? t.How : string.Empty);
            f.Add(e.HasFrom ? Fmt.F3(e.FromPos.X) : string.Empty);
            f.Add(e.HasFrom ? Fmt.F3(e.FromPos.Y) : string.Empty);
            f.Add(e.HasFrom ? Fmt.F3(e.FromPos.Z) : string.Empty);
            f.Add(e.HasFrom ? Fmt.Hex(e.FromArea) : string.Empty);
            f.Add(e.HasFrom ? e.FromAreaName : string.Empty);
            f.Add(e.HasStand ? Fmt.F3(e.Stand.X) : string.Empty);
            f.Add(e.HasStand ? Fmt.F3(e.Stand.Y) : string.Empty);
            f.Add(e.HasStand ? Fmt.F3(e.Stand.Z) : string.Empty);
            f.Add(e.Option);
            f.Add(NoteForCsv(e));
            f.Add(e.Check);
            f.Add(e.Check.Length > 0 ? (e.CheckFailed ? "yes" : "no") : string.Empty);
            f.Add(e.DurationMs > 0 ? e.DurationMs.ToString() : string.Empty);
            f.Add(e.Summary);
            // what the recorder knew about the object (new columns go at the end so older readers still work)
            f.Add(t != null && t.Dead ? "yes" : string.Empty);
            f.Add(t != null && t.Anchor != 0 ? Fmt.Hex(t.Anchor) : string.Empty);
            f.Add(t != null && t.Level != 0 ? Fmt.UInt(t.Level) : string.Empty);
            f.Add(t != null ? t.Toughness : string.Empty);
            f.Add(t != null && t.UseRange > 0 ? Fmt.F3(t.UseRange) : string.Empty);
            f.Add(t != null && t.UsableForQuest ? "yes" : string.Empty);
            f.Add(t != null && t.Spec != 0 ? Fmt.Hex(t.Spec) : string.Empty);
            var tt = new List<string>();
            if (e.TaskTargets != null)
                foreach (ulong id in e.TaskTargets)
                    tt.Add(Fmt.Hex(id));
            f.Add(string.Join(" ", tt.ToArray()));
            f.Add(t != null && t.Health >= 0f ? Fmt.F3(t.Health) : string.Empty);

            var sb = new StringBuilder(256);
            for (int i = 0; i < f.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Fmt.Csv(f[i]));
            }
            return sb.ToString();
        }

        private static string NoteForCsv(RecEvent e)
        {
            if (e.Quests == null)
                return e.Note;
            var sb = new StringBuilder(e.Note);
            foreach (QuestLine q in e.Quests)
            {
                sb.Append(sb.Length > 0 ? " | " : string.Empty);
                sb.Append(Fmt.Hex(q.QuestId)).Append(' ').Append(q.Name).Append(Fmt.Format(" b{0} s{1}", q.Branch, q.Step));
                if (q.Hidden)
                    sb.Append(" hidden");
            }
            return sb.ToString();
        }
    }
}
