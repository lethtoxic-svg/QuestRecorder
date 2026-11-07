// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Reborn.Utilities;
using Reborn.Utilities.Settings;

namespace QuestRecorder
{
    /// <summary>One recording: the .txt timeline and the .csv are written as events arrive.</summary>
    internal sealed class RecordingSession
    {
        public const int MaxEventsInMemory = 200000;
        private const long DraftEveryMs = 60000;

        public readonly string Folder;
        public readonly string BaseName;
        public readonly string TxtPath;
        public readonly string CsvPath;
        public readonly string DraftPath;
        public readonly string Place;
        public readonly DateTime Started;

        private readonly List<RecEvent> _events = new List<RecEvent>();
        private List<RecEvent> _joinEvents; // an earlier recording of the same place this one carries on
        private bool _insert;               // "Record from here": the result goes into another profile
        public string Continues = string.Empty;
        private StreamWriter _txt;
        private StreamWriter _csv;
        private int _seq;
        private bool _draftDirty;
        private long _lastDraftTick;
        private bool _truncated;
        private bool _writeBroken;
        private bool _draftBroken;
        private DateTime _lastTime;

        public int QuestEvents;
        public int Kills;
        public int Markers;
        public int Warnings;
        public string LastHeadline = string.Empty;

        private RecordingSession(string folder, string baseName, string place, DateTime started)
        {
            Folder = folder;
            BaseName = baseName;
            Place = place;
            LiveTrace = new string[0];
            LiveActions = new string[0];
            Started = started;
            _lastTime = started;
            TxtPath = Path.Combine(folder, baseName + ".txt");
            CsvPath = Path.Combine(folder, baseName + ".csv");
            DraftPath = Path.Combine(folder, baseName + "-draft.xml");
            _lastDraftTick = Environment.TickCount64;
        }

        public int Count
        {
            get { return _seq; }
        }

        // ------------------------------------------------------------------ folder and files

        /// <summary>The Recordings folder: the setting, else next to BuddyCron's Logs folder, else next to BuddyCron.</summary>
        public static string ResolveFolder()
        {
            string custom = string.Empty;
            try
            {
                custom = RrSettings.Instance.OutputFolder;
            }
            catch
            {
                custom = string.Empty;
            }
            string root = BuddyCronFolder();
            if (custom.Length > 0)
            {
                try
                {
                    return Path.IsPathRooted(custom) ? Path.GetFullPath(custom) : Path.GetFullPath(Path.Combine(root, custom));
                }
                catch (Exception ex)
                {
                    RrLog.Warn("The recordings folder \"{0}\" is not usable ({1}); using the default one.", custom, ex.Message);
                }
            }
            return Path.Combine(root, "Recordings");
        }

