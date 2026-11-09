// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuestRecorder
{
    /// <summary>"Make profile from a recording".</summary>
    internal static class RecordingLoader
    {
        private static float F(string s)
        {
            float v;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }

        private static ulong U(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;
            ulong v;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
            return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>Splits one CSV line (fields in double quotes may hold commas and "" for a quote).</summary>
        private static List<string> Split(string line)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                            quoted = false;
                    }
                    else
                        sb.Append(c);
                }
                else if (c == '"')
                    quoted = true;
                else if (c == ',')
                {
                    list.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                    sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        /// <summary>Reads all lines even while the recorder is still writing the file (a recording in progress).</summary>
        private static string[] ReadShared(string path)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                    lines.Add(line);
            }
            return lines.ToArray();
        }

        /// <summary>The events of a recording .csv, as the plugin recorded them.</summary>
        public static List<RecEvent> Load(string csvPath, out string place)
        {
            place = string.Empty;
            var events = new List<RecEvent>();
            string[] lines = ReadShared(csvPath);
            if (lines.Length == 0)
                return events;
            List<string> head = Split(lines[0]);
            var ix = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < head.Count; i++)
                ix[head[i].Trim()] = i;
            for (int li = 1; li < lines.Length; li++)
            {
                if (lines[li].Length == 0)
                    continue;
                List<string> r = Split(lines[li]);
                Func<string, string> c = k =>
                {
                    int n;
                    return ix.TryGetValue(k, out n) && n < r.Count ? r[n] : string.Empty;
                };
                EvKind kind;
                if (!Enum.TryParse(c("kind"), out kind))
                    continue;
                double el;
                double.TryParse(c("elapsed_s"), NumberStyles.Float, CultureInfo.InvariantCulture, out el);
                var e = new RecEvent { Kind = kind, Elapsed = el };
                DateTime when;
                if (DateTime.TryParseExact(c("time"), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
                    e.Time = when;
                e.Pos = new Vec3(F(c("x")), F(c("y")), F(c("z")));
                e.Heading = F(c("heading_deg"));
                e.AreaId = U(c("area_id"));
                e.AreaName = c("area_name");
                if (place.Length == 0 && e.AreaName.Length > 0)
                    place = e.AreaName;
                e.QuestId = U(c("quest_id"));
                e.QuestName = c("quest_name");
                e.QuestFqn = c("quest_fqn"); // the quest's internal name (the same in every client language)
                string hid = c("quest_hidden");
                e.QuestHidden = hid == "True" || hid == "true" || hid == "yes";
                e.Branch = U(c("branch"));
                e.Step = U(c("step"));
                if (c("old_step").Length > 0)
                {
                    e.HasOldStep = true;
                    e.OldBranch = U(c("old_branch"));
                    e.OldStep = U(c("old_step"));
                }
                int ti;
                e.TaskIndex = int.TryParse(c("task_index"), out ti) ? ti : -1;
                e.Count = U(c("count"));
                e.Max = U(c("max"));
                e.Text = c("objective");
                foreach (string id in c("task_targets").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    ulong v = U(id);
                    if (v != 0)
                        e.TaskTargets.Add(v);
                }
                e.Option = c("option");
                long dur;
                if (long.TryParse(c("duration_ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out dur))
                    e.DurationMs = dur; // how long a cast took: when it started, not only when it ended
                e.Note = c("note");
                if (kind == EvKind.ConvoChoice)
                {
                    // the wheel is in the summary: "Reply wheel with NPC "X": 1 neutral | 2 dark -> you picked 2 dark"
                    e.OptionGuessed = e.Option.Length == 0 || e.Option == "?";
                    var wm = System.Text.RegularExpressions.Regex.Match(c("summary"), @": ((\d+ (?:neutral|light|dark))( \| \d+ (?:neutral|light|dark))*) -> you picked");
                    if (wm.Success)
                        e.Choices = new List<string>(wm.Groups[1].Value.Split(new[] { " | " }, StringSplitOptions.None));
                }
                if (c("target_name").Length > 0 || c("target_kind").Length > 0)
                {
                    var o = new ObjRef { Name = c("target_name") };
                    string tk = c("target_kind");
                    o.Kind = tk == "object" ? ObjKind.Placeable : tk == "NPC" ? ObjKind.Npc : tk == "player" ? ObjKind.Player : ObjKind.Other;
                    string tid = c("target_id");
                    ulong nid;
                    if (tid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        ulong.TryParse(tid.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out nid))
                        o.NodeId = nid; // the game's id for this one object (a respawn at the same spot is a new id)
                    o.Dead = c("target_dead") == "yes";
                    o.Health = c("target_health").Length > 0 ? F(c("target_health")) : -1f;
                    o.Anchor = U(c("target_anchor"));
                    o.Level = U(c("target_level"));
                    o.Toughness = c("target_toughness");
                    o.UseRange = F(c("target_use_range"));
                    o.UsableForQuest = c("target_quest_usable") == "yes";
                    o.Spec = U(c("target_spec"));
                    o.Indication = c("target_marker"); // the mission marker it showed: "Grant" gives missions, "End" takes the hand-in
                    o.OffersQuest = o.Indication == "Grant";
                    o.TakesTurnIn = o.Indication == "End";
                    o.How = c("target_how");
                    if (c("target_x").Length > 0)
                    {
                        o.HasPos = true;
                        o.Pos = new Vec3(F(c("target_x")), F(c("target_y")), F(c("target_z")));
                    }
                    e.Target = o;
                }
                if (c("before_x").Length > 0)
                {
                    e.HasFrom = true;
                    e.FromPos = new Vec3(F(c("before_x")), F(c("before_y")), F(c("before_z")));
                    e.FromArea = U(c("before_area_id")); // the area you came from (a door into another area)
                    e.FromAreaName = c("before_area_name");
                }
                if (c("stand_x").Length > 0)
                {
                    e.HasStand = true;
                    e.Stand = new Vec3(F(c("stand_x")), F(c("stand_y")), F(c("stand_z")));
                }
                events.Add(e);
            }

            // elapsed_s has one decimal, the time column the milliseconds: the time between two rows gives the exact
            // spacing (as the live recorder had it), kept within elapsed_s's rounding so a clock change never moves a row
            DateTime lastTime = default(DateTime);
            double lastElapsed = 0;
            foreach (RecEvent e in events)
            {
                if (e.Time == default(DateTime))
                {
                    lastTime = default(DateTime);
                    continue;
                }
                if (lastTime != default(DateTime))
                {
                    double t = lastElapsed + (e.Time - lastTime).TotalSeconds;
                    e.Elapsed = Math.Max(e.Elapsed - 0.05, Math.Min(e.Elapsed + 0.05, t));
                }
                lastTime = e.Time;
                lastElapsed = e.Elapsed;
            }

            // Elevator floors are only in the timeline (.txt) next to the .csv: "elevator on floor 1, floors: A | B"
            try
            {
                string txt = Path.ChangeExtension(csvPath, ".txt");
                if (File.Exists(txt))
                {
                    // companions ("NPC "Theron Shan" id ... level 56 companion"): never the cause of a quest tick
                    var companions = new HashSet<string>(StringComparer.Ordinal);
                    var cre = new System.Text.RegularExpressions.Regex(@"NPC ""([^""]+)"" id \S+ at [^,]+,[^,]+,[^,]+, level \d+ companion");
                    foreach (string line in ReadShared(txt))
                    {
                        System.Text.RegularExpressions.Match cm = cre.Match(line);
                        if (cm.Success)
                            companions.Add(cm.Groups[1].Value);
                    }
                    foreach (RecEvent e in events)
                        if (e.Target != null && companions.Contains(e.Target.Name))
                            e.Target.Toughness = "companion";
                    var lifts = new Dictionary<string, KeyValuePair<int, string[]>>(StringComparer.Ordinal);
                    var re = new System.Text.RegularExpressions.Regex(@"object ""[^""]*"" id \S+ at (-?[\d.]+),(-?[\d.]+),(-?[\d.]+),.*elevator on floor (\d+), floors: (.+?)(?: - |$)");
                    foreach (string line in ReadShared(txt))
                    {
                        System.Text.RegularExpressions.Match m = re.Match(line);
                        if (!m.Success)
                            continue;
                        string key = m.Groups[1].Value + "," + m.Groups[2].Value + "," + m.Groups[3].Value;
                        string[] floors = m.Groups[5].Value.Split(new[] { " | " }, StringSplitOptions.None);
                        lifts[key] = new KeyValuePair<int, string[]>(int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), floors);
                    }
                    foreach (RecEvent e in events)
                    {
                        if ((e.Kind != EvKind.ElevatorOpen && e.Kind != EvKind.ElevatorRide) || e.Target == null || !e.Target.HasPos)
                            continue;
                        string key = string.Format(CultureInfo.InvariantCulture, "{0:0.000},{1:0.000},{2:0.000}", e.Target.Pos.X, e.Target.Pos.Y, e.Target.Pos.Z);
                        KeyValuePair<int, string[]> lift;
                        if (lifts.TryGetValue(key, out lift))
                        {
                            e.Target.Floor = lift.Key;
                            e.Target.Destinations = lift.Value;
                        }
                    }
                }
            }
            catch
            {
                // no floors: the elevator steps fall back to walking out and letting BuddyCron take the lift
            }
            return events;
        }

        /// <summary>Builds the ready profile from a recording .csv with the plugin's own builder.</summary>
        public static string MakeProfile(string csvPath, string baseDir, string profileFolderSetting)
        {
            string[] problems;
            return MakeProfile(csvPath, baseDir, profileFolderSetting, out problems);
        }

        /// <summary>
        /// "Place-yyyy-MM-dd_HH-mm(-n)": the place, when the recording started, and " n" for a second one started in the
        /// same minute. False when the name isn't like that (a renamed file).
        /// </summary>
        private static bool ParseStem(string baseName, out string place, out DateTime started, out string extra)
        {
            place = string.Empty;
            started = default(DateTime);
            extra = string.Empty;
            string stemName = baseName;
            int lastDash = baseName.LastIndexOf('-');
            int tail;
            if (lastDash > 0 && baseName.Length - lastDash <= 3 && int.TryParse(baseName.Substring(lastDash + 1), out tail) && baseName.Length - 16 - (baseName.Length - lastDash) > 0)
            {
                // "CZ-198-2026-11-03_15-48-2": a second recording started in the same minute
                string head = baseName.Substring(0, lastDash);
                DateTime test;
                if (DateTime.TryParseExact(head.Substring(head.Length - 16), "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out test))
                {
                    stemName = head;
                    extra = " " + tail.ToString(CultureInfo.InvariantCulture);
                }
            }
            int dash = stemName.Length - 16;
            DateTime parsed;
            if (dash > 1 && stemName[dash - 1] == '-' &&
                DateTime.TryParseExact(stemName.Substring(dash), "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                started = parsed;
                place = stemName.Substring(0, dash - 1);
                return true;
            }
            extra = string.Empty;
            return false;
        }

        /// <summary>Builds the ready profile from a recording .csv with the plugin's own builder; problems = what keeps it
        /// from running on plain BuddyCron (empty = finished).</summary>
        public static string MakeProfile(string csvPath, string baseDir, string profileFolderSetting, out string[] problemsOut)
        {
            problemsOut = new string[0];
            // A recording that carried on an earlier one (or was carried on later) made one profile from all its parts:
            // the remake does the same, timed from the last part as the live recorder was.
            List<string> parts = RecordingSession.PartsOf(csvPath, true);
            string last = parts[parts.Count - 1];
            string root = parts[0];
            string place;
            List<RecEvent> events = Load(last, out place);
            if (events.Count == 0)
                throw new InvalidDataException("No events in " + Path.GetFileName(last));
            if (parts.Count > 1)
            {
                DateTime lastStart = default(DateTime);
                foreach (RecEvent e in events)
                {
                    if (e.Time != default(DateTime))
                    {
                        lastStart = e.Time.AddSeconds(-e.Elapsed);
                        break;
                    }
                }
                var earlier = new List<RecEvent>();
                for (int i = 0; i < parts.Count - 1; i++)
                {
                    string ignored;
                    earlier.AddRange(Load(parts[i], out ignored));
                }
                double newest = double.MinValue;
                foreach (RecEvent e in earlier)
                {
                    e.Elapsed = e.Time != default(DateTime) && lastStart != default(DateTime) ? (e.Time - lastStart).TotalSeconds : e.Elapsed - 1000000;
                    if (e.Elapsed > newest)
                        newest = e.Elapsed;
                }
                if (newest >= 0)
                {
                    // the clock went back between the parts (summer time ended): the earlier parts still come first
                    foreach (RecEvent e in earlier)
                        e.Elapsed -= newest + 1.0;
                }
                earlier.AddRange(events);
                events = earlier;
                RrLog.Info("{0} is one of {1} recordings that carry each other on: the profile is made from all of them.",
                    Path.GetFileName(csvPath), parts.Count);
            }
            // named like the live recorder named it: after the first part (that is the file it kept writing)
            string baseName = Path.GetFileNameWithoutExtension(root);
            DateTime started;
            string extra;
            string stemPlace;
            if (ParseStem(baseName, out stemPlace, out started, out extra))
            {
                if (stemPlace.Length > 0)
                    place = stemPlace; // the place the live recorder filed it under (the planet), not the first area's name
            }
            else
            {
                started = File.GetLastWriteTime(root);
            }
            if (place.Length == 0)
                place = "Recording";

            var safe = new StringBuilder();
            foreach (char ch in place)
                safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '-' : ch);
            string folder = null;
            string chosen = RrSettings.CleanPath(profileFolderSetting);
            if (chosen.Length > 0)
            {
                folder = Path.IsPathRooted(chosen) ? chosen : Path.Combine(baseDir, chosen);
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    RrLog.Warn("The profile folder \"{0}\" can't be used ({1}); using the default one.", folder, ex.Message);
                    folder = null;
                }
            }
            if (folder == null)
                folder = RecordingSession.FindPlaceFolder(baseDir, place);
            if (folder == null)
                folder = Path.Combine(baseDir, "Profiles", "Recorded", safe.ToString()); // one folder per planet / area
            Directory.CreateDirectory(folder);
            string fileName = safe + " - Recorded " + started.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture) + extra + ".xml";
            string path = Path.Combine(folder, fileName);
            try
            {
                // the profile the live recorder saved this recording in (its .txt footer says): the remake replaces that one
                string saved = RecordingSession.FooterProfile(Path.ChangeExtension(last, ".txt"));
                if (saved != null && saved.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.GetDirectoryName(saved)))
                {
                    path = saved;
                    folder = Path.GetDirectoryName(saved);
                    fileName = Path.GetFileName(saved);
                }
            }
            catch (Exception ex)
            {
                RrLog.Debug("Reading where the profile was saved: {0}", ex.Message);
            }
            string name = place + " - Recorded " + started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            List<string> problems;
            string text = ReplayBuilder.Build(events, name, fileName, Path.GetFileNameWithoutExtension(last), out problems);
            problemsOut = problems.ToArray();
            if (File.Exists(path))
            {
                // the profile you have (maybe fixed in the control panel or the review list) is kept: Undo in the control
                // panel puts it back
                string old = File.ReadAllText(path);
                if (!string.Equals(old, text, StringComparison.Ordinal))
                {
                    // a new file every time (to the millisecond, numbered if taken): a second remake never overwrites the first backup
                    string kept = ProfileEditor.NewBackupPath(path, "before fix remake");
                    File.Copy(path, kept, false);
                    RrLog.Info("Your earlier {0} is kept in {1} (Undo in the control panel puts it back).", fileName, kept);
                }
            }
            RecordingSession.WriteWhole(path, text);
            if (!Recorder.IsRecording)
            {
                // show it in the task tracker (never while a recording is going on: the tracker and the review list are that recording's)
                RecordingSession.LiveProblems = problemsOut;
                RecordingSession.LiveTrace = ReplayBuilder.Trace(events).ToArray();
                RecordingSession.LiveProfile = path;
            }
            return path;
        }
    }
}
