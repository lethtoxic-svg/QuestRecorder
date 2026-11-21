// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BuddyCron;
using BuddyCron.Behaviors;
using BuddyCron.Managers;
using BuddyCron.Objects;
using BuddyCron.Profiles;

namespace QuestRecorder
{
    internal enum LookState
    {
        Starting,
        NotAttached,
        Bot,
        Loading,
        NoCharacter,
        InWorld,
        Failed,
        TooSlow,
    }

    /// <summary>What the settings window shows. A new object is published after every look (never changed after).</summary>
    internal sealed class RecorderStatus
    {
        public bool Running;
        public bool Stopping;
        public LookState State;
        public string StateText = string.Empty;
        public double Seconds;
        public int Events;
        public int QuestEvents;
        public int Kills;
        public int Markers;
        public int Warnings;
        public string LastEvent = string.Empty;
        public string Place = string.Empty;
        public string Folder = string.Empty;
        public string BaseName = string.Empty;
        public string Result = string.Empty;
    }

    internal enum ThrottleChange
    {
        None,
        Slower,
        Faster,
        Paused,
        Recovered,
    }

    /// <summary>Reacts to slow looks (each one holds the game while it runs).</summary>
    internal sealed class LookThrottle
    {
        public const double SlowMs = 50;
        public const double VerySlowMs = 200;
        public const double FastMs = 25;
        private const int SlowInARow = 3;
        private const int VerySlowInARow = 4;
        private const int FastInARow = 2;
        private const int MaxLevel = 4;
        private const int MaxIntervalMs = 4000;
        private const long FirstPauseMs = 20000;
        private const long MaxPauseMs = 300000;

        private int _slow;
        private int _verySlow;
        private int _fast;
        private int _looks;
        private bool _probe;

        public int Level;
        public int Pauses;
        public long PausedUntil;
        public double LastMs;
        public double WorstMs;

        public bool IsPaused(long now)
        {
            return now < PausedUntil;
        }

        /// <summary>Whether this look refreshes BuddyCron's cache (every look, except every second one when very throttled).</summary>
        public bool PulseThisLook()
        {
            _looks++;
            if (_probe)
            {
                _probe = false;
                return true;
            }
            return Level < 3 || (_looks & 1) == 0;
        }

        /// <summary>The time between looks: the setting, doubled per level, at most 4 s.</summary>
        public int Interval(int baseMs)
        {
            if (Level == 0)
                return baseMs;
            long v = (long)baseMs << Level;
            return (int)Math.Min(v, Math.Max(baseMs, MaxIntervalMs));
        }

        /// <param name="pulsed">Whether the look refreshed BuddyCron's cache (the part that can be slow). A fast look
        /// without the refresh says nothing about the refresh, so it neither speeds up nor breaks a slow streak.</param>
        public ThrottleChange Observe(double ms, bool pulsed, long now)
        {
            if (!pulsed && ms <= SlowMs)
                return ThrottleChange.None;
            LastMs = ms;
            if (ms > SlowMs)
            {
                _fast = 0;
                _slow++;
                if (ms > WorstMs || _slow == 1)
                    WorstMs = ms;
                _verySlow = ms > VerySlowMs ? _verySlow + 1 : 0;
                // right after a pause one very slow look is enough to pause again (longer)
                if (_verySlow >= VerySlowInARow || (Pauses > 0 && ms > VerySlowMs))
                {
                    Pauses++;
                    long pause = Math.Min(MaxPauseMs, FirstPauseMs << Math.Min(4, Pauses - 1));
                    PausedUntil = now + pause;
                    _verySlow = 0;
                    _slow = 0;
                    Level = MaxLevel;
                    _probe = true;
                    return ThrottleChange.Paused;
                }
                if (_slow >= SlowInARow && Level < MaxLevel)
                {
                    Level++;
                    _slow = 0;
                    return ThrottleChange.Slower;
                }
                return ThrottleChange.None;
            }
            _slow = 0;
            _verySlow = 0;
            bool recovered = false;
            if (Pauses > 0)
            {
                Pauses = 0;
                recovered = true;
            }
            if (ms <= FastMs)
            {
                _fast++;
                if (_fast >= FastInARow && Level > 0)
                {
                    Level--;
                    _fast = 0;
                    return recovered ? ThrottleChange.Recovered : ThrottleChange.Faster;
                }
            }
            else
            {
                _fast = 0;
            }
            return recovered ? ThrottleChange.Recovered : ThrottleChange.None;
        }