        /// <summary>The folder that holds BuddyCron's Logs folder (walks up from the current log file).</summary>
        private static string BuddyCronFolder()
        {
            try
            {
                string log = Logging.LogFilePath;
                if (!string.IsNullOrEmpty(log))
                {
                    DirectoryInfo d = new FileInfo(log).Directory;
                    while (d != null)
                    {
                        if (string.Equals(d.Name, "Logs", StringComparison.OrdinalIgnoreCase) && d.Parent != null)
                            return d.Parent.FullName;
                        d = d.Parent;
                    }
                }
            }
            catch
            {
                // fall through
            }
            try
            {
                string dir = Reborn.Utilities.Utilities.AssemblyDirectory;
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            catch
            {
                // fall through
            }
            try
            {
                string dir = JsonSettings.AssemblyPath;
                if (!string.IsNullOrEmpty(dir))
                    return File.Exists(dir) ? Path.GetDirectoryName(dir) : dir;
            }
            catch
            {
                // fall through
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>Creates the folder and the .txt / .csv files. Throws when they cannot be created.</summary>
        public static RecordingSession Create(Snap first, AreaInfo area, RrSettings cfg)
        {
            string folder = ResolveFolder();
            Directory.CreateDirectory(folder);
            string place = area.Planet.Length > 0 ? area.Planet : area.Label;
            string stem = Fmt.FileName(place, "Recording") + "-" + first.Time.ToString("yyyy-MM-dd_HH-mm", System.Globalization.CultureInfo.InvariantCulture);
            string baseName = stem;
            for (int n = 2; n < 1000; n++)
            {
                if (!File.Exists(Path.Combine(folder, baseName + ".txt")) && !File.Exists(Path.Combine(folder, baseName + ".csv")) &&
                    !File.Exists(Path.Combine(folder, baseName + "-draft.xml")))
                    break;
                baseName = stem + "-" + n.ToString();
            }

            var s = new RecordingSession(folder, baseName, place, first.Time);
            s._insert = Merge.TargetPath != null;
            if (!s._insert)
                s.TryJoin();
            try
            {
                s._txt = OpenWriter(s.TxtPath, new UTF8Encoding(false));
                s._csv = OpenWriter(s.CsvPath, new UTF8Encoding(true)); // BOM so Excel reads the names right
                s.WriteHeader(first, area, cfg);
                s._csv.WriteLine(EventText.CsvHeader);
                s.Flush();
            }
            catch
            {
                s.CloseWriters();
                throw;
            }
            return s;
        }

        private static StreamWriter OpenWriter(string path, Encoding enc)
        {
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var w = new StreamWriter(fs, enc);
            w.NewLine = "\r\n";
            return w;
        }

        private void WriteHeader(Snap first, AreaInfo area, RrSettings cfg)
        {
            _txt.WriteLine("Quest Recorder " + QuestRecorderPlugin.Patch + " - timeline");
            _txt.WriteLine("Started     " + Fmt.Stamp(first.Time));
            _txt.WriteLine("Character   " + (first.PlayerName.Length > 0 ? first.PlayerName : "(unknown)") + ", level " + Fmt.UInt(first.Level));
            _txt.WriteLine("Area        \"" + area.Label + "\" " + Fmt.Hex(area.Id) +
                           (area.Internal.Length > 0 ? " (" + area.Internal + ")" : string.Empty) +
                           (area.Planet.Length > 0 ? ", planet " + area.Planet : string.Empty));
            _txt.WriteLine(Fmt.Format("Settings    look every {0} ms, breadcrumb every {1} m, jump at {2} m, talk partner search {3} m",
                cfg.SampleMs, cfg.BreadcrumbMeters, cfg.JumpMeters, cfg.PartnerRadius));
            _txt.WriteLine("Files       " + BaseName + ".txt (this file), " + BaseName + ".csv (one row per event)");
            if (Continues.Length > 0)
                _txt.WriteLine("Continues   " + Continues);
            _txt.WriteLine("Positions   x,y,z with 3 decimals (the way profiles write XYZ). Ids in hex as profiles write them.");
            _txt.WriteLine("Quest steps branch/step are QuestState.BranchId/StepId; \"task\" is the 0-based task index.");
            _txt.WriteLine(new string('-', 100));
        }

        // ------------------------------------------------------------------ events

        public void Add(RecEvent e)
        {
            _seq++;
            e.Seq = _seq;
            e.Elapsed = Math.Max(0.0, (e.Time - Started).TotalSeconds);
            if (e.Time > _lastTime)
                _lastTime = e.Time;
            if (string.IsNullOrEmpty(e.Summary))
                e.Summary = EventText.Headline(e);

            Tally(e);
            if (e.Kind != EvKind.Breadcrumb)
                LastHeadline = Fmt.Clock(e.Time) + "  " + e.Summary;

            if (_events.Count < MaxEventsInMemory)
            {
                _events.Add(e);
                _draftDirty = true;
            }
            else if (!_truncated)
            {
                _truncated = true;
                RrLog.Warn("Big recording (over {0} events) - still recording, the profile covers the first {0}.", MaxEventsInMemory);
            }

            WriteEvent(e);
            LogEvent(e);
            if (e.Kind == EvKind.QuestTask || e.Kind == EvKind.QuestStep || e.Kind == EvKind.QuestAdded || e.Kind == EvKind.QuestCompleted)
                UpdateTracker();
            if (e.Kind == EvKind.QuestTask || e.Kind == EvKind.QuestStep || e.Kind == EvKind.QuestAdded || e.Kind == EvKind.QuestCompleted ||
                e.Kind == EvKind.ObjectClick || e.Kind == EvKind.NpcClick || e.Kind == EvKind.InteractionOpen || e.Kind == EvKind.BoardOpen ||
                e.Kind == EvKind.ConvoChoice || e.Kind == EvKind.QuestItem || e.Kind == EvKind.ElevatorRide || e.Kind == EvKind.Marker)
                UpdateActions();
        }

        /// <summary>"What you did" shown live in the Quest Recorder window: every action, OK or !! with the reason.</summary>
        public static volatile string[] LiveActions = new string[0];
        private int _actionsWarned;

        private void UpdateActions()
        {
            try
            {
                List<string> a = ReplayBuilder.Interactions(_events, false);
                LiveActions = a.ToArray();
                // a !! line: say it now, while you're still standing there
                for (int i = _actionsWarned; i < a.Count; i++)
                {
                    if (!a[i].StartsWith("!!", StringComparison.Ordinal))
                        continue;
                    RrLog.Warn("{0}", a[i]);
                    int dash = a[i].LastIndexOf("  - ", StringComparison.Ordinal);
                    GameToast.Show("Quest Recorder: " + (dash > 0 ? a[i].Substring(dash + 4) : a[i]), 10000);
                }
                _actionsWarned = a.Count;
            }
            catch (Exception ex)
            {
                if (RrLog.Every("actions", TimeSpan.FromMinutes(10)))
                    RrLog.Debug("What-you-did list failed: {0}", ex.Message);
            }
        }

        /// <summary>Task tracker shown in the Quest Recorder window: what the profile will do for each task so far.</summary>
        public static volatile string[] LiveTrace = new string[0];

        /// <summary>The profile the tracker lines belong to.</summary>
        public static volatile string LiveProfile = string.Empty;
        private int _traceWarned;

        private void UpdateTracker()
        {
            try
            {
                List<string> t = ReplayBuilder.Trace(AllEvents());
                LiveTrace = t.ToArray();
                // say it straight away when a task was ticked but no action was caught (so you can redo it while you are there)
                for (int i = _traceWarned; i < t.Count; i++)
                {
                    if (t[i].IndexOf("!!", StringComparison.Ordinal) < 0)
                        continue;
                    RrLog.Warn("Task tracker: {0}", t[i]);
                    // on screen too, over the game (BuddyCron's overlay toast)
                    string task = t[i];
                    int arrow = task.IndexOf("  ->", StringComparison.Ordinal);
                    if (arrow > 0)
                        task = task.Substring(0, arrow);
                    int dot = task.IndexOf(". ", StringComparison.Ordinal);
                    if (dot > 0 && dot < 5)
                        task = task.Substring(dot + 2);
                    if (!GameToast.Show("Quest Recorder: nothing caught for \"" + task + "\" - it did not see what you used. Redo that one if you can.", 12000) &&
                        RrLog.Every("overlay-off", TimeSpan.FromMinutes(30)))
                        RrLog.Info("(Turn on BuddyCron's overlay in its settings to see these in game.)");
                }
                _traceWarned = t.Count;
            }
            catch (Exception ex)
            {
                if (RrLog.Every("tracker", TimeSpan.FromMinutes(10)))
                    RrLog.Debug("Task tracker failed: {0}", ex.Message);
            }
        }

        private void Tally(RecEvent e)
        {
            switch (e.Kind)
            {
                case EvKind.QuestAdded:
                case EvKind.QuestStep:
                case EvKind.QuestTask:
                case EvKind.QuestCompleted:
                case EvKind.QuestAbandoned:
                case EvKind.QuestRemoved:
                    if (!e.QuestHidden)
                        QuestEvents++;
                    break;
                case EvKind.Kill:
                    Kills++;
                    break;
                case EvKind.Marker:
                    Markers++;
                    break;
                case EvKind.Warning:
                    Warnings++;
                    break;
            }
        }

        private void WriteEvent(RecEvent e)
        {
            if (_writeBroken)
                return;
            try
            {
                foreach (string line in EventText.TxtLines(e))
                    _txt.WriteLine(line);
                _csv.WriteLine(EventText.CsvRow(e));
            }
            catch (Exception ex)
            {
                _writeBroken = true;
                RrLog.Error("Couldn't write the recording file ({0}).", ex.Message);
            }
        }

        private static void LogEvent(RecEvent e)
        {
            if (e.QuestHidden)
                return;
            bool log;
            switch (e.Kind)
            {
                case EvKind.Breadcrumb:
                case EvKind.ConvoEnd:
                case EvKind.CombatStart:
                case EvKind.CombatEnd:
                case EvKind.Mounted:
                case EvKind.Dismounted:
                case EvKind.Cast:
                case EvKind.TaxiOpen:
                case EvKind.TaxiStart:
                case EvKind.ElevatorOpen:
                case EvKind.InteractionOpen:
                case EvKind.BoardClose:
                case EvKind.Loading:
                case EvKind.Revived:
                case EvKind.NpcClick:
                case EvKind.QuestLog:
                    log = false;
                    break;
                default:
                    log = true;
                    break;
            }
            if (!log)
                return;
            bool enabled;
            try
            {
                enabled = RrSettings.Instance.LogEvents;
            }
            catch
            {
                enabled = true;
            }
            if (enabled || e.Kind == EvKind.Warning)
                RrLog.Event("{0}", e.Summary);
        }

        public void Flush()
        {
            if (_writeBroken)
                return;
            try
            {
                if (_txt != null)
                    _txt.Flush();
                if (_csv != null)
                    _csv.Flush();
            }
            catch (Exception ex)
            {
                _writeBroken = true;
                RrLog.Error("Couldn't write the recording file ({0}).", ex.Message);
            }
        }

        // ------------------------------------------------------------------ draft

        /// <summary>Rewrites the draft about once a minute while new events arrive (cheap: pure C#, no game access).</summary>
        public void MaybeWriteDraft(long now)
        {
            if (!_draftDirty || now - _lastDraftTick < DraftEveryMs)
                return;
            _lastDraftTick = now;
            WriteDraft(false, TagCheck.Cached);
        }

        private void WriteDraft(bool final, TagCheckResult tags)
        {
            _draftDirty = false;
            string text;
            try
            {
                var info = new DraftInfo();
                info.Place = Place;
                info.Started = Started;
                info.Ended = _lastTime;
                info.BaseName = BaseName;
                info.Final = final;
                info.Truncated = _truncated;
                text = DraftBuilder.Build(_events, info, RrSettings.Instance, tags);
            }
            catch (Exception ex)
            {
                if (!_draftBroken || final)
                    RrLog.Exception("Building the draft profile", ex);
                _draftBroken = true;
                return;
            }
            // The annotated draft was mistaken for the profile.
            if (!RrSettings.Instance.KeepDraft)
            {
                WriteProfileCopy(text);
                return;
            }
            string tmp = DraftPath + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                try
                {
                    File.Move(tmp, DraftPath, true);
                }
                catch (IOException)
                {
                    // the target is open somewhere: copy over it instead
                    File.Copy(tmp, DraftPath, true);
                    File.Delete(tmp);
                }
                catch (UnauthorizedAccessException)
                {
                    File.Copy(tmp, DraftPath, true);
                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                if (final || RrLog.Every("draft-write", TimeSpan.FromMinutes(10)))
                    RrLog.Warn("Writing the draft {0} failed: {1}", DraftPath, ex.Message);
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch
                {
                    // ignore
                }
            }
            WriteProfileCopy(text);
        }

        // ------------------------------------------------------------------ ready-to-load profile copy

        private string _profilePath;
        private bool _profileAnnounced;

        /// <summary>The same draft, saved where players keep their profiles so it can be loaded straight away.</summary>
        private void WriteProfileCopy(string text)
        {
            try
            {
                if (_profilePath == null)
                {
                    _profilePath = ProfileCopyPath();
                    LiveProfile = _profilePath;
                }
                if (_profilePath.Length == 0)
                    return;
                // The ready-to-load profile is a REPLAY of the recording.
                string replay;
                try
                {
                    string place = Place.Length > 0 ? Place : "Recording";
                    string name = place + " - Recorded " + Started.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                    ReplayBuilder.CreditName = RrSettings.Instance.CreditName;
                    ReplayBuilder.WalkSpacing = RrSettings.Instance.WalkPointMeters;
                    replay = ReplayBuilder.Build(AllEvents(), name, Path.GetFileName(_profilePath), BaseName);
                }
                catch (Exception ex)
                {
                    if (RrLog.Every("replay-build", TimeSpan.FromMinutes(10)))
                        RrLog.Warn("Couldn't build the profile ({0}). Send the recording to LethToxic.", ex.Message);
                    replay = text;
                }
                File.WriteAllText(_profilePath, replay, new UTF8Encoding(false));
                try
                {
                    // every interaction you made, where, and what your quest log did right after
                    File.WriteAllLines(Path.Combine(Folder, BaseName + " (what you did).txt"), ReplayBuilder.Interactions(_events), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    RrLog.Debug("What-you-did list: {0}", ex.Message);
                }
                if (!_profileAnnounced)
                {
                    _profileAnnounced = true;
                    RrLog.Info("Your profile is saved as you play: {0}", _profilePath);
                }
            }
            catch (Exception ex)
            {
                if (RrLog.Every("profile-copy", TimeSpan.FromMinutes(10)))
                    RrLog.Warn("Saving the profile copy failed: {0}", ex.Message);
            }
        }

        private string ProfileCopyPath()
        {
            if (_insert)
                return Path.Combine(Folder, BaseName + " (bit to add).xml");
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string place = Place.Length > 0 ? Place : "Recording";
            var safe = new StringBuilder();
            foreach (char ch in place)
                safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '-' : ch);
            string folder = null;
            string chosen = RrSettings.Instance.ProfileFolder ?? string.Empty;
            if (chosen.Trim().Length > 0)
                folder = Path.IsPathRooted(chosen) ? chosen : Path.Combine(baseDir, chosen);
            if (folder == null && Place.Length > 0)
                folder = FindPlaceFolder(baseDir, Place);
            if (folder == null)
                folder = Path.Combine(baseDir, "Profiles", "Recorded", safe.ToString()); // one folder per planet / area
            Directory.CreateDirectory(folder);
            // two recordings started in the same minute (CZ-198-...-15-48 and -15-48-2): keep the "-2" so neither overwrites the other
            string stem = Fmt.FileName(place, "Recording") + "-" + Started.ToString("yyyy-MM-dd_HH-mm", System.Globalization.CultureInfo.InvariantCulture);
            string extra = BaseName.Length > stem.Length && BaseName.StartsWith(stem, StringComparison.Ordinal) ? " " + BaseName.Substring(stem.Length).Trim('-', ' ') : string.Empty;
            return Path.Combine(folder, safe + " - Recorded " + Started.ToString("yyyy-MM-dd HH-mm", System.Globalization.CultureInfo.InvariantCulture) + extra + ".xml");
        }

        private static readonly string[] SkipFolders = { "Recordings", "Logs", "Plugins", "Settings", "Reports", "Routines", "BotBases", "Quest Behaviors", "CompiledAssemblies", "bin", "obj", "_backup" };

        /// <summary>A folder (two levels deep at most) whose name contains the place name, e.g. LethToxic\CZ-198 Dailies.</summary>
        internal static string FindPlaceFolder(string baseDir, string place)
        {
            try
            {
                foreach (string top in Directory.GetDirectories(baseDir))
                {
                    string topName = Path.GetFileName(top);
                    if (Array.IndexOf(SkipFolders, topName) >= 0)
                        continue;
                    if (topName.IndexOf(place, StringComparison.OrdinalIgnoreCase) >= 0)
                        return top;
                    foreach (string sub in Directory.GetDirectories(top))
                    {
                        string subName = Path.GetFileName(sub);
                        if (Array.IndexOf(SkipFolders, subName) < 0 && subName.IndexOf(place, StringComparison.OrdinalIgnoreCase) >= 0)
                            return sub;
                    }
                }
            }
            catch
            {
                // unreadable folder: fall back to Profiles\Recorded
            }
            return null;
        }

        // ------------------------------------------------------------------ end

        /// <summary>Footer, close the files, final draft. Never throws.</summary>
        public void Finish(string why, IList<string> stats, bool checkTags)
        {
            try
            {
                if (!_writeBroken && _txt != null)
                {
                    _txt.WriteLine(new string('-', 100));
                    _txt.WriteLine("Stopped     " + Fmt.Stamp(DateTime.Now) + (string.IsNullOrEmpty(why) ? string.Empty : " (" + why + ")") +
                                   ", recorded " + Fmt.Duration((_lastTime - Started).TotalSeconds));
                    _txt.WriteLine(Fmt.Format("Events      {0} (quest updates {1}, kills {2}, markers {3}, warnings {4})", _seq,
                        QuestEvents, Kills, Markers, Warnings));
                    if (stats != null)
                    {
                        foreach (string line in stats)
                            _txt.WriteLine(line);
                    }
                    if (RrSettings.Instance.KeepDraft)
                        _txt.WriteLine("Draft       " + DraftPath);
                }
            }
            catch (Exception ex)
            {
                RrLog.Debug("Writing the footer failed: {0}", ex.Message);
            }
            Flush();
            CloseWriters();

            TagCheckResult tags = TagCheck.Cached;
            if (checkTags && tags == null)
            {
                try
                {
                    tags = TagCheck.Run();
                }
                catch (Exception ex)
                {
                    RrLog.Debug("Tag check failed: {0}", ex.Message);
                }
            }
            WriteDraft(true, tags);
            if (_insert && _profilePath != null)
                Merge.Finish(_profilePath);
        }

        // ------------------------------------------------------------------ carrying on an earlier recording

        /// <summary>This recording plus the earlier one(s) it carries on, in time order.</summary>
        private List<RecEvent> AllEvents()
        {
            if (_joinEvents == null)
                return _events;
            var all = new List<RecEvent>(_joinEvents.Count + _events.Count);
            all.AddRange(_joinEvents);
            all.AddRange(_events);
            return all;
        }

        /// <summary>
        /// Logged off halfway through a planet? If the last recording of this place is less than a day old and its
        /// profile wasn't changed in the control panel, this one carries on in the same profile.
        /// </summary>
        private void TryJoin()
        {
            try
            {
                if (!RrSettings.Instance.JoinRecordings || Place.Length == 0)
                    return;
                string prefix = Fmt.FileName(Place, "Recording") + "-";
                string prev = null;
                DateTime best = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(Folder, prefix + "*.csv"))
                {
                    if (Path.GetFileNameWithoutExtension(f) == BaseName)
                        continue;
                    DateTime w = File.GetLastWriteTime(f);
                    if (w > best)
                    {
                        best = w;
                        prev = f;
                    }
                }
                if (prev == null || DateTime.Now - best > TimeSpan.FromHours(24))
                    return;
                string profile = Reports.ProfileFor(prev);
                if (profile == null || !File.Exists(profile))
                    return;
                string fixes = Path.Combine(Path.GetDirectoryName(profile), "_fixer backups");
                if (Directory.Exists(fixes) && (Directory.GetFiles(fixes, Path.GetFileNameWithoutExtension(profile) + " (before fix*").Length > 0 ||
                                                Directory.GetFiles(fixes, Path.GetFileNameWithoutExtension(profile) + " (before review*").Length > 0))
                {
                    // you saved it from the review list or changed it: it's yours, a new recording never touches it
                    RrLog.Info("Your last {0} profile was saved from the review list or changed, so this recording starts a new one.", Place);
                    return;
                }
                var events = new List<RecEvent>();
                LoadChain(prev, events, 0);
                if (events.Count == 0)
                    return;
                // every quest you picked up there was finished: that run is complete, start a new profile
                var open = new HashSet<ulong>();
                foreach (RecEvent e in events)
                {
                    if (e.Kind == EvKind.QuestAdded && !e.QuestHidden)
                        open.Add(e.QuestId);
                    else if (e.Kind == EvKind.QuestCompleted)
                        open.Remove(e.QuestId);
                }
                if (open.Count == 0 && events.Exists(x => x.Kind == EvKind.QuestCompleted))
                {
                    RrLog.Info("Your last {0} recording was finished, so this recording starts a new profile.", Place);
                    return;
                }
                foreach (RecEvent e in events)
                    e.Elapsed = e.Time != default(DateTime) ? (e.Time - Started).TotalSeconds : e.Elapsed - 1000000; // before this recording: negative, still in order
                _joinEvents = events;
                _profilePath = profile;
                LiveProfile = profile;
                Continues = Path.GetFileNameWithoutExtension(prev);
                RrLog.Info("Carrying on your {0} profile from earlier: {1}", Place, Path.GetFileName(profile));
                GameToast.Show("Quest Recorder: carrying on your " + Place + " profile from earlier.", 8000);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Couldn't carry on the last recording: {0}", ex.Message);
                _joinEvents = null;
            }
        }

        /// <summary>Loads a recording and, first, the ones it carried on (the "Continues" line in its .txt).</summary>
        private static void LoadChain(string csv, List<RecEvent> into, int depth)
        {
            if (depth > 10 || !File.Exists(csv))
                return;
            string txt = Path.ChangeExtension(csv, ".txt");
            if (File.Exists(txt))
            {
                foreach (string line in File.ReadLines(txt))
                {
                    if (line.StartsWith("Continues", StringComparison.Ordinal))
                    {
                        string earlier = Path.Combine(Path.GetDirectoryName(csv), line.Substring("Continues".Length).Trim() + ".csv");
                        LoadChain(earlier, into, depth + 1);
                        break;
                    }
                    if (line.StartsWith("---", StringComparison.Ordinal))
                        break;
                }
            }
            string place;
            into.AddRange(RecordingLoader.Load(csv, out place));
        }

        private void CloseWriters()
        {
            try
            {
                if (_txt != null)
                    _txt.Dispose();
            }
            catch
            {
                // ignore
            }
            try
            {
                if (_csv != null)
                    _csv.Dispose();
            }
            catch
            {
                // ignore
            }
            _txt = null;
            _csv = null;
        }
    }
}
