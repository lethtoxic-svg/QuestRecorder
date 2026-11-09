// Quest Recorder - made by LethToxic.
using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;
using Reborn.Utilities.Settings;

namespace QuestRecorder
{
    /// <summary>Which tag the draft uses for "walk to this spot".</summary>
    public enum StandTag
    {
        /// <summary>Like our own profiles (walk there without stopping to fight).</summary>
        NoCombatMoveTo = 0,

        /// <summary>Like Kick's profiles.</summary>
        MoveTo = 1,
    }

    /// <summary>What the draft does with the automatic every-25-metres points.</summary>
    public enum BreadcrumbsInDraft
    {
        /// <summary>Leave them out of the draft (they stay in the .txt and .csv).</summary>
        Off = 0,

        /// <summary>Write them as commented-out stand lines you can switch on.</summary>
        AsComments = 1,

        /// <summary>Write them as real stand lines.</summary>
        AsStands = 2,
    }


    /// <summary>How much of BuddyCron's object cache the recorder refreshes before each look while the bot is stopped.</summary>
    public enum CacheRefresh
    {
        /// <summary>Only the world objects (enough for the recorder, lightest, raises no game events).</summary>
        Light = 0,

        /// <summary>Every manager, as BuddyCron documents for one-shot reads (heavier; also runs BuddyCron's game-event checks).</summary>
        Full = 1,
    }

    /// <summary>Quest Recorder settings: one file for the whole BuddyCron install, Settings\QuestRecorder\Settings.json.</summary>
    public class RrSettings : JsonSettings
    {
        private static readonly object InstanceSync = new object();
        private static RrSettings _instance;

        public static RrSettings Instance
        {
            get
            {
                RrSettings s = _instance;
                if (s != null)
                    return s;
                lock (InstanceSync)
                {
                    if (_instance == null)
                        _instance = Create();
                    return _instance;
                }
            }
        }

        private static RrSettings Create()
        {
            string dir = Path.Combine(JsonSettings.SettingsPath, "QuestRecorder");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // JsonSettings keeps the defaults if the file cannot be written
            }
            return new RrSettings(Path.Combine(dir, "Settings.json"));
        }