        public long PauseLeftMs(long now)
        {
            return Math.Max(0, PausedUntil - now);
        }
    }

    /// <summary>How long each look held the frame lock (0.25 ms buckets).</summary>
    internal sealed class LockStats
    {
        private const double Bucket = 0.25;
        private readonly int[] _hist = new int[401];

        public int Count;
        public double Max;
        public int Over10;

        public void Add(double ms)
        {
            Count++;
            if (ms > Max)
                Max = ms;
            if (ms > 10)
                Over10++;
            int b = (int)(ms / Bucket);
            if (b < 0)
                b = 0;
            if (b >= _hist.Length)
                b = _hist.Length - 1;
            _hist[b]++;
        }

        public double Percentile(double p)
        {
            if (Count == 0)
                return 0;
            int target = Math.Max(1, (int)Math.Ceiling(Count * p));
            int acc = 0;
            for (int i = 0; i < _hist.Length; i++)
            {
                acc += _hist[i];
                if (acc >= target)
                    return Math.Min(Max, (i + 1) * Bucket);
            }
            return Max;
        }
    }

    /// <summary>The recorder: ONE background thread that runs only while a recording is on.</summary>
    internal sealed class Recorder
    {
        private static readonly object Sync = new object();
        private static Recorder _current;
        private static RecorderStatus _lastStatus;

        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ConcurrentQueue<MarkerRequest> _markers = new ConcurrentQueue<MarkerRequest>();
        private volatile bool _stop;
        private volatile bool _quick;
        private volatile string _stopWhy = "you clicked Stop";
        private volatile RecorderStatus _status;
        private readonly LookThrottle _throttle = new LookThrottle();

        private sealed class MarkerRequest
        {
            public string Note = string.Empty;
            public string Source = string.Empty;
        }

        private sealed class Look
        {
            public LookState State;
            public Snap Snap;
            public bool Locked;
            public double LockMs;
        }

        private Recorder()
        {
            _status = new RecorderStatus { Running = true, State = LookState.Starting, StateText = "starting" };
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "Quest Recorder";
        }

        // ------------------------------------------------------------------ control (any thread)

        public static Recorder Current
        {
            get { return Volatile.Read(ref _current); }
        }

        public static bool IsRecording
        {
            get { return Current != null; }
        }

        /// <summary>The running recording's status, else the result of the last one.</summary>
        public static RecorderStatus Status
        {
            get
            {
                Recorder r = Current;
                if (r != null)
                    return r._status;
                return Volatile.Read(ref _lastStatus) ?? new RecorderStatus { StateText = "not recording" };
            }
        }

        public static bool Start(out string message)
        {
            lock (Sync)
            {
                Recorder cur = _current;
                if (cur != null && cur._thread.IsAlive)
                {
                    message = cur._stop
                        ? "The last recording is still saving its files; click Start recording again in a moment."
                        : "Already recording.";
                    return false;
                }
                var r = new Recorder();
                _current = r;
                try
                {
                    r._thread.Start();
                }
                catch (Exception ex)
                {
                    _current = null;
                    message = "The recorder could not start: " + ex.Message;
                    return false;
                }
                message = "Recording started.";
                return true;
            }
        }

