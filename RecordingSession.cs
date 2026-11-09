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
        private long _startedTick;   // Environment.TickCount64 of the first look (0 = not known)
        private double _lastElapsed;

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
            LiveProblems = new string[0];
            LiveProfile = string.Empty; // until this recording has a profile (never the last recording's one)
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
                    string full = Path.IsPathRooted(custom) ? Path.GetFullPath(custom) : Path.GetFullPath(Path.Combine(root, custom));
                    // GetFullPath no longer throws for a bad name: only a folder that exists or can be made is used
                    if (!Directory.Exists(full))
                        Directory.CreateDirectory(full);
                    return full;
                }
                catch (Exception ex)
                {
                    if (RrLog.Every("recordings-folder", TimeSpan.FromMinutes(10)))
                        RrLog.Warn("The recordings folder \"{0}\" is not usable ({1}); using the default one.", custom, ex.Message);
                }
            }
            return Path.Combine(root, "Recordings");
        }

        /// <summary>Makes the folder; false (and a warning) when it can't be made, e.g. a bad name in the settings.</summary>
        private static bool TryMakeFolder(string folder, string what)
        {
            try
            {
                Directory.CreateDirectory(folder);
                return true;
            }
            catch (Exception ex)
            {
                if (RrLog.Every("folder:" + what, TimeSpan.FromMinutes(10)))
                    RrLog.Warn("The {0} \"{1}\" can't be used ({2}); using the default one.", what, folder, ex.Message);
                return false;
            }
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
            s._startedTick = first.Tick;
            s._insert = Merge.TargetPath != null;
            if (!s._insert)
                s.TryJoin(first, area);
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
            // from the tick count, not the clock: summer time ending or a time sync never moves an event back or forward
            if (e.Tick != 0 && _startedTick != 0)
                e.Elapsed = Math.Max(0.0, (e.Tick - _startedTick) / 1000.0);
            else
                e.Elapsed = Math.Max(0.0, (e.Time - Started).TotalSeconds);
            if (e.Elapsed > _lastElapsed)
                _lastElapsed = e.Elapsed;
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
            // The annotated draft is only a notes file, made when you ask for it: it never decides whether the profile is saved.
            if (RrSettings.Instance.KeepDraft)
                WriteNotesDraft(final, tags);
            WriteProfileCopy(final);
        }

        private void WriteNotesDraft(bool final, TagCheckResult tags)
        {
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
            try
            {
                WriteWhole(DraftPath, text);
            }
            catch (Exception ex)
            {
                if (final || RrLog.Every("draft-write", TimeSpan.FromMinutes(10)))
                    RrLog.Warn("Writing the draft {0} failed: {1}", DraftPath, ex.Message);
            }
        }

        /// <summary>
        /// Writes the whole file or leaves the old one: into a .tmp next to it first, then over it. A kill or a full disk
        /// halfway never leaves a cut or empty file behind.
        /// </summary>
        internal static void WriteWhole(string path, string text)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                try
                {
                    File.Move(tmp, path, true);
                }
                catch (IOException)
                {
                    // the target is open somewhere: copy over it instead
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                }
                catch (UnauthorizedAccessException)
                {
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch
                {
                    // ignore
                }
                throw;
            }
        }

        // ------------------------------------------------------------------ ready-to-load profile copy

        /// <summary>What keeps the last profile from running on plain BuddyCron (empty = it's finished).</summary>
        public static volatile string[] LiveProblems = new string[0];

        /// <summary>Forgets the last recording's tracker, actions and profile (a new recording is starting).</summary>
        internal static void ClearLive()
        {
            LiveTrace = new string[0];
            LiveActions = new string[0];
            LiveProblems = new string[0];
            LiveProfile = string.Empty;
        }

        private string _profilePath;
        private bool _profileAnnounced;
        private bool _lastBuildFailed;
        // what this recording last saved in the profile, to see when someone else saved it since (null = nothing yet)
        private string _lastWritten;
        private long _writtenLength;
        private long _writtenTicks;

        private void Remember(string text)
        {
            _lastWritten = text;
            try
            {
                var fi = new FileInfo(_profilePath);
                _writtenLength = fi.Length;
                _writtenTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                _writtenLength = -1;
                _writtenTicks = 0;
            }
        }

        /// <summary>True when the profile was saved by someone else (the control panel, the review list, an editor)
        /// since this recording last wrote it.</summary>
        private bool ChangedSinceWritten()
        {
            if (_lastWritten == null || _profilePath == null || _profilePath.Length == 0 || !File.Exists(_profilePath))
                return false;
            string now;
            try
            {
                var fi = new FileInfo(_profilePath);
                if (fi.Length == _writtenLength && fi.LastWriteTimeUtc.Ticks == _writtenTicks)
                    return false;
                now = File.ReadAllText(_profilePath);
            }
            catch
            {
                return false; // can't read it: the write after this fails the same way, nothing is lost
            }
            if (string.Equals(now, _lastWritten, StringComparison.Ordinal))
            {
                Remember(_lastWritten); // only touched (a copy, a sync tool): still ours
                return false;
            }
            return true;
        }

        /// <summary>
        /// Someone saved the profile while this recording was going on: it stays exactly as they saved it, and the
        /// recording goes on in a copy next to it (writing over it would throw their fix away).
        /// </summary>
        private void LeaveChangedProfile()
        {
            string kept = _profilePath;
            string dir = Path.GetDirectoryName(kept) ?? string.Empty;
            string stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(kept), @" \(recorder copy(?: \d+)?\)$", string.Empty);
            string next = null;
            for (int n = 1; n < 100 && next == null; n++)
            {
                string p = Path.Combine(dir, stem + (n == 1 ? " (recorder copy)" : " (recorder copy " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")") + ".xml");
                if (!File.Exists(p))
                    next = p;
            }
            if (next == null)
                next = Path.Combine(dir, stem + " (recorder copy " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ").xml");
            _profilePath = next;
            _lastWritten = null;
            LiveProfile = next;
            RrLog.Warn("{0} was changed while recording, so it stays as it was saved. This recording goes on in {1}.", Path.GetFileName(kept), Path.GetFileName(next));
            GameToast.Show("Quest Recorder: your profile was changed while recording - it stays as saved. The recording goes on in " + Path.GetFileName(next) + ".", 12000);
        }

        /// <summary>The profile, saved where players keep their profiles so it can be loaded straight away.</summary>
        private void WriteProfileCopy(bool final)
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
                if (ChangedSinceWritten())
                    LeaveChangedProfile();
                // The ready-to-load profile is a REPLAY of the recording.
                string replay;
                List<string> problems;
                try
                {
                    string place = Place.Length > 0 ? Place : "Recording";
                    string name = place + " - Recorded " + Started.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                    ReplayBuilder.CreditName = RrSettings.Instance.CreditName;
                    ReplayBuilder.WalkSpacing = RrSettings.Instance.WalkPointMeters;
                    replay = ReplayBuilder.Build(AllEvents(), name, Path.GetFileName(_profilePath), BaseName, out problems);
                }
                catch (Exception ex)
                {
                    // never the draft in its place: the last profile stays, marked not finished
                    _lastBuildFailed = true;
                    var failed = new List<string> { "the profile couldn't be built (" + ex.Message + ")" };
                    LiveProblems = failed.ToArray();
                    LiveProfile = _profilePath; // the problems go with this profile (a profile made from an old recording may have been shown)
                    if (final || RrLog.Every("replay-build", TimeSpan.FromMinutes(10)))
                        RrLog.Warn("This profile is NOT finished: couldn't build it ({0}). Send the recording to LethToxic.", ex.Message);
                    try
                    {
                        if (_lastWritten != null && File.Exists(_profilePath))
                        {
                            string marked = ReplayBuilder.MarkNotFinished(_lastWritten, failed);
                            WriteWhole(_profilePath, marked);
                            Remember(marked);
                        }
                    }
                    catch (Exception mex)
                    {
                        RrLog.Debug("Marking the profile not finished: {0}", mex.Message);
                    }
                    return;
                }
                WriteWhole(_profilePath, replay);
                Remember(replay);
                _lastBuildFailed = false;
                // Load check: the profile must run on plain BuddyCron (the profile itself says so when it isn't finished)
                LiveProblems = problems.ToArray();
                LiveProfile = _profilePath; // the problems go with this profile (a profile made from an old recording may have been shown)
                if (problems.Count > 0 && (final || RrLog.Every("plain-check", TimeSpan.FromMinutes(10))))
                    RrLog.Warn("This profile is NOT finished: {0}. Send the recording to LethToxic.", string.Join(", ", problems.ToArray()));
                try
                {
                    // every interaction you made, where, and what your quest log did right after
                    List<string> did = ReplayBuilder.Interactions(_events);
                    WriteWhole(Path.Combine(Folder, BaseName + " (what you did).txt"), did.Count > 0 ? string.Join("\r\n", did.ToArray()) + "\r\n" : string.Empty);
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
            {
                folder = Path.IsPathRooted(chosen) ? chosen : Path.Combine(baseDir, chosen);
                if (!TryMakeFolder(folder, "profile folder"))
                    folder = null; // a bad folder in the settings: the profile still gets saved, in the usual place
            }
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
            // where this recording's profile is (the next recording that carries this one on writes there too)
            string profileLine = null;
            if (!_insert)
            {
                try
                {
                    if (_profilePath == null)
                    {
                        _profilePath = ProfileCopyPath();
                        LiveProfile = _profilePath;
                    }
                    else if (ChangedSinceWritten())
                    {
                        LeaveChangedProfile();
                    }
                    if (_profilePath.Length > 0)
                        profileLine = ShortPath(_profilePath);
                }
                catch (Exception ex)
                {
                    RrLog.Debug("Finding the profile for the footer: {0}", ex.Message);
                }
            }
            try
            {
                if (!_writeBroken && _txt != null)
                {
                    _txt.WriteLine(new string('-', 100));
                    _txt.WriteLine("Stopped     " + Fmt.Stamp(DateTime.Now) + (string.IsNullOrEmpty(why) ? string.Empty : " (" + why + ")") +
                                   ", recorded " + Fmt.Duration(_lastElapsed));
                    _txt.WriteLine(Fmt.Format("Events      {0} (quest updates {1}, kills {2}, markers {3}, warnings {4})", _seq,
                        QuestEvents, Kills, Markers, Warnings));
                    if (stats != null)
                    {
                        foreach (string line in stats)
                            _txt.WriteLine(line);
                    }
                    if (RrSettings.Instance.KeepDraft)
                        _txt.WriteLine("Draft       " + DraftPath);
                    if (profileLine != null)
                        _txt.WriteLine(ProfileKey + profileLine);
                }
            }
            catch (Exception ex)
            {
                RrLog.Debug("Writing the footer failed: {0}", ex.Message);
            }
            Flush();
            CloseWriters();

            TagCheckResult tags = TagCheck.Cached;
            if (checkTags && tags == null && RrSettings.Instance.KeepDraft)
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
            if (_insert)
            {
                if (_profilePath != null && !_lastBuildFailed)
                {
                    Merge.Finish(_profilePath);
                }
                else
                {
                    // the bit couldn't be built: nothing half-made goes into your profile
                    string target = Merge.TargetPath;
                    Merge.TargetPath = null;
                    if (target != null)
                    {
                        RrLog.Warn("The bit you recorded couldn't be built, so nothing was added to {0}. Send the recording to LethToxic.", Path.GetFileName(target));
                        GameToast.Show("Quest Recorder: the bit you recorded couldn't be built, so nothing was added to your profile.", 10000);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ carrying on an earlier recording

        private const string ProfileKey = "Profile     ";

        /// <summary>The profile's path as the footer writes it: from BuddyCron's folder when it is in there.</summary>
        private static string ShortPath(string path)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir) && path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && path.Length > baseDir.Length)
                return path.Substring(baseDir.Length).TrimStart('\\', '/');
            return path;
        }

        /// <summary>The profile a recording saved to (the "Profile" line of its .txt footer), or null for older recordings.</summary>
        internal static string FooterProfile(string txt)
        {
            if (!File.Exists(txt))
                return null;
            string found = null;
            string tail;
            using (var fs = new FileStream(txt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // the footer is the last few lines: read only the end of a long timeline
                if (fs.Length > 65536)
                    fs.Seek(-65536, SeekOrigin.End);
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    tail = r.ReadToEnd();
            }
            foreach (string line in tail.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.StartsWith(ProfileKey, StringComparison.Ordinal))
                    found = l.Substring(ProfileKey.Length).Trim();
            }
            if (string.IsNullOrEmpty(found))
                return null;
            return Path.IsPathRooted(found) ? found : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, found);
        }

        /// <summary>A line of a recording's .txt header ("Continues   X" gives X for "Continues"), or empty.</summary>
        internal static string HeaderValue(string txt, string key)
        {
            if (!File.Exists(txt))
                return string.Empty;
            // shared: the recording going on now is still writing its own .txt
            using (var fs = new FileStream(txt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.StartsWith("---", StringComparison.Ordinal))
                        break;
                    if (line.StartsWith(key, StringComparison.Ordinal) && line.Length > key.Length && line[key.Length] == ' ')
                        return line.Substring(key.Length).Trim();
                }
            }
            return string.Empty;
        }

        /// <summary>The recording .csv this one carried on (its "Continues" line), or null.</summary>
        internal static string ContinuesFrom(string csv)
        {
            string earlier = HeaderValue(Path.ChangeExtension(csv, ".txt"), "Continues");
            if (earlier.Length == 0)
                return null;
            return Path.Combine(Path.GetDirectoryName(csv) ?? string.Empty, earlier + ".csv");
        }

        /// <summary>
        /// Every part of one carried-on recording, oldest first: the ones this one carried on and the ones that carried
        /// this one on later. Just the one .csv when it was never carried on.
        /// </summary>
        internal static List<string> PartsOf(string csv, bool alsoLater)
        {
            var parts = new List<string> { csv };
            string at = csv;
            for (int depth = 0; depth < 10; depth++)
            {
                string earlier = ContinuesFrom(at);
                if (earlier == null || !File.Exists(earlier) || parts.Exists(p => string.Equals(p, earlier, StringComparison.OrdinalIgnoreCase)))
                    break;
                parts.Insert(0, earlier);
                at = earlier;
            }
            if (!alsoLater)
                return parts;
            try
            {
                string dir = Path.GetDirectoryName(csv) ?? string.Empty;
                // a later part is a recording of the same place: "<place>-yyyy-MM-dd_HH-mm(-n)"
                System.Text.RegularExpressions.Match pm = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(csv), @"^(.*-)\d{4}-\d\d-\d\d_\d\d-\d\d(?:-\d+)?$");
                string pattern = pm.Success ? pm.Groups[1].Value + "*.txt" : "*.txt";
                at = csv;
                for (int depth = 0; depth < 10; depth++)
                {
                    string stem = Path.GetFileNameWithoutExtension(at);
                    string later = null;
                    foreach (string txt in Directory.GetFiles(dir, pattern))
                    {
                        string c = Path.ChangeExtension(txt, ".csv");
                        if (!File.Exists(c) || parts.Exists(p => string.Equals(p, c, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        if (!string.Equals(HeaderValue(txt, "Continues"), stem, StringComparison.Ordinal))
                            continue;
                        if (later == null || string.CompareOrdinal(c, later) > 0)
                            later = c;
                    }
                    if (later == null)
                        break;
                    parts.Add(later);
                    at = later;
                }
            }
            catch (Exception ex)
            {
                RrLog.Debug("Looking for recordings that carried this one on: {0}", ex.Message);
            }
            return parts;
        }

        /// <summary>The profile a recording saved to: its footer, else its own name, else the first part's name.</summary>
        private static string ProfileOf(string csv)
        {
            string p = FooterProfile(Path.ChangeExtension(csv, ".txt"));
            if (p != null && File.Exists(p))
                return p;
            p = Reports.ProfileFor(csv);
            if (p != null && File.Exists(p))
                return p;
            // a recording that carried on an earlier one saved into the first part's profile
            List<string> parts = PartsOf(csv, false);
            if (parts.Count > 1 && !string.Equals(parts[0], csv, StringComparison.OrdinalIgnoreCase))
            {
                p = Reports.ProfileFor(parts[0]);
                if (p != null && File.Exists(p))
                    return p;
            }
            return null;
        }

        /// <summary>Character name from a recording's .txt ("Character   Name, level 80"), or empty.</summary>
        private static string CharacterOf(string txt)
        {
            string who = HeaderValue(txt, "Character");
            int comma = who.IndexOf(',');
            if (comma >= 0)
                who = who.Substring(0, comma);
            who = who.Trim();
            return who == "(unknown)" ? string.Empty : who;
        }

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
        /// Logged off halfway through a planet? If the last recording of this place is less than a day old, was made by
        /// this character, still has missions open that are in your quest log, and its profile wasn't changed since,
        /// this one carries on in the same profile.
        /// </summary>
        private void TryJoin(Snap first, AreaInfo area)
        {
            try
            {
                if (!RrSettings.Instance.JoinRecordings || Place.Length == 0)
                    return;
                string prefix = Fmt.FileName(Place, "Recording") + "-";
                var recent = new List<KeyValuePair<DateTime, string>>();
                foreach (string f in Directory.GetFiles(Folder, prefix + "*.csv"))
                {
                    if (Path.GetFileNameWithoutExtension(f) == BaseName)
                        continue;
                    DateTime w = File.GetLastWriteTime(f);
                    if (DateTime.Now - w <= TimeSpan.FromHours(24))
                        recent.Add(new KeyValuePair<DateTime, string>(w, f));
                }
                recent.Sort((a, b) => b.Key.CompareTo(a.Key)); // newest first
                // this character's last recording of this place: another character's (an alt, the other faction) is never
                // carried on, one profile never holds two characters' missions
                string prev = null;
                bool other = false;
                for (int i = 0; i < recent.Count && i < 20 && prev == null; i++)
                {
                    string who = CharacterOf(Path.ChangeExtension(recent[i].Value, ".txt"));
                    if (who.Length > 0 && first.PlayerName.Length > 0 && !string.Equals(who, first.PlayerName, StringComparison.Ordinal))
                        other = true;
                    else
                        prev = recent[i].Value;
                }
                if (prev == null)
                {
                    if (other)
                        RrLog.Info("Your last {0} recording was made with another character, so this recording starts a new profile.", Place);
                    return;
                }
                string prevTxt = Path.ChangeExtension(prev, ".txt");
                string profile = ProfileOf(prev);
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
                // saved after that recording stopped (an editor, Make profile): it's yours too. The recorder's own last
                // write comes seconds after the .txt footer.
                if (File.Exists(prevTxt) && File.GetLastWriteTimeUtc(profile) > File.GetLastWriteTimeUtc(prevTxt).AddMinutes(2))
                {
                    RrLog.Info("Your last {0} profile was changed after that recording, so this recording starts a new one.", Place);
                    return;
                }
                var events = new List<RecEvent>();
                LoadChain(prev, events, 0);
                if (events.Count == 0)
                    return;
                // every quest you picked up there was finished, dropped or left the log: that run is complete, start a new
                // profile. A weekly stays open for days, so it never keeps a run going by itself (its tag in any client
                // language), and neither do the game's own bookkeeping quests (companion conversations, [DNT] ...).
                var open = new HashSet<ulong>();
                bool ended = false;
                foreach (RecEvent e in events)
                {
                    if (e.Kind == EvKind.QuestAdded && !e.QuestHidden && !ReplayBuilder.IsWeekly(e.QuestName) && !ReplayBuilder.IsSystemQuest(e))
                    {
                        open.Add(e.QuestId);
                    }
                    else if (e.Kind == EvKind.QuestCompleted || e.Kind == EvKind.QuestAbandoned || e.Kind == EvKind.QuestRemoved)
                    {
                        open.Remove(e.QuestId);
                        ended = true;
                    }
                }
                if (open.Count == 0 && ended)
                {
                    RrLog.Info("Your last {0} recording was finished, so this recording starts a new profile.", Place);
                    return;
                }
                // the missions it left open must still be in your quest log (done since, dropped, or not this character's)
                if (open.Count > 0 && first.QuestsAvailable && first.Quests != null)
                {
                    bool inLog = false;
                    foreach (QSnap q in first.Quests)
                    {
                        if (open.Contains(q.QuestId))
                        {
                            inLog = true;
                            break;
                        }
                    }
                    if (!inLog)
                    {
                        RrLog.Info("The missions your last {0} recording left open aren't in your quest log, so this recording starts a new profile.", Place);
                        return;
                    }
                }
                // carrying on means going on where you were: where that run made quest progress or stopped
                // (a start on the fleet or your ship never carries on a planet run that never went there)
                string here = area != null ? area.Label ?? string.Empty : string.Empty;
                ulong hereId = area != null ? area.Id : 0;
                if (here.Length > 0 || hereId != 0)
                {
                    var areas = new HashSet<string>(StringComparer.Ordinal);
                    var areaIds = new HashSet<ulong>();
                    for (int i = 0; i < events.Count; i++)
                    {
                        RecEvent e = events[i];
                        bool progress = !e.QuestHidden &&
                                        (e.Kind == EvKind.QuestAdded || e.Kind == EvKind.QuestTask || e.Kind == EvKind.QuestStep || e.Kind == EvKind.QuestCompleted);
                        if (!progress && i < events.Count - 1)
                            continue;
                        if (!string.IsNullOrEmpty(e.AreaName))
                            areas.Add(e.AreaName);
                        if (e.AreaId != 0)
                            areaIds.Add(e.AreaId);
                    }
                    if ((areas.Count > 0 || areaIds.Count > 0) && !areas.Contains(here) && !areaIds.Contains(hereId))
                    {
                        RrLog.Info("Your last {0} recording was done somewhere else, so this recording starts a new profile.", Place);
                        return;
                    }
                }
                double newest = double.MinValue;
                foreach (RecEvent e in events)
                {
                    e.Elapsed = e.Time != default(DateTime) ? (e.Time - Started).TotalSeconds : e.Elapsed - 1000000; // before this recording: negative, still in order
                    if (e.Elapsed > newest)
                        newest = e.Elapsed;
                }
                if (newest >= 0)
                {
                    // the clock went back since then (summer time ended): those events still come before this recording
                    foreach (RecEvent e in events)
                        e.Elapsed -= newest + 1.0;
                }
                string text = File.ReadAllText(profile);
                _joinEvents = events;
                _profilePath = profile;
                Remember(text); // a change saved from now on is seen, and kept
                LiveProfile = profile;
                Continues = Path.GetFileNameWithoutExtension(prev);
                RrLog.Info("Carrying on your {0} profile from earlier: {1}", Place, Path.GetFileName(profile));
                GameToast.Show("Quest Recorder: carrying on your " + Place + " profile from earlier.", 8000);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Couldn't carry on the last recording: {0}", ex.Message);
                _joinEvents = null;
                _profilePath = null;
                _lastWritten = null;
                Continues = string.Empty;
            }
        }

        /// <summary>Loads a recording and, first, the ones it carried on (the "Continues" line in its .txt).</summary>
        internal static void LoadChain(string csv, List<RecEvent> into, int depth)
        {
            if (depth > 10 || !File.Exists(csv))
                return;
            string earlier = ContinuesFrom(csv);
            if (earlier != null)
                LoadChain(earlier, into, depth + 1);
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
