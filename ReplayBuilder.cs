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
            public ulong Id; // the game's id for the object (0 = not known)
            public string On; // the NPC a mission item was used on (null = on yourself / at the spot)
            public float OnDist = -1f; // how far you stood from that NPC (game units), -1 = not known
            public bool OnDead; // that NPC was dead (a corpse)
            public float OnHealth = -1f; // that NPC's health (percent) when you used it, -1 = not known
            public bool ForQuest; // the game said it was usable for one of your missions (or takes a hand-in) when you clicked it
            public bool InFight; // a mission item you used while in a fight
            public bool Bag; // a usable bag item (a stim, a medpac), not a mission item
        }

        /// <summary>
        /// What you really did, before the steps are made:
        /// - a click on a mob you had killed is a loot, not a talk (older recordings wrote it as "clicked NPC");
        /// - the start of a cast on an object is the use (it comes before the tick it causes); when you clicked that
        ///   object just before, the click already is the use.
        /// </summary>
        /// <summary>
        /// One count task you did in several ways (Section X Hyperbiology: eggs, bones AND looting Muckwings all count to
        /// 10). Each tick became its own step, and a fight step only knew the one mob at the one spot of its own tick -
        /// with that mob dead the bot stood there forever. Every fight step of the task now knows every mob you fought for
        /// that task and every spot you fought them at (also the "step done" fight that finished it), so the bot goes
        /// round your spots like you did.
        /// </summary>
        private static void ShareTaskFights(List<Step> steps)
        {
            var byTask = new Dictionary<string, List<Step>>(StringComparer.Ordinal);
            var stepOf = new Dictionary<Step, string>();
            foreach (Step s in steps)
            {
                // only "kill and loot" counts (a plain kill fight keeps exactly the spots you fought at - CZ-198 Staff Cuts)
                if (s.Kind != "Grind" || s.Kills == null || s.Kills.Count == 0 || s.Gate == null ||
                    s.Comment.IndexOf("(kill and loot)", StringComparison.Ordinal) < 0)
                    continue;
                Match m = Regex.Match(s.Gate, @"IsTaskValueGreaterThan\((0x[0-9A-F]+), (\d+), (\d+), \d+\)");
                if (!m.Success)
                    continue;
                string key = m.Groups[1].Value + "|" + m.Groups[2].Value + "|" + m.Groups[3].Value;
                List<Step> list;
                if (!byTask.TryGetValue(key, out list))
                    byTask[key] = list = new List<Step>();
                list.Add(s);
                stepOf[s] = m.Groups[1].Value + "|" + m.Groups[2].Value;
            }
            foreach (KeyValuePair<string, List<Step>> kv in byTask)
            {
                List<Step> fights = kv.Value;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (Step f in fights)
                    foreach (Kill k in f.Kills)
                        names.Add(k.Name);
                // the fight that finished that quest step (its gate says "step done"), with the same mobs: same task
                // (on the same branch, when the fights' branch is known: another branch can reuse the step number)
                string[] qs = kv.Key.Split('|');
                string fb = fights[0].Branch;
                if (fights.Exists(f => f.Branch != fb))
                    fb = null;
                foreach (Step s in steps)
                {
                    if (s.Kind != "Grind" || s.Kills == null || s.Gate == null || fights.Contains(s))
                        continue;
                    Match d = Regex.Match(s.Gate, @"IsBranchStepComplete\((0x[0-9A-F]+), (\d+), (\d+)\)");
                    if (d.Success && d.Groups[1].Value == qs[0] && d.Groups[3].Value == qs[1] && (fb == null || d.Groups[2].Value == fb) &&
                        s.Kills.Exists(k => names.Contains(k.Name)))
                        fights.Add(s);
                }
                if (fights.Count < 2)
                    continue;
                var all = new List<Kill>();
                foreach (Step f in fights)
                    foreach (Kill k in f.Kills)
                        if (!all.Exists(a => a.Name == k.Name && a.Pos.DistanceTo(k.Pos) < 0.5f))
                            all.Add(k);
                // only from the same part of the map, near where this fight's own mobs were (within ~150 m: Hyperbiology spots are 80-100 m apart)
                foreach (Step f in fights)
                {
                    List<Kill> own = new List<Kill>(f.Kills);
                    foreach (Kill k in all)
                        if (!f.Kills.Exists(a => a.Name == k.Name && a.Pos.DistanceTo(k.Pos) < 0.5f) &&
                            own.Exists(o => o.Leg == k.Leg && o.Pos.DistanceTo(k.Pos) <= 15f))
                            f.Kills.Add(k);
                }
            }
        }

        /// <summary>
        /// A counter you fed from more than one kind of thing (Section X Hyperbiology "Collect Biological Samples 0/10":
        /// Mutated Beast Eggs, Mutated Beast Bones AND looting Muckwings). One step per tick made the bot walk your exact
        /// spots one by one, past eggs it could have taken. Now it's one collect step until the count is full: click any of
        /// those objects nearby (the spots you clicked plus the same objects the recorder saw around you), and kill and
        /// loot those mobs on the way. A counter fed by one kind of thing stays exactly as you did it.
        /// </summary>
        private static void CollectSteps(List<Step> steps, Dictionary<string, ulong> maxFor, Dictionary<string, List<Vec3>> scanner)
        {
            var byTask = new Dictionary<string, List<Step>>(StringComparer.Ordinal);
            // the branches each counter was on: another branch of the same quest can use the same step and task numbers for
            // something else - those are kept apart (a counter seen on one branch only is grouped as before)
            var branchesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int pass = 0; pass < 2; pass++)
                foreach (Step s in steps)
                {
                    if (s.Gate == null || (s.Kind != "Use" && !(s.Kind == "Grind" && s.Comment.IndexOf("(kill and loot)", StringComparison.Ordinal) >= 0)))
                        continue;
                    Match m = Regex.Match(s.Gate, @"IsTaskValueGreaterThan\((0x[0-9A-F]+), (\d+), (\d+), \d+\)");
                    if (!m.Success)
                        continue;
                    string key = m.Groups[1].Value + ", " + m.Groups[2].Value + ", " + m.Groups[3].Value;
                    List<string> brs;
                    if (!branchesOf.TryGetValue(key, out brs))
                        branchesOf[key] = brs = new List<string>();
                    if (pass == 0)
                    {
                        if (s.Branch != null && !brs.Contains(s.Branch))
                            brs.Add(s.Branch);
                        continue;
                    }
                    if (brs.Count > 1)
                        key += "#" + (s.Branch ?? "?");
                    List<Step> list;
                    if (!byTask.TryGetValue(key, out list))
                        byTask[key] = list = new List<Step>();
                    list.Add(s);
                }
            foreach (KeyValuePair<string, List<Step>> kv0 in byTask)
            {
                List<Step> parts = kv0.Value;
                int cut = kv0.Key.IndexOf('#');
                var kv = new KeyValuePair<string, List<Step>>(cut >= 0 ? kv0.Key.Substring(0, cut) : kv0.Key, parts);
                string[] k3 = kv.Key.Split(new[] { ", " }, StringSplitOptions.None);
                // the branch it was on: from the ticks, else from the parts' own object lines (null = not known)
                string br = null;
                foreach (Step s in parts)
                    if (s.Branch != null)
                    {
                        br = s.Branch;
                        break;
                    }
                if (br == null)
                    foreach (Step s in parts)
                    {
                        string ul = s.Lines.Find(x => x.StartsWith("<UseObject", StringComparison.Ordinal) && Attr(x, "QuestId") == k3[0] &&
                                                      Attr(x, "StepId") == k3[1] && Attr(x, "BranchId").Length > 0);
                        if (ul != null)
                        {
                            br = Attr(ul, "BranchId");
                            break;
                        }
                    }
                // the step that finished it ("step done"), if it was one of these too (on the same branch)
                Step finish = null;
                foreach (Step s in steps)
                {
                    if (s.Gate == null || parts.Contains(s))
                        continue;
                    Match d = Regex.Match(s.Gate, @"IsBranchStepComplete\((0x[0-9A-F]+), (\d+), (\d+)\)");
                    if (d.Success && d.Groups[1].Value == k3[0] && d.Groups[3].Value == k3[1] && (br == null || d.Groups[2].Value == br) &&
                        (s.Kind == "Use" || (s.Kind == "Grind" && s.Comment.IndexOf("(kill and loot)", StringComparison.Ordinal) >= 0)))
                    {
                        finish = s;
                        break;
                    }
                }
                // A "step done" click is this counter's last tick only while the counter still had room (the 10th egg after 9/10).
                // A counter already full (Shadow Spawn "Rune of Freedom 1/1") isn't fed by it - that click was another task's.
                if (finish != null)
                {
                    int reached = 0;
                    foreach (Step s in parts)
                    {
                        Match g = Regex.Match(s.Gate, @"IsTaskValueGreaterThan\(0x[0-9A-F]+, \d+, \d+, (\d+)\)");
                        int n;
                        if (g.Success && int.TryParse(g.Groups[1].Value, out n) && n + 1 > reached)
                            reached = n + 1;
                    }
                    ulong full;
                    if ((maxFor.TryGetValue(kv0.Key, out full) || maxFor.TryGetValue(kv.Key, out full)) && (ulong)reached >= full)
                        finish = null;
                }
                var all = new List<Step>(parts);
                if (finish != null)
                    all.Add(finish);
                // what you did for it: object names and their spots, mobs you looted
                var objects = new List<string>();
                var spots = new List<KeyValuePair<string, Vec3>>();
                var mobs = new List<Kill>();
                foreach (Step s in all)
                {
                    foreach (string l in s.Lines)
                    {
                        Match o = Regex.Match(l, "^<UseObject ObjectName=\"([^\"]+)\"[^>]*XYZ=\"([^\"]+)\"");
                        if (!o.Success)
                            continue;
                        string name = o.Groups[1].Value;
                        if (!objects.Contains(name))
                            objects.Add(name);
                        foreach (Match h in Regex.Matches(l, "XYZ=\"(-?[0-9.]+),(-?[0-9.]+),(-?[0-9.]+)\""))
                        {
                            var v = new Vec3(float.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture), float.Parse(h.Groups[2].Value, CultureInfo.InvariantCulture),
                                             float.Parse(h.Groups[3].Value, CultureInfo.InvariantCulture));
                            if (!spots.Exists(p => p.Value.DistanceTo(v) < 0.3f))
                                spots.Add(new KeyValuePair<string, Vec3>(name, v));
                        }
                    }
                    if (s.Kind == "Grind" && s.Kills != null)
                        foreach (Kill k in s.Kills)
                            if (!mobs.Exists(a => a.Name == k.Name && a.Pos.DistanceTo(k.Pos) < 0.5f))
                                mobs.Add(k);
                }
                // only a counter fed in more than one way: two kinds of objects, or objects and loots
                if (objects.Count == 0 || (objects.Count < 2 && mobs.Count == 0))
                    continue;
                // the same objects the recorder saw around you (the scanner), near your own spots (~150 m)
                var own = new List<Vec3>();
                foreach (KeyValuePair<string, Vec3> p in spots)
                    own.Add(p.Value);
                foreach (Kill k in mobs)
                    own.Add(k.Pos);
                foreach (string name in objects)
                {
                    List<Vec3> seen;
                    if (scanner == null || !scanner.TryGetValue(name, out seen))
                        continue;
                    foreach (Vec3 v in seen)
                        if (own.Exists(o => o.DistanceTo(v) <= 15f) && !spots.Exists(p => p.Value.DistanceTo(v) < 0.3f))
                            spots.Add(new KeyValuePair<string, Vec3>(name, v));
                }
                // One hotspot per cluster (BuddyCron's UseObject sorts the hotspots by distance from where it starts and
                // loops them, taking the nearest object within the step's Radius of the hotspot it's heading to): every
                // spot within ~60 m of a cluster's first spot is that cluster; its hotspot is the real spot nearest the
                // cluster's middle. A short loop, each cluster once, and the 150 m Radius takes every object around it.
                if (spots.Count > 1)
                {
                    var left = new List<KeyValuePair<string, Vec3>>(spots);
                    var hubs = new List<KeyValuePair<string, Vec3>>();
                    while (left.Count > 0)
                    {
                        Vec3 seed = left[0].Value;
                        string kind = left[0].Key; // one cluster per object kind, so eggs and bones each keep a spot
                        var group = left.FindAll(p => p.Key == kind && p.Value.DistanceTo(seed) <= 6f);
                        left.RemoveAll(p => p.Key == kind && p.Value.DistanceTo(seed) <= 6f);
                        float mx = 0f, my = 0f, mz = 0f;
                        foreach (KeyValuePair<string, Vec3> p in group)
                        {
                            mx += p.Value.X;
                            my += p.Value.Y;
                            mz += p.Value.Z;
                        }
                        var mid = new Vec3(mx / group.Count, my / group.Count, mz / group.Count);
                        KeyValuePair<string, Vec3> hub = group[0];
                        foreach (KeyValuePair<string, Vec3> p in group)
                            if (p.Value.DistanceTo(mid) < hub.Value.DistanceTo(mid))
                                hub = p;
                        hubs.Add(hub);
                    }
                    spots = hubs;
                }
                ulong max;
                string gate = finish != null ? finish.Gate
                            : maxFor.TryGetValue(kv0.Key, out max) || maxFor.TryGetValue(kv.Key, out max) ? "HasQuest(" + k3[0] + ") and not IsTaskValueGreaterThan(" + kv.Key + ", " + (max - 1) + ")" : parts[parts.Count - 1].Gate;
                string done = gate.StartsWith("HasQuest(", StringComparison.Ordinal) ? gate : "HasQuest(" + k3[0] + ") and " + gate;
                Step first = parts[0];
                var c = new Step
                {
                    Kind = mobs.Count > 0 ? "Grind" : "Use",
                    Gate = done,
                    Comment = Regex.Replace(first.Comment, @" \(\d+/\d+\).*$", string.Empty) + " - collect: any " + string.Join(", any ", objects.ToArray()) +
                              (mobs.Count > 0 ? ", and kill and loot " + string.Join(", ", mobs.ConvertAll(k => k.Name).FindAll(n => true).ToArray()) : string.Empty),
                    Quest = first.Quest,
                    Task = first.Task,
                    Targets = first.Targets,
                    Kills = mobs.Count > 0 ? mobs : null,
                    At = first.At,
                    Branch = br
                };
                // dedupe mob names in the comment
                if (mobs.Count > 0)
                {
                    var names = new List<string>();
                    foreach (Kill k in mobs)
                        if (!names.Contains(k.Name))
                            names.Add(k.Name);
                    c.Comment = Regex.Replace(first.Comment, @" \(\d+/\d+\).*$", string.Empty) + " - collect: any " + string.Join(", any ", objects.ToArray()) +
                                ", and kill and loot " + string.Join(", ", names.ToArray());
                }
                string ids = finish != null
                    ? Regex.Replace(Regex.Match(finish.Gate, @"IsBranchStepComplete\((0x[0-9A-F]+), (\d+), (\d+)\)").Value, @"IsBranchStepComplete\((0x[0-9A-F]+), (\d+), (\d+)\)", " QuestId=\"$1\" BranchId=\"$2\" StepId=\"$3\"")
                    : " QuestId=\"" + k3[0] + "\" BranchId=\"" + (br != null && br != "0" ? br : "1") + "\" StepId=\"" + k3[1] + "\" TaskId=\"" + k3[2] + "\"";
                var sb = new StringBuilder();
                sb.Append("<UseObject").Append(ids).Append(" Radius=\"150\" UseRange=\"5\" WaitTime=\"3000\" BlacklistDuration=\"30\" IgnoreLOS=\"True\"><Placeables>");
                foreach (string name in objects)
                    sb.Append("<Placeable>").Append(name).Append("</Placeable>");
                sb.Append("</Placeables><Hotspots>");
                foreach (KeyValuePair<string, Vec3> p in spots)
                    sb.Append("<Hotspot Name=\"").Append(p.Key).Append("\" XYZ=\"").Append(X(p.Value)).Append("\" />");
                sb.Append("</Hotspots></UseObject>");
                c.Lines.Add(sb.ToString());
                int at = steps.IndexOf(first);
                foreach (Step s in all)
                    steps.Remove(s);
                steps.Insert(Math.Min(at, steps.Count), c);
            }
        }

        /// <summary>
        /// UseItem's Range for an item used on an NPC: how close you stood when you used it (BuddyCron reads Range in tenths
        /// of a game unit). Section X Shackle Remote: you stood 4-5 m away, the fixed 15 m was "TooFar". Unknown: 15 m.
        /// </summary>
        /// <summary>
        /// How long you held this spot (within ~20 m) right before t, in seconds, and whether it was a defend: at least two
        /// mobs came and died there and you did nothing else (no loot, click, talk, item or cast on an object) - the step
        /// then moved on by itself (waves, "hold the position").
        /// </summary>
        private static double StayedAt(IList<RecEvent> events, double t, Vec3 pos, out bool fought)
        {
            fought = false;
            double from = t;
            int kills = 0;
            bool acted = false;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                RecEvent x = events[i];
                if (x.Elapsed > t)
                    continue;
                if (t - x.Elapsed > 600)
                    break;
                if (!x.Pos.IsZero && x.Pos.DistanceTo(pos) > 2f)
                    break; // you were somewhere else
                if (x.Kind == EvKind.Loading || x.Kind == EvKind.AreaChange)
                    break;
                // the step moving on because of another mission's step: not this spot's doing
                if (x.Elapsed < t && (x.Kind == EvKind.QuestTask || x.Kind == EvKind.QuestStep || x.Kind == EvKind.QuestCompleted))
                    break;
                from = x.Elapsed;
                if (x.Kind == EvKind.Kill)
                    kills++;
                if (x.Kind == EvKind.Loot || x.Kind == EvKind.ObjectClick || x.Kind == EvKind.NpcClick || x.Kind == EvKind.CastStart ||
                    x.Kind == EvKind.ConvoStart || x.Kind == EvKind.QuestItem || (x.Kind == EvKind.Cast && x.Target != null))
                    acted = true;
            }
            fought = kills >= 2 && !acted;
            return t - from;
        }

        /// <summary>
        /// How long you waited at this spot (within ~10 m) right before t with nothing done there, in seconds: since you got
        /// there (your footsteps), or since the last click, cast, item, loot, talk, kill or mission moving, if that came later.
        /// </summary>
        private static double WaitedAt(IList<RecEvent> events, double t, Vec3 pos)
        {
            double from = t;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                RecEvent x = events[i];
                if (x.Elapsed >= t)
                    continue;
                if (t - x.Elapsed > 900)
                    break;
                if (x.Kind == EvKind.Loading || x.Kind == EvKind.AreaChange || x.Kind == EvKind.ElevatorRide || x.Kind == EvKind.TaxiEnd ||
                    x.Kind == EvKind.Died || x.Kind == EvKind.Revived)
                    break;
                if (!x.Pos.IsZero && x.Pos.DistanceTo(pos) > 1f)
                    break; // you were somewhere else
                if (x.Kind == EvKind.ObjectClick || x.Kind == EvKind.NpcClick || x.Kind == EvKind.CastStart || x.Kind == EvKind.Cast ||
                    x.Kind == EvKind.QuestItem || x.Kind == EvKind.Loot || x.Kind == EvKind.ConvoStart || x.Kind == EvKind.ConvoEnd ||
                    x.Kind == EvKind.ConvoChoice || x.Kind == EvKind.Kill || x.Kind == EvKind.QuestTask || x.Kind == EvKind.QuestStep ||
                    x.Kind == EvKind.QuestAdded || x.Kind == EvKind.QuestCompleted || x.Kind == EvKind.HolocomAnswered ||
                    x.Kind == EvKind.BoardOpen || x.Kind == EvKind.InteractionOpen || x.Kind == EvKind.Marker)
                {
                    from = x.Elapsed; // waited since you did that
                    break;
                }
                if (!x.Pos.IsZero)
                    from = x.Elapsed;
            }
            return t - from;
        }

        /// <summary>
        /// Wait at the spot while the step is still on, at most a bit longer than you waited: a few short waits, each only
        /// while the step hasn't moved on (never stuck there if it doesn't).
        /// </summary>
        private static List<string> WaitWhile(string gate, double waited)
        {
            int most = (int)Math.Ceiling(Math.Min(waited * 1.5 + 10, 600));
            int each = Math.Max(5, (most + 5) / 6);
            var l = new List<string>();
            for (int sofar = 0; sofar < most; sofar += each)
                l.Add("<If Condition=\"" + gate + "\"><WaitTimer QuestId=\"1\" WaitTime=\"" + each.ToString(CultureInfo.InvariantCulture) + "\" /></If>"); // (gates are written XML-escaped already)
            return l;
        }

        private static string UseRangeFor(float distUnits)
        {
            if (distUnits <= 0f)
                return "15"; // not known
            // exactly how far you stood, however far that was
            int r = (int)Math.Ceiling(distUnits * 10f - 0.01f); // (5.0000001 m is 5, not 6)
            return Math.Max(3, r).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Profile condition: the game has a reward waiting for this quest (its reward window is up).</summary>
        private static string RewardWaiting(string questHex)
        {
            return "any(r.QuestId == " + questHex + " for r in Me.Quests.PendingQuestRewards)";
        }

        /// <summary>The conversation that started at start was still on at t, or ended at most 8 s before.</summary>
        private static bool ConvoOnAt(List<Convo> convos, double start, double t)
        {
            Convo c = convos.FindLast(v => v.Start == start && v.Partner != HoloPartner);
            return c == null || t <= c.End + 8;
        }

        /// <summary>The partner of a holocom call in the reply list (never a real name).</summary>
        private const string HoloPartner = "\u0001holocom";

        /// <summary>
        /// A conversation (not a holocom call) that started at start was still on at t, or ended at most 8 s before, and began at
        /// most 5 minutes before: a long story talk counts until it ends. Not seen as a conversation with someone: 20 s from its start.
        /// </summary>
        private static bool SceneOn(List<Convo> convos, double start, double t)
        {
            if (start < -1e8 || t < start)
                return false;
            Convo c = convos.FindLast(v => v.Start == start && v.Partner != HoloPartner);
            return c != null ? t - start <= 300 && t <= c.End + 8 : t - start <= 20;
        }

        /// <summary>A holocom call that started at start was still on at t, or ended at most 8 s before (a long call with a cutscene).</summary>
        private static bool CallLasted(List<Convo> convos, double start, double t)
        {
            if (start < -1e8 || t < start)
                return false;
            Convo c = convos.FindLast(v => v.Start == start && v.Partner == HoloPartner);
            return c != null && t - start <= 300 && t <= c.End + 8;
        }

        /// <summary>
        /// When the last kill did what moved at t: t itself, or the start of a scene that began by itself within 3 s of that
        /// kill and was still on (or had just ended) at t - a boss that talks once beaten.
        /// </summary>
        private static double KillMoment(List<Kill> kills, double sceneAt, List<Convo> convos, double t)
        {
            if (kills.Count == 0 || t - kills[kills.Count - 1].At <= 3)
                return t;
            double k = kills[kills.Count - 1].At;
            return sceneAt >= k && sceneAt - k <= 3 && SceneOn(convos, sceneAt, t) ? sceneAt : t;
        }

        /// <summary>The game's own bookkeeping quests, not missions: companion conversation cooldowns ([DNT] ...), companion
        /// conversation quests, and quests whose name is only a raw token ("[taxi_quest_...]").</summary>
        public static bool IsSystemQuest(RecEvent e)
        {
            string n = e.QuestName ?? string.Empty, f = e.QuestFqn ?? string.Empty;
            return n.StartsWith("[DNT]", StringComparison.OrdinalIgnoreCase) || n.IndexOf("Conversation Cooldown", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (f.StartsWith("qst.companion.", StringComparison.OrdinalIgnoreCase) && f.IndexOf(".conversations.", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   Regex.IsMatch(n, @"^\[[a-z0-9_.]+\]$");
        }

        // The tags the game puts in front of a mission's name, in each client language (English, German, French):
        // [DAILY] [TAEGLICH] [QUOTIDIENNE] [JOURNALIERE], [WEEKLY] [WOECHENTLICH] [HEBDOMADAIRE], [HEROIC 2+] [HELDEN 2+] [HEROIQUE 2+]
        // (written with their accents below: the source stays plain ASCII).
        private const string RepeatTag = @"^\[(WEEKLY|DAILY|HEROIC|AREA|W\u00D6CHENTLICH|T\u00C4GLICH|HELDEN|HELDENHAFT|HEBDOMADAIRE|QUOTIDIENNE|JOURNALI\u00C8RE|H\u00C9RO\u00CFQUE)\b";
        private const string WeeklyTag = @"^\[(WEEKLY|W\u00D6CHENTLICH|HEBDOMADAIRE)\b";
        private const string HeroicTag = @"^\[(HEROIC|HELDEN|HELDENHAFT|H\u00C9RO\u00CFQUE)\b";

        /// <summary>A mission the game gives again (daily, weekly, heroic, area): its tag in any client language, or a daily
        /// area's daily by its internal name (the same in every language).</summary>
        public static bool IsRepeatable(RecEvent e)
        {
            string f = e.QuestFqn ?? string.Empty;
            return Regex.IsMatch(e.QuestName ?? string.Empty, RepeatTag, RegexOptions.IgnoreCase) ||
                   (f.StartsWith("qst.daily_area.", StringComparison.OrdinalIgnoreCase) && f.IndexOf(".daily_quest", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>A weekly, by its tag in any client language.</summary>
        public static bool IsWeekly(string questName)
        {
            return Regex.IsMatch(questName ?? string.Empty, WeeklyTag, RegexOptions.IgnoreCase);
        }

        /// <summary>A heroic, by its tag in any client language.</summary>
        public static bool IsHeroic(string questName)
        {
            return Regex.IsMatch(questName ?? string.Empty, HeroicTag, RegexOptions.IgnoreCase);
        }

        /// <summary>A dynamic encounter (7.6): the game starts it by itself when you come by, nobody gives it.</summary>
        private static bool IsEncounter(RecEvent e)
        {
            return (e.QuestFqn ?? string.Empty).StartsWith("qst.dynamic_events.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"quest|step|task" of a count.</summary>
        private static string TaskKey(RecEvent e)
        {
            return Fmt.Hex(e.QuestId) + "|" + e.Step.ToString(CultureInfo.InvariantCulture) + "|" + Math.Max(0, e.TaskIndex).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The counts that move only because another mission ended (a weekly's "complete N dailies"): every tick of that task
        /// came with another mission's completion (within 2 s before to 5 s after - the game can note the end a few looks
        /// late), and it ticked at least twice that way or it is a weekly. By what happened, not by the text, so it works in
        /// every client language.
        /// </summary>
        internal static HashSet<string> MetaTasks(IList<RecEvent> events)
        {
            var ends = new List<RecEvent>();
            var hidden = new HashSet<ulong>();
            int count = events.Count; // (index loops: the live list can grow while this runs)
            for (int i = 0; i < count; i++)
            {
                RecEvent e = events[i];
                if (e.QuestHidden && e.QuestId != 0)
                    hidden.Add(e.QuestId);
                if ((e.Kind == EvKind.QuestCompleted || e.Kind == EvKind.QuestRemoved) && e.QuestId != 0 && !IsSystemQuest(e))
                    ends.Add(e);
            }
            var ticks = new Dictionary<string, int>(StringComparer.Ordinal);
            var weekly = new HashSet<string>(StringComparer.Ordinal);
            var other = new HashSet<string>(StringComparer.Ordinal); // ticked at least once with no other mission ending
            if (ends.Count == 0)
                return other;
            for (int i = 0; i < count; i++)
            {
                RecEvent e = events[i];
                if (e.Kind != EvKind.QuestTask || e.Max == 0 || e.QuestId == 0 || e.QuestHidden)
                    continue;
                string key = TaskKey(e);
                bool withEnd = ends.Exists(x => x.QuestId != e.QuestId && !hidden.Contains(x.QuestId) && x.Elapsed - e.Elapsed >= -2.0 && x.Elapsed - e.Elapsed <= 5.0);
                if (!withEnd)
                {
                    other.Add(key);
                    continue;
                }
                int n;
                ticks.TryGetValue(key, out n);
                ticks[key] = n + 1;
                if (IsWeekly(e.QuestName))
                    weekly.Add(key);
            }
            var meta = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in ticks)
                if (!other.Contains(kv.Key) && (kv.Value >= 2 || weekly.Contains(kv.Key)))
                    meta.Add(kv.Key);
            return meta;
        }

        /// <summary>A count that moved only because another mission ended (see <see cref="MetaTasks"/>; the English
        /// "Complete ..." text too, for recordings with too few ticks to tell).</summary>
        internal static bool IsMetaTick(RecEvent e, HashSet<string> meta)
        {
            return e.Kind == EvKind.QuestTask &&
                   (meta.Contains(TaskKey(e)) || (e.Text ?? string.Empty).StartsWith("Complete ", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The bot, on the profile's side, would pick every reply you picked in this conversation (all of them seen).</summary>
        private static bool BotPicksSame(Convo c, string side)
        {
            for (int i = 0; i < c.Picks.Count; i++)
                if (c.Picks[i] == "?" || i >= c.Wheels.Count || AutoPick(c.Wheels[i], side) != c.Picks[i])
                    return false;
            return true;
        }

        /// <summary>A reward window for this quest came up within 5 s after this time.</summary>
        private static bool RewardSoon(IList<RecEvent> events, ulong quest, double t)
        {
            foreach (RecEvent x in events)
                if (x.Kind == EvKind.RewardWindow && x.QuestId == quest && x.Elapsed >= t && x.Elapsed - t <= 5)
                    return true;
            return false;
        }

        /// <summary>Another (not hidden) mission moved within 2 s of this time, after your click and not from a kill.</summary>
        private static bool AnotherTickNear(IList<RecEvent> events, HashSet<ulong> hidden, ulong quest, double t, double clickAt)
        {
            foreach (RecEvent x in events)
            {
                if ((x.Kind != EvKind.QuestTask && x.Kind != EvKind.QuestStep) || x.QuestId == quest || x.QuestHidden || hidden.Contains(x.QuestId) ||
                    Math.Abs(x.Elapsed - t) > 2.0 || x.Elapsed < clickAt)
                    continue;
                bool byKill = false;
                foreach (RecEvent k in events)
                    if (k.Kind == EvKind.Kill && k.Elapsed <= x.Elapsed && x.Elapsed - k.Elapsed <= 1.0)
                        byKill = true;
                if (!byKill)
                    return true;
            }
            return false;
        }

        /// <summary>A normal (not hidden) mission moved within 2 s of this time.</summary>
        private static bool NormalTickNear(IList<RecEvent> events, HashSet<ulong> hidden, double t)
        {
            foreach (RecEvent x in events)
                if ((x.Kind == EvKind.QuestTask || x.Kind == EvKind.QuestStep) && !x.QuestHidden && !hidden.Contains(x.QuestId) &&
                    Math.Abs(x.Elapsed - t) <= 2.0)
                    return true;
            return false;
        }

        /// <summary>Another (not hidden) mission ended from 2 s before to 5 s after this time (as in <see cref="MetaTasks"/>).</summary>
        private static bool OtherEndNear(IList<RecEvent> events, HashSet<ulong> hidden, ulong quest, double t)
        {
            foreach (RecEvent x in events)
                if ((x.Kind == EvKind.QuestCompleted || x.Kind == EvKind.QuestRemoved) && x.QuestId != 0 && x.QuestId != quest &&
                    !hidden.Contains(x.QuestId) && !IsSystemQuest(x) && x.Elapsed - t >= -2.0 && x.Elapsed - t <= 5.0)
                    return true;
            return false;
        }

        /// <summary>You killed an NPC of this name from just before this time up to 2 minutes after (it fought you).</summary>
        private static bool KilledSoonAfter(IList<RecEvent> events, string name, double t)
        {
            foreach (RecEvent ke in events)
                if (ke.Kind == EvKind.Kill && ke.Target != null && ke.Target.Name == name && ke.Elapsed >= t - 1 && ke.Elapsed <= t + 120)
                    return true;
            return false;
        }

        /// <summary>
        /// A mission item you used out of a fight up to 20 s before this fight started, near where its last mob died, when
        /// that mob wasn't around (seen or killed near there) in the 5 minutes before you used it: the item brought the fight
        /// out (a lure, a challenge totem, a beacon). Null when there is none.
        /// </summary>
        private static Use PreFightItem(List<Use> order, List<Kill> fight, List<Kill> mobSeen, List<Kill> allKills, double fightAt)
        {
            if (order == null || fight == null || fight.Count == 0)
                return null;
            Kill boss = fight[fight.Count - 1];
            for (int i = order.Count - 1; i >= 0; i--)
            {
                Use u = order[i];
                if (u.InFight || u.Bag || u.Pos.IsZero || u.At > fightAt + 1 || fightAt - u.At > 20 || boss.Pos.DistanceTo(u.Pos) > 4f ||
                    u.Name.StartsWith("Heroic Transport", StringComparison.Ordinal) || IsTravelAbility(u.Name))
                    continue;
                bool around = false;
                foreach (List<Kill> seen in new[] { mobSeen, allKills })
                    foreach (Kill m in seen)
                        if (m.At < u.At && u.At - m.At <= 300 && (m.Name == boss.Name || (m.Spec != 0 && m.Spec == boss.Spec)) && m.Pos.DistanceTo(boss.Pos) <= 10f)
                            around = true;
                if (!around)
                    return u;
            }
            return null;
        }

        /// <summary>Walk to where you used that item, then use it once (the plain pass makes it a hook before the fight).</summary>
        private static List<string> PreFightLines(Use u, string q)
        {
            return new List<string>
            {
                "<MoveTo QuestId=\"" + q + "\" Name=\"Where you used " + A(u.Name) + "\" XYZ=\"" + X(u.Pos) + "\" />",
                "<UseMissionItemOnce ItemName=\"" + A(u.Name) + "\"" +
                (u.On != null ? " Npc=\"" + A(u.On) + "\" Range=\"" + UseRangeFor(u.OnDist) + "\"" + (u.OnDead ? " DeadOnly=\"True\"" : string.Empty) : string.Empty) +
                " Before=\"Fight\" />"
            };
        }

        private static IList<RecEvent> ExactActions(IList<RecEvent> events, HashSet<string> meta)
        {
            var killed = new HashSet<ulong>();
            var list = new List<RecEvent>(events.Count);
            // A usable bag item (not a mission item - a stim, a medpac): only part of the run when a quest moves
            // within 6 s of it, even the game's hidden helper quests (Black Hole Glow: the stim that keeps you alive).
            // A use that didn't tick, followed within 2 minutes by a use of the same item that did, is part of that search
            // (Black Hole Radiation Sensor: 5 scans, 3 found a spy - the other 2 spots are where the bot looks too).
            // A stim used in a fight after it did its job stays out.
            var movedAt = new List<KeyValuePair<string, double>>(); // a use that moved a quest itself
            var searchAt = new List<KeyValuePair<string, double>>(); // ... and not just a kill counting (a medpac mid-fight)
            var helpers = new HashSet<ulong>(); // the game's own helper quests (marked hidden at least once)
            foreach (RecEvent h in events)
                if (h.QuestHidden && h.QuestId != 0)
                    helpers.Add(h.QuestId);
            for (int i = 0; i < events.Count; i++)
            {
                RecEvent e = events[i];
                if (e.Kind != EvKind.QuestItem || e.Note != "bag item" || e.Option.Length == 0)
                    continue;
                for (int j = i + 1; j < events.Count && events[j].Elapsed - e.Elapsed <= 6.0; j++)
                    if (events[j].Kind == EvKind.QuestTask || events[j].Kind == EvKind.QuestStep || events[j].Kind == EvKind.QuestAdded)
                    {
                        // a count your kill moved isn't the medpac's doing (a medpac pressed mid-fight right before a kill)
                        bool byKill = false;
                        for (int k = j - 1; k >= 0 && events[j].Elapsed - events[k].Elapsed <= 1.0; k--)
                            if (events[k].Kind == EvKind.Kill)
                                byKill = true;
                        if (!byKill)
                        {
                            movedAt.Add(new KeyValuePair<string, double>(e.Option, e.Elapsed));
                            searchAt.Add(new KeyValuePair<string, double>(e.Option, e.Elapsed));
                        }
                        break;
                    }
            }
            for (int i = 0; i < events.Count; i++)
            {
                RecEvent e = events[i];
                if (e.Kind == EvKind.Kill && e.Target != null && e.Target.NodeId != 0)
                    killed.Add(e.Target.NodeId);
                if (e.Kind == EvKind.QuestItem && e.Note == "bag item" &&
                    !movedAt.Exists(m => m.Key == e.Option && m.Value == e.Elapsed) &&
                    !searchAt.Exists(m => m.Key == e.Option && m.Value > e.Elapsed && m.Value - e.Elapsed <= 120.0))
                    continue; // a medpac in a fight: not a step
                // A mission item that left your mission items (you used your last one, or the game took it away):
                // - a use: a mission step or count moved with it (Section X Egg Sample at the Science Station, the Serum
                //   Injector on the Hyperbeast), or the game's hidden helper quest moved right after (the Glow injector)
                // - taken away: a mission completed or left the log at that same moment (the Hyperbeast Tissue, the last
                //   Shackle Remote, Republic Research) - not a step
                if (e.Kind == EvKind.QuestItem && e.Note == "item gone")
                {
                    bool moved = false, finished = false;
                    for (int j = 0; j < events.Count; j++)
                    {
                        RecEvent x = events[j];
                        double dt = x.Elapsed - e.Elapsed;
                        if (dt < -0.3 || dt > 6.0)
                            continue;
                        if ((x.Kind == EvKind.QuestStep || x.Kind == EvKind.QuestTask) && x.QuestHidden && dt >= 0)
                            moved = true; // the helper quest (Glow injector)
                        else if ((x.Kind == EvKind.QuestStep || x.Kind == EvKind.QuestTask) && !x.QuestHidden && dt <= 4.0 && !IsMetaTick(x, meta))
                            moved = true; // a mission step or count moved with it
                        else if ((x.Kind == EvKind.QuestCompleted || x.Kind == EvKind.QuestRemoved) && !helpers.Contains(x.QuestId) && dt <= 0.3)
                            finished = true; // a mission ended right then: the game took the item away
                    }
                    // something you did just before explains the step (a loot, a click, a kill): that's the step, and the game
                    // took the item away with it (Section X: looting the Hyperbeast took the Serum Injector; the Science
                    // Station click used up the Egg Sample)
                    // (also a turn-in conversation, and a kill or object cast in the same look - those come after the item
                    // in the event list; a kill only counts right at the item, a fight a few seconds earlier doesn't)
                    for (int j = 0; j < events.Count; j++)
                    {
                        if (j == i)
                            continue;
                        double dt = events[j].Elapsed - e.Elapsed;
                        if (dt < -5.0 || dt > 1.0)
                            continue;
                        EvKind k = events[j].Kind;
                        bool before = dt <= 0;
                        if ((before && (k == EvKind.Loot || k == EvKind.ObjectClick || k == EvKind.NpcClick || k == EvKind.ConvoStart ||
                                        k == EvKind.ConvoChoice || k == EvKind.ConvoEnd || k == EvKind.InteractionOpen)) ||
                            (k == EvKind.Kill && dt >= -1.0) ||
                            ((k == EvKind.Cast || k == EvKind.CastStart) && events[j].Target != null && events[j].Target.Kind == ObjKind.Placeable))
                            finished = true;
                    }
                    if (!moved || finished)
                        continue;
                }
                if (e.Kind == EvKind.NpcClick && e.Target != null && (e.Target.Dead || (e.Target.NodeId != 0 && killed.Contains(e.Target.NodeId))))
                {
                    e = e.As(EvKind.Loot);
                }
                else if (e.Kind == EvKind.CastStart)
                {
                    bool clicked = false;
                    for (int j = list.Count - 1; j >= 0 && e.Elapsed - list[j].Elapsed <= 3; j--)
                    {
                        RecEvent c = list[j];
                        if ((c.Kind == EvKind.ObjectClick || c.Kind == EvKind.Command) && c.Target != null && e.Target != null &&
                            (c.Target.NodeId == e.Target.NodeId || c.Target.Name == e.Target.Name))
                        {
                            clicked = true;
                            break;
                        }
                    }
                    if (clicked)
                        continue;
                    e = e.As(EvKind.ObjectClick); // the interact key, a cast you started without a click: the use starts here
                }
                // You selected an object (left-click) and a cast started right after with nothing tied to it:
                // you used / attacked that exact object (CZ-198 Give And Take: Biotoxin 5/6).
                if (e.Kind == EvKind.Command && e.Target != null && e.Target.Kind == ObjKind.Placeable && e.Target.Name.Length > 0 &&
                    !e.Target.Name.StartsWith("#", StringComparison.Ordinal))
                {
                    bool asUse = false;
                    for (int j = i + 1; j < events.Count && events[j].Elapsed - e.Elapsed <= 15.0; j++)
                    {
                        RecEvent c = events[j];
                        // a cast that STARTED within 2 s of the select (a cast is written when it ends: "Opening" on the
                        // 4th Hypermatter Supplies crate ended 3 s later)
                        double started = c.Kind == EvKind.Cast ? c.Elapsed - c.DurationMs / 1000.0 : c.Elapsed;
                        if ((c.Kind == EvKind.Cast || c.Kind == EvKind.CastStart) && started - e.Elapsed <= 2.0 &&
                            (c.Target == null || c.Target.Name.Length == 0 || c.Target.NodeId == e.Target.NodeId))
                        {
                            asUse = true;
                            break;
                        }
                        if (c.Elapsed - e.Elapsed > 2.0 && c.Kind != EvKind.Cast && c.Kind != EvKind.QuestTask && c.Kind != EvKind.QuestStep && c.Kind != EvKind.Breadcrumb)
                            break;
                        if (c.Kind == EvKind.ObjectClick || c.Kind == EvKind.Command)
                            break;
                    }
                    // ...or the quest counted within 4 s of you selecting it, with nothing else used in between (you
                    // pressed the interact key on it: Black Hole Hypermatter Supplies 4/5 had only this)
                    if (!asUse)
                        for (int j = i + 1; j < events.Count && events[j].Elapsed - e.Elapsed <= 4.0; j++)
                        {
                            RecEvent c = events[j];
                            if (c.Kind == EvKind.ObjectClick || c.Kind == EvKind.CastStart || c.Kind == EvKind.Cast || c.Kind == EvKind.QuestItem || c.Kind == EvKind.Command)
                                break;
                            if ((c.Kind == EvKind.QuestTask || c.Kind == EvKind.QuestStep) && !c.QuestHidden)
                            {
                                asUse = true;
                                break;
                            }
                        }
                    if (asUse)
                        list.Add(e.As(EvKind.ObjectClick));
                }
                list.Add(e);
            }
            return list;
        }

        /// <summary>"Already used" is about one object: its game id, or its spot when the id isn't known.</summary>
        private static string UseKey(ulong id, Vec3 pos)
        {
            return id != 0 ? "#" + id.ToString("X") : X(pos);
        }

        /// <summary>An object you used for a step in the last minute that shows no mission marker: it is done, and not who you
        /// talked to, handed in to or got a mission from.</summary>
        private static bool UsedUp(ObjRef o, Dictionary<string, double> consumed, double t)
        {
            if (o == null || o.Kind != ObjKind.Placeable || o.TakesTurnIn || o.OffersQuest || (o.NodeId == 0 && !o.HasPos))
                return false;
            double when;
            return consumed.TryGetValue(UseKey(o.NodeId, o.Pos), out when) && t - when <= 60;
        }

        /// <summary>The NPC a step names ("Speak to Tookreek", "Return to Lieutenant Harkun"), or null.</summary>
        private static string NpcFromStepText(string text)
        {
            Match m = Regex.Match(text ?? string.Empty, @"^\s*(?:Speak|Talk|Return)\s+(?:with|to)\s+(?:the\s+)?(.+?)\s*$", RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
        }

        /// <summary>The start of the note on a kill another player tagged first.</summary>
        private const string TaggedNote = "tagged by another player";

        /// <summary>Your own companion (you talked to them, they aren't a quest NPC the bot walks to).</summary>
        private static bool IsCompanion(ObjRef o)
        {
            return o != null && string.Equals(o.Toughness, "companion", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Kill
        {
            public string Name;
            public Vec3 Pos;
            public double At;
            public int Leg; // the part of the map (between lifts / taxis / loading screens) you killed it in
            public HashSet<string> Credit = new HashSet<string>(StringComparer.Ordinal); // "quest|task" counts this kill moved
            public ulong Spec; // its type id (what quest tasks list), 0 = not known
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
            public List<ulong> Targets; // what the game counts for this step's task (null = not known)
            public List<Kill> Kills;
            /// <summary>Other quests the same kills counted for: the fight goes on until they're done too.</summary>
            public Dictionary<ulong, string> Also;
            /// <summary>Other tasks of this same quest the same kills counted for (task -> its gate): the fight goes on until they're done too.</summary>
            public Dictionary<int, string> AlsoTasks;
            /// <summary>The quest branch this step's tick was on (null = not known).</summary>
            public string Branch;
            /// <summary>When the step was made (recording time).</summary>
            public double At = -1e9;
            /// <summary>More about how the step was found: shown in the recorder window only, never in the profile.</summary>
            public string Note = string.Empty;
            /// <summary>The recorder isn't sure about this step (yellow in the review list).</summary>
            public bool Unsure;
            /// <summary>Short name for its fight area (null = made from the label).</summary>
            public string Area;
            /// <summary>A door route's own "not through yet" test (null = none).</summary>
            public string Side;
            /// <summary>A weekly's pick-up: taken from its board whenever it's offered (the game can still call a done weekly complete).</summary>
            public bool Weekly;
            /// <summary>A pick-up from a mission board / terminal (the game said the mission came from its board).</summary>
            public bool Board;
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
                                                l.StartsWith("<PickUpQuest", StringComparison.Ordinal) || l.StartsWith("<PickUpIfOffered", StringComparison.Ordinal) ||
                                                l.IndexOf("<AnswerHolocomCall", StringComparison.Ordinal) >= 0);
                if (li < 0)
                    continue;
                string line = s.Lines[li];
                // a holocom call has nobody to talk to: its replies are kept only when they are all one side's (TempConvoSkip, the
                // way the hand-made profiles answer calls)
                bool call = line.IndexOf("<AnswerHolocomCall", StringComparison.Ordinal) >= 0; // (also one inside an If)
                string npc = line.StartsWith("<TalkTo", StringComparison.Ordinal) ? Attr(line, "Name") : Attr(line, line.StartsWith("<TurnInQuest", StringComparison.Ordinal) ? "TurnInName" : "GiverName");
                if (npc.Length == 0)
                    npc = Attr(line, "Name");
                // (the line holds the name XML-escaped, the conversation the game's own name)
                string who = call ? HoloPartner : System.Net.WebUtility.HtmlDecode(npc);
                Convo c = convos.FindLast(v => v.Partner == who && v.Start <= s.At + 1 && s.At - v.End <= 30);
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
                    AddNote(s, "your " + type.ToLowerInvariant() + " replies");
                }
                else if (call)
                {
                    if (!asBot || sides.Count > 1)
                        AddNote(s, "your holocom replies (not kept, the bot answers by its side): " + string.Join(", ", c.Picks.ToArray()));
                }
                else if (!asBot || sides.Count > 1)
                {
                    var nums = c.Picks.ConvertAll(p => p.Substring(0, p.IndexOf(' ')));
                    string q = Attr(line, "QuestId");
                    s.Lines[li] = "<TalkAndReply QuestId=\"" + (q.Length > 0 ? q : "1") + "\" Name=\"" + npc + "\" Replies=\"" + string.Join(",", nums.ToArray()) +
                                  "\" XYZ=\"" + Attr(line, "XYZ") + "\" />";
                    if (!line.StartsWith("<TalkTo", StringComparison.Ordinal))
                        s.Lines.Insert(li + 1, line); // still pick up / hand in afterwards if the talk didn't do it
                    AddNote(s, "your replies: " + string.Join(", ", c.Picks.ToArray()));
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

        /// <summary>Turn to the way you faced before a special-bar ability (Macrobinoculars scans). Off until BuddyCron's
        /// turn call in FaceHeadingCode is checked in game: a wrong call stops the profile's code from compiling.</summary>
        public static bool FaceRecordedHeading = false;

        /// <summary>Pick your own reply-wheel numbers (the ones the bot wouldn't pick by itself) with a BuddyCron Hook while
        /// BuddyCron's own TalkTo / PickUpQuest / TurnInQuest talks. Off until it is checked in game that the Hook runs during
        /// BuddyCron's conversation and that ConvoOracle.SelectConversationOption from a Hook picks that wheel number.</summary>
        public static bool ReplayReplyPicks = false;

        /// <summary>A pick-up from a giver runs only while the mission log has room (it holds 25; the game's hidden helper
        /// quests left out of the count), so a full log skips it instead of BuddyCron's PickUpQuest trying forever. Off until
        /// it is checked in game that the profile condition can read Me.Quests.ActiveQuestStates and which missions count.</summary>
        public static bool PickUpOnlyWithRoom = false;
        private const string LogHasRoom = "25 > len([x for x in Me.Quests.ActiveQuestStates if not x.IsHidden])";

        /// <summary>A hand-in to an object with a same-name twin close by is pinned to that exact object (SearchRadius 0.001,
        /// as for object pick-ups). Off until it is checked in game that TurnInQuest compares SearchRadius with the distance
        /// squared like PickUpQuest does; off, every object hand-in keeps SearchRadius 20.</summary>
        public static bool TurnInPinnedTwin = false;

        /// <summary>The one BuddyCron call that turns the character to a heading in degrees (MovementManager.Rotate takes degrees).</summary>
        private const string FaceHeadingCode = "BuddyCron.Managers.MovementManager.Rotate(deg);";

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

        /// <summary>"Not through the door yet": the height when the door changes floors, else the door's own side, like a lift.</summary>
        private static string DoorSide(Vec3 from, Vec3 to)
        {
            if (Math.Abs(to.Y - from.Y) >= 2f)
                return string.Format(CultureInfo.InvariantCulture, "(Me.Position.Y {0} {1:0.##})", from.Y > to.Y ? "&gt;" : "&lt;", (from.Y + to.Y) / 2f);
            return SideOf(from, to);
        }

        private static string A(string s)
        {
            return Fmt.XmlAttr(s ?? string.Empty);
        }

        // ---- Plain BuddyCron: the profile uses only BuddyCron's own steps, so nobody needs this plugin to run it ----

        /// <summary>The &lt;CodeChunks&gt; the last plain pass made (BuddyCron's own code-in-profile), written into the header.</summary>
        [ThreadStatic] private static List<string> _codeChunks;

        /// <summary>A C# string literal from an XML-escaped attribute value.</summary>
        private static string CsString(string xmlValue)
        {
            string s = System.Net.WebUtility.HtmlDecode(xmlValue ?? string.Empty);
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Turns every step only Quest Recorder knows into BuddyCron's own:
        /// PickUpIfOffered -> PickUpQuest, TalkAndReply -> TalkTo, EquipItem -> UseInventoryItem, ClickObject -> MoveTo + Interact,
        /// one mission item -> UseItem with a condition, and a row of mission items or a special-bar quest ability -> a BuddyCron
        /// Hook (code inside the profile, like the CZ-198 Tentacle code) that waits for each cast.
        /// </summary>
        private static void PlainSteps(List<Step> steps)
        {
            _codeChunks = new List<string>();
            int hookNo = 0;
            bool faceAdded = false;
            foreach (Step s in steps)
            {
                var outLines = new List<string>();
                string lastSpot = null;
                string replyClose = null; // the reply Hook to take out after the pick-up / hand-in line that does the talking
                int replyCloseAt = -1;
                for (int li = 0; li < s.Lines.Count; li++)
                {
                    if (replyClose != null && li > replyCloseAt)
                    {
                        outLines.Add("<Hook Name=\"" + replyClose + "\" Operation=\"R\" Location=\"TreeStart\" />");
                        replyClose = null;
                    }
                    string l = s.Lines[li];
                    if (l.StartsWith("<MoveTo ", StringComparison.Ordinal))
                    {
                        string xyz = Attr(l, "XYZ");
                        if (xyz.Length > 0)
                            lastSpot = xyz;
                    }
                    if (l.StartsWith("<PickUpIfOffered ", StringComparison.Ordinal))
                    {
                        l = "<PickUpQuest " + l.Substring("<PickUpIfOffered ".Length);
                        l = Regex.Replace(l, " (Repeatable|Tries)=\"[^\"]*\"", string.Empty);
                    }
                    if (l.StartsWith("<PickUpQuest ", StringComparison.Ordinal))
                    {
                        // A mission board / terminal: BuddyCron's PickUpQuest asks the board for the mission forever when the
                        // board doesn't list it (done this week, used up, or not for this character), and skips a weekly the game
                        // still calls "complete" while the board offers it again. A BuddyCron Hook at the board takes it when the
                        // board lists it and otherwise moves straight on.
                        if (IsBoardPickUp(s, l))
                        {
                            List<string> board = BoardPickUp(l, ++hookNo, lastSpot);
                            if (board.Count > 0)
                            {
                                outLines.AddRange(board);
                                continue;
                            }
                        }
                        outLines.Add(l);
                        continue;
                    }
                    if (l.StartsWith("<TalkAndReply ", StringComparison.Ordinal))
                    {
                        // BuddyCron's own talk. For a pick-up or a hand-in the line right after it (put back there) does the
                        // talking: no second talk in front of it.
                        string q = Attr(l, "QuestId");
                        string after = li + 1 < s.Lines.Count ? s.Lines[li + 1] : string.Empty;
                        bool owned = ((after.StartsWith("<PickUpQuest ", StringComparison.Ordinal) || after.StartsWith("<PickUpIfOffered ", StringComparison.Ordinal)) && !IsBoardPickUp(s, after)) ||
                                     after.StartsWith("<TurnInQuest ", StringComparison.Ordinal);
                        string reply = ReplayReplyPicks ? ReplyHook(Attr(l, "Replies"), ++hookNo) : null;
                        if (reply != null)
                            outLines.Add("<Hook Name=\"" + reply + "\" Operation=\"I\" Index=\"0\" Location=\"TreeStart\" />");
                        if (!owned)
                        {
                            outLines.Add("<TalkTo QuestId=\"" + (q.Length > 0 ? q : "1") + "\" Name=\"" + Attr(l, "Name") + "\" WaitTime=\"5\" XYZ=\"" + Attr(l, "XYZ") + "\" />");
                            if (reply != null)
                                outLines.Add("<Hook Name=\"" + reply + "\" Operation=\"R\" Location=\"TreeStart\" />");
                        }
                        else if (reply != null)
                        {
                            replyClose = reply;
                            replyCloseAt = li + 1;
                        }
                        continue;
                    }
                    if (l.StartsWith("<EquipItem ", StringComparison.Ordinal))
                    {
                        Match gq = Regex.Match(s.Gate ?? string.Empty, @"HasQuest\((0x[0-9A-F]+)\)");
                        outLines.Add("<UseInventoryItem QuestId=\"" + (gq.Success ? gq.Groups[1].Value : Fmt.Hex(s.Quest)) + "\" ItemName=\"" + Attr(l, "ItemName") + "\" />");
                        continue;
                    }
                    if (l.StartsWith("<ClickObject ", StringComparison.Ordinal))
                    {
                        string n = Attr(l, "Name"), xyz = Attr(l, "XYZ"), stand = Attr(l, "Stand");
                        if (stand.Length > 0 && Attr(l, "Fight") == "True" && xyz.Split(',').Length == 3)
                        {
                            // before a fight: walk to where you stood, then up to the object until it can be clicked
                            outLines.AddRange(WalkUpClick(n, xyz, stand, ++hookNo));
                            lastSpot = stand;
                            continue;
                        }
                        // walk to where you stood when you clicked it (the object's own spot can be out of reach), then click it
                        Vec3 sv, ov;
                        string to = stand.Length > 0 && TryVec(stand, out sv) && TryVec(xyz, out ov) && sv.DistanceTo(ov) < 10f ? stand : xyz;
                        outLines.Add("<MoveTo QuestId=\"1\" Name=\"" + n + "\" XYZ=\"" + to + "\" />");
                        outLines.Add("<Interact Name=\"" + n + "\" XYZ=\"" + xyz + "\" />");
                        lastSpot = to;
                        continue;
                    }
                    bool once = l.StartsWith("<UseMissionItemOnce ", StringComparison.Ordinal);
                    bool ability = l.StartsWith("<UseQuestAbility ", StringComparison.Ordinal);
                    if (!once && !ability)
                    {
                        outLines.Add(l);
                        continue;
                    }
                    // where the bot uses it: the MoveTo right before it (the spot you used it at)
                    string baseMove = li > 0 && s.Lines[li - 1].StartsWith("<MoveTo ", StringComparison.Ordinal) && Attr(s.Lines[li - 1], "XYZ").Length > 0
                                      ? s.Lines[li - 1] : null;
                    // a run: this line and the item / ability uses right after it. The MoveTos between them (a row you used at
                    // two places) are walked by BuddyCron while the hook waits; a MoveTo after the last use stays as it is.
                    var run = new List<string> { l };
                    var runAt = new List<string> { null }; // the spot each one was used at (null: the spot before the run)
                    var walks = new List<string>();
                    var pending = new List<string>();
                    string usedAt = null;
                    int lj = li + 1, lastUse = li;
                    while (lj < s.Lines.Count)
                    {
                        string n = s.Lines[lj];
                        if (n.StartsWith("<UseMissionItemOnce ", StringComparison.Ordinal))
                        {
                            foreach (string pm in pending)
                                if (Attr(pm, "XYZ").Length > 0)
                                {
                                    walks.Add(pm);
                                    usedAt = Attr(pm, "XYZ");
                                }
                            pending.Clear();
                            run.Add(n);
                            runAt.Add(usedAt);
                            lastUse = lj;
                        }
                        else if (n.StartsWith("<MoveTo ", StringComparison.Ordinal))
                            pending.Add(n);
                        else
                            break;
                        lj++;
                    }
                    li = lastUse;
                    // a special-bar ability: walk to where you used it first (the hook casts where the bot stands)
                    string abilitySpot = ability ? Attr(l, "XYZ") : string.Empty;
                    if (abilitySpot.Length > 0)
                    {
                        string am = "<MoveTo QuestId=\"1\" Name=\"Where you used " + Attr(l, "AbilityName") + "\" XYZ=\"" + abilitySpot + "\" />";
                        if (abilitySpot != lastSpot)
                            outLines.Add(am);
                        lastSpot = abilitySpot;
                        baseMove = am;
                    }
                    // used before a fight it brings out (a lure, a totem): once, then the fight
                    bool beforeFight = Attr(l, "Before") == "Fight";
                    bool simple = run.Count == 1 && once && !beforeFight && Attr(l, "Npc").Length == 0 && Attr(l, "AbilityName").Length == 0;
                    if (simple)
                    {
                        // one mission item on yourself, once: BuddyCron's UseItem until the step moves on (Vny's Heroic Transport way)
                        // (in your mission items or your bags - a stim is a bag item - like the Heroic Transport check)
                        string item = Attr(l, "ItemName");
                        string py = Fmt.PyStr(System.Net.WebUtility.HtmlDecode(item));
                        string has = A("(any(i.Name == " + py + " for i in Me.QuestItems) or any(i.Name == " + py + " for i in Me.Backpack))");
                        string cond = has + (s.Gate != null && s.Gate.Length > 0 ? " and (" + s.Gate + ")" : string.Empty);
                        outLines.Add("<UseItem ItemName=\"" + item + "\" Condition=\"" + cond + "\" WaitTime=\"3000\" Range=\"100000\"><Hotspots><Hotspot Name=\"Where you used it\" XYZ=\"" +
                                     (lastSpot ?? "0,0,0") + "\" /></Hotspots></UseItem>");
                        continue;
                    }
                    if (walks.Count > 0)
                        lastSpot = usedAt;
                    // a row (runes in order), an item on an NPC, or a quest ability: a BuddyCron Hook that does them in order,
                    // each after the cast before it has finished, and tries the row again if the step didn't move on
                    hookNo++;
                    string hook = "UseInOrder" + hookNo, v = "_r" + hookNo;
                    var names = new List<string>();
                    var npcs = new List<string>();
                    var abil = new List<string>();
                    var opens = new List<string>();
                    var heads = new List<string>(); // the way you faced when you used it (special-bar abilities)
                    var deads = new List<string>(); // used on a corpse
                    var fights = new List<string>(); // may be used in a fight (you did, or the NPC fought you right after)
                    var ranges = new List<string>(); // how close you stood to the NPC (game units)
                    var hps = new List<string>(); // used on it once its health was down to this (0 = any)
                    var hasAt = new List<string>();
                    var spots = new List<string>(); // where you used it
                    bool face = false;
                    for (int ri = 0; ri < run.Count; ri++)
                    {
                        string r = run[ri];
                        float hd;
                        if (FaceRecordedHeading && Attr(r, "AbilityName").Length > 0 && float.TryParse(Attr(r, "Heading"), NumberStyles.Float, CultureInfo.InvariantCulture, out hd))
                        {
                            heads.Add(hd.ToString("0.#", CultureInfo.InvariantCulture) + "f");
                            face = true;
                        }
                        else
                            heads.Add("float.NaN");
                        string an = Attr(r, "AbilityName");
                        bool isAb = an.Length > 0;
                        names.Add(CsString(isAb ? an : Attr(r, "ItemName")));
                        abil.Add(isAb ? "true" : "false");
                        string npc = Attr(r, "Npc");
                        if (npc.Length == 0)
                        {
                            Match nm = Regex.Match(r, "<Npc>([^<]+)</Npc>");
                            npc = nm.Success ? nm.Groups[1].Value : string.Empty;
                        }
                        npcs.Add(CsString(npc));
                        opens.Add(CsString(Attr(r, "OpenWith")));
                        deads.Add(Attr(r, "DeadOnly") == "True" ? "true" : "false");
                        // (an ability line written without InCombat is used in a fight or not, as before)
                        bool fightOk = Attr(r, "InCombat") == "True" || (r.StartsWith("<UseQuestAbility ", StringComparison.Ordinal) && Attr(r, "InCombat").Length == 0);
                        fights.Add(fightOk ? "true" : "false");
                        float rm;
                        float ru = float.TryParse(Attr(r, "Range"), NumberStyles.Float, CultureInfo.InvariantCulture, out rm) && rm > 0f ? (rm >= 1f ? rm / 10f : rm) : 1.5f;
                        ranges.Add(ru.ToString("0.###", CultureInfo.InvariantCulture) + "f");
                        int hp;
                        hps.Add(int.TryParse(Attr(r, "HealthPercent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hp) && hp > 0 && hp < 100
                                ? hp.ToString(CultureInfo.InvariantCulture) + "f" : "0f");
                        string spotXyz = runAt[ri] ?? (baseMove != null ? Attr(baseMove, "XYZ") : null);
                        Vec3 wv;
                        if (spotXyz != null && TryVec(spotXyz, out wv) && !float.IsNaN(wv.X + wv.Y + wv.Z) && !float.IsInfinity(wv.X + wv.Y + wv.Z))
                        {
                            hasAt.Add("true");
                            spots.Add(string.Format(CultureInfo.InvariantCulture, "new System.Numerics.Vector3({0:0.###}f, {1:0.###}f, {2:0.###}f)", wv.X, wv.Y, wv.Z));
                        }
                        else
                        {
                            hasAt.Add("false");
                            spots.Add("new System.Numerics.Vector3(0f, 0f, 0f)");
                        }
                    }
                    string nl = "\r\n";
                    if (face && !faceAdded)
                    {
                        _codeChunks.Add("\t\t<CodeChunk Name=\"FaceHeading\" Type=\"Definition\"><![CDATA[" + nl +
                                        "\t\t\tstatic void FaceHeading(float deg) { " + FaceHeadingCode + " }" + nl +
                                        "\t\t]]></CodeChunk>");
                        faceAdded = true;
                    }
                    _codeChunks.Add("\t\t<CodeChunk Name=\"" + hook + "State\" Type=\"Definition\"><![CDATA[" + nl +
                                    "\t\t\tstatic int " + v + "_i, " + v + "_t, " + v + "_wi = -1;" + nl +
                                    "\t\t\tstatic bool " + v + "_p, " + v + "_s, " + v + "_ok, " + v + "_w;" + nl +
                                    "\t\t\tstatic System.DateTime " + v + "_next, " + v + "_last, " + v + "_at, " + v + "_done, " + v + "_ws, " + v + "_tw;" + nl +
                                    "\t\t]]></CodeChunk>");
                    string mover = "BuddyCron.Navigation.Navigator.PlayerMover";
                    // with the step's gate the row is tried again 8 s after it ends, until the step moves on; without one (or
                    // before a fight) it is done once while the profile waits
                    bool looped = s.Gate != null && s.Gate.Length > 0 && !beforeFight;
                    int wait = run.Count * 6 + 4;
                    string again = (looped ? 8 : wait + 5).ToString(CultureInfo.InvariantCulture);
                    string body =
                        "var names = new[] { " + string.Join(", ", names) + " };" + nl +
                        "var npcs = new[] { " + string.Join(", ", npcs) + " };" + nl +
                        "var isAbility = new[] { " + string.Join(", ", abil) + " };" + nl +
                        "var openWith = new[] { " + string.Join(", ", opens) + " };" + nl +
                        "var deadOnly = new[] { " + string.Join(", ", deads) + " };" + nl +
                        "var inFight = new[] { " + string.Join(", ", fights) + " };" + nl +
                        "var useRange = new[] { " + string.Join(", ", ranges) + " };" + nl +
                        "var healthAt = new[] { " + string.Join(", ", hps) + " };" + nl +
                        "var hasSpot = new[] { " + string.Join(", ", hasAt) + " };" + nl +
                        "var spots = new[] { " + string.Join(", ", spots) + " };" + nl +
                        (face ? "var headings = new[] { " + string.Join(", ", heads) + " };" + nl : string.Empty) +
                        "var me = BuddyCron.Core.Player;" + nl +
                        "var now = System.DateTime.Now;" + nl +
                        "if ((now - " + v + "_last).TotalSeconds > 60) { " + v + "_i = 0; " + v + "_t = 0; " + v + "_p = false; " + v + "_s = false; " + v + "_wi = -1; " + v + "_next = now; }" + nl +
                        v + "_last = now;" + nl +
                        "if (" + v + "_i >= names.Length) { if ((now - " + v + "_done).TotalSeconds > " + again + ") { " + v + "_i = 0; " + v + "_t = 0; " + v + "_p = false; " + v + "_s = false; " + v + "_wi = -1; " + v + "_next = now; } return false; }" + nl +
                        "if (me.IsCasting) { if (" + v + "_p) " + v + "_s = true; return true; }" + nl +
                        "if (" + v + "_p && " + v + "_s) { " + v + "_i++; " + v + "_t = 0; " + v + "_p = false; " + v + "_s = false; " + v + "_next = now.AddSeconds(1.5); if (" + v + "_i >= names.Length) " + v + "_done = now; return true; }" + nl +
                        "if (now < " + v + "_next) return true;" + nl +
                        "if (" + v + "_p && (now - " + v + "_at).TotalSeconds < 1.5) return true;" + nl +
                        // pressed and taken, and no cast bar came: an instant use, done (not pressed 4 times)
                        "if (" + v + "_p && " + v + "_ok) { " + v + "_i++; " + v + "_t = 0; " + v + "_p = false; " + v + "_next = now.AddSeconds(1.5); if (" + v + "_i >= names.Length) " + v + "_done = now; return true; }" + nl +
                        // dead, or in a fight this use wasn't part of: BuddyCron's own death handling and combat routine run first
                        // (and the walk-back wait starts over once alive and out of the fight: a long fight or corpse run never
                        // uses it where the bot happens to stand)
                        "if (me.IsDead || (me.InCombat && !inFight[" + v + "_i])) { if (" + v + "_w) { " + mover + ".MoveStop(); " + v + "_w = false; } " + v + "_p = false; " + v + "_wi = -1; return false; }" + nl +
                        "if (" + v + "_t >= 4) { " + v + "_i++; " + v + "_t = 0; " + v + "_p = false; " + v + "_next = now.AddSeconds(1.5); if (" + v + "_i >= names.Length) " + v + "_done = now; return true; }" + nl +
                        "if (" + v + "_wi != " + v + "_i) { " + v + "_wi = " + v + "_i; " + v + "_ws = now; " + v + "_tw = System.DateTime.MinValue; }" + nl +
                        // away from where you used it (after a death, or the next place of the row): the profile's own MoveTo walks there
                        "if (hasSpot[" + v + "_i] && " + v + "_tw == System.DateTime.MinValue && (now - " + v + "_ws).TotalSeconds < 120 && System.Numerics.Vector3.Distance(me.Location, spots[" + v + "_i]) > 1.5f) { " + v + "_w = true; return false; }" + nl +
                        "BuddyCron.Objects.HeroCharacter target = null;" + nl +
                        "if (npcs[" + v + "_i].Length > 0)" + nl +
                        "    foreach (var c in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroCharacter>())" + nl +
                        "        if (c != null && c.IsValid && c.Name == npcs[" + v + "_i] && c.IsDead == deadOnly[" + v + "_i] && (!hasSpot[" + v + "_i] || System.Numerics.Vector3.Distance(c.Location, spots[" + v + "_i]) < 10f) && (target == null || c.Distance < target.Distance)) target = c;" + nl +
                        // an item for an NPC that isn't here: not used on yourself (tried again, then skipped)
                        "if (target == null && npcs[" + v + "_i].Length > 0 && !isAbility[" + v + "_i]) { " + v + "_t++; " + v + "_next = now.AddSeconds(1.5); return true; }" + nl +
                        // "weaken it, then use it": in the fight, the combat routine fights it down to where you used it first
                        "if (target != null && me.InCombat && healthAt[" + v + "_i] > 0f && target.HealthPercent > healthAt[" + v + "_i]) { " + v + "_p = false; return false; }" + nl +
                        "if (target != null && target.Distance > useRange[" + v + "_i])" + nl +
                        "{" + nl +
                        "    if (" + v + "_tw == System.DateTime.MinValue) " + v + "_tw = now;" + nl +
                        "    if ((now - " + v + "_tw).TotalSeconds < 20) { " + v + "_w = true; " + mover + ".MoveTowards(target.Location); return true; }" + nl +
                        "}" + nl +
                        mover + ".MoveStop();" + nl +
                        v + "_w = false;" + nl +
                        v + "_ok = false;" + nl +
                        "if (isAbility[" + v + "_i])" + nl +
                        "{" + nl +
                        "    if (openWith[" + v + "_i].Length > 0 && !BuddyCron.Managers.AbilityManager.HasAbility(names[" + v + "_i]))" + nl +
                        "    {" + nl +
                        "        var opener = System.Linq.Enumerable.FirstOrDefault(BuddyCron.Core.Me.QuestItems, i => i.Name == openWith[" + v + "_i])" + nl +
                        "                ?? System.Linq.Enumerable.FirstOrDefault(BuddyCron.Core.Me.Backpack, i => i.Name == openWith[" + v + "_i]);" + nl +
                        "        if (opener != null) opener.Use();" + nl +
                        "        " + v + "_t++; " + v + "_next = now.AddSeconds(3); return true;" + nl +
                        "    }" + nl +
                        (face ? "    if (target == null && !float.IsNaN(headings[" + v + "_i])) FaceHeading(headings[" + v + "_i]);" + nl : string.Empty) +
                        "    " + v + "_ok = BuddyCron.Managers.AbilityManager.Cast(names[" + v + "_i], target ?? (BuddyCron.Objects.HeroCharacter)me, false).Success;" + nl +
                        "}" + nl +
                        "else" + nl +
                        "{" + nl +
                        "    var item = System.Linq.Enumerable.FirstOrDefault(BuddyCron.Core.Me.QuestItems, i => i.Name == names[" + v + "_i])" + nl +
                        "            ?? System.Linq.Enumerable.FirstOrDefault(BuddyCron.Core.Me.Backpack, i => i.Name == names[" + v + "_i]);" + nl +
                        "    if (item == null) { " + v + "_i++; " + v + "_t = 0; " + v + "_p = false; " + v + "_s = false; " + v + "_next = now.AddSeconds(1.5); if (" + v + "_i >= names.Length) " + v + "_done = now; return true; }" + nl +
                        "    try { var ab = item.Ability; " + v + "_ok = ab != null && ab.CastingTime <= 0f && ab.ChannelingTime <= 0f; } catch { " + v + "_ok = false; }" + nl +
                        "    if (target != null) item.UseAbility(target); else item.Use();" + nl +
                        "}" + nl +
                        v + "_p = true; " + v + "_s = false; " + v + "_at = now; " + v + "_t++;" + nl +
                        "return true;";
                    var sbh = new StringBuilder();
                    sbh.Append("\t\t<CodeChunk Name=\"").Append(hook).Append("\" Type=\"Hook\"><![CDATA[").Append(nl);
                    foreach (string bl in body.Split(new[] { nl }, StringSplitOptions.None))
                        sbh.Append("\t\t\t").Append(bl).Append(nl);
                    sbh.Append("\t\t]]></CodeChunk>");
                    _codeChunks.Add(sbh.ToString());
                    outLines.Add("<Hook Name=\"" + hook + "\" Operation=\"I\" Index=\"0\" Location=\"TreeStart\" />");
                    if (looped)
                    {
                        // until the step moves on; each round the bot walks back to where you used them when it is away (after a
                        // death) and through the row's places, the hook waiting while BuddyCron walks
                        var back = new StringBuilder();
                        if (baseMove != null)
                            back.Append(WhenAway(baseMove));
                        foreach (string w in walks)
                            back.Append(WhenAway(w));
                        outLines.Add("<While Condition=\"" + s.Gate + "\">" + back.ToString() + "<WaitTimer QuestId=\"1\" WaitTime=\"3\" /></While>");
                    }
                    else
                    {
                        // once: back to where you used it when the bot is away from there, then the row's other places
                        if (baseMove != null)
                            outLines.Add(WhenAway(baseMove));
                        outLines.AddRange(walks);
                        outLines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"" + wait.ToString(CultureInfo.InvariantCulture) + "\" />");
                    }
                    outLines.Add("<Hook Name=\"" + hook + "\" Operation=\"R\" Location=\"TreeStart\" />");
                }
                if (replyClose != null)
                    outLines.Add("<Hook Name=\"" + replyClose + "\" Operation=\"R\" Location=\"TreeStart\" />");
                s.Lines.Clear();
                s.Lines.AddRange(outLines);
            }
        }

        /// <summary>
        /// A MoveTo that only walks when the bot is more than 10 m from its spot (the hook waits from 15 m), so a round at the
        /// spot asks BuddyCron for no path.
        /// </summary>
        private static string WhenAway(string moveTo)
        {
            Vec3 p;
            if (!TryVec(Attr(moveTo, "XYZ"), out p))
                return moveTo;
            return Fmt.Format("<If Condition=\"(((Me.Position.X - ({0})) ** 2 + (Me.Position.Y - ({1})) ** 2 + (Me.Position.Z - ({2})) ** 2) &gt; 1)\">",
                              Fmt.F3(p.X), Fmt.F3(p.Y), Fmt.F3(p.Z)) + moveTo + "</If>";
        }

        /// <summary>
        /// A pick-up from a mission board / terminal: an object giver (it has a SearchRadius) that the game said gave it from its
        /// board, or one named like a board (Mission / Terminal) as the weekly-board rule below.
        /// </summary>
        private static bool IsBoardPickUp(Step s, string pickupLine)
        {
            if (Attr(pickupLine, "SearchRadius").Length == 0 && Attr(pickupLine, "GiverType") != "Placeable")
                return false; // an NPC giver: BuddyCron's PickUpQuest talks to it
            if (s.Board)
                return true;
            string gn = Attr(pickupLine, "GiverName");
            return gn.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) >= 0 || gn.IndexOf("Mission", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Steps (and code chunks) that take a mission from its board only when the board lists it: walk to the board, open it,
        /// read its list after a second, take the mission if it's there, and move on when it isn't (or after 20 s).
        /// </summary>
        private static List<string> BoardPickUp(string pickupLine, int no, string standXyz)
        {
            string id = Attr(pickupLine, "QuestId"), giver = Attr(pickupLine, "GiverName"), xyz = Attr(pickupLine, "XYZ");
            var lines = new List<string>();
            string[] p = xyz.Split(',');
            if (id.Length == 0 || giver.Length == 0 || p.Length != 3)
                return lines;
            string hook = "TakeFromBoard" + no, v = "_w" + no;
            string nl = "\r\n";
            string body =
                "ulong id = " + id + "UL;" + nl +
                "var spot = new System.Numerics.Vector3(" + p[0].Trim() + "f, " + p[1].Trim() + "f, " + p[2].Trim() + "f);" + nl +
                "var me = BuddyCron.Core.Player;" + nl +
                "var now = System.DateTime.Now;" + nl +
                "if ((now - " + v + "_last).TotalSeconds > 30) { " + v + "_start = now; " + v + "_open = System.DateTime.MinValue; " + v + "_done = false; }" + nl +
                v + "_last = now;" + nl +
                "if (" + v + "_done || System.Linq.Enumerable.Contains(BuddyCron.Core.Me.Quests.ActiveQuestIds, id)) return false;" + nl +
                "if ((now - " + v + "_start).TotalSeconds > 20) { " + v + "_done = true; return false; }" + nl +
                "if (me.MissionBoardOpen)" + nl +
                "{" + nl +
                "    if (" + v + "_open == System.DateTime.MinValue) " + v + "_open = now;" + nl +
                "    if ((now - " + v + "_open).TotalSeconds < 1) return true;" + nl +
                "    bool listed = false, onBoard = false, unknown = false;" + nl +
                "    foreach (var mq in me.MissionBoardQuests)" + nl +
                "    {" + nl +
                "        if (mq == null) continue;" + nl +
                "        ulong q = mq.QuestId;" + nl +
                "        if ((q >> 48) != 0xE000UL) { unknown = true; continue; }" + nl +
                "        listed = true;" + nl +
                "        if (q == id) onBoard = true;" + nl +
                "    }" + nl +
                "    if (listed && !onBoard && !unknown) { " + v + "_done = true; return false; }" + nl +
                "    if ((now - " + v + "_click).TotalSeconds >= 2) { BuddyCron.Objects.HeroGuiApiGfx.Instance.AcceptMission(id); " + v + "_click = now; }" + nl +
                "    return true;" + nl +
                "}" + nl +
                v + "_open = System.DateTime.MinValue;" + nl +
                "if ((now - " + v + "_click).TotalSeconds < 3) return true;" + nl +
                "BuddyCron.Objects.HeroObject best = null; bool bestMarked = false;" + nl +
                "foreach (var o in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroObject>())" + nl +
                "{" + nl +
                "    if (o == null || !o.IsValid || o.Name != " + CsString(giver) + " || System.Numerics.Vector3.DistanceSquared(o.Location, spot) > 9f) continue;" + nl +
                "    bool marked = o.QuestIndication.ToString() != \"None\";" + nl +
                "    if (best == null || (marked && !bestMarked) || (marked == bestMarked && System.Numerics.Vector3.DistanceSquared(o.Location, spot) < System.Numerics.Vector3.DistanceSquared(best.Location, spot))) { best = o; bestMarked = marked; }" + nl +
                "}" + nl +
                "if (best == null || !best.IsWithinInteractRange) { if ((now - " + v + "_start).TotalSeconds > 6) { " + v + "_done = true; return false; } return true; }" + nl +
                "best.Interact(); " + v + "_click = now;" + nl +
                "return true;";
            _codeChunks.Add("\t\t<CodeChunk Name=\"" + hook + "State\" Type=\"Definition\"><![CDATA[" + nl +
                            "\t\t\tstatic System.DateTime " + v + "_click, " + v + "_start, " + v + "_last, " + v + "_open;" + nl +
                            "\t\t\tstatic bool " + v + "_done;" + nl +
                            "\t\t]]></CodeChunk>");
            var sbh = new StringBuilder();
            sbh.Append("\t\t<CodeChunk Name=\"").Append(hook).Append("\" Type=\"Hook\"><![CDATA[").Append(nl);
            foreach (string bl in body.Split(new[] { nl }, StringSplitOptions.None))
                sbh.Append("\t\t\t").Append(bl).Append(nl);
            sbh.Append("\t\t]]></CodeChunk>");
            _codeChunks.Add(sbh.ToString());
            lines.Add("<MoveTo QuestId=\"1\" Name=\"" + giver + "\" XYZ=\"" + (string.IsNullOrEmpty(standXyz) ? xyz : standXyz) + "\" />");
            lines.Add("<Hook Name=\"" + hook + "\" Operation=\"I\" Index=\"0\" Location=\"TreeStart\" />");
            lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"1\" />");
            lines.Add("<Hook Name=\"" + hook + "\" Operation=\"R\" Location=\"TreeStart\" />");
            return lines;
        }

        /// <summary>
        /// A click on an object before a fight (the Field Generator that brings out the commander): walk to where you stood,
        /// then a BuddyCron Hook walks up to that object until it can be clicked and clicks it.
        /// </summary>
        private static List<string> WalkUpClick(string name, string xyz, string stand, int no)
        {
            var lines = new List<string>();
            string[] p = xyz.Split(',');
            string hook = "WalkUpClick" + no, v = "_c" + no;
            string nl = "\r\n";
            string body =
                "var spot = new System.Numerics.Vector3(" + p[0] + "f, " + p[1] + "f, " + p[2] + "f);" + nl +
                "if ((System.DateTime.Now - " + v + "_click).TotalSeconds < 3) return false;" + nl +
                "BuddyCron.Objects.HeroPlaceable best = null;" + nl +
                "foreach (var o in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroPlaceable>())" + nl +
                "    if (o != null && o.IsValid && o.Name == " + CsString(name) + " && o.IsUsable && System.Numerics.Vector3.DistanceSquared(o.Location, spot) <= 4f && (best == null || System.Numerics.Vector3.DistanceSquared(o.Location, spot) < System.Numerics.Vector3.DistanceSquared(best.Location, spot))) best = o;" + nl +
                "if (best == null) return false;" + nl +
                "if (!best.IsWithinInteractRange) { BuddyCron.Navigation.Navigator.PlayerMover.MoveTowards(best.Location); return true; }" + nl +
                "BuddyCron.Navigation.Navigator.PlayerMover.MoveStop(); best.Interact(); " + v + "_click = System.DateTime.Now;" + nl +
                "return false;";
            _codeChunks.Add("\t\t<CodeChunk Name=\"" + hook + "State\" Type=\"Definition\"><![CDATA[" + nl + "\t\t\tstatic System.DateTime " + v + "_click;" + nl + "\t\t]]></CodeChunk>");
            var sbh = new StringBuilder();
            sbh.Append("\t\t<CodeChunk Name=\"").Append(hook).Append("\" Type=\"Hook\"><![CDATA[").Append(nl);
            foreach (string bl in body.Split(new[] { nl }, StringSplitOptions.None))
                sbh.Append("\t\t\t").Append(bl).Append(nl);
            sbh.Append("\t\t]]></CodeChunk>");
            _codeChunks.Add(sbh.ToString());
            lines.Add("<MoveTo QuestId=\"1\" Name=\"" + name + "\" XYZ=\"" + stand + "\" />");
            lines.Add("<Hook Name=\"" + hook + "\" Operation=\"I\" Index=\"0\" Location=\"TreeStart\" />");
            lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"4\" />");
            lines.Add("<Hook Name=\"" + hook + "\" Operation=\"R\" Location=\"TreeStart\" />");
            return lines;
        }

        /// <summary>
        /// The code chunks of a BuddyCron Hook that picks your reply-wheel numbers in order ("2,1"), one per wheel, while
        /// BuddyCron's own talk runs (the calls of Quest Recorder's TalkAndReply). Wheels after the last one: BuddyCron answers
        /// them by the profile's side. Returns the Hook's name, or null when there is nothing to pick.
        /// </summary>
        private static string ReplyHook(string replies, int no)
        {
            var picks = new List<string>();
            foreach (string p in (replies ?? string.Empty).Split(','))
            {
                int n;
                if (int.TryParse(p.Trim(), out n) && n > 0)
                    picks.Add(n.ToString(CultureInfo.InvariantCulture));
            }
            if (picks.Count == 0)
                return null;
            string hook = "Reply" + no, v = "_a" + no;
            string nl = "\r\n";
            string body =
                "var picks = new[] { " + string.Join(", ", picks) + " };" + nl +
                "var now = System.DateTime.Now;" + nl +
                "var cnv = BuddyCron.Managers.ExtraNodeManager.ConvoOracle;" + nl +
                "if (cnv == null || !cnv.IsInConversation) { if ((now - " + v + "_seen).TotalSeconds > 3) { " + v + "_k = 0; " + v + "_node = -2; } return false; }" + nl +
                v + "_seen = now;" + nl +
                "if (" + v + "_k >= picks.Length || !cnv.IsWaitingForChoice || !cnv.ChoicesShown) return false;" + nl +
                "long node = System.Convert.ToInt64(cnv.CurrentNodeNumber);" + nl +
                "if (node == " + v + "_node) return (now - " + v + "_at).TotalSeconds < 2;" + nl +
                v + "_node = node;" + nl +
                v + "_at = now;" + nl +
                "cnv.SelectConversationOption(picks[" + v + "_k]);" + nl +
                v + "_k++;" + nl +
                "return true;";
            _codeChunks.Add("\t\t<CodeChunk Name=\"" + hook + "State\" Type=\"Definition\"><![CDATA[" + nl +
                            "\t\t\tstatic int " + v + "_k;" + nl +
                            "\t\t\tstatic long " + v + "_node = -2;" + nl +
                            "\t\t\tstatic System.DateTime " + v + "_seen, " + v + "_at;" + nl +
                            "\t\t]]></CodeChunk>");
            var sbh = new StringBuilder();
            sbh.Append("\t\t<CodeChunk Name=\"").Append(hook).Append("\" Type=\"Hook\"><![CDATA[").Append(nl);
            foreach (string bl in body.Split(new[] { nl }, StringSplitOptions.None))
                sbh.Append("\t\t\t").Append(bl).Append(nl);
            sbh.Append("\t\t]]></CodeChunk>");
            _codeChunks.Add(sbh.ToString());
            return hook;
        }

        /// <summary>The step names only Quest Recorder knows (a profile must have none of them).</summary>
        public static readonly string[] RecorderOnlySteps = { "PickUpIfOffered", "TalkAndReply", "UseMissionItemOnce", "ClickObject", "UseQuestAbility", "EquipItem" };

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
            HashSet<string> meta = MetaTasks(events);
            for (int i = 0; i < events.Count; i++)
            {
                RecEvent e = events[i];
                // a quest change nothing you did caused (no click, item, talk or kill just before): flag it
                if ((e.Kind == EvKind.QuestTask || e.Kind == EvKind.QuestStep) && !e.QuestHidden && e.QuestId != 0 && !explained.Contains(e) &&
                    !(e.QuestName.Length > 0 && char.IsLower(e.QuestName[0])) && // the game's own helper quests
                    !(e.Kind == EvKind.QuestTask && (e.Max == 0 || IsMetaTick(e, meta))))
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
                    case EvKind.Loot: what = "looted \"" + (e.Target != null ? e.Target.Name : "?") + "\""; break;
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
            List<string> problems;
            return Build(events, profileName, fileName, recordingName, out problems);
        }

        /// <summary>The profile, checked: anything that keeps it from loading and running on plain BuddyCron is in problems,
        /// and then the profile itself says it is not finished.</summary>
        public static string Build(IList<RecEvent> events, string profileName, string fileName, string recordingName, out List<string> problems)
        {
            string text = Build(events, profileName, fileName, recordingName, null);
            problems = PlainProblems(text);
            if (problems.Count > 0)
                text = MarkNotFinished(text, problems);
            return text;
        }

        /// <summary>
        /// What keeps a profile from running on plain BuddyCron: XML that doesn't load, a step only Quest Recorder knows,
        /// code of a type other than Hook / Definition, a Hook with no code, code with brackets that don't match.
        /// Empty when it's fine.
        /// </summary>
        public static List<string> PlainProblems(string profile)
        {
            var problems = new List<string>();
            var doc = new System.Xml.XmlDocument();
            try
            {
                doc.LoadXml(profile ?? string.Empty);
            }
            catch (System.Xml.XmlException ex)
            {
                problems.Add("the XML doesn't load (" + ex.Message + ")");
                return problems;
            }
            var only = new HashSet<string>(RecorderOnlySteps, StringComparer.Ordinal);
            var hooks = new HashSet<string>(StringComparer.Ordinal); // code chunks of type Hook
            var named = new List<string>(); // hooks the steps use
            foreach (System.Xml.XmlNode n in doc.SelectNodes("//*"))
            {
                var el = n as System.Xml.XmlElement;
                if (el == null)
                    continue;
                string tag = el.LocalName;
                string p = null;
                if (only.Contains(tag) || tag == "RunCode")
                    p = "a step only Quest Recorder knows (" + tag + ")";
                else if (tag == "CodeChunk")
                {
                    string type = el.GetAttribute("Type");
                    if (type == "Hook")
                        hooks.Add(el.GetAttribute("Name"));
                    else if (type != "Definition")
                        p = "code of type \"" + type + "\" (" + el.GetAttribute("Name") + ")";
                    if (p == null && !Balanced(el.InnerText))
                        p = "code with brackets that don't match (" + el.GetAttribute("Name") + ")";
                }
                else if (tag == "Hook" && !named.Contains(el.GetAttribute("Name")))
                    named.Add(el.GetAttribute("Name"));
                if (p != null && !problems.Contains(p))
                    problems.Add(p);
            }
            foreach (string h in named)
                if (!hooks.Contains(h))
                    problems.Add("a Hook with no code (" + h + ")");
            return problems;
        }

        /// <summary>Every ( { [ in the code is closed in order (string and char literals skipped).</summary>
        private static bool Balanced(string code)
        {
            var open = new Stack<char>();
            char quote = '\0';
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                        i++; // an escaped character
                    else if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                    quote = c;
                else if (c == '(' || c == '{' || c == '[')
                    open.Push(c);
                else if (c == ')' || c == '}' || c == ']')
                {
                    char want = c == ')' ? '(' : (c == '}' ? '{' : '[');
                    if (open.Count == 0 || open.Pop() != want)
                        return false;
                }
            }
            return open.Count == 0 && quote == '\0';
        }

        /// <summary>Marks a profile as not finished: its name, and a pop-up at the start that says what is wrong.</summary>
        public static string MarkNotFinished(string text, List<string> problems)
        {
            if (text == null || text.Contains("<!-- Not finished -->"))
                return text;
            int n = text.IndexOf("</Name>", StringComparison.Ordinal);
            if (n >= 0)
                text = text.Substring(0, n) + " (not finished)" + text.Substring(n);
            const string questing = "\t<Questing>\r\n";
            int at = text.IndexOf(questing, StringComparison.Ordinal);
            if (at >= 0)
            {
                at += questing.Length;
                text = text.Substring(0, at) + "\t\t<!-- Not finished -->\r\n\t\t<UserDialog Title=\"Quest Recorder\" Message=\"" +
                       A("Not finished: " + string.Join(", ", problems.ToArray())) + "\" />\r\n" + text.Substring(at);
            }
            return text;
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
                            else if ((l.StartsWith("<Interact", StringComparison.Ordinal) || l.StartsWith("<ClickObject", StringComparison.Ordinal)))
                                what = "click " + Attr(l, "Name") + " (it starts the fight), then " + what;
                        break;
                    }
                    case "Use":
                    {
                        var objs = new List<string>();
                        foreach (string l in s.Lines)
                            if (l.StartsWith("<UseObject", StringComparison.Ordinal))
                                objs.Add("use " + Attr(l, "ObjectName") + " at " + Attr(l, "XYZ"));
                            else if ((l.StartsWith("<Interact", StringComparison.Ordinal) || l.StartsWith("<ClickObject", StringComparison.Ordinal)))
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
                // the label is the profile's comment (the control panel finds the step by it); notes go after the arrow
                if (s.Note.Length > 0)
                    what += "  (" + s.Note.Trim() + ")";
                if (s.Unsure)
                    what = "?? " + what;
                trace.Add(n.ToString(CultureInfo.InvariantCulture) + ". " + s.Comment + "  ->  " + what);
            }
        }

        private static string Build(IList<RecEvent> events, string profileName, string fileName, string recordingName, List<string> trace)
        {
            // The shuttle can land you before its cast is noted ("jump while casting Heroic Transport"): put the cast
            // first, so the hop is seen as the mission item that moved you.
            // (in any client language: the jump came while casting it - not a travel ability, not a cast on an object
            // such as a door - and the cast, or the mission item named after it, is noted right after)
            var ordered = new List<RecEvent>(events);
            for (int i = 0; i + 1 < ordered.Count; i++)
            {
                RecEvent j = ordered[i];
                if (j.Kind != EvKind.Jump)
                    continue;
                string casting = (j.Note ?? string.Empty).StartsWith("while casting ", StringComparison.Ordinal) ? j.Note.Substring("while casting ".Length) : string.Empty;
                if (IsTravelAbility(casting))
                    casting = string.Empty;
                for (int k = i + 1; k < ordered.Count && ordered[k].Elapsed - j.Elapsed <= 3; k++)
                {
                    RecEvent c = ordered[k];
                    bool cast = casting.Length > 0 && c.Option == casting && c.Kind == EvKind.Cast && (c.Target == null || c.Target.Kind != ObjKind.Placeable);
                    bool itemOf = casting.Length > 0 && c.Kind == EvKind.QuestItem && c.Note.Length == 0 && c.Option.StartsWith(casting, StringComparison.Ordinal);
                    if ((c.Kind == EvKind.Cast && c.Option.StartsWith("Heroic Transport", StringComparison.Ordinal)) ||
                        (c.Kind == EvKind.QuestItem && c.Option.StartsWith("Heroic Transport", StringComparison.Ordinal)) || cast || itemOf)
                    {
                        ordered.RemoveAt(k);
                        ordered.Insert(i, c);
                        i++;
                        break;
                    }
                }
            }
            events = ordered;
            // the mission items that left your bags during the recording (before the filter below drops the ones that
            // weren't a use): a step that ticked right after you targeted an NPC may have been one of them used on it
            var goneItems = new List<RecEvent>();
            foreach (RecEvent ev in events)
                if (ev.Kind == EvKind.QuestItem && ev.Note == "item gone" && ev.Option.Length > 0)
                    goneItems.Add(ev);
            // the counts that only move because another mission ended (a weekly's "complete N dailies"), in any client language
            HashSet<string> metaTasks = MetaTasks(events);
            events = ExactActions(events, metaTasks);
            // Quest abilities (special / temporary bar) and items you put on: kept only when a mission moved soon after
            // (6 s for an ability, 20 s for an item you put on), so a new class ability or a gear swap never becomes a step.
            // Each ability remembers the mission item you used before it (Macrobinoculars opens the scan).
            var abilityNames = new HashSet<string>(StringComparer.Ordinal);
            var abilityOpen = new Dictionary<string, string>(StringComparer.Ordinal);
            var abilityHeading = new Dictionary<string, float>(StringComparer.Ordinal); // where you used an ability (or its name) -> the way you faced
            {
                var keep = new List<RecEvent>(events.Count);
                string lastItem = null;
                double lastItemAt = -1e9;
                for (int i = 0; i < events.Count; i++)
                {
                    RecEvent x = events[i];
                    bool ability = x.Kind == EvKind.QuestItem && x.Note == "ability";
                    bool equip = x.Kind == EvKind.QuestItem && x.Note == "equipped";
                    // (a mission item only: a medpac or stim from your bags never opens a bar)
                    if (x.Kind == EvKind.QuestItem && !ability && !equip && x.Note != "item gone" && x.Note != "bag item" && x.Option.Length > 0)
                    {
                        lastItem = x.Option;
                        lastItemAt = x.Elapsed;
                    }
                    if (ability || equip)
                    {
                        double win = ability ? 6 : 20;
                        bool moved = false;
                        for (int j = i + 1; j < events.Count && events[j].Elapsed - x.Elapsed <= win; j++)
                            if (events[j].Kind == EvKind.QuestTask || events[j].Kind == EvKind.QuestStep || events[j].Kind == EvKind.QuestCompleted)
                            {
                                moved = true;
                                break;
                            }
                        if (!moved)
                            continue;
                        if (ability)
                        {
                            abilityNames.Add(x.Option);
                            if (!abilityOpen.ContainsKey(x.Option) && lastItem != null && x.Elapsed - lastItemAt <= 120)
                                abilityOpen[x.Option] = lastItem;
                            if (!x.Pos.IsZero)
                                abilityHeading[X(x.Pos)] = x.Heading;
                            if (!abilityHeading.ContainsKey(x.Option))
                                abilityHeading[x.Option] = x.Heading;
                        }
                    }
                    keep.Add(x);
                }
                events = keep;
            }
            // PvP missions (warzones, starfighter) are left out: the bot can't play them
            {
                var pvp = new HashSet<ulong>();
                string clickName = string.Empty;
                double clickAt = -1e9;
                foreach (RecEvent ev in events)
                {
                    if ((ev.Kind == EvKind.ObjectClick || ev.Kind == EvKind.NpcClick) && ev.Target != null)
                    {
                        clickName = ev.Target.Name;
                        clickAt = ev.Elapsed;
                    }
                    if (ev.QuestId == 0 || pvp.Contains(ev.QuestId))
                        continue;
                    string text = (ev.QuestName ?? string.Empty) + "|" +
                                  (ev.Kind == EvKind.QuestAdded ? (ev.Target != null ? ev.Target.Name : string.Empty) + "|" + ev.Option + "|" + (ev.Elapsed - clickAt <= 10 ? clickName : string.Empty) : string.Empty);
                    if (Regex.IsMatch(text, @"\b(PvP|Warzones?|Starfighter)\b", RegexOptions.IgnoreCase))
                        pvp.Add(ev.QuestId);
                }
                if (pvp.Count > 0)
                {
                    var kept = new List<RecEvent>(events.Count);
                    foreach (RecEvent ev in events)
                        if (ev.QuestId == 0 || !pvp.Contains(ev.QuestId))
                            kept.Add(ev);
                    events = kept;
                }
            }
            _sameName = new Dictionary<string, List<Vec3>>(StringComparer.Ordinal);
            foreach (RecEvent e in events)
            {
                if ((e.Kind == EvKind.ObjectClick || e.Kind == EvKind.Spot || e.Kind == EvKind.Cast || e.Kind == EvKind.CastStart) &&
                    e.Target != null && e.Target.Kind == ObjKind.Placeable && e.Target.HasPos && e.Target.Name.Length > 0)
                {
                    List<Vec3> l;
                    if (!_sameName.TryGetValue(e.Target.Name, out l))
                        _sameName[e.Target.Name] = l = new List<Vec3>();
                    if (!l.Exists(p => p.DistanceTo(e.Target.Pos) <= 0.05f))
                        l.Add(e.Target.Pos);
                }
            }
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
            // the quest steps whose counts all move only when another mission ends (a weekly's "complete N dailies" step)
            var metaSteps = new HashSet<string>(StringComparer.Ordinal);
            {
                var plainSteps = new HashSet<string>(StringComparer.Ordinal);
                foreach (RecEvent e in events)
                {
                    if (e.Kind != EvKind.QuestTask || e.Max == 0 || e.QuestId == 0 || e.QuestHidden)
                        continue;
                    string qs = Fmt.Hex(e.QuestId) + "|" + e.Step.ToString(CultureInfo.InvariantCulture);
                    if (metaTasks.Contains(TaskKey(e)))
                        metaSteps.Add(qs);
                    else
                        plainSteps.Add(qs);
                }
                metaSteps.ExceptWith(plainSteps);
            }
            // (a title the game hadn't read yet, "#...", gives way to the real one read later)
            var questTitles = new Dictionary<ulong, string>();
            foreach (RecEvent e in events)
            {
                if (e.QuestId == 0 || string.IsNullOrEmpty(e.QuestName))
                    continue;
                string had;
                if (!questTitles.TryGetValue(e.QuestId, out had) ||
                    (had.StartsWith("#", StringComparison.Ordinal) && !e.QuestName.StartsWith("#", StringComparison.Ordinal)))
                    questTitles[e.QuestId] = e.QuestName;
            }
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
            var mobSeen = new List<Kill>(); // the mob scanner: mobs your missions count that you came near (not killed - spots only)
            int leg = 0; // which part of the map you're in (goes up with each lift / taxi / loading screen)
            double legAt = -1e9;
            ObjRef talk = null;
            ObjRef lastLoot = null; // the last body you looted
            string lastBagItem = null; // the last usable bag item you used (a stim)
            string pendingEquip = null; // an item you just put on (a quest disguise)
            double pendingEquipAt = -1e9;
            double lastBagItemAt = -1e9;
            Vec3 lastBagItemPos = default(Vec3);
            double lastLootAt = -1e9;
            double talkAt = -1e9;
            bool talkClicked = false; // you clicked who you talked to (not a scene that started by itself)
            bool talkGave = false; // that talk gave you a mission (its end isn't a later step's talk)
            double sceneAfterKillAt = -1e9; // a scene that started by itself right after a kill (nothing clicked)
            ulong holoQuest = 0; // the mission whose holocom call was answered last, and when
            double holoQuestAt = -1e9;
            double lastPickAt = -1e9; // your last reply pick
            var branchedAt = new List<KeyValuePair<ulong, double>>(); // a quest that took another branch right after your replies
            var rewardAt = new Dictionary<ulong, double>(); // a reward window came up for it (since it was last picked up)
            var pickupOf = new Dictionary<ulong, Step>(); // each quest's last pick-up step
            var abandoned = new HashSet<ulong>(); // quests you dropped from your log
            double boardAt = -1e9; // when you last opened a mission board
            ObjRef lastClicked = null; // the last object or NPC you clicked (not lifts or taxis)
            double lastClickedAt = -1e9;
            Vec3 lastClickedStand = default(Vec3); // where you stood when you clicked it
            double otherCastAt = -1e9; // your last cast that wasn't on the object you last clicked (a travel cast, a mount ...)
            Use giverUse = null; // the object you clicked for a pick-up (it may also bring out the fight that follows)
            ObjRef questWindow = null; // the NPC whose quest window you last opened
            double questWindowAt = -1e9;
            double holoAt = -1e9;
            string item = null;
            double itemAt = -1e9;
            double rideAt = -1e9;
            // you were dead (since diedAt), or revived at revivedAt: a loading screen or move then is the medical center, not a door or item
            // (also for recordings older than the "you were revived" note on the loading screen)
            bool dead = false;
            double diedAt = -1e9, revivedAt = -1e9;
            double areaAt = -1e9; // you last came into another area
            double fightAt = -1e9;
            double castEndAt = -1e9;
            Step pendingRide = null;
            bool pendingOpenOnly = false; // the lift's panel opened, no ride seen yet
            Vec3 rideFrom = default(Vec3);
            ObjRef pendingLift = null;
            string pendingDest = string.Empty;
            double liftOpenAt = -1e9;
            Vec3 itemPos = default(Vec3);
            string itemOn = null; // the NPC you used the item on, if you had one targeted
            bool inCombat = false, itemInCombat = false; // in a fight right now / when you used the item
            float itemOnDist = -1f; // how far you stood from the NPC you used the item on
            bool itemOnDead = false; // that NPC was a corpse
            float itemOnHealth = -1f; // that NPC's health when you used it
            double actionAt = -1e9; // your last click or cast (not a loot)
            string castItem = null; // a cast on nothing (maybe a mission item) and where it ended
            double castItemAt = -1e9;
            Vec3 castItemPos = default(Vec3);
            string npcClickName = null;
            Vec3 npcClickPos = default(Vec3);
            double npcClickAt = -1e9;
            bool npcClickLoot = false; // the last "NPC click" was a loot (a body), not a talk
            List<Use> sameTake = null; // the clicks the last tick took, and when (one click can count for two quests)
            double sameTakeAt = -1e9;
            ulong sameTakeQuest = 0;
            Step lastWalk = null;
            var finalAt = new Dictionary<ulong, double>();
            double lastWalkAt = -1e9;
            int dark = 0, light = 0;
            var vendors = new Dictionary<string, Vec3>(StringComparer.Ordinal);
            double lastProgressAt = -1e9;
            ulong lastProgressQuest = 0;
            EvKind lastProgressKind = EvKind.Start;
            var stepText = new Dictionary<ulong, string>();
            var countsSeen = new Dictionary<string, long[]>(StringComparer.Ordinal); // "quest|step|task" -> {count, max}
            var taskTexts = new Dictionary<string, string>(StringComparer.Ordinal); // "quest|step|task" -> its objective text
            var picked = new List<ulong>();
            var weeklies = new HashSet<ulong>(); // a weekly can be picked up again (3 times a week): never loop just for it
            var knownItems = new List<string>(); // every mission item you used in this recording
            var itemForTask = new Dictionary<string, string>(StringComparer.Ordinal); // quest/step/task -> the item you did it with
            var scanner = new Dictionary<string, List<Vec3>>(StringComparer.Ordinal); // same-kind objects near your clicks
            var itemOrder = new List<Use>(); // mission items used since the last tick, in order
            ObjRef targetNpc = null; // the NPC you last selected
            double targetNpcAt = -1e9;
            var usedItemNames = new HashSet<string>(StringComparer.Ordinal); // items already written as a "used on your target" step
            // every spot you used each mission item, in your order - also the uses that didn't count
            // (Black Hole spies: 5 scans, 3 counted - the spies aren't always in the same place)
            var itemSpots = new Dictionary<string, List<Vec3>>(StringComparer.Ordinal);
            foreach (RecEvent ev in events)
            {
                string used = ev.Kind == EvKind.QuestItem ? ev.Option
                            : ev.Kind == EvKind.Cast ? ItemFromCast(ev.Option) : null;
                if (string.IsNullOrEmpty(used) || used.StartsWith("Heroic Transport", StringComparison.Ordinal) || ev.Pos.IsZero)
                    continue;
                List<Vec3> sl;
                if (!itemSpots.TryGetValue(used, out sl))
                    itemSpots[used] = sl = new List<Vec3>();
                if (sl.Count < 20 && !sl.Exists(v => v.DistanceTo(ev.Pos) < 0.5f))
                    sl.Add(ev.Pos);
            }
            var completedOrder = new List<ulong>();
            double bareCastAt = -1e9;
            Vec3 bareCastPos = default(Vec3);

            // a mob another player tagged first can still move your count (shared tagging): its kill counts when a count the
            // game lists that mob type for ticked within a second after it
            var sharedKills = new HashSet<RecEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                RecEvent ke = events[i];
                if (ke.Kind != EvKind.Kill || ke.Target == null || ke.Target.Spec == 0 || !(ke.Note ?? string.Empty).StartsWith(TaggedNote, StringComparison.Ordinal))
                    continue;
                for (int j = i + 1; j < events.Count && events[j].Elapsed - ke.Elapsed <= 1.0; j++)
                    if (events[j].Kind == EvKind.QuestTask && events[j].TaskTargets != null && events[j].TaskTargets.Contains(ke.Target.Spec))
                    {
                        sharedKills.Add(ke);
                        break;
                    }
            }

            bool haveTrail = false;
            Vec3 lastTrail = default(Vec3);
            var convos = new List<Convo>(); // every conversation and the replies you picked
            Convo convo = null;
            double prevT = 0;
            ulong tickQuest = 0; // the quest the last event moved, and the branch that tick was on
            string tickBranch = null;
            foreach (RecEvent e in events)
            {
                double t = e.Elapsed;
                foreach (Step made in steps)
                    if (made.At < -1e8)
                    {
                        made.At = prevT; // when each step was made (to find the conversation it came from)
                        if (made.Branch == null && tickQuest != 0 && made.Quest == tickQuest)
                            made.Branch = tickBranch;
                    }
                tickQuest = 0;
                prevT = t;
                if ((e.Kind == EvKind.ObjectClick || e.Kind == EvKind.NpcClick) && e.Target != null && e.Target.Name.Length > 0 && !transport.Contains(e.Target.Name))
                {
                    lastClicked = e.Target;
                    lastClickedAt = t;
                    lastClickedStand = e.Pos;
                }
                // (when that cast started: one you began before the click and the click cut short is no later cast)
                // (a travel cast always counts, even when the game tied it to the object you had just clicked)
                if (e.Kind == EvKind.Cast && (IsTravelAbility(e.Option ?? string.Empty) || !(e.Target != null && lastClicked != null && e.Target.Name == lastClicked.Name)))
                    otherCastAt = Math.Max(otherCastAt, t - Math.Max(0, e.DurationMs) / 1000.0);
                switch (e.Kind)
                {
                    case EvKind.ConvoStart:
                        // (a holocom call has nobody next to you: kept under a name no one has, for its replies and its end)
                        convo = e.Note == How.HolocomCall ? new Convo { Partner = HoloPartner, Start = t, End = t + 600 }
                              : e.Target == null ? null : new Convo { Partner = e.Target.Name, Start = t, End = t + 600 };
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
                if (pendingRide != null && !e.Pos.IsZero && e.Pos.DistanceTo(rideFrom) >= 3f && pendingOpenOnly && pendingLift.NodeId != 0 &&
                    e.Kind != EvKind.ElevatorRide && e.Kind != EvKind.Loading && e.Kind != EvKind.AreaChange && e.Kind != EvKind.Jump)
                {
                    // You opened the lift's panel but didn't ride it: you walked off (this recorder sees every ride, even
                    // a short one). No ride - but one spot where you walked to, so the navigator goes your way and doesn't
                    // stand at the lift (CZ-198 22:11: the bot froze at that lift until LethToxic took it down by hand).
                    pendingRide.Kind = "Ride"; // its own gate: only while you're still on the lift's side
                    pendingRide.Gate = SideOf(rideFrom, e.Pos);
                    pendingRide.Comment = "Walk past the " + pendingLift.Name;
                    AddNote(pendingRide, "you didn't ride it");
                    pendingRide.Lines[0] = "<MoveTo QuestId=\"1\" Name=\"Past the " + A(pendingLift.Name) + "\" XYZ=\"" + X(e.Pos) + "\" />";
                    pendingRide = null;
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
                // A lift, taxi, loading screen or area change starts a new part of the map: a fight only roams
                // spots from its own part (CZ-198: the first fight must not take the Freight Depot spots below).
                if (e.Kind == EvKind.ElevatorOpen || e.Kind == EvKind.TaxiStart || e.Kind == EvKind.Loading || e.Kind == EvKind.AreaChange)
                {
                    if (e.Elapsed - legAt > 60)
                        leg++;
                    legAt = e.Elapsed;
                }
                else if (e.Kind == EvKind.ElevatorRide || e.Kind == EvKind.TaxiEnd)
                {
                    if (e.Elapsed - legAt > 60)
                        leg++; // a ride the recorder saw without the panel opening first
                    legAt = e.Elapsed;
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
                    // Walk points only when you ask for them (setting): the steps are already in your order at your
                    // exact spots, so the bot runs straight from one to the next. A point every 10 m made it crawl.
                    // Positions are in game units of 10 m.
                    else if (WalkSpacing > 0f && lastTrail.DistanceTo(e.Pos) * 10f >= WalkSpacing)
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
                            var spot = new Step { Kind = "Route", Comment = e.Note.StartsWith(PauseNote, StringComparison.Ordinal) ? "Pause here" : "Wait here" };
                            spot.Lines.Add("<MoveTo QuestId=\"1\" Name=\"" + A(spot.Comment) + "\" XYZ=\"" + X(e.Pos) + "\" />");
                            spot.Lines.Add(e.Note.StartsWith(PauseNote, StringComparison.Ordinal)
                                ? "<UserDialog Title=\"Quest Recorder\" Message=\"Do this part by hand, then press Continue.\" IsBotContinueAllowed=\"True\" />"
                                : "<WaitTimer QuestId=\"1\" WaitTime=\"10\" />");
                            steps.Add(spot);
                            break;
                        }
                        markers.Add(new KeyValuePair<string, Vec3>(e.Note, e.Pos));
                        break;
                    case EvKind.ObjectClick:
                    case EvKind.Cast:
                    {
                        // The game's loot cast with nothing clicked (area loot): you looted the mob that just died next to you
                        // (Section X Lost Reconnaissance: a Flight Recorder out of a Tuk'ata - "Partially Digested").
                        if ((e.Option == "Looting" || e.Option == "Loot") && e.Target == null && (lastLoot == null || t - lastLootAt > 5) && allKills.Count > 0)
                        {
                            Kill lk = allKills[allKills.Count - 1];
                            if (t - lk.At <= 20 && !e.Pos.IsZero && lk.Pos.DistanceTo(e.Pos) <= 3f)
                            {
                                lastLoot = new ObjRef { Name = lk.Name, Kind = ObjKind.Npc, Pos = lk.Pos, HasPos = true, Spec = lk.Spec, How = "the mob that just died next to you (the game's loot cast)" };
                                lastLootAt = t;
                            }
                        }
                        if (e.Option != "Loot")
                            actionAt = t; // a click or a cast of yours (not the loot cast itself)
                        // "Using Rune of Passion..." is the cast of the mission item "Rune of Passion" (older recordings
                        // didn't tie it to the item): a mission item use, in your order
                        string usedItem = e.Kind == EvKind.Cast ? ItemFromCast(e.Option) : null;
                        if (usedItem != null)
                        {
                            if (!knownItems.Contains(usedItem))
                                knownItems.Add(usedItem);
                            if (itemOrder.Count == 0 || itemOrder[itemOrder.Count - 1].Name != usedItem || t - itemOrder[itemOrder.Count - 1].At > 5)
                                itemOrder.Add(new Use { Name = usedItem, Pos = e.Pos, At = t, InFight = inCombat });
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
                        // a second click on the same object already used (no new cast on it). Same object = same game id:
                        // a container that respawns at the same spot is a new one and counts (CZ-198 Biotoxin 3-5).
                        if (e.Kind == EvKind.ObjectClick && consumed.TryGetValue(UseKey(o.NodeId, p), out when) && t - when <= 30)
                            break;
                        // the cast of that same object ending just after its tick (the game counted it first): part of that use,
                        // not a new one for the next step (CZ-198: Biotoxin loot finishing after Biotoxin 3/6 counted)
                        if (e.Kind == EvKind.Cast && o.NodeId != 0 && consumed.TryGetValue(UseKey(o.NodeId, p), out when) && t - when <= 3)
                            break;
                        if (e.Kind == EvKind.ObjectClick && t - castEndAt <= 1.5 && uses.Count > 0 && uses[uses.Count - 1].Name != o.Name)
                            break; // a stray click right as a cast on another object ended (CZ-198 Kolto after the last pod)
                        if (e.Kind == EvKind.Cast)
                            castEndAt = t;
                        if (uses.Count > 0 && X(uses[uses.Count - 1].Pos) == key)
                        {
                            uses[uses.Count - 1].At = t; // the click and its cast are one use
                            if (o.NodeId != 0)
                                uses[uses.Count - 1].Id = o.NodeId;
                            if (e.Kind == EvKind.Cast)
                                uses[uses.Count - 1].Cast = true;
                            if (o.UsableForQuest || o.TakesTurnIn)
                                uses[uses.Count - 1].ForQuest = true;
                            break;
                        }
                        uses.Add(new Use { Name = o.Name, Pos = p, At = t, Cast = e.Kind == EvKind.Cast, Stand = e.Pos, Id = o.NodeId, ForQuest = o.UsableForQuest || o.TakesTurnIn });
                        break;
                    }
                    case EvKind.Loot:
                        if (e.Target != null)
                        {
                            lastLoot = e.Target;
                            lastLootAt = t;
                            // a loot is also what the "kill and loot" step looks for (Section X Targeting Beacon)
                            if (e.Target.Name.Length > 0)
                            {
                                npcClickName = e.Target.Name;
                                npcClickPos = e.Target.HasPos ? e.Target.Pos : e.Pos;
                                npcClickAt = t;
                                npcClickLoot = true;
                            }
                        }
                        break;
                    case EvKind.ConvoStart:
                        if (e.Note == How.HolocomCall)
                            holoAt = t;
                        else
                        {
                            if (e.Target != null && e.Target.Name.Length > 0 && !IsCompanion(e.Target))
                            {
                                talk = e.Target;
                                talkAt = t;
                                // you clicked who you talk to (and maybe walked up to them first)
                                talkClicked = lastClicked != null && t - lastClickedAt <= 20 && lastClicked.Name == e.Target.Name;
                                talkGave = false;
                            }
                            // a scene that started by itself (nothing clicked) right after a kill: a boss that talks once beaten
                            if (allKills.Count > 0 && t - allKills[allKills.Count - 1].At <= 3 && !(lastClicked != null && t - lastClickedAt <= 6))
                                sceneAfterKillAt = t;
                        }
                        break;
                    case EvKind.HolocomAnswered:
                        holoQuest = e.QuestId;
                        holoQuestAt = t;
                        break;
                    case EvKind.RewardWindow:
                        if (e.QuestId != 0)
                            rewardAt[e.QuestId] = t;
                        break;
                    case EvKind.BoardOpen:
                        boardAt = t;
                        break;
                    case EvKind.Spot:
                        // the scanner: a mob one of your missions counts, seen near you - another spot where its fight finds them
                        if (e.Target != null && e.Target.Kind == ObjKind.Npc && e.Target.HasPos && e.Target.Name.Length > 0)
                        {
                            if (mobSeen.Count < 2000)
                                mobSeen.Add(new Kill { Name = e.Target.Name, Pos = e.Target.Pos, At = t, Leg = leg, Spec = e.Target.Spec });
                        }
                        // the scanner: another object like the one you clicked, nearby
                        else if (e.Target != null && e.Target.HasPos && e.Target.Name.Length > 0)
                        {
                            List<Vec3> sl;
                            if (!scanner.TryGetValue(e.Target.Name, out sl))
                                scanner[e.Target.Name] = sl = new List<Vec3>();
                            if (sl.Count < 40)
                                sl.Add(e.Target.Pos);
                        }
                        break;
                    case EvKind.QuestItem:
                        if (e.Note == "equipped")
                        {
                            // an item you put on (a quest disguise): its own step, right before the mission moved
                            pendingEquip = e.Option;
                            pendingEquipAt = t;
                            break;
                        }
                        if (e.Note == "bag item" && e.Option.Length > 0)
                        {
                            lastBagItem = e.Option;
                            lastBagItemAt = t;
                            lastBagItemPos = e.Pos;
                        }
                        if (e.Option.Length > 0)
                        {
                            if (!knownItems.Contains(e.Option))
                                knownItems.Add(e.Option);
                            string on = e.Target != null && e.Target.Kind != ObjKind.Placeable && e.Target.Name.Length > 0 && !string.Equals(e.Target.Toughness, "companion", StringComparison.OrdinalIgnoreCase) ? e.Target.Name : null;
                            float onDist = on != null && e.Target.HasPos && !e.Pos.IsZero ? e.Pos.DistanceTo(e.Target.Pos) : -1f;
                            bool onDead = on != null && e.Target.Dead;
                            float onHealth = on != null ? e.Target.Health : -1f;
                            // the same use seen twice (its cast, then its cooldown): one use - but the same item on another
                            // NPC a moment later is a second use (two prisoners freed with one Shackle Remote)
                            Use lastUse = itemOrder.Count > 0 ? itemOrder[itemOrder.Count - 1] : null;
                            if (lastUse == null || lastUse.Name != e.Option || t - lastUse.At > 5 || (on != null && lastUse.On != null && on != lastUse.On))
                                itemOrder.Add(new Use { Name = e.Option, Pos = e.Pos, At = t, On = on, OnDist = onDist, OnDead = onDead, OnHealth = onHealth,
                                                         InFight = inCombat, Bag = e.Note == "bag item" }); // (its cast may have been noted already)
                            else if (lastUse.On == null)
                            {
                                lastUse.On = on;
                                lastUse.OnDist = onDist;
                                lastUse.OnDead = onDead;
                                lastUse.OnHealth = onHealth;
                            }
                            else if (on != null && lastUse.On == on)
                            {
                                onDead = lastUse.OnDead;
                                onHealth = lastUse.OnHealth; // the same use seen again: keep how it was when you used it
                            }
                            item = e.Option;
                            itemAt = t;
                            itemPos = e.Pos;
                            itemOn = on;
                            itemInCombat = inCombat;
                            itemOnDist = onDist;
                            itemOnDead = onDead;
                            itemOnHealth = onHealth;
                        }
                        break;
                    case EvKind.Died:
                        dead = true;
                        diedAt = t;
                        break;
                    case EvKind.Revived:
                        dead = false;
                        revivedAt = t;
                        break;
                    case EvKind.Loading:
                        // handled with the jump / area change it causes (below) - not a revive at a medical center
                        if (e.HasFrom && item != null && t - itemAt <= 30 &&
                            !((dead && t - diedAt <= 600) || t - revivedAt <= 10 || e.Note.StartsWith(How.Revived, StringComparison.Ordinal)))
                            goto case EvKind.Jump;
                        break;
                    case EvKind.NpcClick:
                        if (e.Target != null && e.Target.Name.Length > 0 && !string.Equals(e.Target.Toughness, "companion", StringComparison.OrdinalIgnoreCase))
                        {
                            npcClickName = e.Target.Name;
                            npcClickPos = e.Target.HasPos ? e.Target.Pos : e.Pos;
                            npcClickAt = t;
                            npcClickLoot = false;
                        }
                        break;
                    case EvKind.Target:
                        // the NPC you selected (a friendly one you then used a mission item on: the Section X Hyperbeast)
                        if (e.Target != null && e.Target.Kind != ObjKind.Placeable && e.Target.Name.Length > 0 && !e.Target.Dead &&
                            !string.Equals(e.Target.Toughness, "companion", StringComparison.OrdinalIgnoreCase))
                        {
                            targetNpc = e.Target;
                            targetNpcAt = t;
                        }
                        break;
                    case EvKind.ConvoChoice:
                        if (!e.OptionGuessed && e.Option.EndsWith(" dark", StringComparison.Ordinal))
                            dark++;
                        else if (!e.OptionGuessed && e.Option.EndsWith(" light", StringComparison.Ordinal))
                            light++;
                        lastPickAt = t;
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
                            pendingOpenOnly = false; // ridden (Section X lifts move you less than 3 m)
                            break;
                        }
                        liftOpenAt = t;
                        if (w != null && w.Name.Length > 0)
                        {
                            // walk points so far come first
                            if (markers.Count > 0)
                            {
                                var before = new Step { Kind = "Route", Comment = "Walk" };
                                AddRoute(before, markers);

                                steps.Add(before);
                                markers.Clear();
                            }
                            // The ride is its own step, not tied to one quest step.
                            var ride = new Step { Kind = "Ride", Comment = "Elevator: " + w.Name };
                            ride.Lines.Add(string.Empty); // written when you come out (below)
                            steps.Add(ride);
                            pendingRide = ride;
                            pendingOpenOnly = e.Kind == EvKind.ElevatorOpen;
                            pendingLift = w;
                            pendingDest = dest;
                            rideFrom = e.HasStand ? e.Stand : (e.Kind == EvKind.ElevatorOpen ? e.Pos : e.FromPos);
                        }
                        break;
                    }
                    case EvKind.TaxiStart:
                    case EvKind.TaxiEnd:
                        rideAt = t;
                        break;
                    case EvKind.ShipTravel:
                        rideAt = t;
                        // your ship flew you to another planet: BuddyCron's SpaceTravel, only while on board that ship
                        if (e.Option.Length > 0 && e.AreaId != 0)
                        {
                            FlushRoute(steps, markers);
                            var fly = new Step { Kind = "Ride", Comment = "Fly to " + e.Option, Gate = "(Core.Player.AreaId == " + Fmt.Hex(e.AreaId) + ")",
                                                 Unsure = true, Note = "the galaxy map's name for it: check that BuddyCron's SpaceTravel knows " + e.Option +
                                                                       "; it only runs on board the ship you recorded on (another class's ship is another area)" };
                            fly.Lines.Add("<SpaceTravel Planet=\"" + A(e.Option) + "\" />");
                            // the flight's loading screen (hand-made profiles wait 15-90 s after a SpaceTravel)
                            fly.Lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"30\" />");
                            steps.Add(fly);
                        }
                        break;
                    case EvKind.AreaChange:
                    case EvKind.Jump:
                    {
                        // a revive at a medical center moved you (dead until now, or just revived): no item hop, door or walk points
                        bool reviveMove = (dead && t - diedAt <= 600) || t - revivedAt <= 10 || e.Note.StartsWith(How.Revived, StringComparison.Ordinal);
                        // A mission item that moved you.
                        if ((item == null || t - itemAt > 30) && castItem != null && t - castItemAt <= 3 && e.Kind == EvKind.Jump)
                        {
                            item = castItem;
                            itemAt = castItemAt;
                            itemPos = castItemPos;
                        }
                        castItem = null;
                        if (reviveMove)
                            item = null; // what you used before you died didn't move you
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
                        // A door into another area you clicked (your ship's ramp, its exit, an airlock - not a lift or taxi):
                        // click it where you did, then wait while still in the area you came from (the loading screen).
                        // (not an object a step already used, and you hadn't walked off since the click: walking through a
                        // phase barrier after clicking something else keeps the walk points below)
                        // (a click after the last ride: the ride's own arrival is no door - your ship's exit after it flew you is)
                        // (not an object that gives or takes missions, and nothing else cast after the click: a quick travel
                        // or other travel cast right after using a terminal or dropbox moved you, not the object)
                        if (e.Kind == EvKind.AreaChange && (t - rideAt > 20 || lastClickedAt > rideAt) && e.HasFrom && e.FromArea != 0 && e.AreaId != 0 &&
                            e.FromArea != e.AreaId && !e.Pos.IsZero && !reviveMove &&
                            lastClicked != null && lastClicked.Kind == ObjKind.Placeable && lastClicked.Name.Length > 0 &&
                            !lastClicked.Name.StartsWith("#", StringComparison.Ordinal) && t - lastClickedAt <= 15 && lastClickedAt > areaAt &&
                            !lastClicked.OffersQuest && !lastClicked.TakesTurnIn && otherCastAt <= lastClickedAt &&
                            !UsedUp(lastClicked, consumed, t) && !e.FromPos.IsZero &&
                            ((!lastClickedStand.IsZero && e.FromPos.DistanceTo(lastClickedStand) <= 1.5f) || (lastClicked.HasPos && e.FromPos.DistanceTo(lastClicked.Pos) <= 1.5f)))
                        {
                            FlushRoute(steps, markers);
                            string dn = lastClicked.Name;
                            Vec3 dp = lastClicked.HasPos ? lastClicked.Pos : lastClickedStand;
                            string inArea = "(Core.Player.AreaId == " + Fmt.Hex(e.FromArea) + ")";
                            var door = new Step { Kind = "Ride", Comment = "Through " + dn, Gate = inArea };
                            float sd = lastClickedStand.IsZero ? 0f : lastClickedStand.DistanceTo(dp);
                            if (sd >= 0.3f && sd < 10f)
                                door.Lines.Add("<MoveTo QuestId=\"1\" Name=\"Stand for " + A(dn) + "\" XYZ=\"" + X(lastClickedStand) + "\" />");
                            door.Lines.Add("<Interact Name=\"" + A(dn) + "\" IgnoreLOS=\"True\" WaitTime=\"3\" XYZ=\"" + X(dp) + "\" />");
                            door.Lines.AddRange(WaitWhile(inArea, 60));
                            steps.Add(door);
                            // that click was the way in: no step's use, giver or hand-in
                            uses.RemoveAll(u => u.Name == dn && u.At >= lastClickedAt - 1);
                            lastClicked = null;
                            areaAt = t;
                            break;
                        }
                        if (e.Kind == EvKind.AreaChange)
                            areaAt = t;
                        // a phase door / doorway you went through.
                        // (not a quick travel or fleet pass you cast: BuddyCron plans its own way, no walk points for it)
                        if (t - rideAt > 20 && e.HasFrom && !reviveMove &&
                            e.Note.IndexOf("Quick Travel", StringComparison.OrdinalIgnoreCase) < 0 &&
                            e.Note.IndexOf("Fleet Pass", StringComparison.OrdinalIgnoreCase) < 0 &&
                            e.Note.IndexOf("Stronghold", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            markers.Add(new KeyValuePair<string, Vec3>("Before the " + (e.Kind == EvKind.AreaChange ? "area change" : "door / phase"), e.FromPos));
                            markers.Add(new KeyValuePair<string, Vec3>("Came out here", e.Pos));
                        }
                        break;
                    }
                    case EvKind.CombatStart:
                        fightAt = t;
                        inCombat = true;
                        break;
                    case EvKind.CombatEnd:
                        inCombat = false;
                        break;
                    case EvKind.Kill:
                        if ((e.Note.Length == 0 || sharedKills.Contains(e)) && e.Target != null)
                        {
                            var k = new Kill { Name = e.Target.Name, Pos = e.Target.HasPos ? e.Target.Pos : e.Pos, At = t, Leg = leg, Spec = e.Target.Spec };
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
                        // (the game's own helper and bookkeeping quests: no pick-up. Clicks on objects the game said were for one
                        // of your missions stay for the step they move, however long that step takes)
                        if (e.QuestHidden || e.QuestId == 0 || IsSystemQuest(e))
                        {
                            uses.RemoveAll(u => t - u.At > 10 && !u.ForQuest);
                            break;
                        }
                        string q = Fmt.Hex(e.QuestId);
                        picked.Add(e.QuestId);
                        rewardAt.Remove(e.QuestId); // a new go at it
                        // a weekly comes back (3 times a week): only "not in my log", like the Oricon weekly. Dailies and heroics: not in my log and not done.
                        // (its tag in any client language)
                        string qName;
                        if (!questTitles.TryGetValue(e.QuestId, out qName))
                            qName = e.QuestName ?? string.Empty;
                        bool repeat = IsRepeatable(e) || Regex.IsMatch(qName, RepeatTag, RegexOptions.IgnoreCase);
                        bool isWeekly = repeat && IsWeekly(qName);
                        // a dynamic encounter starts again whenever it is up: also only "not in my log", and nobody gives it
                        bool encounter = IsEncounter(e);
                        var s = new Step { Kind = "Pickup", Gate = isWeekly || encounter ? "(not HasQuest(" + q + "))" : "(not HasQuest(" + q + ")) and (not IsQuestComplete(" + q + "))", Comment = "Pick up " + qName, Weekly = isWeekly };
                        if (isWeekly)
                            weeklies.Add(e.QuestId);
                        // put down to the mission board, but you were talking to someone after opening it: they gave it
                        // (End of Torvix comes from General Konya, not the Black Hole Mission Terminal)
                        // the game handed it out by itself (an [AREA] mission when you walk in), or in a holocom call nothing you clicked set off
                        bool selfGranted = encounter || e.Note == How.NoGiver ||
                                           (e.Note == How.Holocom && !(lastClicked != null && holoAt >= lastClickedAt && holoAt - lastClickedAt <= 6));
                        ObjRef giver = null;
                        if (!selfGranted)
                        {
                            // the NPC you were talking to when it came (also when you had clicked an object just before that conversation)
                            bool talked = e.Target != null && e.Target.Name.Length > 0 && (e.Note == How.Offered || e.Note == How.Talking);
                            // else your last click (the terminal, the Recon Droid, General Konya) - unless a conversation, quest window or board
                            // opened after it, or it is an object you had already used for a step
                            bool clickFresh = lastClicked != null && t - lastClickedAt <= 60 && !UsedUp(lastClicked, consumed, t);
                            bool openedLater = talkAt > lastClickedAt + 6 || questWindowAt > lastClickedAt + 8 || boardAt > lastClickedAt + 10;
                            bool clickStartedTalk = talkAt >= lastClickedAt && talkAt <= lastClickedAt + 6;
                            // with someone talking to you, a plain object (no mission marker, not usable for a mission) never beats them
                            bool plainObject = lastClicked != null && lastClicked.Kind == ObjKind.Placeable && !lastClicked.OffersQuest && !lastClicked.UsableForQuest;
                            giver = clickFresh && !openedLater && !(talked && (clickStartedTalk || plainObject)) ? lastClicked : e.Target;
                            if (UsedUp(giver, consumed, t) || IsCompanion(giver))
                                giver = null;
                            // a mob you fought or looted never gives a mission (Dreadtooth)
                            if (giver != null && !talked && giver.Kind == ObjKind.Npc && !giver.OffersQuest && (giver.Dead || killedNames.Contains(giver.Name)))
                                giver = null;
                            // a giver stands next to you when the mission lands
                            if (giver != null && giver.HasPos && !e.Pos.IsZero && giver.Pos.DistanceTo(e.Pos) > 2f)
                                giver = null;
                            // it came with another mission's progress (a bonus that step opened): not given by what you clicked
                            if (giver != null && !talked && e.Note != How.Board && e.Note != "from its quest window" &&
                                AnotherTickNear(events, hiddenQuests, e.QuestId, t, giver == lastClicked ? lastClickedAt : t - 2))
                                giver = null;
                            // a kill after your click set it off (a bonus mission that comes with the first mob of a kind): not
                            // given by what you clicked before the fight
                            if (giver != null && !talked && giver == lastClicked && e.Note != How.Board && e.Note != "from its quest window" &&
                                allKills.Count > 0 && allKills[allKills.Count - 1].At > lastClickedAt && t - allKills[allKills.Count - 1].At <= 3)
                                giver = null;
                        }
                        // the talk gave this mission: its end is not what a later step was done with
                        if (giver != null && talk != null && giver.Name == talk.Name)
                            talkGave = true;
                        // the object you clicked that gave it: kept for a fight it brings out (the Field Generator brings out the
                        // commander) - not a mission board, a holocom call or a mission the game handed out by itself
                        if (giver != null && giver == lastClicked && giver.Kind == ObjKind.Placeable && t - lastClickedAt <= 10 &&
                            string.IsNullOrEmpty(e.Option) && e.Note != How.Board && e.Note != How.Holocom)
                        {
                            Use gu = uses.FindLast(u => u.Name == lastClicked.Name);
                            giverUse = gu ?? new Use { Name = lastClicked.Name, Pos = lastClicked.HasPos ? lastClicked.Pos : e.Pos, At = lastClickedAt,
                                                       Stand = lastClickedStand.IsZero ? e.Pos : lastClickedStand, Id = lastClicked.NodeId };
                        }
                        // the click on the giver / board was the pick-up; when nothing you clicked gave it (walked in, a holocom, a bonus that
                        // came with a step) the clicks of the last 10 s stay for the step they move
                        if (giver != null && giver.Name.Length > 0)
                            uses.Clear();
                        else
                            uses.RemoveAll(u => t - u.At > 10 && !u.ForQuest);
                        if (giver != null && giver.Name.Length > 0)
                        {
                            Vec3 gp = giver.HasPos ? giver.Pos : e.Pos;
                            // an object: stand where you stood when you clicked it (the object's own spot can be out of reach)
                            Vec3 stand = giver == lastClicked ? lastClickedStand : e.Pos;
                            string range = "20";
                            if (giver.Kind == ObjKind.Placeable && giver.HasPos && !stand.IsZero)
                            {
                                float d = stand.DistanceTo(gp);
                                if (d >= 0.3f && d < 10f)
                                {
                                    s.Lines.Add("<MoveTo QuestId=\"1\" Name=\"Stand for " + A(giver.Name) + "\" XYZ=\"" + X(stand) + "\" />");
                                    range = Math.Max(20, (int)Math.Ceiling((d + 1f) * 10f)).ToString(CultureInfo.InvariantCulture);
                                }
                            }
                            // dailies / weeklies / heroics: the giver stops offering them once done today, and BuddyCron's
                            // PickUpQuest would click it forever - PickUpIfOffered gives up after 3 tries.
                            // An object giver is pinned to its own spot (a twin terminal 0.07 away is shut out: SearchRadius is squared).
                            // (no GiverType: not checked that BuddyCron's PickUpQuest reads it, and it finds objects without it)
                            if (PickUpOnlyWithRoom)
                                s.Gate += " and (" + LogHasRoom + ")";
                            s.Board = giver.Kind == ObjKind.Placeable && e.Note == How.Board && e.Target != null && e.Target.Name == giver.Name;
                            s.Lines.Add("<" + (repeat ? "PickUpIfOffered" : "PickUpQuest") + " QuestName=\"" + A(qName) + "\" QuestId=\"" + q + "\"" +
                                        (giver.Kind == ObjKind.Placeable ? (giver.HasPos ? " SearchRadius=\"0.001\"" : " SearchRadius=\"20\"") : string.Empty) +
                                        " UseRange=\"" + range + "\" GiverName=\"" + A(giver.Name) + "\" XYZ=\"" + X(gp) + "\"" + (isWeekly && repeat ? " Repeatable=\"True\"" : string.Empty) + " />");
                        }
                        else
                        {
                            // walk to where it landed in your log so the game hands it out again (an area mission can take
                            // a few seconds more to come)
                            s.Lines.Add("<MoveTo QuestId=\"1\" Name=\"" + A(qName) + "\" XYZ=\"" + X(e.Pos) + "\" />");
                            s.Lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"3\" />");
                            s.Lines.Add("<If Condition=\"not HasQuest(" + q + ")\"><WaitTimer QuestId=\"1\" WaitTime=\"5\" /></If>");
                            // what started it that the bot can't do by itself: said in the review list (yellow)
                            string fromItem = null;
                            if (item != null && t - itemAt <= 6 && itemAt >= lastClickedAt && !item.StartsWith("Heroic Transport", StringComparison.Ordinal))
                                fromItem = item;
                            else if (lastBagItem != null && t - lastBagItemAt <= 6 && lastBagItemAt >= lastClickedAt)
                                fromItem = lastBagItem;
                            else
                                foreach (RecEvent g in goneItems)
                                {
                                    // (not an item the game took away as another mission ended right then)
                                    if (g.Elapsed > t + 1 || t - g.Elapsed > 6 || g.Elapsed < lastClickedAt || g.Option.StartsWith("Heroic Transport", StringComparison.Ordinal))
                                        continue;
                                    bool ended = false;
                                    foreach (RecEvent x in events)
                                        if ((x.Kind == EvKind.QuestCompleted || x.Kind == EvKind.QuestRemoved || x.Kind == EvKind.QuestAbandoned) && Math.Abs(x.Elapsed - g.Elapsed) <= 1.0)
                                            ended = true;
                                    if (!ended)
                                        fromItem = g.Option;
                                }
                            if (fromItem != null)
                            {
                                s.Unsure = true;
                                AddNote(s, "it came right after you used " + fromItem + ": if the bot doesn't get it, use " + fromItem + " by hand");
                            }
                            else if (e.Note == How.Holocom)
                            {
                                s.Unsure = true;
                                AddNote(s, "it came in a holocom call: if the bot doesn't get it, answer the call by hand");
                            }
                        }
                        pickupOf[e.QuestId] = s;
                        steps.Add(s);
                        break;
                    }
                    case EvKind.QuestTask:
                    case EvKind.QuestStep:
                    {
                        if (e.QuestId == 0 || IsSystemQuest(e))
                            break; // (a companion conversation cooldown has nothing to do in the world)
                        if (pendingEquip != null && t - pendingEquipAt <= 20 && !e.QuestHidden && !hiddenQuests.Contains(e.QuestId))
                        {
                            string eq = Fmt.Hex(e.QuestId);
                            // (the gate goes into the profile as it is: the name quoted for the condition, then XML-escaped)
                            string worn = A(Fmt.PyStr(pendingEquip));
                            var eqLines = new List<string> { "<EquipItem ItemName=\"" + A(pendingEquip) + "\" />" };
                            Step put = AddStep(steps, "Item", "HasQuest(" + eq + ") and not HasItemEquipped(" + worn + ")", "Put on " + pendingEquip, eqLines, e.QuestId);
                            AddNote(put, "like you did, right before " + e.QuestName + " moved on");
                            pendingEquip = null;
                        }
                        if (e.QuestHidden || hiddenQuests.Contains(e.QuestId)) // the game marks its helper quests hidden only sometimes
                        {
                            // A loot that moved the game's own helper quest (Black Hole Glow: looting a Radiation Treatment
                            // Droid gives the anti-rad injector that keeps you alive on the lower level): kill and loot that
                            // mob there, until the helper quest has moved on. Without it the bot walks into the radiation.
                            if (e.Kind == EvKind.QuestStep && lastLoot != null && t - lastLootAt <= 4 && lastLoot.Name.Length > 0)
                            {
                                string hq = Fmt.Hex(e.QuestId);
                                ulong hb = e.HasOldStep ? e.OldBranch : e.Branch, hs = e.HasOldStep ? e.OldStep : e.Step;
                                var lootStep = new Step
                                {
                                    Kind = "Grind",
                                    Gate = Fmt.Format("HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})", hq, hb, hs),
                                    Comment = "Kill and loot " + lastLoot.Name,
                                    Note = "your loot moved one of the game's hidden helper quests",
                                    Area = lastLoot.Name,
                                    Quest = e.QuestId,
                                    Kills = new List<Kill> { new Kill { Name = lastLoot.Name, Pos = lastLoot.HasPos ? lastLoot.Pos : e.Pos, At = t, Leg = leg, Spec = lastLoot.Spec } }
                                };
                                steps.Add(lootStep);
                                lastLoot = null;
                                break;
                            }
                            // A bag item (a stim) that moved the game's helper quest: use it here, where you did (Glow: the
                            // stim that keeps you alive on the lower level). Used once; the helper quest moving on ends it.
                            // (not when a normal mission moved at the same moment: a medpac in a Black Hole fight isn't a step)
                            // (not a count of a bonus mission that started hidden and shows now, when you clicked a mission object
                            // after the stim: the click moved it - Section X Cleanse the Cannon)
                            if (lastBagItem != null && t - lastBagItemAt <= 6 && !NormalTickNear(events, hiddenQuests, t) &&
                                (e.QuestHidden || !(e.Kind == EvKind.QuestTask && e.Max > 0 && uses.Exists(u => u.ForQuest && u.At >= lastBagItemAt && u.At <= t))))
                            {
                                string iq = Fmt.Hex(e.QuestId);
                                ulong ib = e.HasOldStep ? e.OldBranch : e.Branch, ist = e.HasOldStep ? e.OldStep : e.Step;
                                var useStep = new Step
                                {
                                    Kind = "Item",
                                    Gate = Fmt.Format("HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})", iq, ib, ist),
                                    Comment = "Use " + lastBagItem,
                                    Note = "it moved the game's helper quest (e.g. a stim that keeps you alive)",
                                    Quest = e.QuestId
                                };
                                useStep.Lines.Add("<MoveTo QuestId=\"1\" Name=\"Where you used " + A(lastBagItem) + "\" XYZ=\"" + X(lastBagItemPos) + "\" />");
                                useStep.Lines.Add("<UseMissionItemOnce ItemName=\"" + A(lastBagItem) + "\" />");
                                steps.Add(useStep);
                                // that stim is done: it doesn't carry on to the next step or into a row of item uses
                                if (item == lastBagItem)
                                    item = null;
                                itemOrder.RemoveAll(u => u.Name == lastBagItem);
                                lastBagItem = null;
                                break;
                            }
                            // The same with a mission item (an injector you pressed from the mission items): use it here, once.
                            // (not when a normal mission moved at the same moment: then the item belongs to that step)
                            // (only a quest hidden right now: a bonus mission that started hidden keeps its normal count steps)
                            if (e.QuestHidden && item != null && t - itemAt <= 6 && !item.StartsWith("Heroic Transport", StringComparison.Ordinal) &&
                                !NormalTickNear(events, hiddenQuests, t))
                            {
                                string mq = Fmt.Hex(e.QuestId);
                                ulong mb = e.HasOldStep ? e.OldBranch : e.Branch, ms = e.HasOldStep ? e.OldStep : e.Step;
                                var mStep = new Step
                                {
                                    Kind = "Item",
                                    Gate = Fmt.Format("HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})", mq, mb, ms),
                                    Comment = "Use " + item,
                                    Note = "it moved the game's helper quest (e.g. an injector that keeps you alive)",
                                    Quest = e.QuestId
                                };
                                Vec3 at = itemPos.IsZero ? e.Pos : itemPos;
                                mStep.Lines.Add("<MoveTo QuestId=\"1\" Name=\"Where you used " + A(item) + "\" XYZ=\"" + X(at) + "\" />");
                                mStep.Lines.Add("<UseMissionItemOnce ItemName=\"" + A(item) + "\" />");
                                steps.Add(mStep);
                                string doneItem = item;
                                itemOrder.RemoveAll(u => u.Name == doneItem);
                                item = null;
                                break;
                            }
                            // only skip what the game marks hidden right now: a bonus mission starts hidden and then
                            // counts normally (Section X Cleanse the Cannon), its counts must stay in the profile
                            if (e.QuestHidden)
                                break;
                        }
                        if (IsMetaTick(e, metaTasks))
                            break; // a weekly's "Complete X" ticks by itself (another mission ending moved it)
                        // ...and its last one, which shows as its step finishing: the other mission's own end does that
                        // (it must not take the clicks or the fight that finished the other mission)
                        if (e.Kind == EvKind.QuestStep && e.HasOldStep && metaSteps.Contains(Fmt.Hex(e.QuestId) + "|" + e.OldStep.ToString(CultureInfo.InvariantCulture)) &&
                            OtherEndNear(events, hiddenQuests, e.QuestId, t))
                            break;
                        string q = Fmt.Hex(e.QuestId);
                        bool isCount = e.Kind == EvKind.QuestTask && e.Max > 0;
                        int task = e.TaskIndex >= 0 ? e.TaskIndex : 0;
                        // another branch right after your replies: the replies may have picked it (checked after the build)
                        if (e.Kind == EvKind.QuestStep && e.HasOldStep && e.OldBranch != e.Branch && t - lastPickAt <= 15)
                            branchedAt.Add(new KeyValuePair<ulong, double>(e.QuestId, t));
                        string gate, what;
                        if (isCount)
                        {
                            gate = Fmt.Format("HasQuest({0}) and not IsTaskValueGreaterThan({0}, {1}, {2}, {3})", q, e.Step, task, e.Count > 0 ? e.Count - 1 : 0);
                            what = Fmt.Format("{0}: {1} ({2}/{3})", e.QuestName, e.Text, e.Count, e.Max);
                            countsSeen[q + "|" + e.Step + "|" + task] = new[] { (long)e.Count, (long)e.Max };
                            taskTexts[q + "|" + e.Step + "|" + task] = e.Text;
                            tickBranch = e.Branch.ToString(CultureInfo.InvariantCulture);
                            // the kill right before this count moved it: remember which task it counted for
                            if (allKills.Count > 0 && t - allKills[allKills.Count - 1].At <= 1.0 &&
                                (e.TaskTargets.Count == 0 || allKills[allKills.Count - 1].Spec == 0 || e.TaskTargets.Contains(allKills[allKills.Count - 1].Spec))) // only if the game counts that mob type for this task
                                allKills[allKills.Count - 1].Credit.Add(q + "|" + task);
                        }
                        else
                        {
                            ulong ob = e.HasOldStep ? e.OldBranch : e.Branch;
                            ulong os = e.HasOldStep ? e.OldStep : e.Step;
                            gate = Fmt.Format("HasQuest({0}) and not IsBranchStepComplete({0}, {1}, {2})", q, ob, os);
                            what = Fmt.Format("{0}: step {2} done{3}", e.QuestName, ob, os, e.Text.Length > 0 ? " (next: " + e.Text + ")" : string.Empty);
                            // The last count of a task finished the step (Staff Cuts 34/35 -> step done): write it as
                            // that count, 35/35. Only when exactly one task of the step was one short.
                            string oneShort = null;
                            long oneShortMax = 0;
                            int shortTasks = 0;
                            foreach (KeyValuePair<string, long[]> cs in countsSeen)
                            {
                                if (!cs.Key.StartsWith(q + "|" + os + "|", StringComparison.Ordinal) || cs.Value[0] >= cs.Value[1])
                                    continue;
                                shortTasks++;
                                if (cs.Value[0] == cs.Value[1] - 1)
                                {
                                    oneShort = cs.Key.Substring(cs.Key.LastIndexOf('|') + 1);
                                    oneShortMax = cs.Value[1];
                                }
                            }
                            if (shortTasks == 1 && oneShort != null)
                            {
                                string tt;
                                taskTexts.TryGetValue(q + "|" + os + "|" + oneShort, out tt);
                                gate = Fmt.Format("HasQuest({0}) and not IsTaskValueGreaterThan({0}, {1}, {2}, {3})", q, os, oneShort, oneShortMax - 1);
                                what = Fmt.Format("{0}: {1} ({2}/{2}) - step {3} done{4}", e.QuestName, tt ?? "count", oneShortMax, os,
                                                  e.Text.Length > 0 ? " (next: " + e.Text + ")" : string.Empty);
                            }
                            tickBranch = ob.ToString(CultureInfo.InvariantCulture);
                            // that step is over: its counts are history (another branch can use the same step number for
                            // other tasks - a count left from this one must not make that step's finish "one short")
                            if (e.Kind == EvKind.QuestStep && e.HasOldStep && (e.OldStep != e.Step || e.OldBranch != e.Branch))
                                foreach (string dk in new List<string>(countsSeen.Keys))
                                    if (dk.StartsWith(q + "|" + os + "|", StringComparison.Ordinal))
                                    {
                                        countsSeen.Remove(dk);
                                        taskTexts.Remove(dk);
                                    }
                        }
                        tickQuest = e.QuestId;
                        // the same moment: another quest counting the same action, or this quest's task finishing its step
                        if (e.Kind == EvKind.QuestStep || (isCount && e.Count >= e.Max))
                            finalAt[e.QuestId] = t; // this tick finished the step / count
                        bool sameMoment = t - lastProgressAt <= 1.0;
                        // (not when a second use of an item is still waiting for its count: two prisoners freed with one
                        // Shackle Remote, both counts within a second - each count is its own use on its own NPC)
                        if (sameMoment && item != null && t - itemAt <= 20 && isCount && lastProgressQuest == e.QuestId)
                            sameMoment = false;
                        // the mobs that died just now (for the fight the last tick made)
                        List<Kill> justDied = allKills.FindAll(k => k.At <= t + 0.01 && t - k.At <= 4.0);
                        // (not another quest's count whose mob types the game lists, when none of the mobs that just died is one of
                        // them: the kill didn't move it - you walked into its spot or did something else - it gets its own step)
                        if (sameMoment && lastProgressQuest != e.QuestId && isCount && e.TaskTargets.Count > 0 && steps.Count > 0 &&
                            steps[steps.Count - 1].Kind == "Grind" && steps[steps.Count - 1].Quest != e.QuestId &&
                            justDied.Count > 0 && justDied.TrueForAll(k => k.Spec != 0 && !e.TaskTargets.Contains(k.Spec)))
                            sameMoment = false;
                        if (sameMoment && (lastProgressQuest != e.QuestId || lastProgressKind == EvKind.QuestTask)) // another quest, or a second count / the step of this quest ticking for the same action (Section X Hyperbiology ticks two counts per sample)
                        {
                            // One kill counting for two quests (Control the Battlefield + Asset Liquidation): the fight
                            // keeps going until this quest's count is reached too.
                            Step fight = steps.Count > 0 ? steps[steps.Count - 1] : null;
                            // the same kills moved another task of this quest's step too (a named mob that also counts toward
                            // "Forces", an AoE that killed two kinds): the fight goes on until that task is done too, and hunts its mobs
                            List<Kill> forTask = isCount && e.TaskTargets.Count > 0 ? justDied.FindAll(k => k.Spec != 0 && e.TaskTargets.Contains(k.Spec)) : new List<Kill>();
                            if (fight != null && fight.Kind == "Grind" && fight.Quest == e.QuestId && isCount && e.TaskIndex != fight.Task && fight.Kills != null &&
                                forTask.Count > 0 && fight.Gate != null && fight.Gate.IndexOf("IsTaskValueGreaterThan(" + q + ", " + e.Step + ", ", StringComparison.Ordinal) >= 0 &&
                                fight.Comment.IndexOf("(kill and loot)", StringComparison.Ordinal) < 0)
                            {
                                if (fight.AlsoTasks == null)
                                    fight.AlsoTasks = new Dictionary<int, string>();
                                fight.AlsoTasks[task] = gate;
                                foreach (Kill k in forTask)
                                {
                                    k.Credit.Add(q + "|" + task);
                                    if (!fight.Kills.Contains(k))
                                        fight.Kills.Add(k);
                                }
                                if (fight.Targets != null)
                                {
                                    var both = new List<ulong>(fight.Targets);
                                    foreach (ulong sp in e.TaskTargets)
                                        if (!both.Contains(sp))
                                            both.Add(sp);
                                    fight.Targets = both;
                                }
                                if (e.Text.Length > 0 && fight.Comment.IndexOf("; " + e.Text, StringComparison.Ordinal) < 0)
                                    fight.Comment += "; " + e.Text + " (" + e.Count + "/" + e.Max + ")";
                            }
                            else if (fight != null && fight.Kind == "Grind" && fight.Quest != e.QuestId && kills.Count + (fight.Kills != null ? 1 : 0) > 0)
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
                            else if (isCount && sameTake != null && sameTakeQuest != e.QuestId && t - sameTakeAt <= 1.0)
                            {
                                // one click counted for two quests: this count gets that click too, next to its earlier clicks
                                string ids2 = Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\" TaskId=\"{3}\" TaskValue=\"{4}\"", q, e.Branch, e.Step, task, e.Count > 0 ? e.Count - 1 : 0);
                                var cs2 = new Step { Kind = "Use", Gate = gate, Comment = what, Quest = e.QuestId, At = t, Branch = e.Branch.ToString(CultureInfo.InvariantCulture) };
                                foreach (Use u in sameTake)
                                    cs2.Lines.AddRange(UseAt(u.Name, u.Pos, u.Stand, ids2));
                                int at2 = steps.FindLastIndex(st => st.Quest == sameTakeQuest && Math.Abs(st.At - sameTakeAt) < 0.05);
                                steps.Insert(at2 >= 0 ? at2 : steps.Count, cs2);
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
                            var route = new Step { Kind = "Route", Comment = "Walk" };
                            AddRoute(route, markers);
                            steps.Add(route);
                            markers.Clear();
                        }

                        // what you clicked since the last progress, in your order, at the exact spots: that's the step
                        // (no guessing which object the task means from its name)
                        List<string> ow = Words(doneText);
                        var recent = uses.FindAll(u => t - u.At <= (isCount ? 30 : 180)); // a puzzle can take a few minutes
                        // the conversation this step was done with, if one is on: a holocom call, or a talk (one you started
                        // yourself by clicking them counts until it ends - a story talk can run well past 20 s)
                        bool callNow = t - holoAt <= 20 || (CallLasted(convos, holoAt, t) &&
                                       (holoQuest == 0 || holoQuest == e.QuestId || hiddenQuests.Contains(holoQuest) || Math.Abs(holoQuestAt - holoAt) > 30));
                        bool talkNow = talk != null && ConvoOnAt(convos, talkAt, t) && (t - talkAt <= 20 || (talkClicked && !talkGave && SceneOn(convos, talkAt, t)));
                        // what you clicked before that conversation and the game didn't mark as for a mission (a loot crate on the
                        // way) didn't do this step - the talk did (a click that opened the conversation, or came after it, stays)
                        double convAt = Math.Max(callNow ? holoAt : -1e9, talkNow && talkClicked && talk.Kind != ObjKind.Placeable ? talkAt : -1e9);
                        if (convAt > -1e8)
                        {
                            recent = recent.FindAll(u => u.ForQuest || u.At >= convAt - 6);
                            if (item != null && t - itemAt <= 20 && itemAt < convAt - 6)
                                item = null; // (a mission item used before it, the same)
                        }
                        // a scene that started by itself right after the last kill (a boss that talks once beaten): the kill moved
                        // this, as if it had moved right then - unless the step was to speak to someone
                        double killAt = KillMoment(kills, sceneAfterKillAt, convos, t);
                        if (killAt < t && NpcFromStepText(doneText) != null)
                            killAt = t;
                        var named = new List<Use>();
                        bool keepRest = false;
                        List<Use> take = recent;
                        // a kill right before the tick and no object the objective names.
                        var spawners = new List<Use>();
                        if (named.Count == 0 && kills.Count > 0 && killAt - kills[kills.Count - 1].At <= 3 && (take.Count == 0 || t - take[take.Count - 1].At > 3))
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
                        // A step finished after several objects - or a count after several clicks on objects of one name (one
                        // spent, or not the one that counted): the earlier ones are clicked once each, only the last one waits
                        // for the count (each waiting for it, the bot clicked the first one forever)
                        if (take.Count > 1 && (!isCount || (spawners.Count == 0 && !keepRest && take.TrueForAll(u => u.Name == take[0].Name))))
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
                            lines.Add("<UserDialog Title=\"Quest Recorder\" Message=\"" + A("Do the " + at.Name + " by hand, then press Continue.") + "\" IsBotContinueAllowed=\"True\" />");
                            foreach (Use u in take)
                            {
                                consumed[UseKey(u.Id, u.Pos)] = t;
                                uses.Remove(u);
                            }
                            AddNote(AddStep(steps, "Use", gate, what, lines, e.QuestId), "do it by hand");
                            break;
                        }
                        bool killCause = kills.Count > 0 && killAt - kills[kills.Count - 1].At <= 3;
                        // an object use that ended within 3 s of the tick did it, even if a mob died at the same moment
                        if (take.Count > 0 && t - take[take.Count - 1].At <= 3)
                            killCause = false;
                        // a step a fight finished: what you clicked before it is clicked once (End of Torvix: the Master Safety
                        // Control brings the waves), not used again and again until the step is done
                        bool clickOnce = killCause && !isCount;
                        // Every use in the order you did it, tied to the quest so it can never loop.
                        string countIds = isCount
                            ? Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\" TaskId=\"{3}\" TaskValue=\"{4}\"", q, e.Branch, e.Step, task, e.Count > 0 ? e.Count - 1 : 0)
                            : useIds;
                        foreach (Use u in take)
                        {
                            if (spawners.Contains(u) || clickOnce)
                            {
                                // the object only starts the fight. Clicked by its exact spot (Quest Recorder's ClickObject):
                                // BuddyCron's Interact takes the one nearest the bot and skips one it clicked for 30 s, so
                                // a row of clicks on side-by-side objects (Torvix Pipeline Controls) came out wrong.
                                // The bot stands where you stood; before a fight it then walks up to it until it can click it.
                                lines.Add("<ClickObject QuestId=\"" + q + "\" Name=\"" + A(u.Name) + "\" XYZ=\"" + X(u.Pos) + "\"" +
                                          (!u.Stand.IsZero || killCause ? " Stand=\"" + X(u.Stand.IsZero ? u.Pos : u.Stand) + "\"" : string.Empty) +
                                          (killCause ? " Fight=\"True\"" : string.Empty) + " />");
                                consumed[UseKey(u.Id, u.Pos)] = t;
                                uses.Remove(u);
                                continue;
                            }
                            lines.AddRange(UseAt(u.Name, u.Pos, u.Stand, countIds));
                            consumed[UseKey(u.Id, u.Pos)] = t;
                            uses.Remove(u);
                        }
                        // the clicks this tick took: another quest counting the same click a moment later gets them too
                        var plainUses = take.FindAll(u => !spawners.Contains(u) && !clickOnce);
                        if (plainUses.Count > 0)
                        {
                            sameTake = plainUses;
                            sameTakeAt = t;
                            sameTakeQuest = e.QuestId;
                        }
                        if (named.Count == 0 && !keepRest)
                            uses.Clear();

                        if (killCause)
                        {
                            // a boss step keeps every mob from the whole fight, in the order you killed them.
                            double from = Math.Min(fightAt, killAt - 3);
                            var use = kills.FindAll(k => isCount || k.At >= from - 1);
                            if (killAt < t)
                            {
                                // the scene after the kill was part of the fight, not a talk for a later step
                                if (talk != null && talkAt == sceneAfterKillAt)
                                    talk = null;
                                sceneAfterKillAt = -1e9;
                            }
                            // a mission item you used just before this fight, where its mob then died, and that mob wasn't
                            // around before (a lure, a totem): used once first (not when an object you clicked brought it out)
                            Use preFight = lines.Count == 0 && !(giverUse != null && giverUse.At >= fightAt - 15 && fightAt >= giverUse.At - 1 && giverUse.At <= t)
                                           ? PreFightItem(orderNow, use, mobSeen, allKills, fightAt) : null;
                            if (preFight != null)
                                lines.AddRange(PreFightLines(preFight, q));
                            Step prev = steps.Count > 0 ? steps[steps.Count - 1] : null;
                            // (only that same count of that same step: once the last kill finished a step, the fight waits for
                            // that step - the next step's first count of the same task number is a fight of its own)
                            if (isCount && lines.Count == 0 && prev != null && prev.Kind == "Grind" && prev.Quest == e.QuestId && prev.Task == e.TaskIndex &&
                                prev.Gate != null && prev.Gate.IndexOf("IsTaskValueGreaterThan(" + q + ", " + e.Step + ", " + task + ", ", StringComparison.Ordinal) >= 0)
                            {
                                prev.Gate = gate; // the same kill count going on: its stop point moves up
                                prev.Comment = what;
                                prev.Kills.AddRange(use);
                            }
                            else
                            {
                                var g = new Step { Kind = "Grind", Gate = gate, Comment = what, Quest = e.QuestId, Task = e.TaskIndex, Targets = e.TaskTargets.Count > 0 ? e.TaskTargets : null, Kills = use };
                                g.Lines.AddRange(lines);
                                // the object you clicked for a pick-up brought this fight out (the Field Generator): click it first
                                // (only a fight that started after that click, within 15 s, near it)
                                if (giverUse != null && giverUse.At >= fightAt - 15 && fightAt >= giverUse.At - 1 && giverUse.At <= t && use.Exists(k => k.Pos.DistanceTo(giverUse.Pos) <= 1f) &&
                                    !g.Lines.Exists(l => Attr(l, "Name") == A(giverUse.Name) || Attr(l, "ObjectName") == A(giverUse.Name)))
                                {
                                    g.Lines.Insert(0, "<ClickObject QuestId=\"" + q + "\" Name=\"" + A(giverUse.Name) + "\" XYZ=\"" + X(giverUse.Pos) + "\" Stand=\"" +
                                                      X(giverUse.Stand.IsZero ? giverUse.Pos : giverUse.Stand) + "\" Fight=\"True\" />");
                                    giverUse = null;
                                }
                                if (preFight != null)
                                    AddNote(g, "uses " + preFight.Name + " first, as you did right before this fight");
                                steps.Add(g);
                            }
                            // an item used before that kill wasn't what moved this count, and it doesn't carry on to the next
                            // step either (a medpac pressed mid-fight became "use Medpac" on the next talk)
                            if (item != null && itemAt <= kills[kills.Count - 1].At)
                                item = null;
                            kills.Clear();
                            break;
                        }
                        // kept for a count you got by looting what the mobs dropped (the tick comes after the loot, not the kill)
                        List<Kill> lootKills = kills.FindAll(k => t - k.At <= 20);
                        kills.Clear();
                        // a stim or medpac you used before a click that moved this: the click did it, the stim isn't part of it
                        if (item != null && item == lastBagItem && Math.Abs(itemAt - lastBagItemAt) < 0.01 && take.Exists(u => u.At >= itemAt))
                            item = null;
                        if (item != null && t - itemAt <= 20)
                        {
                            // a quest item you used right before (UseItem fires it at the spot you used it)
                            string itemIds = isCount
                                ? Fmt.Format(" BranchId=\"{0}\" StepId=\"{1}\" TaskId=\"{2}\" TaskValue=\"{3}\"", e.Branch, e.Step, task, e.Count > 0 ? e.Count - 1 : 0) // done as soon as this count goes up
                                : Fmt.Format(" BranchId=\"{0}\" StepId=\"{1}\"", e.HasOldStep ? e.OldBranch : e.Branch, e.HasOldStep ? e.OldStep : e.Step);
                            // the same item used on two NPCs before the first tick (two prisoners, one Shackle Remote): this
                            // tick is the first use, the next one waits for the next tick - each on its own NPC
                            List<Use> sameUses = orderNow.FindAll(u => u.Name == item && t - u.At <= 20);
                            Use nextUse = null;
                            if (sameUses.Count > 1 && sameUses.Exists(u => u.On != null && u.On != sameUses[0].On))
                            {
                                itemOn = sameUses[0].On;
                                itemOnDist = sameUses[0].OnDist;
                                itemOnDead = sameUses[0].OnDead;
                                itemOnHealth = sameUses[0].OnHealth;
                                itemPos = sameUses[0].Pos;
                                nextUse = sameUses[1];
                                for (int su = 1; su < sameUses.Count; su++)
                                    itemOrder.Add(sameUses[su]);
                            }
                            Vec3 usedAt = itemPos.IsZero ? e.Pos : itemPos;
                            // used on an NPC (a scanner on a spy): on the nearest one of that name around the spot, like the Korriban ID Scanner (1.5 m, radius 200)
                            // (used in a fight: may be used in a fight again)
                            // a corpse: BuddyCron only uses it on dead ones with DeadOnly. Used in a fight on a weakened mob
                            // ("weaken it, then use the item"): fight it first, use it once its health is down to where you did.
                            string howNpc = itemOnDead ? " DeadOnly=\"True\""
                                          : itemInCombat && itemOnHealth > 0f && itemOnHealth < 95f
                                              ? " HealthPercent=\"" + Math.Min(100, (int)Math.Ceiling(itemOnHealth) + 5).ToString(CultureInfo.InvariantCulture) + "\""
                                              : string.Empty;
                            // a mob you killed right after using the item on it (Section X Hyperbeast + Serum Injector): it attacks
                            // as soon as the bot walks up, and BuddyCron's UseItem with InCombat="False" gives up once a fight starts,
                            // so the item was never used - allow the use in the fight
                            bool killedAfter = false;
                            if (itemOn != null)
                                foreach (RecEvent ke in events)
                                    if (ke.Kind == EvKind.Kill && ke.Target != null && ke.Target.Name == itemOn && ke.Elapsed >= t - 1 && ke.Elapsed <= t + 120)
                                        killedAfter = true;
                            string onNpc = itemOn != null ? " Range=\"" + UseRangeFor(itemOnDist) + "\" Radius=\"200\" InCombat=\"" + (itemInCombat || killedAfter ? "True" : "False") + "\"" + howNpc + "><Npcs><Npc>" + A(itemOn) + "</Npc></Npcs>" : ">";
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
                                    // on the NPC you used it on, from as close as you stood (a corpse: DeadOnly)
                                    // (used in a fight, or on a mob that fought you right after: it may be used in a fight)
                                    lines.Add("<UseMissionItemOnce ItemName=\"" + A(u.Name) + "\"" +
                                              (u.On != null ? " Npc=\"" + A(u.On) + "\" Range=\"" + UseRangeFor(u.OnDist) + "\"" + (u.OnDead ? " DeadOnly=\"True\"" : string.Empty) : string.Empty) +
                                              (u.InFight || (u.On != null && KilledSoonAfter(events, u.On, u.At)) ? " InCombat=\"True\"" : string.Empty) + " />");
                                }
                                // the last one where you used it, when that was somewhere else
                                if (!itemPos.IsZero && lastSpot.DistanceTo(itemPos) > 5f)
                                    lines.Add("<MoveTo QuestId=\"" + q + "\" Name=\"Where you used " + A(item) + "\" XYZ=\"" + X(itemPos) + "\" />");
                                // the last one once too: if the order went wrong, the whole row is tried again next round
                                // instead of pressing one rune forever
                                lines.Add("<UseMissionItemOnce ItemName=\"" + A(item) + "\"" +
                                          (itemOn != null ? " Npc=\"" + A(itemOn) + "\" Range=\"" + UseRangeFor(itemOnDist) + "\"" + (itemOnDead ? " DeadOnly=\"True\"" : string.Empty) : string.Empty) +
                                          (itemInCombat || killedAfter ? " InCombat=\"True\"" : string.Empty) + " />");
                                lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"3\" />");
                            }
                            else
                            {
                                // a count done with this item: every spot you used it, in your order (also the ones that
                                // didn't count), so the bot tries them all like you did until the count goes up
                                string spots = "<Hotspot Name=\"Where you used it\" XYZ=\"" + X(usedAt) + "\" />";
                                List<Vec3> all;
                                // (also the last count, which shows up as "step done": that step's counts were done with it too)
                                // (a step change carries no task number: any count of that step done with this item)
                                bool countTask = isCount;
                                if (!countTask && e.HasOldStep)
                                {
                                    string stepKey = q + "/" + e.OldStep + "/";
                                    foreach (KeyValuePair<string, string> kv in itemForTask)
                                        if (kv.Key.StartsWith(stepKey, StringComparison.Ordinal) && kv.Value == item)
                                            countTask = true;
                                }
                                // (only the spots near this one - within ~150 m - not every spot that item was ever used, from
                                // other steps or other places)
                                if (countTask && itemSpots.TryGetValue(item, out all) && all.FindAll(v => v.DistanceTo(usedAt) <= 15f).Count > 1)
                                {
                                    spots = string.Empty;
                                    foreach (Vec3 v in all)
                                        if (v.DistanceTo(usedAt) <= 15f)
                                            spots += "<Hotspot Name=\"Where you used it\" XYZ=\"" + X(v) + "\" />";
                                }
                                lines.Add("<UseItem QuestId=\"" + q + "\"" + itemIds + " ItemName=\"" + A(item) + "\" WaitTime=\"3000\" XYZ=\"" + X(usedAt) + "\"" + onNpc +
                                          "<Hotspots>" + spots + "</Hotspots></UseItem>");
                            }
                            itemForTask[q + "/" + e.Step + "/" + task] = item; // this task is done with this item
                            if (nextUse != null)
                            {
                                // the next use of the same item (on the next NPC) belongs to the next tick
                                itemAt = nextUse.At;
                                itemPos = nextUse.Pos;
                                itemOn = nextUse.On;
                                itemOnDist = nextUse.OnDist;
                                itemOnDead = nextUse.OnDead;
                                itemOnHealth = nextUse.OnHealth;
                            }
                            else
                                item = null;
                            AddStep(steps, "Item", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (take.Count > 0)
                        {
                            AddStep(steps, "Use", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (callNow)
                        {
                            lines.Add("<AnswerHolocomCall QuestName=\"" + A(e.QuestName) + "\" QuestId=\"" + q + "\" />");
                            holoAt = -1e9;
                            AddStep(steps, "Holocom", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (talkNow && !UsedUp(talk, consumed, t))
                        {
                            lines.Add("<" + (talk.Kind == ObjKind.Placeable ? "Interact" : "TalkTo") + " QuestId=\"" + q + "\" Name=\"" + A(talk.Name) + "\" WaitTime=\"5\" XYZ=\"" +
                                      X(talk.HasPos ? talk.Pos : e.Pos) + "\" />");
                            talk = null;
                            AddStep(steps, "Talk", gate, what, lines, e.QuestId);
                            break;
                        }
                        // the talk was credited to an object you had already used (older recordings): the NPC the step names
                        // ("Speak to Tookreek"), where you clicked them or where it ticked
                        string saidTo = talk != null && t - talkAt <= 20 && UsedUp(talk, consumed, t) && ConvoOnAt(convos, talkAt, t) ? NpcFromStepText(doneText) : null;
                        if (saidTo != null && !killedNames.Contains(saidTo) && saidTo.IndexOf("Dropbox", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            Vec3 sp;
                            if (!npcSpots.TryGetValue(saidTo, out sp))
                                sp = e.Pos;
                            lines.Add("<TalkTo QuestId=\"" + q + "\" Name=\"" + A(saidTo) + "\" WaitTime=\"5\" XYZ=\"" + X(sp) + "\" />");
                            Step said = AddStep(steps, "Talk", gate, what, lines, e.QuestId);
                            said.Unsure = true;
                            AddNote(said, "the talk was credited to " + talk.Name + ", which you had already used: talks to the NPC the step names");
                            talk = null;
                            break;
                        }
                        // A count that moved right after you looted a mob whose kill already counted for another task (Section X
                        // Lost Reconnaissance: the Tuk'ata counted as a Wild Beast, then its loot was a Flight Recorder): kill and
                        // loot that mob for this count too.
                        if (isCount && lootKills.Count == 0 && lastLoot != null && lastLoot.Kind == ObjKind.Npc && t - lastLootAt <= 2.5 && actionAt <= lastLootAt)
                        {
                            List<Kill> again = allKills.FindAll(k => k.Name == lastLoot.Name && t - k.At <= 30);
                            if (again.Count > 0)
                                lootKills = new List<Kill> { new Kill { Name = again[again.Count - 1].Name, Pos = again[again.Count - 1].Pos, At = again[again.Count - 1].At, Leg = again[again.Count - 1].Leg, Spec = again[again.Count - 1].Spec } };
                        }
                        if (isCount && lootKills.Count > 0)
                        {
                            // Story quests "Collect 6 datapads" style: you killed, then looted, then it ticked.
                            var lg = new Step { Kind = "Grind", Gate = gate, Comment = what + " (kill and loot)", Quest = e.QuestId, Task = e.TaskIndex, Targets = e.TaskTargets.Count > 0 ? e.TaskTargets : null, Kills = lootKills };
                            lg.Lines.AddRange(lines);
                            steps.Add(lg);
                            break;
                        }
                        // (only when the loot was the last thing you did - within 2.5 s, no cast or click after it: in CZ-198
                        // the Pipes / Junction Box casts right at the tick did those steps, not the loot a few seconds before)
                        // (the mob may have died a while before and already counted for another mission's fight - you fought on and
                        // came back for its body: Section X Targeted Misfire, the Jedi Special Operative)
                        List<Kill> stepLoot = lastLoot != null && !lootKills.Exists(k => k.Name == lastLoot.Name)
                            ? allKills.FindAll(k => k.Name == lastLoot.Name && t - k.At <= 60 && k.Pos.DistanceTo(lastLoot.HasPos ? lastLoot.Pos : e.Pos) <= 3f)
                            : lootKills;
                        if (!isCount && lastLoot != null && t - lastLootAt <= 2.5 && actionAt <= lastLootAt && stepLoot.Exists(k => k.Name == lastLoot.Name))
                        {
                            // a step finished by looting a mob you had just killed (Section X Hyperbiology: kill the Hyperbeast,
                            // loot its tissue): kill and loot it until the step is done
                            string lootName = lastLoot.Name;
                            var ls = new Step { Kind = "Grind", Gate = gate, Comment = what + " (kill and loot)", Quest = e.QuestId, Task = e.TaskIndex, Targets = e.TaskTargets.Count > 0 ? e.TaskTargets : null,
                                                Kills = stepLoot.FindAll(k => k.Name == lootName).ConvertAll(k => new Kill { Name = k.Name, Pos = k.Pos, At = k.At, Leg = k.Leg, Spec = k.Spec }) };
                            ls.Lines.AddRange(lines);
                            steps.Add(ls);
                            lastLoot = null;
                            break;
                        }
                        if (isCount && npcClickName != null && t - npcClickAt <= 6 && killedNames.Contains(npcClickName))
                        {
                            // you looted the body of a mob you had killed.
                            var lk = new Step { Kind = "Grind", Gate = gate, Comment = what + " (kill and loot)", Quest = e.QuestId, Task = e.TaskIndex, Targets = e.TaskTargets.Count > 0 ? e.TaskTargets : null,
                                                Kills = new List<Kill> { new Kill { Name = npcClickName, Pos = npcClickPos, At = t, Leg = leg } } };
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
                            AddNote(AddStep(steps, "Item", gate, what, lines, e.QuestId), "your " + sameItem);
                            break;
                        }
                        // (no guessing from names: a tick with no click of yours just walks to where it ticked)
                        if (npcClickName != null && !npcClickLoot && t - npcClickAt <= 30 && !killedNames.Contains(npcClickName) && !followers.Contains(npcClickName) &&
                            (e.Pos.IsZero || e.Pos.DistanceTo(npcClickPos) <= 1.5f))
                        {
                            // you clicked an NPC (no conversation) and the count went up a bit later (Black Hole 'Identify.
                            lines.Add("<TalkTo QuestId=\"" + q + "\" Name=\"" + A(npcClickName) + "\" WaitTime=\"5\" XYZ=\"" + X(npcClickPos) + "\" />");
                            npcClickName = null;
                            AddStep(steps, "Talk", gate, what, lines, e.QuestId);
                            break;
                        }
                        if (hiddenQuests.Contains(e.QuestId) && !isCount)
                            break; // a hidden helper quest moving on by itself (the game's own bookkeeping): nothing to walk to
                        // You targeted an NPC just before and nothing else of yours was seen, but a mission item you held left
                        // your bags later in this same mission (Section X Hyperbiology: the Serum Injector used on the friendly
                        // Hyperbeast - no cast bar, no cooldown; the game took the injector away two steps later). Use that
                        // item on that NPC. Marked "not sure" (yellow): the game gave no direct signal for the use.
                        if (!isCount && targetNpc != null && (t - targetNpcAt <= 6 || (t - targetNpcAt <= 15 && fightAt < targetNpcAt && !(allKills.Count > 0 && allKills[allKills.Count - 1].At > targetNpcAt))) && !kills.Exists(kk => kk.Name == targetNpc.Name && kk.At <= t))
                        {
                            RecEvent later = null;
                            // (only an item the game took away at a later STEP of this same mission - not one another mission
                            // ended with, and not one the game took when this mission finished: that's the item it hands in,
                            // like Section X Research Cancellation's Republic Research - "using" it on the boss would stall)
                            foreach (RecEvent g in goneItems)
                            {
                                if (g.Elapsed <= t || g.Elapsed - t > 300 || usedItemNames.Contains(g.Option))
                                    continue;
                                bool sameQuest = false;
                                bool handedIn = false;
                                foreach (RecEvent x in events)
                                    if (x.QuestId == e.QuestId && Math.Abs(x.Elapsed - g.Elapsed) <= 0.5 &&
                                        (x.Kind == EvKind.QuestCompleted || x.Kind == EvKind.QuestRemoved))
                                        handedIn = true;
                                if (handedIn)
                                    continue;
                                foreach (RecEvent x in events)
                                    if (x.QuestId == e.QuestId && Math.Abs(x.Elapsed - g.Elapsed) <= 2.0 &&
                                        (x.Kind == EvKind.QuestStep || x.Kind == EvKind.QuestTask || x.Kind == EvKind.QuestCompleted))
                                    {
                                        sameQuest = true;
                                        break;
                                    }
                                if (sameQuest)
                                {
                                    later = g;
                                    break;
                                }
                            }
                            if (later != null)
                            {
                                string ids = Fmt.Format(" BranchId=\"{0}\" StepId=\"{1}\"", e.HasOldStep ? e.OldBranch : e.Branch, e.HasOldStep ? e.OldStep : e.Step);
                                Vec3 np = targetNpc.HasPos ? targetNpc.Pos : e.Pos;
                                // a mob you killed right after (Section X Hyperbeast + Serum Injector) attacks as soon as the bot walks
                                // up, and UseItem with InCombat="False" gives up once a fight starts: allow the use in the fight
                                bool killedAfter = false;
                                foreach (RecEvent ke in events)
                                    if (ke.Kind == EvKind.Kill && ke.Target != null && ke.Target.Name == targetNpc.Name && ke.Elapsed >= t - 1 && ke.Elapsed <= t + 120)
                                        killedAfter = true;
                                lines.Add("<UseItem QuestId=\"" + q + "\"" + ids + " ItemName=\"" + A(later.Option) + "\" WaitTime=\"3000\" XYZ=\"" + X(np) +
                                          "\" Range=\"" + UseRangeFor(targetNpc.HasPos ? e.Pos.DistanceTo(targetNpc.Pos) : -1f) + "\" Radius=\"200\" InCombat=\"" + (killedAfter ? "True" : "False") + "\"><Npcs><Npc>" + A(targetNpc.Name) + "</Npc></Npcs>" +
                                          "<Hotspots><Hotspot Name=\"" + A(targetNpc.Name) + "\" XYZ=\"" + X(np) + "\" /></Hotspots></UseItem>");
                                usedItemNames.Add(later.Option);
                                string onWho = targetNpc.Name;
                                targetNpc = null;
                                Step guess = AddStep(steps, "Item", gate, what, lines, e.QuestId);
                                guess.Unsure = true;
                                AddNote(guess, "you targeted " + onWho + " and held " + later.Option + " - the game gave no signal for the use");
                                break;
                            }
                        }
                        // A count the game didn't say how you did (Section X Lost Reconnaissance: a Flight Recorder you looted
                        // that the recorder didn't see), but your other counts of it were done with an object: any of that
                        // object near where it ticked (yellow - not sure).
                        if (isCount && !e.Pos.IsZero)
                        {
                            string key = "IsTaskValueGreaterThan(" + q + ", " + e.Step + ", " + task + ",";
                            string sibling = null;
                            foreach (Step ps in steps)
                                if (ps.Gate != null && ps.Gate.Contains(key))
                                    foreach (string l in ps.Lines)
                                        if (l.StartsWith("<UseObject ", StringComparison.Ordinal))
                                            sibling = Attr(l, "ObjectName"); // (XML-escaped as written)
                            if (!string.IsNullOrEmpty(sibling))
                            {
                                lines.Add("<UseObject ObjectName=\"" + sibling + "\"" + countIds + " UseRange=\"20\" IgnoreLOS=\"True\" WaitTime=\"3000\" BlacklistDuration=\"5\" Radius=\"50\" XYZ=\"" + X(e.Pos) + "\" />");
                                Step guess = AddStep(steps, "Use", gate, what, lines, e.QuestId);
                                guess.Unsure = true;
                                AddNote(guess, "the game didn't say what you used - any " + sibling + " near where it ticked, like your other counts");
                                break;
                            }
                        }
                        // Defend / hold here: you stood at this spot a while, the fight came to you, and the step moved on by
                        // itself (waves, "hold the position", a timer). Walking there and moving straight on would leave
                        // before it's done: the bot waits there, fighting what comes, until the step moves on.
                        bool fought = false;
                        double stay = !isCount && !e.Pos.IsZero ? StayedAt(events, t, e.Pos, out fought) : 0;
                        if (!isCount && stay >= 20 && fought)
                        {
                            lines.Add("<While Condition=\"" + gate + "\"><MoveTo QuestId=\"1\" Name=\"Defend here\" XYZ=\"" + X(e.Pos) + "\" /><WaitTimer QuestId=\"1\" WaitTime=\"3\" /></While>");
                            AddNote(AddStep(steps, "Walk", gate, what, lines, e.QuestId),
                                    "defend: you held this spot " + ((int)stay).ToString(CultureInfo.InvariantCulture) + " s while the fight came to you - the bot waits here until it moves on");
                            break;
                        }
                        // The step moved on by itself a while after you got there, with nothing done (a timer, a scene, an NPC
                        // you waited for): the bot waits there too, while the step is on, at most a bit longer than you did -
                        // walking there and straight on ran the next steps while this one was still going.
                        double waited = !isCount && !e.Pos.IsZero ? WaitedAt(events, t, e.Pos) : 0;
                        if (waited >= 10)
                        {
                            lines.Add("<MoveTo QuestId=\"" + q + "\" Name=\"Where it ticked\" XYZ=\"" + X(e.Pos) + "\" />");
                            lines.AddRange(WaitWhile(gate, waited));
                            AddNote(AddStep(steps, "Walk", gate, what, lines, e.QuestId),
                                    "you waited here " + ((int)waited).ToString(CultureInfo.InvariantCulture) + " s and it moved on by itself - the bot waits here while it's on");
                            break;
                        }
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
                        // gone with its reward window up (the game didn't say why): handed in
                        if (!e.QuestHidden && e.QuestId != 0 && !IsSystemQuest(e) &&
                            (rewardAt.ContainsKey(e.QuestId) || RewardSoon(events, e.QuestId, t)))
                            goto case EvKind.QuestCompleted;
                        // picked up and gone again without any progress.
                        ulong gone = e.QuestId;
                        if (gone != 0 && !steps.Exists(st => st.Quest == gone && st.Kind != "Pickup"))
                        {
                            steps.RemoveAll(st => st.Kind == "Pickup" && st.Gate != null && st.Gate.Contains(Fmt.Hex(gone)));
                            picked.Remove(gone);
                        }
                        break;
                    }
                    case EvKind.QuestAbandoned:
                    {
                        // you dropped it: what that go at it made goes too (a mission taken by mistake, a story quest dropped after
                        // the wrong reply and taken again) - a fight that counted for another quest too stays, for that one
                        ulong dropped = e.QuestId;
                        if (e.QuestHidden || dropped == 0 || IsSystemQuest(e))
                            break;
                        abandoned.Add(dropped);
                        string dh = Fmt.Hex(dropped);
                        string dropPart = " + " + e.QuestName + ": ";
                        Step took;
                        int from = pickupOf.TryGetValue(dropped, out took) ? Math.Max(0, steps.IndexOf(took)) : 0;
                        for (int di = steps.Count - 1; di >= from; di--)
                        {
                            Step ds = steps[di];
                            if (ds == took)
                            {
                                steps.RemoveAt(di);
                                continue;
                            }
                            if (ds.Also != null && ds.Also.Remove(dropped))
                            {
                                int cut = ds.Comment.IndexOf(dropPart, StringComparison.Ordinal);
                                if (cut >= 0)
                                {
                                    int next = ds.Comment.IndexOf(" + ", cut + 3, StringComparison.Ordinal);
                                    ds.Comment = ds.Comment.Substring(0, cut) + (next >= 0 ? ds.Comment.Substring(next) : string.Empty);
                                }
                            }
                            if (ds.Quest != dropped)
                                continue;
                            if (ds.Also != null && ds.Also.Count > 0)
                            {
                                ulong keep = new List<ulong>(ds.Also.Keys)[0];
                                ds.Quest = keep;
                                ds.Gate = ds.Also[keep];
                                ds.Also.Remove(keep);
                                ds.AlsoTasks = null;
                                ds.Task = -1;
                                ds.Targets = null;
                                int own = ds.Comment.IndexOf(" + ", StringComparison.Ordinal);
                                if (own >= 0)
                                    ds.Comment = ds.Comment.Substring(own + 3);
                                for (int dl = 0; dl < ds.Lines.Count; dl++)
                                    ds.Lines[dl] = ds.Lines[dl].Replace("QuestId=\"" + dh + "\"", "QuestId=\"" + Fmt.Hex(keep) + "\"");
                            }
                            else
                                steps.RemoveAt(di);
                        }
                        picked.RemoveAll(pq => pq == dropped);
                        weeklies.Remove(dropped);
                        pickupOf.Remove(dropped);
                        finalAt.Remove(dropped);
                        stepText.Remove(dropped);
                        rewardAt.Remove(dropped);
                        foreach (string dk in new List<string>(countsSeen.Keys))
                            if (dk.StartsWith(dh + "|", StringComparison.Ordinal))
                                countsSeen.Remove(dk);
                        foreach (string dk in new List<string>(taskTexts.Keys))
                            if (dk.StartsWith(dh + "|", StringComparison.Ordinal))
                                taskTexts.Remove(dk);
                        foreach (string dk in new List<string>(itemForTask.Keys))
                            if (dk.StartsWith(dh + "/", StringComparison.Ordinal))
                                itemForTask.Remove(dk);
                        if (sameTakeQuest == dropped)
                            sameTake = null;
                        break;
                    }
                    case EvKind.QuestCompleted:
                    {
                        if (e.QuestHidden || e.QuestId == 0 || IsSystemQuest(e))
                            break;
                        string q = Fmt.Hex(e.QuestId);
                        // The last count of a task can go with the quest leaving your log (Section X Research Stations: 2/3, then
                        // done): when exactly one task of the step was one short, the click that finished it is that last count.
                        string pre = q + "|" + e.Step + "|";
                        int shorts = 0;
                        string shortTask = null;
                        long shortMax = 0;
                        foreach (KeyValuePair<string, long[]> sc in countsSeen)
                        {
                            if (!sc.Key.StartsWith(pre, StringComparison.Ordinal) || sc.Value[0] >= sc.Value[1])
                                continue;
                            shorts++;
                            if (sc.Value[0] == sc.Value[1] - 1)
                            {
                                shortTask = sc.Key.Substring(pre.Length);
                                shortMax = sc.Value[1];
                            }
                        }
                        if (shorts == 1 && shortTask != null)
                        {
                            string ctext;
                            if (!taskTexts.TryGetValue(pre + shortTask, out ctext))
                                ctext = "count";
                            string cg = Fmt.Format("HasQuest({0}) and not IsTaskValueGreaterThan({0}, {1}, {2}, {3})", q, e.Step, shortTask, shortMax - 1);
                            string cw = Fmt.Format("{0}: {1} ({2}/{2})", e.QuestName, ctext, shortMax);
                            string cids = Fmt.Format(" QuestId=\"{0}\" BranchId=\"{1}\" StepId=\"{2}\" TaskId=\"{3}\" TaskValue=\"{4}\"", q, e.Branch, e.Step, shortTask, shortMax - 1);
                            List<Use> lastCounted = null;
                            int insertAt = steps.Count;
                            // only a task you counted with clicks before (or a click on a mission object), and only when no kill
                            // came after that click (a kill count that ends on its last kill keeps its last fight)
                            string taskPart = "IsTaskValueGreaterThan(" + q + ", " + e.Step + ", " + shortTask + ", ";
                            bool byClicks = steps.Exists(st => st.Quest == e.QuestId && st.Kind == "Use" && st.Gate != null && st.Gate.IndexOf(taskPart, StringComparison.Ordinal) >= 0);
                            // (the reward window comes ~1.2 s after the finishing tick, and later after a short cutscene: 3 s)
                            if (sameTake != null && sameTakeQuest != e.QuestId && t - sameTakeAt <= 3.0 &&
                                (byClicks || sameTake.Exists(u => u.ForQuest)) && !kills.Exists(k => k.At > sameTakeAt))
                            {
                                // the click another quest's tick took a moment ago (it counted for both)
                                lastCounted = sameTake;
                                int at3 = steps.FindLastIndex(st => st.Quest == sameTakeQuest && Math.Abs(st.At - sameTakeAt) < 0.05);
                                if (at3 >= 0)
                                    insertAt = at3;
                            }
                            else if (uses.Count > 0 && t - uses[uses.Count - 1].At <= 3 && (byClicks || uses[uses.Count - 1].ForQuest) &&
                                     !kills.Exists(k => k.At > uses[uses.Count - 1].At))
                            {
                                lastCounted = new List<Use> { uses[uses.Count - 1] };
                                uses.RemoveAt(uses.Count - 1);
                            }
                            if (lastCounted != null)
                            {
                                var cs3 = new Step { Kind = "Use", Gate = cg, Comment = cw, Quest = e.QuestId, At = t, Branch = e.Branch.ToString(CultureInfo.InvariantCulture) };
                                foreach (Use lu in lastCounted)
                                {
                                    cs3.Lines.AddRange(UseAt(lu.Name, lu.Pos, lu.Stand, cids));
                                    consumed[UseKey(lu.Id, lu.Pos)] = t;
                                }
                                steps.Insert(insertAt, cs3);
                                finalAt[e.QuestId] = t; // done by that click: no extra last click or last fight
                            }
                            countsSeen[pre + shortTask] = new[] { shortMax, shortMax };
                        }
                        // Finished by your last kill or use, with no count tick in between.
                        double fin;
                        bool ticked = finalAt.TryGetValue(e.QuestId, out fin) && t - fin <= 3.0; // a finishing tick ~1.2 s before the reward window
                        // a weekly whose count only other missions' ends move, ending with one of them (the 5th heroic done): that
                        // mission's own end did it - not its last click, fight or talk
                        bool byOther = metaSteps.Contains(q + "|" + e.Step.ToString(CultureInfo.InvariantCulture)) && OtherEndNear(events, hiddenQuests, e.QuestId, t);
                        if (byOther)
                            ticked = true;
                        // a scene that started by itself right after your last kill (a boss that talks once beaten): the kill finished
                        // it - unless the last step was to speak to someone
                        string endText;
                        double killEnd = KillMoment(kills, sceneAfterKillAt, convos, t);
                        if (killEnd < t && stepText.TryGetValue(e.QuestId, out endText) && NpcFromStepText(endText) != null)
                            killEnd = t;
                        bool scene = killEnd < t;
                        // (a talk you started by clicking them counts until it ends, when you clicked nothing after it)
                        bool talked = !scene && !byOther && talk != null && !UsedUp(talk, consumed, t) &&
                                      (t - talkAt <= 20 || (talkClicked && !talkGave && SceneOn(convos, talkAt, t) && !uses.Exists(uc => uc.At > talkAt + 1)));
                        // finished in a holocom call that came after any talk (a class mission's last "use your holocom"): the bot answers it
                        // (not when a kill right before it or a click after the call did it)
                        bool byCall = !ticked && !scene && holoAt > -1e8 && holoAt > talkAt && (t - holoAt <= 20 || CallLasted(convos, holoAt, t) || e.Note == How.Holocom) &&
                                      (holoQuest == 0 || holoQuest == e.QuestId || hiddenQuests.Contains(holoQuest) || Math.Abs(holoQuestAt - holoAt) > 30) &&
                                      !(kills.Count > 0 && t - kills[kills.Count - 1].At <= 3) && !uses.Exists(uc => uc.At > holoAt + 1);
                        if (byCall)
                            talked = false;
                        if (!ticked && !talked && !byCall)
                        {
                            string doneGate = "HasQuest(" + q + ") and not IsQuestComplete(" + q + ")";
                            Use lastUse = uses.Count > 0 && t - uses[uses.Count - 1].At <= 3 ? uses[uses.Count - 1] : null;
                            if (lastUse == null && uses.Count > 0)
                            {
                                // the last thing you clicked before the reward came up (no kill after it)
                                Use lastClick = uses.FindLast(u => t - u.At <= (u.ForQuest ? 60 : 10));
                                if (lastClick != null && (kills.Count == 0 || kills[kills.Count - 1].At < lastClick.At))
                                    lastUse = lastClick;
                            }
                            if (kills.Count > 0 && killEnd - kills[kills.Count - 1].At <= 3 && (lastUse == null || lastUse.At < kills[kills.Count - 1].At))
                            {
                                double from = Math.Min(fightAt, killEnd - 3);
                                var g = new Step { Kind = "Grind", Gate = doneGate, Comment = e.QuestName + ": last fight", Quest = e.QuestId,
                                                   Kills = kills.FindAll(k => k.At >= from - 1) };
                                // the thing you clicked that brought the boss out (Kregg's Locker draws Kregg out), also when that click
                                // was a pick-up (the Field Generator gives Research Cancellation and brings out its commander)
                                Use spawner = uses.FindLast(u => u.At <= fightAt + 1 && fightAt - u.At <= 15);
                                bool fromGiver = false;
                                if (spawner == null && giverUse != null && giverUse.At >= fightAt - 15 && fightAt >= giverUse.At - 1 && giverUse.At <= t && g.Kills.Exists(k => k.Pos.DistanceTo(giverUse.Pos) <= 1f))
                                {
                                    spawner = giverUse;
                                    fromGiver = true;
                                }
                                if (spawner != null)
                                {
                                    // walked up to from where you stood and clicked once, not clicked again and again during the fight
                                    g.Lines.Add("<ClickObject QuestId=\"" + q + "\" Name=\"" + A(spawner.Name) + "\" XYZ=\"" + X(spawner.Pos) + "\" Stand=\"" +
                                                X(spawner.Stand.IsZero ? spawner.Pos : spawner.Stand) + "\" Fight=\"True\" />");
                                    g.Comment = e.QuestName + ": click " + spawner.Name + ", then the last fight";
                                    if (fromGiver)
                                        giverUse = null;
                                    else
                                        uses.Remove(spawner);
                                }
                                else
                                {
                                    // or the mission item you used right before it (a lure, a totem): used once first
                                    Use preFight = PreFightItem(itemOrder, g.Kills, mobSeen, allKills, fightAt);
                                    if (preFight != null)
                                    {
                                        g.Lines.AddRange(PreFightLines(preFight, q));
                                        g.Comment = e.QuestName + ": use " + preFight.Name + ", then the last fight";
                                        AddNote(g, "uses " + preFight.Name + " first, as you did right before this fight");
                                        itemOrder.Remove(preFight);
                                    }
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
                        if (talked)
                            by = talk;
                        else if (!scene && !byCall && !byOther && e.Target != null && e.Target.Name.Length > 0 && !IsCompanion(e.Target) && e.Note.IndexOf("clicked", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 e.Note.IndexOf("talk", StringComparison.OrdinalIgnoreCase) >= 0 && !UsedUp(e.Target, consumed, t))
                            by = e.Target;
                        bool byExact = by != null; // (a spot the game gave, not one guessed from the step text)
                        // the talk was credited to an object you had already used (older recordings): the NPC you had selected then, standing
                        // where you finished it
                        bool talkUsedUp = !scene && !byCall && !byOther && ((talk != null && t - talkAt <= 20 && UsedUp(talk, consumed, t)) ||
                                          (e.Note.IndexOf("talk", StringComparison.OrdinalIgnoreCase) >= 0 && UsedUp(e.Target, consumed, t)));
                        if (by == null && talkUsedUp && targetNpc != null && Math.Abs(targetNpcAt - talkAt) <= 10 && targetNpc.HasPos && !e.Pos.IsZero &&
                            targetNpc.Pos.DistanceTo(e.Pos) <= 1f && !killedNames.Contains(targetNpc.Name) && !followers.Contains(targetNpc.Name))
                            by = targetNpc;
                        if (by == null && talkUsedUp)
                        {
                            // no NPC selected: the NPC the last step named ("Speak to Tookreek"), where you clicked them or where you finished
                            string lastText;
                            string named = stepText.TryGetValue(e.QuestId, out lastText) ? NpcFromStepText(lastText) : null;
                            if (named != null && !killedNames.Contains(named))
                            {
                                Vec3 np;
                                bool seenAt = npcSpots.TryGetValue(named, out np);
                                bool obj = named.IndexOf("Dropbox", StringComparison.OrdinalIgnoreCase) >= 0;
                                by = new ObjRef { Name = named, Kind = obj ? ObjKind.Placeable : ObjKind.Npc, Pos = seenAt ? np : e.Pos, HasPos = seenAt || !e.Pos.IsZero };
                                AddNote(s, "the talk was credited to something you had already used: handed in to " + named + ", named by the step");
                            }
                            else
                                AddNote(s, "the talk was credited to something you had already used, and no NPC was found: no hand-in written");
                            s.Unsure = true;
                        }
                        if (by != null)
                        {
                            s.Gate = "HasQuest(" + q + ") and not IsQuestComplete(" + q + ")";
                            s.Quest = e.QuestId;
                            // an object with a same-name twin close by is pinned to its own spot, like its pick-up (SearchRadius is squared:
                            // 20 also takes the twin up to 4.5 away)
                            string turnRadius = TurnInPinnedTwin && byExact && by.HasPos && TwinWithin(by.Name, by.Pos, 4.5f) ? "0.001" : "20";
                            s.Lines.Add("<TurnInQuest QuestName=\"" + A(e.QuestName) + "\" QuestId=\"" + q + "\"" + (by.Kind == ObjKind.Placeable ? " Type=\"Placeable\" SearchRadius=\"" + turnRadius + "\" UseRange=\"20\"" : string.Empty) +
                                        " TurnInName=\"" + A(by.Name) + "\" XYZ=\"" + X(by.HasPos ? by.Pos : e.Pos) + "\" />");
                        }
                        else if (byCall)
                        {
                            // (answered only while the quest is still open: the call is what finishes it)
                            s.Lines.Add("<If Condition=\"HasQuest(" + q + ") and not IsQuestComplete(" + q + ")\"><AnswerHolocomCall QuestName=\"" + A(e.QuestName) + "\" QuestId=\"" + q + "\" /></If>");
                            AddNote(s, "it ended in a holocom call: the bot answers it, then takes the reward");
                            holoAt = -1e9;
                        }
                        if (scene)
                        {
                            // that scene is spent: not a talk a later step or hand-in was done with
                            if (talk != null && talkAt == sceneAfterKillAt)
                                talk = null;
                            sceneAfterKillAt = -1e9;
                        }
                        // Take the reward right here, where you did (CZ-198: the quest leaves your log the moment it's
                        // done while its reward waits, so "HasQuest" skipped this and every reward came at the end).
                        // CompleteQuest finishes at once when no reward is waiting.
                        // The game counts the quest complete the moment it finishes, while its reward window still waits
                        // (checked live, Section X Data Raid: IsQuestComplete was already true, reward not taken) - so
                        // "not IsQuestComplete" skipped the reward, and also paused 5 s here for quests not even done yet.
                        // Now: the quest is still finishing, or the game says its reward is waiting.
                        if (s.Gate == null)
                        {
                            s.Gate = "(HasQuest(" + q + ") and not IsQuestComplete(" + q + ")) or " + RewardWaiting(q);
                            s.Lines.Add("<WaitTimer QuestId=\"1\" WaitTime=\"" + (byCall ? "5" : "3") + "\" />");
                        }
                        else
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
                {
                    made.At = prevT; // (the steps the last event made: their time, for the board pick below)
                    if (made.Branch == null && tickQuest != 0 && made.Quest == tickQuest)
                        made.Branch = tickBranch;
                }
            // A weekly that was already in your log when you started (Black Hole: you had the weekly, the board only
            // offered the 4 dailies): picked up at the same mission board as the missions you took - marked "not sure"
            // (yellow in the review list), the recording never saw where it came from.
            // (each weekly at the board of its own area and time: a recording across two daily areas has two boards)
            {
                // the object pick-ups from a mission board / terminal: the game said it came from a board, or the name says so
                var boards = new List<Step>();
                var boardArea = new Dictionary<Step, ulong>();
                for (int i = 0; i < steps.Count; i++)
                {
                    if (steps[i].Kind != "Pickup")
                        continue;
                    string pl = steps[i].Lines.Find(ln => ln.StartsWith("<PickUp", StringComparison.Ordinal));
                    if (pl == null || Attr(pl, "SearchRadius").Length == 0)
                        continue; // an object giver only
                    string gn = Attr(pl, "GiverName"), gq = Attr(pl, "QuestId");
                    RecEvent added = null;
                    foreach (RecEvent ev in events)
                        if (ev.Kind == EvKind.QuestAdded && Fmt.Hex(ev.QuestId) == gq && (added == null || Math.Abs(ev.Elapsed - steps[i].At) < Math.Abs(added.Elapsed - steps[i].At)))
                            added = ev;
                    // (the game's "from the board" only when the giver written is that board's own terminal: a board you had
                    // just closed is also noted for what you clicked next)
                    bool fromBoard = added != null && added.Note == How.Board && added.Target != null && A(added.Target.Name) == gn;
                    if (!fromBoard && gn.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) < 0 && gn.IndexOf("Mission", StringComparison.OrdinalIgnoreCase) < 0)
                        continue; // only a mission board / terminal gives area weeklies
                    boards.Add(steps[i]);
                    boardArea[steps[i]] = added != null ? added.AreaId : 0;
                }
                if (boards.Count > 0)
                {
                    var had = new List<KeyValuePair<ulong, string>>();
                    var hadAt = new Dictionary<ulong, RecEvent>(); // each one's first move in the recording
                    foreach (RecEvent ev in events)
                    {
                        if (ev.QuestId == 0 || ev.QuestHidden || hiddenQuests.Contains(ev.QuestId) || picked.Contains(ev.QuestId) || abandoned.Contains(ev.QuestId))
                            continue;
                        if (ev.Kind != EvKind.QuestTask && ev.Kind != EvKind.QuestStep && ev.Kind != EvKind.QuestCompleted)
                            continue;
                        string nm = ev.QuestName ?? string.Empty;
                        if (!IsWeekly(nm) || had.Exists(h => h.Key == ev.QuestId))
                            continue;
                        had.Add(new KeyValuePair<ulong, string>(ev.QuestId, nm));
                        hadAt[ev.QuestId] = ev;
                    }
                    var lastAt = new Dictionary<Step, Step>(); // the step last put after each board
                    foreach (KeyValuePair<ulong, string> h in had)
                    {
                        // its board: one in the area it moved in (any, if none there), the last one before it first moved (else the first)
                        RecEvent first = hadAt[h.Key];
                        List<Step> near = boards.FindAll(b => first.AreaId != 0 && boardArea[b] == first.AreaId);
                        if (near.Count == 0)
                            near = boards;
                        Step bs = near.FindLast(b => b.At <= first.Elapsed) ?? near[0];
                        string pl = bs.Lines.Find(ln => ln.StartsWith("<PickUp", StringComparison.Ordinal));
                        string board = Attr(pl, "GiverName"), boardXyz = Attr(pl, "XYZ"), boardRadius = Attr(pl, "SearchRadius");
                        string boardRange = Attr(pl, "UseRange").Length > 0 ? Attr(pl, "UseRange") : "20";
                        string boardStand = bs.Lines.Find(ln => ln.StartsWith("<MoveTo", StringComparison.Ordinal)); // where you stood at it
                        string hq = Fmt.Hex(h.Key);
                        weeklies.Add(h.Key);
                        var ws = new Step
                        {
                            Kind = "Pickup",
                            Gate = "(not HasQuest(" + hq + "))",
                            Comment = "Pick up " + h.Value,
                            Note = "it was already in your log when you recorded - picked up at " + System.Net.WebUtility.HtmlDecode(board) + " like your other missions",
                            Unsure = true,
                            At = bs.At,
                            Weekly = true,
                            Board = true
                        };
                        if (PickUpOnlyWithRoom)
                            ws.Gate += " and (" + LogHasRoom + ")";
                        if (boardStand != null)
                            ws.Lines.Add(boardStand);
                        ws.Lines.Add("<PickUpQuest QuestName=\"" + A(h.Value) + "\" QuestId=\"" + hq + "\"" +
                                     " SearchRadius=\"" + boardRadius + "\" UseRange=\"" + boardRange + "\" GiverName=\"" + board + "\" XYZ=\"" + boardXyz + "\" />");
                        Step after;
                        if (!lastAt.TryGetValue(bs, out after))
                            after = bs;
                        steps.Insert(steps.IndexOf(after) + 1, ws);
                        lastAt[bs] = ws;
                    }
                }
            }
            foreach (Step made in steps)
                if (made.At < -1e8)
                {
                    made.At = prevT;
                    if (made.Branch == null && tickQuest != 0 && made.Quest == tickQuest)
                        made.Branch = tickBranch;
                }
            ApplyReplies(steps, convos, dark >= light ? "dark" : "light");
            // The game's own count for each task: if you stopped (or a tick got lost) before the end, the last step of
            // that task still runs until the task is done (19/20 when you stopped -> goes on to 20/20).
            var maxFor = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (RecEvent ev in events)
                if (ev.Kind == EvKind.QuestTask && ev.Max > 0 && !ev.QuestHidden)
                {
                    maxFor[Fmt.Hex(ev.QuestId) + ", " + ev.Step + ", " + Math.Max(0, ev.TaskIndex)] = ev.Max;
                    maxFor[Fmt.Hex(ev.QuestId) + ", " + ev.Step + ", " + Math.Max(0, ev.TaskIndex) + "#" + ev.Branch.ToString(CultureInfo.InvariantCulture)] = ev.Max; // (on that branch)
                }
            ShareTaskFights(steps);
            CollectSteps(steps, maxFor, scanner);
            var lastOfTask = new Dictionary<string, Step>(StringComparer.Ordinal);
            foreach (Step s in steps)
            {
                if (s.Gate == null)
                    continue;
                foreach (Match m in Regex.Matches(s.Gate, @"IsTaskValueGreaterThan\((0x[0-9A-F]+, \d+, \d+), (\d+)\)"))
                    lastOfTask[m.Groups[1].Value] = s;
                // (another task of the same step the same kills moved: that fight is its last step too, unless a later one is)
                if (s.AlsoTasks != null)
                    foreach (string ag in s.AlsoTasks.Values)
                        foreach (Match m in Regex.Matches(ag, @"IsTaskValueGreaterThan\((0x[0-9A-F]+, \d+, \d+), (\d+)\)"))
                            lastOfTask[m.Groups[1].Value] = s;
            }
            foreach (KeyValuePair<string, Step> kv in lastOfTask)
            {
                ulong max;
                if (!maxFor.TryGetValue(kv.Key, out max))
                    continue;
                Step s = kv.Value;
                // Only a plain fight runs on to the full count. A step that uses an object first (a ZX-10 pod that lets
                // out the droid) can't: the pods you used are spent, so no droid would ever come and the bot would wait
                // forever (CZ-198 Manual Shutdown, stopped at 4/5). It stays at your count; the end says it's unfinished.
                if (s.Lines.Exists(l => l.StartsWith("<UseObject", StringComparison.Ordinal) || (l.StartsWith("<Interact", StringComparison.Ordinal) || l.StartsWith("<ClickObject", StringComparison.Ordinal)) ||
                                        l.StartsWith("<UseItem", StringComparison.Ordinal) || l.StartsWith("<UseMissionItemOnce", StringComparison.Ordinal)))
                    continue;
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
                if (s.AlsoTasks != null)
                    foreach (int k in new List<int>(s.AlsoTasks.Keys))
                        s.AlsoTasks[k] = Regex.Replace(s.AlsoTasks[k], @"IsTaskValueGreaterThan\(" + Regex.Escape(kv.Key) + @", \d+\)", full);
            }
            KeepBestOfRepeats(steps);
            // a quest that took another branch right after replies the bot wouldn't pick by itself: said in the review list
            string pickSide = dark >= light ? "dark" : "light";
            foreach (KeyValuePair<ulong, double> br in branchedAt)
            {
                Convo bc = convos.FindLast(v => v.Picks.Count > 0 && v.Start <= br.Value && br.Value - v.End <= 15);
                if (bc == null || BotPicksSame(bc, pickSide))
                    continue;
                Step bs = steps.Find(st => st.Quest == br.Key && Math.Abs(st.At - br.Value) < 0.05);
                if (bs == null)
                    continue;
                bs.Unsure = true;
                AddNote(bs, "this quest took another branch after your replies: the profile follows only that branch");
            }
            ShareItemTargets(steps, mobSeen, itemSpots);
            // A fight where none of the mobs you killed are on the game's list for that task: say so (yellow in the
            // review list) instead of guessing.
            foreach (Step s in steps)
            {
                if (s.Kind != "Grind" || s.Targets == null || s.Targets.Count == 0 || s.Kills == null || s.Unsure)
                    continue;
                bool known = s.Kills.Exists(k => k.Spec != 0);
                if (known && !s.Kills.Exists(k => k.Spec != 0 && s.Targets.Contains(k.Spec)))
                {
                    s.Unsure = true;
                    AddNote(s, "the game doesn't count the mobs you killed here for this task");
                }
            }
            MergeCountClicks(steps, scanner);
            // a fight roams every spot you killed those mobs at near it, not one spot where it camps the respawn
            foreach (Step s in steps)
            {
                if (s.Kind != "Grind" || s.Kills == null || s.Kills.Count == 0)
                    continue;
                var names = new HashSet<string>(s.Kills.ConvertAll(k => k.Name));
                var own = new List<Kill>(s.Kills);
                // You fought it in a few places: those are the fight, exactly where you did it (CZ-198 Staff Cuts).
                // Only a fight you did all in one spot gets your other spots too, so it doesn't camp one respawn.
                var spots = new List<Vec3>();
                foreach (Kill k in own)
                    if (!spots.Exists(p => p.DistanceTo(k.Pos) <= 1f))
                        spots.Add(k.Pos);
                if (spots.Count >= 3)
                    continue;
                foreach (Kill k in allKills)
                    if (names.Contains(k.Name) && !s.Kills.Contains(k) && own.Exists(o => o.Leg == k.Leg && o.Pos.DistanceTo(k.Pos) <= 3f)) // 3 = about 30 m in game units
                        s.Kills.Add(new Kill { Name = k.Name, Pos = k.Pos, At = k.At, Leg = k.Leg, Credit = k.Credit, Spec = k.Spec });
            }
            // the mob scanner: every mob this fight's mission counts that you came near (killed or not) is a spot too,
            // after your own spots. Groups linked to each other (each within ~50 m of the next) all count, so the bot goes on
            // to the next group instead of waiting for a respawn; ordered nearest-next, so it walks group to group, not
            // a big circle. Same part of the map, at most ~150 m from where you fought.
            if (mobSeen.Count > 0)
                foreach (Step s in steps)
                {
                    if (s.Kind != "Grind" || s.Kills == null || s.Kills.Count == 0)
                        continue;
                    var names = new HashSet<string>(s.Kills.ConvertAll(k => k.Name));
                    var own = new List<Kill>(s.Kills);
                    var cands = new List<Kill>();
                    foreach (Kill m in mobSeen)
                    {
                        bool counts = m.Spec != 0 && s.Targets != null && s.Targets.Contains(m.Spec);
                        if (!counts && !(names.Contains(m.Name) && (s.Targets == null || m.Spec == 0)))
                            continue;
                        if (!own.Exists(o => o.Leg == m.Leg && o.Pos.DistanceTo(m.Pos) <= 15f)) // ~150 m: BuddyCron picks fight hotspots at random, so keep them close
                            continue;
                        if (s.Kills.Exists(k => k.Pos.DistanceTo(m.Pos) <= 2f) || cands.Exists(c => c.Pos.DistanceTo(m.Pos) <= 2f))
                            continue; // already a spot
                        cands.Add(m);
                    }
                    Vec3 last = own[own.Count - 1].Pos;
                    for (int added = 0; added < 40 && cands.Count > 0; added++)
                    {
                        // linked: within ~50 m of a spot the fight already has; of those, the nearest to the last one
                        Kill next = null;
                        float best = float.MaxValue;
                        foreach (Kill c in cands)
                        {
                            if (!s.Kills.Exists(k => k.Leg == c.Leg && k.Pos.DistanceTo(c.Pos) <= 5f))
                                continue;
                            float d = c.Pos.DistanceTo(last);
                            if (d < best)
                            {
                                best = d;
                                next = c;
                            }
                        }
                        if (next == null)
                            break; // the rest aren't linked to this fight
                        cands.Remove(next);
                        s.Kills.Add(new Kill { Name = next.Name, Pos = next.Pos, At = next.At, Leg = next.Leg, Spec = next.Spec });
                        last = next.Pos;
                    }
                }
            // Quest abilities (special / temporary bar) were recorded like mission items: cast them with Quest Recorder's
            // UseQuestAbility (same spots, NPCs and range as UseItem), opening the bar with its mission item first.
            if (abilityNames.Count > 0)
                foreach (Step s in steps)
                    for (int li = 0; li < s.Lines.Count; li++)
                    {
                        string l = s.Lines[li];
                        bool use = l.StartsWith("<UseItem ", StringComparison.Ordinal);
                        bool once = l.StartsWith("<UseMissionItemOnce ", StringComparison.Ordinal);
                        if (!use && !once)
                            continue;
                        string an = Attr(l, "ItemName"); // as written in the line (XML-escaped)
                        string raw = null;
                        foreach (string nm in abilityNames)
                            if (A(nm) == an)
                                raw = nm;
                        if (raw == null)
                            continue;
                        string open;
                        string openAttr = abilityOpen.TryGetValue(raw, out open) && open != raw ? " OpenWith=\"" + A(open) + "\"" : string.Empty;
                        if (use)
                            l = "<UseQuestAbility " + l.Substring("<UseItem ".Length).Replace(" ItemName=\"" + an + "\"", " AbilityName=\"" + an + "\"" + openAttr)
                                                                                  .Replace("</UseItem>", "</UseQuestAbility>");
                        else
                            l = l.Replace(" ItemName=\"" + an + "\"", " AbilityName=\"" + an + "\"" + openAttr);
                        // the way you faced when you used it (read only by the plain pass: never written into the profile)
                        float facing;
                        if (abilityHeading.TryGetValue(Attr(l, "XYZ"), out facing) || abilityHeading.TryGetValue(raw, out facing))
                            l = l.Replace(" AbilityName=\"" + an + "\"", " AbilityName=\"" + an + "\" Heading=\"" + facing.ToString("0.#", CultureInfo.InvariantCulture) + "\"");
                        s.Lines[li] = l;
                    }
            HoldLaterStages(steps, questTitles);
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
                    if (steps[j].Quest != 0 && questTitles.TryGetValue(steps[j].Quest, out title) && IsHeroic(title))
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
            PlainSteps(steps);
            return Render(steps, picked, profileName, fileName, recordingName, dark > light ? "Dark" : (light > dark ? "Light" : null), vendors, completedOrder);
        }

        /// <summary>Walk points collected so far become a route step before what comes next.</summary>
        private static void FlushRoute(List<Step> steps, List<KeyValuePair<string, Vec3>> markers)
        {
            if (markers.Count == 0)
                return;
            var route = new Step { Kind = "Route", Comment = "Walk" };
            AddRoute(route, markers);
            steps.Add(route);
            markers.Clear();
        }

        /// <summary>Writes walk points as route lines (lift rides become UseTransport).</summary>
        private static void AddRoute(Step route, List<KeyValuePair<string, Vec3>> markers)
        {
            // a door / phase you went through: the route runs only while you're still on the side you came from
            var doors = new List<KeyValuePair<int, string>>(); // the "Came out here" point of each door, and its "not through yet" test
            for (int i = 0; i + 1 < markers.Count; i++)
                if (markers[i].Key == "Before the door / phase" && markers[i + 1].Key == "Came out here")
                    doors.Add(new KeyValuePair<int, string>(i + 1, DoorSide(markers[i].Value, markers[i + 1].Value)));
            if (doors.Count > 0)
            {
                var sides = new List<string>();
                foreach (KeyValuePair<int, string> d in doors)
                    sides.Add(d.Value);
                route.Side = sides.Count == 1 ? sides[0] : "(" + string.Join(" or ", sides) + ")";
                route.Comment = "Through the door";
            }
            // more than one door: each door's part of the walk runs only while you're still before that door
            int door = 0;
            bool inPart = false;
            for (int mi = 0; mi < markers.Count; mi++)
            {
                KeyValuePair<string, Vec3> m = markers[mi];
                if (doors.Count > 1 && door < doors.Count && !inPart)
                {
                    route.Lines.Add("<If Condition=\"" + doors[door].Value + "\">");
                    inPart = true;
                }
                if (m.Key.StartsWith(RideKey, StringComparison.Ordinal))
                {
                    string[] wd = m.Key.Substring(RideKey.Length).Split('|');
                    route.Lines.Add("<UseTransport TransporterName=\"" + wd[0] + "\" Type=\"Wonkavator\" Option=\"" + wd[1] + "\" XYZ=\"" + X(m.Value) + "\" />");
                }
                else
                    route.Lines.Add("<MoveTo QuestId=\"1\" Name=\"" + A(m.Key) + "\" XYZ=\"" + X(m.Value) + "\" />");
                if (inPart && mi == doors[door].Key)
                {
                    route.Lines.Add("</If>");
                    inPart = false;
                    door++;
                }
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
                   "\" IgnoreLOS=\"True\" WaitTime=\"3000\" BlacklistDuration=\"5\" Radius=\"2\" XYZ=\"" + X(pos) + "\" />"; // Radius 2 = only the object within 2 m of where you used it (default is any within 5 m)
        }

        /// <summary>One object use.</summary>
        /// <summary>Every spot an object of each name was seen at in this recording (clicked or seen nearby).</summary>
        [ThreadStatic]
        private static Dictionary<string, List<Vec3>> _sameName;

        /// <summary>
        /// Another object with the same name stands within 10 m of this one. BuddyCron's UseObject and Interact take
        /// the object with that name NEAREST TO THE BOT (within 10 m of it), not the one at the profile's spot - so
        /// the bot must stand where you stood (CZ-198 pods come in pairs 3 m apart).
        /// </summary>
        private static bool HasTwin(string name, Vec3 pos)
        {
            List<Vec3> spots;
            if (_sameName == null || !_sameName.TryGetValue(name, out spots))
                return false;
            return spots.Exists(p => p.DistanceTo(pos) > 0.1f && p.DistanceTo(pos) < 1f);
        }

        /// <summary>Another object with the same name was seen within this distance of this one (not that one itself).</summary>
        private static bool TwinWithin(string name, Vec3 pos, float far)
        {
            List<Vec3> spots;
            if (_sameName == null || !_sameName.TryGetValue(name, out spots))
                return false;
            return spots.Exists(p => p.DistanceTo(pos) > 0.01f && p.DistanceTo(pos) <= far);
        }

        private static List<string> UseAt(string name, Vec3 pos, Vec3 stand, string ids)
        {
            var l = new List<string>();
            float d = stand.IsZero ? 0f : stand.DistanceTo(pos);
            bool twin = !stand.IsZero && d < 10f && HasTwin(name, pos);
            if (!stand.IsZero && (twin || d > 1.5f || Math.Abs(stand.Y - pos.Y) > 1f) && d < 10f)
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
            return "<UseObject ObjectName=\"" + A(name) + "\" QuestId=\"1\" IgnoreQuestRelation=\"True\" UseRange=\"20\" IgnoreLOS=\"True\" WaitTime=\"3000\" Radius=\"2\" XYZ=\"" + X(pos) + "\" />";
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
                // that count's quest, step and task: a run is one task (two pylons of one name for two tasks of a step are
                // two steps - one UseObject done by either would leave the other task undone; the next step's count of the
                // same object is the next step)
                Match tk = Regex.Match(s.Gate, @"IsTaskValueGreaterThan\((0x[0-9A-F]+), (\d+), (\d+), \d+\)");
                if (!tk.Success)
                    continue;
                string sameTask = "IsTaskValueGreaterThan(" + tk.Groups[1].Value + ", " + tk.Groups[2].Value + ", " + tk.Groups[3].Value + ", ";
                // the run of steps for the same quest clicking the same kind of object
                var run = new List<Step> { s };
                for (int j = i + 1; j < steps.Count; j++)
                {
                    Step n = steps[j];
                    if (n.Kind == "Route")
                        continue;
                    if (n.Quest != s.Quest || SingleUseObject(n) != name)
                        break;
                    // the click that finished this step ends the run (same step, and same branch when known)
                    Match fin = Regex.Match(n.Gate, @"IsBranchStepComplete\((0x[0-9A-F]+), (\d+), (\d+)\)");
                    bool ends = fin.Success && fin.Groups[1].Value == tk.Groups[1].Value && fin.Groups[3].Value == tk.Groups[2].Value &&
                                (s.Branch == null || fin.Groups[2].Value == s.Branch);
                    if (!ends && n.Gate.IndexOf(sameTask, StringComparison.Ordinal) < 0)
                        break; // another task, or another step
                    if (!ends && s.Branch != null && n.Branch != null && n.Branch != s.Branch)
                        break; // the same numbers on another branch
                    run.Add(n);
                    if (ends)
                        break;
                }
                // only the spots you clicked: nothing added from elsewhere (no scanner spots)
                var scanned = new List<Vec3>();
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
                line = Regex.Replace(line, " Radius=\"[^\"]*\"", string.Empty) + " Radius=\"2\">"; // each spot: only the object you used there
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
        /// Staged bonus missions ("Stage 1 - X", "Stage 2 - X", "Final Stage - X"): a later stage's steps wait until every
        /// earlier stage is done (the Final Stage boss only comes out after Stage 2). When other steps come between the last
        /// step of a stage and the last step of the next one, that last step is tried again right before it.
        /// </summary>
        private static void HoldLaterStages(List<Step> steps, Dictionary<ulong, string> titles)
        {
            var chains = new Dictionary<string, List<KeyValuePair<int, ulong>>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<ulong, string> kv in titles)
            {
                Match m = Regex.Match(kv.Value ?? string.Empty, @"^\s*(?:\[[^\]]*\]\s*)?(?:Stage\s+(\d+)|Final\s+Stage)\s*-\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                if (!m.Success)
                    continue;
                int no;
                if (!m.Groups[1].Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out no))
                    no = int.MaxValue; // the final stage
                List<KeyValuePair<int, ulong>> chain;
                if (!chains.TryGetValue(m.Groups[2].Value, out chain))
                    chains[m.Groups[2].Value] = chain = new List<KeyValuePair<int, ulong>>();
                chain.Add(new KeyValuePair<int, ulong>(no, kv.Key));
            }
            foreach (List<KeyValuePair<int, ulong>> chain in chains.Values)
            {
                if (chain.Count < 2)
                    continue;
                chain.Sort((a, b) => a.Key.CompareTo(b.Key));
                string hold = string.Empty;
                for (int k = 1; k < chain.Count; k++)
                {
                    ulong prev = chain[k - 1].Value, cur = chain[k].Value;
                    string ph = Fmt.Hex(prev);
                    hold += " and not (HasQuest(" + ph + ") and not IsQuestComplete(" + ph + "))";
                    // other steps came between: the earlier stage's last step again, right before the later stage's last one
                    int lastEarlier = steps.FindLastIndex(s => s.Quest == prev && s.Gate != null && s.Kind != "Pickup" && s.Kind != "Complete");
                    int lastLater = steps.FindLastIndex(s => s.Quest == cur && s.Gate != null && s.Kind != "Pickup" && s.Kind != "Complete");
                    if (lastEarlier >= 0 && lastLater > lastEarlier)
                    {
                        bool between = false;
                        for (int j = lastEarlier + 1; j < lastLater; j++)
                            if (steps[j].Quest != prev && steps[j].Quest != cur && steps[j].Kind != "Complete" && steps[j].Kind != "Route" && steps[j].Kind != "Ride")
                                between = true;
                        if (between)
                        {
                            Step src = steps[lastEarlier];
                            var copy = new Step
                            {
                                Kind = src.Kind, Gate = src.Gate, Comment = src.Comment, Quest = src.Quest, Task = src.Task, Targets = src.Targets,
                                Kills = src.Kills != null ? new List<Kill>(src.Kills) : null,
                                Also = src.Also != null ? new Dictionary<ulong, string>(src.Also) : null,
                                AlsoTasks = src.AlsoTasks != null ? new Dictionary<int, string>(src.AlsoTasks) : null,
                                At = src.At, Note = src.Note, Unsure = src.Unsure, Area = src.Area, Side = src.Side, Branch = src.Branch, Weekly = src.Weekly, Board = src.Board
                            };
                            copy.Lines.AddRange(src.Lines);
                            steps.Insert(lastLater, copy);
                            lastEarlier = lastLater; // the copy is now the earlier stage's last step
                        }
                    }
                    // the profile runs once, top to bottom: only the later stage's steps after the earlier stage's last step wait
                    // for it (one held before it would be passed while the earlier stage is still on, and never come back)
                    for (int si = lastEarlier + 1; si < steps.Count; si++)
                    {
                        Step s = steps[si];
                        if (s.Quest == cur && s.Gate != null && s.Kind != "Pickup" && s.Kind != "Complete")
                            s.Gate = (s.Gate.IndexOf(" or ", StringComparison.Ordinal) >= 0 ? "(" + s.Gate + ")" : s.Gate) + hold;
                        if (s.Also != null && s.Also.ContainsKey(cur))
                            s.Also[cur] = (s.Also[cur].IndexOf(" or ", StringComparison.Ordinal) >= 0 ? "(" + s.Also[cur] + ")" : s.Also[cur]) + hold;
                        if (s.Quest == cur && s.Gate != null && s.AlsoTasks != null)
                            foreach (int at in new List<int>(s.AlsoTasks.Keys))
                                s.AlsoTasks[at] = (s.AlsoTasks[at].IndexOf(" or ", StringComparison.Ordinal) >= 0 ? "(" + s.AlsoTasks[at] + ")" : s.AlsoTasks[at]) + hold;
                    }
                }
            }
        }

        /// <summary>
        /// A mission item used on NPCs for one count (Black Hole Identify Torvix's Spies): the spies change kind and place,
        /// so every step of that count takes every NPC name and every spot you used it on, and the spots where the mob scanner
        /// saw those NPCs nearby. Only when you used it at more spots than it counted, or at spots far apart: NPCs that stand
        /// where they always do (Section X prisoners) keep one step each, as you did them.
        /// </summary>
        private static void ShareItemTargets(List<Step> steps, List<Kill> mobSeen, Dictionary<string, List<Vec3>> itemSpots)
        {
            var groups = new Dictionary<string, List<KeyValuePair<Step, int>>>(StringComparer.Ordinal);
            foreach (Step s in steps)
            {
                if (s.Kind != "Item" || s.Gate == null)
                    continue;
                Match g = Regex.Match(s.Gate, @"IsTaskValueGreaterThan\((0x[0-9A-F]+), (\d+), (\d+), \d+\)");
                if (!g.Success)
                    continue;
                for (int li = 0; li < s.Lines.Count; li++)
                {
                    string l = s.Lines[li];
                    if (!l.StartsWith("<UseItem ", StringComparison.Ordinal) || l.IndexOf("<Npcs>", StringComparison.Ordinal) < 0)
                        continue;
                    string key = g.Groups[1].Value + "|" + g.Groups[2].Value + "|" + g.Groups[3].Value + "|" + Attr(l, "ItemName");
                    List<KeyValuePair<Step, int>> list;
                    if (!groups.TryGetValue(key, out list))
                        groups[key] = list = new List<KeyValuePair<Step, int>>();
                    list.Add(new KeyValuePair<Step, int>(s, li));
                }
            }
            foreach (List<KeyValuePair<Step, int>> group in groups.Values)
            {
                var names = new List<string>(); // as written (XML-escaped)
                var spots = new List<Vec3>();
                foreach (KeyValuePair<Step, int> at in group)
                {
                    string l = at.Key.Lines[at.Value];
                    foreach (Match n in Regex.Matches(l, "<Npc>([^<]+)</Npc>"))
                        if (!names.Contains(n.Groups[1].Value))
                            names.Add(n.Groups[1].Value);
                    foreach (Match h in Regex.Matches(l, "<Hotspot [^>]*XYZ=\"([^\"]+)\""))
                    {
                        Vec3 v;
                        if (TryVec(h.Groups[1].Value, out v) && !spots.Exists(p => p.DistanceTo(v) < 0.5f))
                            spots.Add(v);
                    }
                }
                if (spots.Count == 0)
                    continue;
                List<Vec3> usedAt;
                bool moreUses = itemSpots.TryGetValue(System.Net.WebUtility.HtmlDecode(Attr(group[0].Key.Lines[group[0].Value], "ItemName")), out usedAt) && usedAt.Count > group.Count;
                bool spread = spots.Exists(a => spots.Exists(b => a.DistanceTo(b) > 2f));
                if (!moreUses && !spread)
                    continue;
                // the mob scanner: those NPCs seen near your spots (within ~150 m)
                var own = new List<Vec3>(spots);
                foreach (Kill m in mobSeen)
                {
                    if (spots.Count >= 12)
                        break;
                    if (names.Contains(A(m.Name)) && own.Exists(p => p.DistanceTo(m.Pos) <= 15f) && !spots.Exists(p => p.DistanceTo(m.Pos) < 0.5f))
                        spots.Add(m.Pos);
                }
                var npcs = new StringBuilder("<Npcs>");
                foreach (string n in names)
                    npcs.Append("<Npc>").Append(n).Append("</Npc>");
                npcs.Append("</Npcs>");
                var hot = new StringBuilder("<Hotspots>");
                foreach (Vec3 v in spots)
                    hot.Append("<Hotspot Name=\"Where you used it\" XYZ=\"").Append(X(v)).Append("\" />");
                hot.Append("</Hotspots>");
                foreach (KeyValuePair<Step, int> at in group)
                {
                    string l = at.Key.Lines[at.Value];
                    int ownNames = Regex.Matches(l, "<Npc>([^<]+)</Npc>").Count;
                    int ownSpots = Regex.Matches(l, "<Hotspot ").Count;
                    if (ownNames >= names.Count && ownSpots >= spots.Count)
                        continue; // it already has them all
                    l = ReplacePart(l, "<Npcs>", "</Npcs>", npcs.ToString());
                    l = ReplacePart(l, "<Hotspots>", "</Hotspots>", hot.ToString());
                    at.Key.Lines[at.Value] = l;
                }
            }
        }

        /// <summary>The line with the part from open to close (both included) replaced; unchanged when it has no such part.</summary>
        private static string ReplacePart(string line, string open, string close, string with)
        {
            int a = line.IndexOf(open, StringComparison.Ordinal);
            int b = a >= 0 ? line.IndexOf(close, a, StringComparison.Ordinal) : -1;
            if (a < 0 || b < 0)
                return line;
            return line.Substring(0, a) + with + line.Substring(b + close.Length);
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
                // (a walk's note says how long you waited or held the spot that run: two runs of it are still one step)
                string key = s.Comment + "|" + (s.Kind == "Walk" ? string.Empty : s.Note) + "|" + s.Gate;
                Step keep;
                if (!first.TryGetValue(key, out keep))
                {
                    first[key] = s;
                    continue;
                }
                drop.Add(s);
                bool keepDoesSomething = keep.Kind != "Walk";
                bool thisDoesSomething = s.Kind != "Walk";
                // two walks: one that waits or holds the spot there (it has a note) wins over one that only walks; else the newest
                if (thisDoesSomething || (!keepDoesSomething && (s.Note.Length > 0 || keep.Note.Length == 0)))
                {
                    keep.Kind = s.Kind;
                    keep.Lines.Clear();
                    keep.Lines.AddRange(s.Lines);
                    keep.Kills = s.Kills;
                    keep.Task = s.Task;
                    // what goes with those lines: the fight's other tasks and quests, the mobs the game counts, the branch
                    keep.Targets = s.Targets;
                    keep.Also = s.Also;
                    keep.AlsoTasks = s.AlsoTasks;
                    keep.Area = s.Area;
                    keep.Branch = s.Branch ?? keep.Branch;
                    keep.Weekly = keep.Weekly || s.Weekly;
                    keep.Board = s.Board || (keep.Board && s.Kind == "Pickup");
                    keep.Note = s.Note;
                    keep.Unsure = s.Unsure;
                }
            }
            if (drop.Count > 0)
                steps.RemoveAll(drop.Contains);
        }

        /// <summary>Adds to the step's note (shown in the recorder window only).</summary>
        private static Step AddNote(Step s, string note)
        {
            s.Note = s.Note.Length > 0 ? s.Note + "; " + note : note;
            return s;
        }

        private static Step AddStep(List<Step> steps, string kind, string gate, string what, List<string> lines, ulong quest)
        {
            var s = new Step { Kind = kind, Gate = gate, Comment = what, Quest = quest };
            s.Lines.AddRange(lines);
            steps.Add(s);
            return s;
        }

        /// <summary>A fight area's name: the quest task (or the mob), short.</summary>
        private static string AreaName(Step s)
        {
            string a = Fmt.OneLine(s.Area ?? s.Comment);
            foreach (string cut in new[] { " + ", "; ", " - collect:", " (next: ", " (kill and loot)" })
            {
                int at = a.IndexOf(cut, StringComparison.Ordinal);
                if (at > 0)
                    a = a.Substring(0, at);
            }
            if (a.Length > 90)
            {
                int sp = a.LastIndexOf(' ', 90);
                a = a.Substring(0, sp > 30 ? sp : 90);
            }
            return a.TrimEnd(' ', '-', ':', ',');
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
                sb.Append("\t<ForceAlignment Type=\"").Append(side).Append("\" AutoSkip=\"True\" />").Append(nl);
            if (vendors.Count > 0)
            {
                sb.Append("\t<Vendors>").Append(nl);
                foreach (KeyValuePair<string, Vec3> v in vendors)
                    sb.Append("\t\t<Hotspot Name=\"").Append(A(v.Key)).Append("\" XYZ=\"").Append(X(v.Value)).Append("\" />").Append(nl);
                sb.Append("\t</Vendors>").Append(nl);
            }
            if (_codeChunks != null && _codeChunks.Count > 0)
            {
                sb.Append("\t<CodeChunks>").Append(nl);
                foreach (string c in _codeChunks)
                    sb.Append(c).Append(nl);
                sb.Append("\t</CodeChunks>").Append(nl);
            }
            sb.Append(nl);
            // Fights go in the header as GrindAreas (the way the hand-made profiles do it); the steps use GrindRef.
            StringBuilder head = sb;
            var areas = new StringBuilder();
            var areaNames = new HashSet<string>(StringComparer.Ordinal);
            sb = new StringBuilder();
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
                string next = null; // the next gated step
                for (int j = i + 1; j < steps.Count && next == null; j++)
                {
                    Step n = steps[j];
                    if (n.Gate != null && n.Kind != "Pickup" && n.Kind != "Route" && n.Kind != "Skip")
                        next = n.Gate;
                }
                // a door route: walk through only while you're still on the near side (not a lift's side test: Black Hole Glow
                // walked out only once it was already outside), while any quest with a step after it is still open - like a ride
                if (steps[i].Side != null)
                {
                    var open = new List<string>();
                    var qs = new HashSet<ulong>();
                    for (int j = i + 1; j < steps.Count; j++)
                    {
                        ulong q = steps[j].Quest;
                        if (q != 0 && steps[j].Kind != "Skip" && qs.Add(q))
                            open.Add(Fmt.Format("(HasQuest({0}) and not IsQuestComplete({0}))", Fmt.Hex(q)));
                    }
                    next = open.Count > 0 ? steps[i].Side + " and (" + string.Join(" or ", open) + ")" : null;
                }
                steps[i].Gate = next;
                if (next == null)
                    steps[i].Kind = "Skip";
            }
            foreach (Step s in steps)
            {
                // nothing to do in it (e.g. a lift ride at the very end of the recording): leave it out - an empty step stops BuddyCron loading the profile
                // Lifts are ridden by BuddyCron's navigator by itself (Timothy: no UseTransport for lifts).
                s.Lines.RemoveAll(l => l.StartsWith("<UseTransport", StringComparison.Ordinal) && l.Contains("Type=\"Wonkavator\""));
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
                    // Mobs: only the kinds whose kill moved this fight's count (a Loyalist doesn't belong in the
                    // Partisans fight). A fight whose kills moved no count keeps every kind you killed.
                    if (s.Quest != 0 && s.Task >= 0)
                    {
                        string mine = Fmt.Hex(s.Quest) + "|" + s.Task;
                        var counted = new List<string>();
                        foreach (Kill k in s.Kills)
                        {
                            bool ok = k.Credit.Contains(mine);
                            if (!ok && s.AlsoTasks != null)
                                foreach (int at in s.AlsoTasks.Keys)
                                    if (k.Credit.Contains(Fmt.Hex(s.Quest) + "|" + at))
                                        ok = true;
                            if (!ok && s.Also != null)
                                foreach (ulong aq in s.Also.Keys)
                                    foreach (string c in k.Credit)
                                        if (c.StartsWith(Fmt.Hex(aq) + "|", StringComparison.Ordinal))
                                            ok = true;
                            if (ok && !counted.Contains(k.Name))
                                counted.Add(k.Name);
                        }
                        if (counted.Count > 0)
                            mobs = counted;
                    }
                    // ask the game: only the mob types it counts for this task
                    if (s.Targets != null && s.Targets.Count > 0)
                    {
                        var listed = new List<string>();
                        foreach (Kill k in s.Kills)
                            if (k.Spec != 0 && s.Targets.Contains(k.Spec) && !listed.Contains(k.Name))
                                listed.Add(k.Name);
                        if (listed.Count > 0)
                            mobs = listed;
                    }
                    string fightGate = s.Gate;
                    if ((s.Also != null && s.Also.Count > 0) || (s.AlsoTasks != null && s.AlsoTasks.Count > 0))
                    {
                        fightGate = "(" + s.Gate + ")";
                        if (s.AlsoTasks != null)
                            foreach (string g in s.AlsoTasks.Values)
                                fightGate += " or (" + g + ")";
                        if (s.Also != null)
                            foreach (string g in s.Also.Values)
                                fightGate += " or (" + g + ")";
                    }
                    // the fight's area goes in the header under a name of its own
                    string area = AreaName(s);
                    string baseArea = area;
                    for (int n = 2; !areaNames.Add(area); n++)
                        area = baseArea + " (" + n + ")";
                    areas.Append("\t\t<GrindArea Name=\"").Append(A(area)).Append("\">").Append(nl);
                    areas.Append("\t\t\t<MinLevel>1</MinLevel>").Append(nl).Append("\t\t\t<MaxLevel>100</MaxLevel>").Append(nl).Append("\t\t\t<Mobs>");
                    foreach (string m in mobs)
                        areas.Append("<Mob>").Append(A(m)).Append("</Mob>");
                    areas.Append("</Mobs>").Append(nl).Append("\t\t\t<Hotspots>").Append(nl);
                    foreach (Kill a in anchors)
                        areas.Append("\t\t\t\t<Hotspot Name=\"").Append(A(a.Name)).Append("\" XYZ=\"").Append(X(a.Pos)).Append("\" />").Append(nl);
                    areas.Append("\t\t\t</Hotspots>").Append(nl).Append("\t\t</GrindArea>").Append(nl);
                    sb.Append("\t\t<If Condition=\"").Append(fightGate).Append("\">").Append(nl);
                    foreach (string l in s.Lines)
                        sb.Append("\t\t\t").Append(l).Append(nl);
                    sb.Append("\t\t\t<Grind GrindRef=\"").Append(A(area)).Append("\" While=\"").Append(fightGate).Append("\" />").Append(nl);
                    sb.Append("\t\t</If>").Append(nl);
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
            if (picked.Count > 0)
            {
                // the end, like the hand-made profiles: no reload loop. Something left over is said plainly.
                var conds = new List<string>();
                foreach (ulong q in picked)
                {
                    string h = Fmt.Hex(q);
                    string c = "(HasQuest(" + h + ") and not IsQuestComplete(" + h + "))";
                    if (!conds.Contains(c))
                        conds.Add(c);
                }
                sb.Append("\t\t<If Condition=\"").Append(string.Join(" or ", conds)).Append("\">").Append(nl);
                sb.Append("\t\t\t<UserDialog Title=\"Quest Recorder\" Message=\"A quest from this profile isn't finished. Finish it by hand, or start the profile again.\" />").Append(nl);
                sb.Append("\t\t</If>").Append(nl);
            }
            sb.Append("\t\t<UserDialog Title=\"Done!\" Message=\"This profile is done.\" />").Append(nl);
            sb.Append("\t</Questing>").Append(nl).Append("</Profile>").Append(nl);
            if (areas.Length > 0)
                head.Append("\t<GrindAreas>").Append(nl).Append(areas).Append("\t</GrindAreas>").Append(nl).Append(nl);
            return head.Append(sb).ToString();
        }
    }
}