        /// <summary>Asks the recording to stop (it finishes its files on its own thread). Does not wait.</summary>
        public static bool RequestStop(string why, bool quick)
        {
            Recorder r = Current;
            if (r == null)
                return false;
            r._stopWhy = why ?? "stopped";
            if (quick)
                r._quick = true;
            r._stop = true;
            try
            {
                r._wake.Set();
            }
            catch
            {
                // already disposed
            }
            return true;
        }

        /// <summary>Stops the recording and waits up to <paramref name="timeoutMs"/> for its files to be finished.</summary>
        public static bool StopAndWait(int timeoutMs, string why, bool quick)
        {
            Recorder r = Current;
            if (r == null)
                return true;
            RequestStop(why, quick);
            if (r._thread == Thread.CurrentThread)
                return false;
            try
            {
                bool ended = r._thread.Join(timeoutMs);
                if (!ended)
                    RrLog.Warn("The recorder did not finish within {0} s; it finishes its files on its own.", timeoutMs / 1000.0);
                return ended;
            }
            catch (Exception ex)
            {
                RrLog.Debug("Waiting for the recorder failed: {0}", ex.Message);
                return false;
            }
        }

        /// <summary>Queues a marker at your position on the next look (the recorder looks right away).</summary>
        public static bool AddMarker(string note, string source)
        {
            Recorder r = Current;
            if (r == null || r._stop)
                return false;
            r._markers.Enqueue(new MarkerRequest { Note = Fmt.OneLine(note), Source = source ?? string.Empty });
            try
            {
                r._wake.Set();
            }
            catch
            {
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ the thread

        private void Run()
        {
            var reader = new GameReader();
            var stats = new LockStats();
            RecordingSession session = null;
            Detector det = null;
            Snap last = null;
            bool gapLoading = false;
            bool gapBot = false;
            int fails = 0;
            int totalFails = 0;
            int notInWorldLooks = 0;
            long backoffUntil = 0;
            bool textWarned = false;
            bool pauseSkipped = false;
            string why = null;
            LookState state = LookState.Starting;

            RrLog.Info("Recording - waiting for the game.");
            try
            {
                while (!_stop)
                {
                    long loopStart = Environment.TickCount64;
                    RrSettings cfg = RrSettings.Instance;
                    RrLog.Verbose = cfg.VerboseLogging;
                    int sleep = _throttle.Interval(cfg.SampleMs);

                    bool paused = _throttle.IsPaused(loopStart);
                    // A marker you add during a pause still gets your position: one look without the (slow) refresh.
                    bool markerLook = paused && !_markers.IsEmpty && last != null && !gapBot;
                    if (paused && !markerLook)
                    {
                        state = LookState.TooSlow;
                        sleep = (int)Math.Min(1000L, Math.Max(50L, _throttle.PauseLeftMs(loopStart)));
                        pauseSkipped = true;
                    }
                    else if (loopStart >= backoffUntil)
                    {
                        if (pauseSkipped && !markerLook)
                        {
                            // nothing was recorded during the pause: the combat log goes on from now, not from then
                            pauseSkipped = false;
                            reader.SkipCombatLog();
                        }
                        Look look = null;
                        bool pulse = false;
                        try
                        {
                            // After a loading screen (or at the start) the object cache must be refreshed.
                            pulse = !markerLook && (_throttle.PulseThisLook() || gapLoading || gapBot || last == null);
                            look = TakeLook(reader, gapBot ? null : last, gapLoading, cfg, pulse);
                            fails = 0;
                        }
                        catch (Exception ex)
                        {
                            fails++;
                            totalFails++;
                            long backoff = Math.Min(30000L, 1000L << Math.Min(5, fails - 1));
                            backoffUntil = loopStart + backoff;
                            state = LookState.Failed;
                            if (RrLog.Every("look-failed", TimeSpan.FromSeconds(60)))
                                RrLog.Warn("Reading the game failed ({0}: {1}); trying again in {2} s.", ex.GetType().Name, ex.Message, backoff / 1000);
                            RrLog.Debug("Look failed: {0}", ex);
                            if (det != null && last != null && fails == 3)
                                SafeWarning(det, last, "reading the game failed 3 times in a row: " + ex.Message);
                        }

                        if (look != null)
                        {
                            if (look.Locked)
                            {
                                stats.Add(look.LockMs);
                                if (!markerLook)
                                {
                                    ThrottleChange tc = _throttle.Observe(look.LockMs, pulse, Environment.TickCount64);
                                    OnThrottle(tc, cfg, det, last);
                                }
                            }
                            state = look.State;
                            switch (look.State)
                            {
                                case LookState.InWorld:
                                {
                                    Snap snap = look.Snap;
                                    if (session != null && det != null && last != null && snap.PlayerId != last.PlayerId)
                                    {
                                        // Another character logged in: the first one's recording ends here with its own profile,
                                        // and this one gets its own (one profile with both characters' quests fits neither).
                                        try
                                        {
                                            string done = FinishSession(session, det, last, reader, stats, totalFails, notInWorldLooks,
                                                "another character logged in");
                                            RrLog.Good("Another character logged in. {0} This character gets a recording of its own.", done);
                                        }
                                        catch (Exception ex)
                                        {
                                            RrLog.Exception("Finishing the first character's recording", ex);
                                        }
                                        session = null;
                                        det = null;
                                        last = null;
                                        stats = new LockStats();
                                        totalFails = 0;
                                        notInWorldLooks = 0;
                                        textWarned = false;
                                    }
                                    long gapMs = last != null ? snap.Tick - last.Tick : 0;
                                    List<string> problems = reader.TakeProblems();
                                    if (session == null)
                                    {
                                        try
                                        {
                                            session = RecordingSession.Create(snap, reader.Area(snap.AreaId), cfg);
                                        }
                                        catch (Exception ex)
                                        {
                                            RrLog.Error("Could not create the recording files in {0}: {1}", SafeFolder(), ex.Message);
                                            why = "the recording files could not be created";
                                            _stop = true;
                                            break;
                                        }
                                        det = new Detector(session, reader);
                                        RrLog.Good("Recording to {0} (.txt, .csv). The profile goes to Profiles\\Recorded.", System.IO.Path.Combine(session.Folder, session.BaseName));
                                    }
                                    try
                                    {
                                        if (last == null)
                                            det.Begin(snap);
                                        else
                                            det.Process(last, snap, gapLoading, gapMs, gapBot);
                                        MarkerRequest m;
                                        while (_markers.TryDequeue(out m))
                                            det.Marker(snap, m.Note, m.Source);
                                    }
                                    catch (Exception ex)
                                    {
                                        // A bug in the comparison must not end the recording: skip this look, back off a little.
                                        fails++;
                                        totalFails++;
                                        backoffUntil = Environment.TickCount64 + Math.Min(30000L, 1000L << Math.Min(5, fails - 1));
                                        if (RrLog.Every("process-failed", TimeSpan.FromSeconds(60)))
                                            RrLog.Exception("Comparing two looks", ex);
                                    }
                                    if (problems != null)
                                    {
                                        foreach (string p in problems)
                                        {
                                            RrLog.Debug("Read problem: {0}", p);
                                            int colon = p.IndexOf(':');
                                            string key = "problem:" + (colon > 0 ? p.Substring(0, colon) : p);
                                            if (RrLog.Every(key, TimeSpan.FromMinutes(5)))
                                                RrLog.Warn("Couldn't read part of the game ({0}), carrying on without it.", p);
                                            SafeWarning(det, snap, "read problem: " + p);
                                        }
                                    }
                                    if (!textWarned && reader.TextDisabled)
                                    {
                                        textWarned = true;
                                        SafeWarning(det, snap, "objective texts are not read for the rest of this recording (the game did not give them)");
                                    }
                                    last = snap;
                                    gapLoading = false;
                                    gapBot = false;
                                    break;
                                }
                                case LookState.Bot:
                                    if (!gapBot)
                                    {
                                        gapBot = true;
                                        reader.ResetCharacter(true);
                                        if (det != null && last != null)
                                            det.Paused(last, "the bot started; the recorder waits until it stops");
                                        RrLog.Info("Bot is running - recording paused until it stops.");
                                    }
                                    sleep = 1000;
                                    break;
                                case LookState.Loading:
                                case LookState.NoCharacter:
                                    // each of these looks also refreshes BuddyCron's cache: once a second is plenty
                                    gapLoading = true;
                                    notInWorldLooks++;
                                    sleep = Math.Max(1000, sleep);
                                    break;
                                default:
                                    notInWorldLooks++;
                                    sleep = 2000;
                                    break;
                            }
                        }
                    }

                    if (_throttle.IsPaused(Environment.TickCount64))
                        state = LookState.TooSlow;
                    if (session != null)
                    {
                        session.Flush();
                        session.MaybeWriteDraft(Environment.TickCount64);
                    }
                    Publish(session, state, false, string.Empty);

                    long spent = Environment.TickCount64 - loopStart;
                    int wait = (int)Math.Max(20L, sleep - spent);
                    // A reply wheel is up: look again quickly, so the reply you pick is seen before the conversation
                    // moves past it (one look every 0.4-0.8 s missed most picks).
                    if (last != null && last.InConvo && last.ReplyNode >= 0 && !_throttle.IsPaused(Environment.TickCount64))
                        wait = Math.Min(wait, 100);
                    if (!_stop)
                        _wake.WaitOne(wait);
                }
            }
            catch (Exception ex)
            {
                RrLog.Exception("The recorder", ex);
                why = "stopped by an error: " + ex.Message;
            }
            finally
            {
                Finish(session, det, last, reader, stats, totalFails, notInWorldLooks, why ?? _stopWhy);
            }
        }

        /// <summary>Logs a throttle change and notes it in the recording (outside the frame lock).</summary>
        private void OnThrottle(ThrottleChange tc, RrSettings cfg, Detector det, Snap last)
        {
            LookThrottle t = _throttle;
            switch (tc)
            {
                case ThrottleChange.Slower:
                    if (RrLog.Every("throttle-slower", TimeSpan.FromMinutes(2)))
                        RrLog.Warn("Reading the game is slow ({0} ms), so I'm checking every {1} s to stop the game stuttering.",
                            Fmt.F1(t.LastMs), Fmt.F1(t.Interval(cfg.SampleMs) / 1000.0));
                    SafeWarning(det, last, Fmt.Format("looks were slow ({0} ms); now one look every {1} s", Fmt.F1(t.LastMs),
                        Fmt.F1(t.Interval(cfg.SampleMs) / 1000.0)));
                    break;
                case ThrottleChange.Faster:
                    RrLog.Debug("Looks are fast again; now one look every {0} s.", Fmt.F1(t.Interval(cfg.SampleMs) / 1000.0));
                    break;
                case ThrottleChange.Paused:
                {
                    long left = t.PauseLeftMs(Environment.TickCount64);
                    RrLog.Warn("Each look held the game for about {0} ms (normally a few ms). Quest Recorder pauses for {1} s so the game " +
                               "is not frozen, then tries again. The recording goes on afterwards.", Fmt.F1(t.LastMs), left / 1000);
                    if (det != null && last != null)
                    {
                        try
                        {
                            det.Paused(last, Fmt.Format("each look held the game for about {0} ms; paused for {1} s (nothing is recorded meanwhile)",
                                Fmt.F1(t.LastMs), left / 1000));
                        }
                        catch
                        {
                            // a note must never stop the recording
                        }
                    }
                    break;
                }
                case ThrottleChange.Recovered:
                    RrLog.Info("Back to normal ({0} ms), recording again.", Fmt.F1(t.LastMs));
                    SafeWarning(det, last, Fmt.Format("recording again after a pause for slow looks (look {0} ms) at {1}", Fmt.F1(t.LastMs),
                        Fmt.Clock(DateTime.Now)));
                    break;
            }
        }

        private static void SafeWarning(Detector det, Snap s, string text)
        {
            try
            {
                if (det != null && s != null)
                    det.Warning(s, text);
            }
            catch
            {
                // a warning must never stop the recording
            }
        }

        private static string SafeFolder()
        {
            try
            {
                return RecordingSession.ResolveFolder();
            }
            catch
            {
                return "the recordings folder";
            }
        }

        /// <summary>One look: the only place that takes the frame lock.</summary>
        internal static bool BotOwnsGame()
        {
            try
            {
                if (!TreeRoot.IsRunning)
                    return false;
                // "Keep recording while the bot plays": record the bot (or a helper driving it) building a profile
                if (RrSettings.Instance.RecordBot)
                    return false;
                var bot = BotManager.Current;
                return bot == null || bot.IsAutonomous;
            }
            catch
            {
                return TreeRoot.IsRunning;
            }
        }

        private static Look TakeLook(GameReader reader, Snap prev, bool afterLoading, RrSettings cfg, bool pulse)
        {
            var look = new Look();
            if (!Core.IsInitialized || Core.Memory == null)
            {
                look.State = LookState.NotAttached;
                return look;
            }
            if (BotOwnsGame())
            {
                look.State = LookState.Bot; // the bot owns the game: do not even take the lock
                return look;
            }
            // A bot that does not move by itself.
            bool assistRunning = TreeRoot.IsRunning;
            if (assistRunning)
                pulse = false;
            PulseFlags refresh = cfg.Refresh == CacheRefresh.Full ? PulseFlags.Managers : PulseFlags.ObjectManager;
            var sw = new Stopwatch();
            try
            {
                using (Core.Memory.AcquireFrame())
                {
                    sw.Start();
                    look.Locked = true;
                    if (BotOwnsGame())
                    {
                        look.State = LookState.Bot;
                        return look;
                    }
                    // Refresh first: IsLoading reads true while BuddyCron's client singletons are unresolved.
                    if (pulse)
                        Pulsator.Pulse(refresh);
                    if (CommonBehaviors.IsLoading)
                    {
                        look.State = LookState.Loading;
                        return look;
                    }
                    HeroLocalPlayer me = Core.Me;
                    if (me == null || !me.IsValid)
                    {
                        look.State = LookState.NoCharacter;
                        return look;
                    }
                    look.Snap = reader.Read(me, prev, afterLoading, cfg);
                    look.State = LookState.InWorld;
                }
            }
            finally
            {
                sw.Stop();
                look.LockMs = sw.Elapsed.TotalMilliseconds;
            }
            return look;
        }

        private void Publish(RecordingSession session, LookState state, bool stopping, string result)
        {
            var s = new RecorderStatus();
            s.Running = !stopping || string.IsNullOrEmpty(result);
            s.Stopping = stopping || _stop;
            s.State = state;
            s.StateText = StateText(state);
            LookThrottle t = _throttle;
            if (state == LookState.TooSlow)
                s.StateText = Fmt.Format("paused: each look held the game for about {0} ms; trying again in {1} s", Fmt.F1(t.LastMs),
                    (t.PauseLeftMs(Environment.TickCount64) + 999) / 1000);
            else if (t.Level > 0 && !stopping)
                s.StateText += Fmt.Format(" (slowed down: looks took up to {0} ms, so one look every {1} s)", Fmt.F1(t.WorstMs),
                    Fmt.F1(t.Interval(SafeSampleMs()) / 1000.0));
            if (session != null)
            {
                s.Seconds = Math.Max(0, (DateTime.Now - session.Started).TotalSeconds);
                s.Events = session.Count;
                s.QuestEvents = session.QuestEvents;
                s.Kills = session.Kills;
                s.Markers = session.Markers;
                s.Warnings = session.Warnings;
                s.LastEvent = session.LastHeadline;
                s.Place = session.Place;
                s.Folder = session.Folder;
                s.BaseName = session.BaseName;
            }
            s.Result = result ?? string.Empty;
            _status = s;
        }

        private static int SafeSampleMs()
        {
            try
            {
                return RrSettings.Instance.SampleMs;
            }
            catch
            {
                return 400;
            }
        }

        private static string StateText(LookState s)
        {
            switch (s)
            {
                case LookState.Starting:
                    return "starting";
                case LookState.NotAttached:
                    return "waiting: BuddyCron is not attached to the game";
                case LookState.Bot:
                    return "paused: the bot is running";
                case LookState.Loading:
                    return "waiting: loading screen";
                case LookState.NoCharacter:
                    return "waiting: no character in the world";
                case LookState.InWorld:
                    return "recording";
                case LookState.TooSlow:
                    return "paused: looks were too slow";
                default:
                    return "reading the game failed; trying again";
            }
        }

        /// <summary>The stop event, the footer and the profile of one recording; the text saying where it went.</summary>
        private string FinishSession(RecordingSession session, Detector det, Snap last, GameReader reader, LockStats stats, int totalFails,
            int notInWorldLooks, string why)
        {
            try
            {
                det.End(last, why);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Writing the stop event failed: {0}", ex.Message);
            }
            var lines = new List<string>();
            lines.Add(Fmt.Format("Looks       {0} in the world, {1} waiting (loading screen, character select or not attached), {2} failed",
                stats.Count, notInWorldLooks, totalFails));
            lines.Add(Fmt.Format("Frame lock  median {0} ms, 95% {1} ms, max {2} ms, {3} looks over 10 ms (the game waits only while one look is copied)",
                Fmt.F1(stats.Percentile(0.5)), Fmt.F1(stats.Percentile(0.95)), Fmt.F1(stats.Max), stats.Over10));
            lines.Add("Objectives  " + (reader.TextDisabled ? "objective texts were switched off during this recording (the game did not give them)" : "objective texts read when the game gave them"));
            session.Finish(why, lines, !_quick);
            return Fmt.Format("Saved {0} events to {1} (.txt, .csv).", session.Count,
                System.IO.Path.Combine(session.Folder, session.BaseName));
        }

        private void Finish(RecordingSession session, Detector det, Snap last, GameReader reader, LockStats stats, int totalFails,
            int notInWorldLooks, string why)
        {
            string result;
            try
            {
                Publish(session, LookState.InWorld, true, string.Empty);
                if (session != null && det != null)
                {
                    MarkerRequest m;
                    while (_markers.TryDequeue(out m))
                    {
                        if (last != null)
                            det.Marker(last, (m.Note.Length > 0 ? m.Note + " " : string.Empty) + "(position of the last look)", m.Source);
                    }
                    result = FinishSession(session, det, last, reader, stats, totalFails, notInWorldLooks, why);
                    RrLog.Good("Recording stopped ({0}). {1}", why, result);
                }
                else
                {
                    result = "Nothing was recorded: your character was never in the world while recording.";
                    RrLog.Info("Recording stopped ({0}). {1}", why, result);
                }
            }
            catch (Exception ex)
            {
                result = "Finishing the recording failed: " + ex.Message;
                RrLog.Exception("Finishing the recording", ex);
            }

            try
            {
                Publish(session, LookState.InWorld, true, result);
                RecorderStatus final = _status;
                final.Running = false;
                final.Stopping = false;
                final.StateText = "not recording";
                lock (Sync)
                {
                    _lastStatus = final;
                    if (ReferenceEquals(_current, this))
                        _current = null;
                }
            }
            catch
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_current, this))
                        _current = null;
                }
            }
            try
            {
                _wake.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