        public RrSettings(string path)
            : base(path)
        {
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        /// <summary>A folder as typed or pasted: no spaces around it and no quotes (Explorer's "Copy as path" adds them).</summary>
        internal static string CleanPath(string value)
        {
            return (value ?? string.Empty).Trim().Trim('"').Trim();
        }

        private bool _seenWelcome;

        [Browsable(false)]
        public bool SeenWelcome
        {
            get { return _seenWelcome; }
            set { SetField(ref _seenWelcome, value, nameof(SeenWelcome)); }
        }

        // ------------------------------------------------------------------ 1. Recording

        private int _walkPoint = 0;

        [Category("1. Recording")]
        [DisplayName("Walk point every (m)")]
        [Description("0 (default) = no walk points: the profile only has what matters (clicks, talks, fights, doors, lifts, shuttles) and BuddyCron walks between them by itself. Set 4-60 to also save your footsteps every that many metres, if the bot ever takes a bad way round.")]
        [DefaultValue(0)]
        public int WalkPointMeters
        {
            get { return _walkPoint; }
            set { SetField(ref _walkPoint, value <= 0 ? 0 : Clamp(value, 4, 60), nameof(WalkPointMeters)); }
        }

        private bool _stuckPopup = true;

        [Category("1. Recording")]
        [DisplayName("Tell me when the bot is stuck")]
        [Description("While a profile runs: if the bot sits on one step for 5 minutes and nothing in your quest log moves, a pop-up offers to open the fix panel at that step. On by default.")]
        [DefaultValue(true)]
        public bool StuckPopup
        {
            get { return _stuckPopup; }
            set { SetField(ref _stuckPopup, value, nameof(StuckPopup)); }
        }

        private bool _joinRecordings = true;

        [Category("1. Recording")]
        [DisplayName("Carry on my last recording")]
        [Description("Logged off halfway through a planet? The next recording of the same place (within a day) carries on in the same profile instead of making a new one. On by default.")]
        [DefaultValue(true)]
        public bool JoinRecordings
        {
            get { return _joinRecordings; }
            set { SetField(ref _joinRecordings, value, nameof(JoinRecordings)); }
        }


        private bool _autoRecord;

        // no longer used: recording starts only when you press Start recording (kept so old settings files still load)
        [Browsable(false)]
        [Category("1. Recording")]
        [DisplayName("Auto-record")]
        [Description("Not used any more: press Start recording in the Quest Recorder window.")]
        [DefaultValue(false)]
        public bool AutoRecord
        {
            get { return _autoRecord; }
            set { SetField(ref _autoRecord, value, nameof(AutoRecord)); }
        }

        private bool _recordBot;

        [Category("1. Recording")]
        [DisplayName("Keep recording while the bot plays")]
        [Description("Off (normal): the recorder pauses while the bot runs, so it only records you. On: it records the bot too - for building a profile by letting the bot (or a helper driving it) play the content. Off by default.")]
        [DefaultValue(false)]
        public bool RecordBot
        {
            get { return _recordBot; }
            set { SetField(ref _recordBot, value, nameof(RecordBot)); }
        }

        private string _creditName = string.Empty;

        [Category("1. Recording")]
        [DisplayName("Your name for credits")]
        [Description("Put your name here and every profile you record says \"Recorded by <you>\". Leave it empty if you don't want that.")]
        [DefaultValue("")]
        public string CreditName
        {
            get { return _creditName ?? string.Empty; }
            set { SetField(ref _creditName, (value ?? string.Empty).Trim(), nameof(CreditName)); }
        }
        private string _profileFolder = string.Empty;

        [Category("1. Recording")]
        [DisplayName("Profile folder")]
        [Description("Where your profiles get saved. Leave empty and they go in Profiles\\Recorded\\<planet>.")]
        [DefaultValue("")]
        public string ProfileFolder
        {
            get { return CleanPath(_profileFolder); }
            set { SetField(ref _profileFolder, CleanPath(value), nameof(ProfileFolder)); }
        }


        private Keys _undoHotkey = Keys.Control | Keys.Shift | Keys.Z;

        [Category("2. Markers")]
        [DisplayName("Undo last action hotkey")]
        [Description("Press this right after a misclick and the recorder forgets that click. Default Ctrl+Shift+Z.")]
        [DefaultValue(Keys.Control | Keys.Shift | Keys.Z)]
        public Keys UndoHotkey
        {
            get { return _undoHotkey; }
            set { SetField(ref _undoHotkey, value, nameof(UndoHotkey)); }
        }

        private Keys _pauseHotkey = Keys.Control | Keys.Shift | Keys.P;

        [Category("2. Markers")]
        [DisplayName("Pause here hotkey")]
        [Description("While recording: the profile pauses at this spot so you do that part by hand. Default Ctrl+Shift+P. None = off.")]
        [DefaultValue(Keys.Control | Keys.Shift | Keys.P)]
        public Keys PauseHotkey
        {
            get { return _pauseHotkey; }
            set { SetField(ref _pauseHotkey, value, nameof(PauseHotkey)); }
        }

        private Keys _waitHotkey = Keys.Control | Keys.Shift | Keys.W;

        [Category("2. Markers")]
        [DisplayName("Wait here hotkey")]
        [Description("While recording: the bot waits 10 seconds at this spot (a respawn, a door). Default Ctrl+Shift+W. None = off.")]
        [DefaultValue(Keys.Control | Keys.Shift | Keys.W)]
        public Keys WaitHotkey
        {
            get { return _waitHotkey; }
            set { SetField(ref _waitHotkey, value, nameof(WaitHotkey)); }
        }

        private int _sampleMs;

        [Category("1. Recording")]
        [DisplayName("Look every (ms)")]
        [Description("How often the recorder checks the game, in ms (300-1000). Default 400 is fine for most people.")]
        [DefaultValue(400)]
        public int SampleMs
        {
            get { return _sampleMs; }
            set { SetField(ref _sampleMs, Clamp(value, 300, 1000), nameof(SampleMs)); }
        }

        private int _breadcrumbMeters;

        [Category("1. Recording")]
        [DisplayName("Breadcrumb every (m)")]
        [Description("How often it saves your footsteps, in metres. The profile walks along these. Default 5.")]
        [DefaultValue(5)]
        public int BreadcrumbMeters
        {
            get { return _breadcrumbMeters; }
            set { SetField(ref _breadcrumbMeters, value <= 0 ? 0 : Clamp(value, 5, 200), nameof(BreadcrumbMeters)); }
        }

        private int _jumpMeters;

        [Category("1. Recording")]
        [DisplayName("Jump distance (m)")]
        [Description("Anything that moves you further than this in one go counts as a jump (elevators, shuttles, quick travel). Default 30.")]
        [DefaultValue(30)]
        public int JumpMeters
        {
            get { return _jumpMeters; }
            set { SetField(ref _jumpMeters, Clamp(value, 10, 500), nameof(JumpMeters)); }
        }

        private int _partnerRadius;

        [Category("1. Recording")]
        [DisplayName("Talk partner search (m)")]
        [Description("If you start a conversation without targeting anyone, the nearest quest NPC within this range is used. Default 8.")]
        [DefaultValue(8)]
        public int PartnerRadius
        {
            get { return _partnerRadius; }
            set { SetField(ref _partnerRadius, Clamp(value, 3, 20), nameof(PartnerRadius)); }
        }

        private bool _readQuestText;

        [Category("1. Recording")]
        [DisplayName("Read objective text")]
        [Description("Reads the objective text (like \"Speak with Duke Kendoh\") so the tracker is easier to read. Turns itself off if the game doesn't give it.")]
        [DefaultValue(true)]
        public bool ReadQuestText
        {
            get { return _readQuestText; }
            set { SetField(ref _readQuestText, value, nameof(ReadQuestText)); }
        }

        private CacheRefresh _refresh;

        [Category("1. Recording")]
        [DisplayName("Cache refresh")]
        [Description("Leave on Light. Only try Full if names or kills go missing.")]
        [DefaultValue(CacheRefresh.Light)]
        public CacheRefresh Refresh
        {
            get { return _refresh; }
            set { SetField(ref _refresh, value, nameof(Refresh)); }
        }

        // ------------------------------------------------------------------ 2. Markers

        private Keys _markerHotkey;

        [Category("2. Markers")]
        [DisplayName("Add marker hotkey")]
        [Description("Drops a marker where you're standing (uses the note box text as its name). Only while recording. Default Ctrl+Shift+M.")]
        [DefaultValue(Keys.Control | Keys.Shift | Keys.M)]
        public Keys MarkerHotkey
        {
            get { return _markerHotkey; }
            set { SetField(ref _markerHotkey, value, nameof(MarkerHotkey)); }
        }

        // ------------------------------------------------------------------ 3. Output

        private bool _keepDraft;

        [Category("3. Output")]
        [DisplayName("Keep notes draft")]
        [Description("Also saves a notes file (-draft.xml) next to the recording, for people who write profiles by hand. It's not the profile to run. Off by default.")]
        [DefaultValue(false)]
        public bool KeepDraft
        {
            get { return _keepDraft; }
            set { SetField(ref _keepDraft, value, nameof(KeepDraft)); }
        }
        private string _outputFolder = string.Empty;

        [Category("3. Output")]
        [DisplayName("Recordings folder")]
        [Description("Where the raw recordings go. Leave empty for the Recordings folder next to BuddyCron's Logs.")]
        [DefaultValue("")]
        public string OutputFolder
        {
            get { return CleanPath(_outputFolder); }
            set { SetField(ref _outputFolder, CleanPath(value), nameof(OutputFolder)); }
        }

        private StandTag _standTag;

        [Category("3. Output")]
        [DisplayName("Stand tag in draft")]
        [Description("NoCombatMoveTo (default, like our profiles) or MoveTo (like Kick's). If BuddyCron does not know " +
                     "NoCombatMoveTo, the draft uses MoveTo by itself.")]
        [DefaultValue(StandTag.NoCombatMoveTo)]
        public StandTag DraftStandTag
        {
            get { return _standTag; }
            set { SetField(ref _standTag, value, nameof(DraftStandTag)); }
        }

        private BreadcrumbsInDraft _draftBreadcrumbs;

        [Category("3. Output")]
        [DisplayName("Breadcrumbs in draft")]
        [Description("Off, AsComments (default: commented-out stand lines, at most 8 per stretch) or AsStands.")]
        [DefaultValue(BreadcrumbsInDraft.AsComments)]
        public BreadcrumbsInDraft DraftBreadcrumbs
        {
            get { return _draftBreadcrumbs; }
            set { SetField(ref _draftBreadcrumbs, value, nameof(DraftBreadcrumbs)); }
        }

        private int _draftWaitTime;

        [Category("3. Output")]
        [DisplayName("WaitTime in draft (s)")]
        [Description("WaitTime written on TalkTo, Interact and UseObject lines, in seconds (as in our profiles). 1 to 30. Default 5.")]
        [DefaultValue(5)]
        public int DraftWaitTime
        {
            get { return _draftWaitTime; }
            set { SetField(ref _draftWaitTime, Clamp(value, 1, 30), nameof(DraftWaitTime)); }
        }

        // ------------------------------------------------------------------ 4. Log

        private bool _logEvents;

        [Category("4. Log")]
        [DisplayName("Show events in log")]
        [Description("Shows quests, talks, clicks, kills and markers in the BuddyCron log while you record.")]
        [DefaultValue(true)]
        public bool LogEvents
        {
            get { return _logEvents; }
            set { SetField(ref _logEvents, value, nameof(LogEvents)); }
        }

        private bool _verbose;

        [Category("4. Log")]
        [DisplayName("Verbose logging")]
        [Description("Extra log lines for when something goes wrong.")]
        [DefaultValue(false)]
        public bool VerboseLogging
        {
            get { return _verbose; }
            set { SetField(ref _verbose, value, nameof(VerboseLogging)); }
        }
    }
}
