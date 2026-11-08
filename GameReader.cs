// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using BuddyCron;
using BuddyCron.Managers;
using BuddyCron.Objects;
using BuddyCron.Objects.Oracle;
using BuddyCron.Objects.Static;
using BuddyCron.Scripting;
using BuddyCron.ServiceClient;

namespace QuestRecorder
{
    /// <summary>Copies one snapshot of the game into plain C# objects.</summary>
    internal sealed class GameReader
    {
        // At most this many optional engine-call items (quest titles / step lists for quests already known) per look.
        private const int OptionalEngineItemsPerLook = 3;
        // At most this many engine-call items of any kind per look (new quests, step lists, "was it completed?").
        private const int EngineItemsPerLook = 10;
        // Engine-call work stops once a look has spent this long reading (the rest waits for the next looks).
        private const double ReadBudgetMs = 8.0;
        // Nearby-object searches are skipped once a look has spent this long reading.
        private const double NearbyStopMs = 30.0; // a normal look already takes ~14 ms: at 15 most searches were skipped
        // A quest that left the log waits at most this many looks for its "was it completed?" check.
        private const int MaxRemovedWaits = 15;
        private const int MaxTaxiChoices = 40;
        // Nearby-object searches stop after this long or this many objects.
        private const long NearbyBudgetMs = 3;
        private const int NearbyMaxObjects = 4000;
        private const int MaxTracked = 60;
        private const int MaxNames = 5000;

        private readonly Dictionary<ulong, string> _names = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, QuestInfo> _quests = new Dictionary<ulong, QuestInfo>();
        private readonly Dictionary<string, StepInfo> _steps = new Dictionary<string, StepInfo>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, AreaInfo> _areas = new Dictionary<ulong, AreaInfo>();
        private readonly Dictionary<ulong, string> _abilityNames = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, TrackedMob> _tracked = new Dictionary<ulong, TrackedMob>();
        private Dictionary<ulong, string> _questItemAbilities = new Dictionary<ulong, string>();
        private Dictionary<string, string> _questItemByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unmatchedLogged = new HashSet<string>();
        private string _questItemList = "none";
        private readonly Dictionary<string, float> _itemCooldown = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _itemStack = new Dictionary<string, int>(StringComparer.Ordinal);
        // quest items kept in the bags (a city pass, a plot item you right-click): looked up every few seconds
        private List<HeroItem> _bagQuestItems = new List<HeroItem>();
        private long _bagQuestItemsTick = NeverRead;
        private const long NeverRead = -1000000000L;
        private long _questItemsTick = NeverRead;

        private bool _haveCmd;
        private ulong _lastCmdTime;
        private ObjRef _lastClick;
        private long _lastClickTick;

        private bool _taxiPending;
        private long _taxiPendingUntil;
        private Vec3 _taxiLandPos;
        private DateTime _taxiLandTime;

        private WonkaMemo _wonka;

        private bool _textDisabled;
        private int _textFailures;
        private int _optionalItems;
        private int _engineItems;
        private readonly Stopwatch _readSw = new Stopwatch();
        private readonly Dictionary<ulong, int> _removedWaits = new Dictionary<ulong, int>();

        // Taxi map: the destinations it offered (spec id -> predecessor), read while it was open.
        private Dictionary<ulong, ulong> _taxiNet;
        private int _taxiNetReads;
        private readonly Dictionary<ulong, string> _taxiNames = new Dictionary<ulong, string>();

        private readonly HashSet<string> _seenCommands = new HashSet<string>(StringComparer.Ordinal);

        private readonly List<string> _problems = new List<string>();

        private sealed class TrackedMob
        {
            public long LastListedTick;
            public bool Checked;
            public bool WasDead;
        }

        /// <summary>Read problems collected during the last look (logged by the recorder after the lock).</summary>
        public List<string> TakeProblems()
        {
            if (_problems.Count == 0)
                return null;
            var copy = new List<string>(_problems);
            _problems.Clear();
            return copy;
        }

        /// <summary>True once the objective-text lookup failed too often and was switched off for the session.</summary>
        public bool TextDisabled
        {
            get { return _textDisabled; }
        }

        // ------------------------------------------------------------------ caches (recorder thread only)

        public QuestInfo Quest(ulong questId)
        {
            QuestInfo qi;
            if (!_quests.TryGetValue(questId, out qi))
            {
                qi = new QuestInfo { QuestId = questId, Name = string.Empty };
                _quests[questId] = qi;
            }
            return qi;
        }

        public StepInfo Step(ulong questId, ulong branch, ulong step)
        {
            string key = Key(questId, branch, step);
            StepInfo si;
            if (!_steps.TryGetValue(key, out si))
            {
                si = new StepInfo { QuestId = questId, Branch = branch, Step = step };
                _steps[key] = si;
            }
            return si;
        }

        public AreaInfo Area(ulong areaId)
        {
            AreaInfo a;
            if (_areas.TryGetValue(areaId, out a))
                return a;
            return new AreaInfo { Id = areaId };
        }

        private static string Key(ulong q, ulong b, ulong s)
        {
            return q.ToString("X16") + ":" + b.ToString() + ":" + s.ToString();
        }

        /// <summary>Forget everything tied to the character (another character logged in).</summary>
        public void ResetCharacter()
        {
            _tracked.Clear();
            _lastClick = null;
            _taxiPending = false;
            _taxiNet = null;
            _wonka = null;
            _names.Clear();
            _removedWaits.Clear();
            _questItemAbilities = new Dictionary<ulong, string>();
            _questItemByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _unmatchedLogged.Clear();
            _questItemsTick = NeverRead; // not long.MinValue: now - MinValue overflows and the list was never read
            // another character: its items aren't "used" or "gone" compared with the last one's
            _itemStack.Clear();
            _itemCooldown.Clear();
            _questNames = new HashSet<string>(StringComparer.Ordinal);
            _bagNames = new HashSet<string>(StringComparer.Ordinal);
            _bagPlot = new HashSet<string>(StringComparer.Ordinal);
            _bagPlotBefore = new HashSet<string>(StringComparer.Ordinal);
            _bagGone = null;
            _goneQueue.Clear();
            _wasQuestItem.Clear();
            _castItemName = null;
            _baseAbilities = null;
            _abilityName.Clear();
            _abilityFails = 0;
            _abilityOff = false;
            _abilityCd.Clear();
            _abilityUsedAt.Clear();
            _equipped = null;
            _logItemName = null;
            _instName = null;
            _questEmptyReads = 0;
            _bagEmptyReads = 0;
        }

        // item uses: the item whose cast was recorded (its end isn't a second use), items that left the bags waiting to be
        // reported (one per look), and every name seen among the mission items
        private string _castItemName;
        private long _castItemSeen;
        private readonly List<string> _goneQueue = new List<string>();
        private readonly HashSet<string> _wasQuestItem = new HashSet<string>(StringComparer.Ordinal);
        private int _questEmptyReads;
        private int _bagEmptyReads;

        // ------------------------------------------------------------------ per-look budget

        /// <summary>True when one more engine-call item fits in this look.</summary>
        private bool CanSpend(bool mustHave)
        {
            if (_readSw.Elapsed.TotalMilliseconds > ReadBudgetMs)
                return false;
            if (_engineItems >= EngineItemsPerLook)
                return false;
            return mustHave || _optionalItems < OptionalEngineItemsPerLook;
        }

        private void Spend(bool mustHave, int items)
        {
            _engineItems += items;
            if (!mustHave)
                _optionalItems++;
        }

        // ------------------------------------------------------------------ the look

        /// <summary>One snapshot.</summary>
        public Snap Read(HeroLocalPlayer me, Snap prev, bool afterLoading, RrSettings cfg)
        {
            _optionalItems = 0;
            _engineItems = 0;
            _readSw.Restart();
            var s = new Snap();
            s.Time = DateTime.Now;
            s.Tick = Environment.TickCount64;
            s.PlayerId = me.NodeId;
            if (prev != null && prev.PlayerId != s.PlayerId)
            {
                // Another character: a fresh baseline.
                ResetCharacter();
                prev = null;
            }

            try
            {
                s.PlayerName = NameOf(me);
                s.Pos = V(me.Location);
                s.Heading = me.Heading;
                s.AreaId = me.AreaId;
                s.GalaxyAreaId = me.GalaxyMapAreaId;
                s.CompanionId = me.CompanionGuid;
                s.Level = me.Level;
                s.InCombat = me.InCombat;
                s.IsDead = me.IsDead;
                s.IsMounted = me.IsMounted;
                s.IsInTaxi = me.IsInTaxi;
                s.InConvo = me.IsInConversation;
                s.IsCasting = me.IsCasting;
                s.BoardOpen = me.MissionBoardOpen;
                chrInteractionType it = me.CurrentInteractionType;
                s.Interaction = (int)it;
                s.InteractionName = it.ToString();
                s.CastSpec = s.IsCasting ? me.CastingAbilitySpecId : 0UL;
                s.TargetId = me.TargetId;
            }
            catch (Exception ex)
            {
                // Without the basics the look is useless: let the recorder count it as a failed look.
                throw new InvalidOperationException("reading your character failed: " + ex.Message, ex);
            }

            if (prev == null || prev.AreaId != s.AreaId)
            {
                // Node ids are only meaningful inside one area: start the name and mob caches over.
                _names.Clear();
                _tracked.Clear();
                _propOwners.Clear();
                Part("area", () => EnsureArea(s.AreaId));
            }

            Part("quests", () => ReadQuests(me, prev, cfg, s));
            if (!s.QuestsAvailable && prev != null && s.Quests.Count == 0)
                s.Quests = prev.Quests; // keep the last good list so nothing looks "removed"
            Part("clicks", () => ReadClick(s));
            Part("conversation", () => ReadConversation(me, prev, cfg, s));
            Part("reply wheel", () => ReadReplies(prev, s));
            Part("interaction", () => ReadInteraction(prev, s));
            Part("combat", () => ReadCombat(me, prev, s));
            Part("casts", () => ReadCast(me, prev, s));
            Part("items", () => ReadInstantItems(me, s));
            Part("task scan", () => ScanTaskObjects(s));
            Part("combat log", () => ReadCombatLog(s));
            Part("quest abilities", () => ReadQuestAbilities(s));
            Part("equipped", () => ReadEquipped(me, s));
            Part("taxi map", () => ReadTaxiMap(s));
            Part("movement", () => ReadMovement(me, prev, afterLoading, s));
            Part("mission board", () => ReadBoard(me, prev, s));
            Part("target", () => ReadTarget(prev, s));
            return s;
        }

        /// <summary>You picked a new target (a mob, an NPC or an object): what it is and where.</summary>
        private void ReadTarget(Snap prev, Snap s)
        {
            if (s.TargetId == 0 || s.TargetId == s.PlayerId || s.TargetId == s.CompanionId)
                return;
            if (prev != null && prev.TargetId == s.TargetId)
                return;
            ObjRef t = Describe(HeroObjectManager.GetByNodeId(s.TargetId), "you targeted it");
            if (t != null && (t.Kind == ObjKind.Npc || t.Kind == ObjKind.Placeable))
                s.NewTarget = t;
        }

        private void Part(string what, Action read)
        {
            try
            {
                read();
            }
            catch (Exception ex)
            {
                if (_problems.Count < 8)
                    _problems.Add(what + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ quests

        private void ReadQuests(HeroLocalPlayer me, Snap prev, RrSettings cfg, Snap s)
        {
            HeroQuestComponent qc = me.Quests;
            if (qc == null || !qc.IsAvailable)
                return; // e.g. while dead: keep the last good list (Read carries it over)

            var prevById = new Dictionary<ulong, QSnap>();
            if (prev != null)
            {
                foreach (QSnap q in prev.Quests)
                    prevById[q.QuestId] = q;
            }

            var list = new List<QSnap>();
            var seen = new HashSet<ulong>();
            foreach (QuestState st in qc.ActiveQuestStates)
            {
                if (st == null)
                    continue;
                var q = new QSnap();
                q.QuestId = st.QuestId;
                if (q.QuestId == 0 || !seen.Add(q.QuestId))
                    continue;
                q.Branch = st.BranchId;
                q.Step = st.StepId;
                q.Holocom = st.HolocomIsWaiting;
                q.Hidden = st.IsHidden;
                IReadOnlyList<ulong> tc = st.TaskCount;
                if (tc != null && tc.Count > 0)
                {
                    q.Counts = new ulong[tc.Count];
                    for (int i = 0; i < tc.Count; i++)
                        q.Counts[i] = tc[i];
                }
                list.Add(q);

                QSnap old;
                bool known = prevById.TryGetValue(q.QuestId, out old);
                bool stepChanged = known && (old.Branch != q.Branch || old.Step != q.Step);
                // A quest that appeared since the last look (not the quest log found at the first look).
                bool isNew = prev != null && !known;

                // Title: always for a quest that just appeared, otherwise a few per look.
                QuestInfo qi = Quest(q.QuestId);
                if (!qi.Resolved && qi.Attempts < 3 && CanSpend(isNew))
                {
                    Spend(isNew, 1);
                    qi.Attempts++;
                    ResolveQuest(st, qi);
                }

                // Task list of the current step: right after a change when it fits, otherwise a few per look.
                StepInfo si = Step(q.QuestId, q.Branch, q.Step);
                bool mustStep = stepChanged || isNew;
                if (!si.Resolved && si.Attempts < 3 && CanSpend(mustStep))
                {
                    Spend(mustStep, 2);
                    si.Attempts++;
                    ResolveStep(st, si, cfg.ReadQuestText);
                }

                // Ask BuddyCron's own condition functions, so the timeline shows whether the numbers really are the.
                if (stepChanged)
                {
                    var c = new StepCheck();
                    if (CanSpend(true))
                    {
                        Spend(true, 1);
                        try
                        {
                            c.OldDone = ConditionParser.IsBranchStepComplete(q.QuestId, (uint)old.Branch, (uint)old.Step);
                            c.NewDone = ConditionParser.IsBranchStepComplete(q.QuestId, (uint)q.Branch, (uint)q.Step);
                            c.Ran = true;
                        }
                        catch
                        {
                            c.Ran = false;
                        }
                    }
                    if (s.StepChecks == null)
                        s.StepChecks = new Dictionary<ulong, StepCheck>();
                    s.StepChecks[q.QuestId] = c;
                }
                else if (known && old.Counts != null)
                {
                    int n = Math.Min(old.Counts.Length, q.Counts.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (q.Counts[i] <= old.Counts[i])
                            continue;
                        if (!CanSpend(true))
                            break;
                        Spend(true, 1);
                        try
                        {
                            bool gt = ConditionParser.IsTaskValueGreaterThan(q.QuestId, (uint)q.Step, (uint)i, (uint)old.Counts[i]);
                            if (s.TaskChecks == null)
                                s.TaskChecks = new Dictionary<string, bool>(StringComparer.Ordinal);
                            s.TaskChecks[q.QuestId.ToString("X16") + ":" + i.ToString()] = gt;
                        }
                        catch
                        {
                            // no live check for this one
                        }
                    }
                }
            }

            if (list.Count == 0 && prev != null && prev.Quests.Count >= 3)
                return; // a whole quest log never empties in one look: not loaded yet (Read keeps the last list)

            // The scanner only looks for what your missions still need right now: the mob / object types the game counts
            // for the current step of each mission in your log, and only tasks that aren't full yet. A finished mission or
            // a full counter stops being scanned for.
            _taskSpecs.Clear();
            foreach (QSnap q in list)
            {
                StepInfo cur = Step(q.QuestId, q.Branch, q.Step);
                if (!cur.Resolved)
                    continue;
                foreach (TaskInfo ti in cur.Tasks)
                {
                    if (ti.Max > 0 && ti.Index >= 0 && ti.Index < q.Counts.Length && q.Counts[ti.Index] >= ti.Max)
                        continue; // this counter is full
                    foreach (ulong spec in ti.Targets)
                        _taskSpecs.Add(spec);
                }
            }

            foreach (PendingQuestRewards pr in qc.PendingQuestRewards)
            {
                if (pr != null)
                    s.PendingRewards.Add(pr.QuestId);
            }

            // Quests that left the log: ask once whether they were completed (1-2 engine calls each).
            if (prev != null)
            {
                foreach (QSnap old in prev.Quests)
                {
                    if (seen.Contains(old.QuestId))
                        continue;
                    int waited;
                    _removedWaits.TryGetValue(old.QuestId, out waited);
                    var ri = new RemovedInfo();
                    if (!CanSpend(true))
                    {
                        if (waited < MaxRemovedWaits)
                        {
                            _removedWaits[old.QuestId] = waited + 1;
                            list.Add(old);
                            continue;
                        }
                        ri.Known = false; // waited long enough: report it without the answer
                    }
                    else
                    {
                        Spend(true, 2);
                        try
                        {
                            ri.Completed = qc.IsQuestCompleted(old.QuestId);
                            if (!ri.Completed)
                                ri.Abandoned = qc.IsQuestAbandoned(old.QuestId);
                            ri.Known = true;
                        }
                        catch
                        {
                            ri.Known = false;
                        }
                    }
                    _removedWaits.Remove(old.QuestId);
                    if (s.Removed == null)
                        s.Removed = new Dictionary<ulong, RemovedInfo>();
                    s.Removed[old.QuestId] = ri;
                }
            }
            if (_removedWaits.Count > 0)
            {
                List<ulong> back = null;
                foreach (ulong id in _removedWaits.Keys)
                {
                    if (seen.Contains(id))
                        (back ?? (back = new List<ulong>())).Add(id);
                }
                if (back != null)
                {
                    foreach (ulong id in back)
                        _removedWaits.Remove(id);
                }
            }

            s.Quests = list;
            s.QuestsAvailable = true;
        }

        private static void ResolveQuest(QuestState st, QuestInfo qi)
        {
            QuestDefinition def = st.Definition;
            if (def != null)
            {
                qi.Name = Fmt.OneLine(def.Name);
                qi.Fqn = Fmt.OneLine(def.InternalName);
            }
            if (string.IsNullOrEmpty(qi.Name))
                qi.Name = Fmt.OneLine(st.DisplayName);
            qi.Resolved = def != null && !string.IsNullOrEmpty(qi.Name) && !qi.Name.StartsWith("#", StringComparison.Ordinal);
        }

        private void ResolveStep(QuestState st, StepInfo si, bool readText)
        {
            QuestStepDefinition sd = st.StepDefinition;
            if (sd == null || sd.StepId != si.Step || st.BranchId != si.Branch)
                return;
            IReadOnlyList<QuestTaskDefinition> tasks = sd.Tasks;
            QuestDefinition def = readText && !_textDisabled ? st.Definition : null;
            si.Tasks.Clear();
            if (tasks != null)
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    QuestTaskDefinition td = tasks[i];
                    if (td == null)
                        continue;
                    var ti = new TaskInfo { Index = i, TaskId = td.TaskId, Max = td.TaskCountMax };
                    if (def != null && !_textDisabled)
                        ti.Text = TaskText(def, td);
                    ti.Targets = TaskTargets(td);
                    foreach (ulong spec in ti.Targets)
                        _taskSpecs.Add(spec); // the task scanner looks for these
                    si.Tasks.Add(ti);
                }
            }
            si.Resolved = true;
        }

        /// <summary>
        /// What the game itself counts for this task: the mob / object type ids (qstTaskObjects, checked live on
        /// CZ-198: Workforce Reduction lists its 6 Czerka types). Empty for talk / walk tasks.
        /// </summary>
        private static List<ulong> TaskTargets(QuestTaskDefinition task)
        {
            var list = new List<ulong>();
            try
            {
                GomContainer c;
                if (task.TryGetField<GomContainer>(GomFieldId.qstTaskObjects, out c) && c.IsValid && c.Count > 0)
                    foreach (ulong k in c.Keys<ulong>())
                        if (k != 0 && !list.Contains(k))
                            list.Add(k);
                if (task.TryGetField<GomContainer>(GomFieldId.qstTaskInteractionObjects, out c) && c.IsValid && c.Count > 0)
                    foreach (ulong k in c.Keys<ulong>())
                        if (k != 0 && !list.Contains(k))
                            list.Add(k);
            }
            catch
            {
                // no list for this task: the recorder simply can't double-check it
            }
            return list;
        }

        /// <summary>Objective text through the quest's text-retriever map (the PugTools path; unverified on a live client).</summary>
        private string TaskText(QuestDefinition def, QuestTaskDefinition task)
        {
            try
            {
                ulong key;
                if (!task.TryGetField<ulong>(GomFieldId.qstTaskStringid, out key) || key == 0)
                    return string.Empty;
                GomContainer map;
                if (!def.TryGetField<GomContainer>(GomFieldId.locTextRetrieverMap, out map) || !map.IsValid)
                    return string.Empty;
                HeroClass retr;
                if (!map.TryGetValue<HeroClass>(key, out retr) || retr == null)
                    return string.Empty;
                string cached;
                if (retr.TryGetField<string>(GomFieldId.strLocalizedTextRetrieverCachedText, out cached) && !string.IsNullOrEmpty(cached))
                    return Fmt.OneLine(cached);
                string bucket;
                long sid;
                if (!retr.TryGetField<string>(GomFieldId.strLocalizedTextRetrieverBucket, out bucket) || string.IsNullOrEmpty(bucket))
                    return string.Empty;
                if (!retr.TryGetField<long>(GomFieldId.strLocalizedTextRetrieverStringID, out sid) || sid == 0)
                    return string.Empty;
                HeroStrOracle str = ExtraNodeManager.StrOracle;
                return str == null ? string.Empty : Fmt.OneLine(str.Localize(bucket, (ulong)sid));
            }
            catch (Exception ex)
            {
                _textFailures++;
                if (_textFailures >= 3 && !_textDisabled)
                {
                    _textDisabled = true;
                    _problems.Add("objective text: switched off for this session after 3 failures (" + ex.GetType().Name + ": " + ex.Message + ")");
                }
                return string.Empty;
            }
        }

        // ------------------------------------------------------------------ clicks and conversations

        private void ReadClick(Snap s)
        {
            HeroCtlOracle ctl = ExtraNodeManager.CtlOracle;
            if (ctl == null)
                return;
            ulong t = ctl.LastCommandStartTimeRaw;
            if (!_haveCmd)
            {
                _haveCmd = true; // the first value is only the baseline
                _lastCmdTime = t;
                return;
            }
            if (t == _lastCmdTime)
                return;
            _lastCmdTime = t;
            ulong id = ctl.LastCommandNodeUnderMouseId;
            string cmd;
            try
            {
                cmd = Fmt.OneLine(ctl.LastCommand);
            }
            catch
            {
                cmd = string.Empty;
            }
            bool use = IsUseCommand(cmd);
            // the interact key works on what you have targeted, not what's under the mouse
            if (use && (id == 0 || id == s.PlayerId) && s.TargetId != 0 && s.TargetId != s.PlayerId && s.TargetId != s.CompanionId)
                id = s.TargetId;
            if (id == 0 || id == s.PlayerId)
                return;
            // every command over an object is recorded - except ability keys and camera/UI keys in a fight
            if (!use && (cmd.IndexOf("Quickslot", StringComparison.OrdinalIgnoreCase) >= 0 || (s.InCombat && !_seenCommands.Add(cmd))))
                return;
            HeroObject o = HeroObjectManager.GetByNodeId(id);
            if (o == null)
                o = ctl.LastCommandNodeUnderMouse;
            ObjRef r = Describe(o, use ? "you clicked it" : "under the mouse");
            if (r == null || r.Kind == ObjKind.Player)
                return;
            if (!use)
            {
                // Not a right-click "use": never a cause in the draft, only noted once so the name can be checked.
                s.OtherCommand = cmd;
                s.OtherCommandTarget = r;
                return;
            }
            s.Click = r;
            s.ClickCommand = cmd;
            if (r.Kind == ObjKind.Placeable && r.HasPos && r.Name.Length > 0)
                s.SameKind = ScanSameKind(r);
            _lastClick = r;
            _lastClickTick = s.Tick;
        }

        // the object types your quests' tasks count (from the game's own task data), and the objects already noted
        // ------------------------------------------------------------------ the game's combat log

        private string _logPath;
        private long _logPos = -1;
        private long _logScanTick = NeverRead;
        private long _logReadTick = NeverRead;
        private string _logRest = string.Empty;
        private string _logItemName;
        private long _logItemSeen;
        private string _instName;
        private long _instSeen;

        /// <summary>
        /// The game's own report of every ability you activate: its combat log (Documents\Star Wars - The Old Republic\
        /// CombatLogs, when combat logging is on in the game's settings). A mission item used with no cast bar, no
        /// cooldown and nothing leaving your bags (Section X: the Serum Injector on the Hyperbeast) shows up there as
        /// "AbilityActivate", with the item and who it was used on. Only your own uses of your mission items; a use the
        /// recorder already saw (cast, cooldown, item gone) isn't counted twice.
        /// </summary>
        private void ReadCombatLog(Snap s)
        {
            if (s.Tick - _logReadTick < 500)
                return;
            _logReadTick = s.Tick;
            if (_logPath == null || s.Tick - _logScanTick > 30000)
            {
                _logScanTick = s.Tick;
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Star Wars - The Old Republic", "CombatLogs");
                if (!System.IO.Directory.Exists(dir))
                    return; // combat logging was never on: nothing to read
                System.IO.FileInfo newest = null;
                foreach (System.IO.FileInfo f in new System.IO.DirectoryInfo(dir).GetFiles("combat_*.txt"))
                    if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                        newest = f;
                if (newest == null)
                    return;
                if (newest.FullName != _logPath)
                {
                    _logPath = newest.FullName;
                    _logPos = newest.Length; // only what happens from now on
                    _logRest = string.Empty;
                    return;
                }
            }
            string text;
            using (var fs = new System.IO.FileStream(_logPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                if (fs.Length < _logPos)
                    _logPos = 0;
                if (fs.Length == _logPos)
                    return;
                long n = Math.Min(fs.Length - _logPos, 256 * 1024);
                fs.Seek(_logPos, System.IO.SeekOrigin.Begin);
                var buf = new byte[n];
                int read = fs.Read(buf, 0, (int)n);
                _logPos += read;
                text = _logRest + System.Text.Encoding.Latin1.GetString(buf, 0, read);
            }
            int last = text.LastIndexOf('\n');
            _logRest = last < 0 ? text : text.Substring(last + 1);
            if (last < 0)
                return;
            string me = s.PlayerName ?? string.Empty;
            string meUtf = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes(me)); // (the log may be UTF-8: read as Latin-1, "Øø" looks like this)
            string[] lines = text.Substring(0, last).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                // [time] [source] [target] [ability {id}] [Event {id}: what {id}]
                List<string> f = LogFields(line);
                if (f.Count < 5 || !IsMe(f[1], me, meUtf))
                    continue;
                // who you have targeted (the game logs it as TargetSet): who an item with no target of its own went on
                if (f[4].IndexOf("TargetSet", StringComparison.Ordinal) >= 0)
                {
                    _logTargetName = f[2].Length > 0 && f[2] != "=" && !f[2].StartsWith("@", StringComparison.Ordinal) ? LogName(f[2]) : null;
                    continue;
                }
                if (f[4].IndexOf("AbilityActivate", StringComparison.Ordinal) < 0)
                    continue;
                string abl = LogName(f[3]);
                string item = LogItem(abl);
                if (item == null)
                    continue; // not one of your mission items
                if (s.QuestItemName == item || s.InstantItemName == item || IsSameCast(item, s) || (_instName == item && s.Tick - _instSeen < 4000))
                    continue; // already recorded from its cast / cooldown / leaving your bags
                // the activation line says [=]: who it went on is on the effect line right after (Radiation Sensor ->
                // "Tagged" on the Imperial Soldier), or else your target (Serum Injector -> the Hyperbeast)
                string stamp = f[0];
                string onName = null;
                for (int j = i + 1; j < lines.Length && j <= i + 40; j++)
                {
                    List<string> g = LogFields(lines[j].TrimEnd('\r'));
                    if (g.Count < 5 || g[0] != stamp)
                        break;
                    if (IsMe(g[1], me, meUtf) && LogName(g[3]) == abl && g[2].Length > 0 && g[2] != "=" && !g[2].StartsWith("@", StringComparison.Ordinal))
                    {
                        onName = LogName(g[2]);
                        break;
                    }
                }
                if (onName == null)
                    onName = _logTargetName;
                s.LogItemName = item;
                s.LogItemTarget = onName != null ? LogTarget(onName, s) : null;
                _logItemName = item;
                _logItemSeen = s.Tick;
                return; // one per look
            }
        }

        private string _logTargetName;
        private readonly Dictionary<string, string> _castNameItem = new Dictionary<string, string>(StringComparer.Ordinal); // cast bar text -> mission item, learned from its casts

        /// <summary>The combat log source is you (not a companion, not another player).</summary>
        private static bool IsMe(string src, string me, string meUtf)
        {
            // "@Name#123|(x,y,z,h)|(hp/max)" is you; "@Name#123/Theron Shan {..}|..." is your companion (the "/" before the
            // first "|" - the health part has one too)
            int bar = src.IndexOf('|');
            string head = bar >= 0 ? src.Substring(0, bar) : src;
            if (!head.StartsWith("@", StringComparison.Ordinal) || head.IndexOf('/') >= 0)
                return false;
            string n = LogName(head.Substring(1));
            return me.Length == 0 || n == me || n == meUtf;
        }

        /// <summary>
        /// The mission item a logged ability belongs to: its own name or its ability's ("Radiation Sensor"), or the cast
        /// bar text "Using Serum..." for the one mission item whose name starts with it ("Serum Injector").
        /// </summary>
        private string LogItem(string abl)
        {
            if (abl.Length == 0)
                return null;
            string item;
            if (_questItemByName.TryGetValue(abl, out item) || _castNameItem.TryGetValue(abl, out item))
                return item;
            if (!abl.StartsWith("Using ", StringComparison.Ordinal))
                return null;
            string bare = abl.Substring(6).TrimEnd('.', ' ');
            if (bare.Length < 3)
                return null;
            string found = null;
            foreach (string v in _questItemByName.Values)
                if (v == bare || v.StartsWith(bare + " ", StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null && found != v)
                        return null; // two items fit: don't guess
                    found = v;
                }
            return found;
        }

        /// <summary>The [..] fields of a combat log line.</summary>
        private static List<string> LogFields(string line)
        {
            var list = new List<string>();
            int i = 0;
            while (i < line.Length && list.Count < 6)
            {
                int a = line.IndexOf('[', i);
                if (a < 0)
                    break;
                int b = line.IndexOf(']', a + 1);
                if (b < 0)
                    break;
                list.Add(line.Substring(a + 1, b - a - 1).Trim());
                i = b + 1;
            }
            return list;
        }

        /// <summary>"Hyperbeast {1234}:5678|(x,y,z,h)|(hp)" or "Name#123|..." -> "Hyperbeast" / "Name".</summary>
        private static string LogName(string field)
        {
            int cut = field.Length;
            foreach (char c in new[] { '{', '#', '|' })
            {
                int k = field.IndexOf(c);
                if (k >= 0 && k < cut)
                    cut = k;
            }
            return field.Substring(0, cut).Trim();
        }

        /// <summary>Who the combat log says you used it on: that NPC (the nearest of that name), or null for yourself.</summary>
        private ObjRef LogTarget(string field, Snap s)
        {
            if (field.Length == 0 || field == "=" || field.StartsWith("@", StringComparison.Ordinal))
                return null; // yourself, nothing, or a player
            string name = LogName(field);
            if (name.Length == 0)
                return null;
            try
            {
                HeroCharacter best = null;
                float bestD = 4f; // within ~40 m
                foreach (HeroCharacter c in HeroObjectManager.GetObjects<HeroCharacter>())
                {
                    if (c == null || !c.IsValid || c is HeroPlayer)
                        continue;
                    string n = NameOf(c);
                    if (n != name)
                        continue;
                    float d = V(c.Location).DistanceTo(s.Pos);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = c;
                    }
                }
                return best != null ? Describe(best, "who the game's combat log says you used it on") : null;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ quest abilities, equipped items

        private HashSet<string> _baseAbilities; // your abilities when the recording began (your class kit)
        private readonly Dictionary<string, float> _abilityCd = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _abilityUsedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _abilityTick = NeverRead;
        private readonly Dictionary<HeroAbility, string> _abilityName = new Dictionary<HeroAbility, string>();
        private int _abilityFails;
        private bool _abilityOff;

        /// <summary>
        /// Abilities a quest gives you for a while (the special / temporary bar: Macrobinoculars' scan, the Seeker Droid's
        /// dig): anything you can cast now that you couldn't when the recording began, and that isn't a mission item.
        /// A use is its cast starting or its cooldown starting (instant ones). The profile builder keeps a use only if a
        /// mission moved right after, so a new class ability from a level-up never becomes a step.
        /// </summary>
        private void ReadQuestAbilities(Snap s)
        {
            if (_abilityOff || s.Tick - _abilityTick < 1000)
                return;
            _abilityTick = s.Tick;
            var names = new List<string>();
            bool trouble = false;
            try
            {
                foreach (HeroAbility a in AbilityManager.KnownAbilities)
                {
                    if (a == null)
                        continue;
                    // each ability's name is asked once (the wrappers are stable): asking the game for every name on every
                    // look made it throw ("process exception") over and over
                    string n;
                    if (!_abilityName.TryGetValue(a, out n))
                    {
                        try
                        {
                            n = Fmt.OneLine(a.Name);
                        }
                        catch
                        {
                            n = null; // some abilities can't give their name (GetAbilityNameStringID not declared): skip them for good
                        }
                        if (_abilityName.Count < 2000)
                            _abilityName[a] = n;
                    }
                    if (!string.IsNullOrEmpty(n))
                        names.Add(n);
                }
            }
            catch
            {
                trouble = true;
            }
            // the game keeps refusing: stop asking for the rest of this recording (everything else carries on)
            _abilityFails = trouble ? _abilityFails + 1 : 0;
            if (_abilityFails >= 5)
            {
                _abilityOff = true;
                RrLog.Info("The game won't say which special-bar abilities you have - quest abilities aren't recorded this time (everything else is).");
                return;
            }
            if (trouble)
                return;
            if (names.Count == 0)
                return;
            if (_baseAbilities == null)
            {
                _baseAbilities = new HashSet<string>(names, StringComparer.Ordinal);
                return;
            }
            foreach (string n in names)
            {
                if (_baseAbilities.Contains(n) || _questItemByName.ContainsKey(n))
                    continue; // your class kit, or a mission item (recorded as an item)
                float cd;
                try
                {
                    cd = AbilityManager.GetCooldownRemaining(n, true);
                }
                catch
                {
                    continue;
                }
                float before;
                bool known = _abilityCd.TryGetValue(n, out before);
                _abilityCd[n] = cd;
                bool cast = s.IsCasting && s.CastName == n;
                bool cooled = known && cd > 1.6f && cd > before + 1f;
                long last;
                if ((cast || cooled) && (!_abilityUsedAt.TryGetValue(n, out last) || s.Tick - last > 4000))
                {
                    _abilityUsedAt[n] = s.Tick;
                    s.AbilityName = n;
                    s.AbilityTarget = AbilityTarget(s);
                    return; // one per look
                }
            }
        }

        /// <summary>Your target when you used a quest ability, if it's an NPC within ~30 m (who you used it on).</summary>
        private ObjRef AbilityTarget(Snap s)
        {
            try
            {
                if (s.TargetId == 0 || s.TargetId == s.PlayerId || s.TargetId == s.CompanionId)
                    return null;
                var npc = HeroObjectManager.GetByNodeId(s.TargetId) as HeroNPC;
                if (npc == null || !npc.IsValid || V(npc.Location).DistanceTo(s.Pos) > 3f)
                    return null;
                return Describe(npc, "your target when you used the ability");
            }
            catch
            {
                return null;
            }
        }

        private HashSet<string> _equipped;
        private long _equipTick = NeverRead;

        /// <summary>An item you put on during the recording (a quest disguise): anything newly equipped. The profile builder
        /// keeps it only if a mission moved soon after.</summary>
        private void ReadEquipped(HeroLocalPlayer me, Snap s)
        {
            if (s.Tick - _equipTick < 1000)
                return;
            _equipTick = s.Tick;
            var now = new HashSet<string>(StringComparer.Ordinal);
            foreach (HeroItem it in me.EquippedItems)
            {
                if (it == null)
                    continue;
                string n = Fmt.OneLine(it.Name);
                if (n.Length > 0)
                    now.Add(n);
            }
            if (now.Count == 0)
                return; // not loaded yet
            if (_equipped != null)
                foreach (string n in now)
                    if (!_equipped.Contains(n))
                    {
                        s.EquippedName = n;
                        break;
                    }
            _equipped = now;
        }

        private readonly HashSet<ulong> _taskSpecs = new HashSet<ulong>();
        private readonly HashSet<ulong> _taskSpotted = new HashSet<ulong>();
        private long _taskScanTick = NeverRead;

        /// <summary>
        /// The task scanner: every 3 s, the objects within 80 m that one of your tasks counts (the game says which object
        /// types each task counts), also the ones you never clicked - the eggs and bones you walked past are spots for the
        /// collect step too. Up to 30 new ones per look.
        /// </summary>
        private void ScanTaskObjects(Snap s)
        {
            if (_taskSpecs.Count == 0 || s.Tick - _taskScanTick < 3000 || s.Pos.IsZero)
                return;
            _taskScanTick = s.Tick;
            var found = new List<ObjRef>();
            try
            {
                foreach (HeroPlaceable p in HeroObjectManager.GetObjects<HeroPlaceable>())
                {
                    if (p == null || found.Count >= 30 || _taskSpotted.Contains(p.NodeId))
                        continue;
                    try
                    {
                        Vec3 pos = V(p.Location);
                        if (pos.IsZero || pos.DistanceTo(s.Pos) > 8f)
                            continue;
                        ulong spec;
                        if (!p.TryGetField<ulong>(GomFieldId.spnSpawnedSpec, out spec) || !_taskSpecs.Contains(spec))
                            continue;
                        string n = Fmt.OneLine(p.Name);
                        if (n.Length == 0)
                            continue;
                        _taskSpotted.Add(p.NodeId);
                        found.Add(new ObjRef { Name = n, NodeId = p.NodeId, Kind = ObjKind.Placeable, Pos = pos, HasPos = true, Spec = spec, How = "an object one of your tasks counts (scanner)" });
                    }
                    catch
                    {
                        // skip this one
                    }
                }
            }
            catch
            {
                // no scan this time
            }
            // the mobs your tasks count, too: every one you came within 80 m of is a spot where that fight can find them
            try
            {
                foreach (HeroNPC npc in HeroObjectManager.GetObjects<HeroNPC>())
                {
                    if (npc == null || found.Count >= 60 || _taskSpotted.Contains(npc.NodeId) || npc.NodeId == s.CompanionId)
                        continue;
                    try
                    {
                        Vec3 pos = V(npc.Location);
                        if (pos.IsZero || pos.DistanceTo(s.Pos) > 8f)
                            continue;
                        ulong spec;
                        if (!npc.TryGetField<ulong>(GomFieldId.spnSpawnedSpec, out spec) || !_taskSpecs.Contains(spec))
                            continue;
                        string n = NameOf(npc);
                        if (string.IsNullOrEmpty(n))
                            continue;
                        _taskSpotted.Add(npc.NodeId);
                        found.Add(new ObjRef { Name = n, NodeId = npc.NodeId, Kind = ObjKind.Npc, Pos = pos, HasPos = true, Spec = spec, How = "a mob one of your tasks counts (scanner)" });
                    }
                    catch
                    {
                        // skip this one
                    }
                }
            }
            catch
            {
                // no mob scan this time
            }
            s.TaskSpots = found.Count > 0 ? found : null;
        }

        /// <summary>
        /// The scanner: every other object with the same name within 80 m of the one you clicked (up to 20), so a
        /// "click 4 of these" step knows all the spots, not only the ones you happened to use.
        /// </summary>
        private List<ObjRef> ScanSameKind(ObjRef clicked)
        {
            var list = new List<ObjRef>();
            try
            {
                foreach (HeroPlaceable p in HeroObjectManager.GetObjects<HeroPlaceable>())
                {
                    if (p == null || p.NodeId == clicked.NodeId || list.Count >= 20)
                        continue;
                    string n;
                    Vec3 pos;
                    try
                    {
                        n = Fmt.OneLine(p.Name);
                        if (n != clicked.Name)
                            continue;
                        pos = V(p.Location);
                    }
                    catch
                    {
                        continue;
                    }
                    if (pos.IsZero || pos.DistanceTo(clicked.Pos) > 8f)
                        continue;
                    list.Add(new ObjRef { Name = n, NodeId = p.NodeId, Kind = ObjKind.Placeable, Pos = pos, HasPos = true, How = "same kind nearby (scanner)" });
                }
            }
            catch
            {
                // no scan this time
            }
            return list;
        }

        /// <summary>The right-click "use / talk / interact" command ("sysInputEvent_DefaultAction" per BuddyCron's docs).</summary>
        private static bool IsUseCommand(string cmd)
        {
            // right-click "use", or the interact key
            return cmd.Length == 0 || cmd.IndexOf("DefaultAction", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   cmd.IndexOf("Interact", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private ObjRef RecentClick(Snap s, long withinMs, string how)
        {
            if (_lastClick == null || s.Tick - _lastClickTick > withinMs)
                return null;
            return _lastClick.Clone(how);
        }

        private void ReadConversation(HeroLocalPlayer me, Snap prev, RrSettings cfg, Snap s)
        {
            if (!s.InConvo)
                return;
            if (prev != null && prev.InConvo)
                return; // only on the look where the conversation started
            HeroCnvOracle cnv = ExtraNodeManager.ConvoOracle;
            if (cnv != null)
            {
                cnvConversationType ct = cnv.ConversationType;
                s.ConvoTypeName = ct.ToString();
                s.ConvoIsComlink = ct == cnvConversationType.Comlink;
            }
            if (s.ConvoIsComlink)
                return; // a holocom call has nobody standing next to you
            s.ConvoPartner = GuessPartner(s, Math.Max(2, cfg.PartnerRadius / 10));
            if (s.ConvoPartner != null && s.ConvoPartner.OffersQuest && s.ConvoPartner.NodeId != 0)
            {
                try
                {
                    s.OfferedQuestId = me.Quests.GetQuestForSpawnedObject(s.ConvoPartner.NodeId);
                }
                catch
                {
                    s.OfferedQuestId = 0;
                }
            }
        }

        /// <summary>Conversation choice wheels.</summary>
        private void ReadReplies(Snap prev, Snap s)
        {
            if (!s.InConvo)
                return; // holocom calls count too: they can have a wheel
            HeroCnvOracle cnv = ExtraNodeManager.ConvoOracle;
            if (cnv == null)
                return;
            s.ConvoNode = Convert.ToInt64(cnv.CurrentNodeNumber);
            if (!cnv.IsWaitingForChoice || !cnv.ChoicesShown || s.ConvoNode < 0)
                return;
            s.ReplyNode = s.ConvoNode;
            if (prev != null && prev.InConvo && prev.ReplyNode == s.ReplyNode)
                return; // the same wheel is still up: read once
            if (!CanSpend(true))
            {
                s.ReplyNode = -1; // no budget left in this look: read it on the next one
                return;
            }
            var list = new List<ReplyOpt>();
            foreach (var c in cnv.CurrentReplyChoices())
            {
                list.Add(new ReplyOpt
                {
                    Option = Convert.ToInt64(c.OptionNumber),
                    Node = Convert.ToInt64(c.NodeNumber),
                    Align = c.IsDark ? "dark" : (c.IsLight ? "light" : "neutral"),
                });
            }
            Spend(true, Math.Max(1, list.Count));
            if (list.Count == 0)
            {
                s.ReplyNode = -1; // not resolved yet: try again next look
                return;
            }
            s.Replies = list;
        }

        /// <summary>BuddyCron has no "who am I talking to".</summary>
        private ObjRef GuessPartner(Snap s, int radius)
        {
            ObjRef click = RecentClick(s, 6000, "you clicked it");
            if (click != null && (click.Kind == ObjKind.Npc || click.Kind == ObjKind.Placeable))
            {
                // re-read so the quest marker is the one shown now
                ObjRef fresh = Describe(HeroObjectManager.GetByNodeId(click.NodeId), click.How);
                return fresh ?? click;
            }
            if (s.TargetId != 0 && s.TargetId != s.PlayerId && s.TargetId != s.CompanionId)
            {
                ObjRef t = Describe(HeroObjectManager.GetByNodeId(s.TargetId), "your target");
                if (t != null && (t.Kind == ObjKind.Npc || t.Kind == ObjKind.Placeable) && (!t.HasPos || t.Pos.DistanceTo(s.Pos) <= 2f))
                    return t;
            }
            List<KeyValuePair<float, HeroObject>> near = Nearby(s, radius);
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (!(kv.Value is HeroNPC))
                    continue;
                cnvQuestIndication qi = kv.Value.QuestIndication;
                if (qi != cnvQuestIndication.None)
                    return Describe(kv.Value, "nearest NPC with a quest marker (guess)");
            }
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Value is HeroNPC && kv.Key <= 4f)
                    return Describe(kv.Value, "nearest NPC (guess)");
            }
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Value is HeroPlaceable && kv.Key <= 1f)
                    return Describe(kv.Value, "nearest object (guess)");
            }
            return null;
        }

        // ------------------------------------------------------------------ interactions (taxi, elevator, vendor ...)

        private void ReadInteraction(Snap prev, Snap s)
        {
            if (prev == null || prev.Interaction == s.Interaction || prev.PlayerId != s.PlayerId)
                return;
            var it = (chrInteractionType)s.Interaction;
            switch (it)
            {
                case chrInteractionType.None:
                case chrInteractionType.Conversation:
                case chrInteractionType.MissionBoard:
                    return;
                case chrInteractionType.Taxi:
                {
                    _taxiNet = null; // a new map: what it offers is read while it is open (ReadTaxiMap)
                    _taxiNetReads = 0;
                    ObjRef r = null;
                    HeroGuiApiGfx gui = ExtraNodeManager.GuiApiGfx;
                    if (gui != null)
                        r = Describe(gui.TaxiSourceTerminal, "taxi map opened from");
                    if (r == null)
                        r = RecentClick(s, 8000, "you clicked it");
                    if (r == null)
                        r = NearestWhere(s, 2, o => o.IsTaxiTerminal, "nearest taxi terminal (guess)");
                    s.InteractionTarget = r;
                    return;
                }
                case chrInteractionType.Wonkavator:
                {
                    ObjRef r = null;
                    ObjRef click = RecentClick(s, 8000, "you clicked it");
                    if (click != null)
                        r = Describe(HeroObjectManager.GetByNodeId(click.NodeId), click.How);
                    if (r == null || !r.IsWonkavator)
                        r = NearestWhere(s, 2, o => { var p = o as HeroPlaceable; return p != null && p.IsWonkavator; }, "nearest elevator (guess)") ?? r;
                    s.InteractionTarget = r;
                    if (r != null)
                        _wonka = new WonkaMemo { Wonka = r, Stand = s.Pos, AreaId = s.AreaId, Tick = s.Tick };
                    return;
                }
                default:
                {
                    ObjRef r = RecentClick(s, 8000, "you clicked it");
                    if (r == null && s.TargetId != 0 && s.TargetId != s.CompanionId)
                        r = Describe(HeroObjectManager.GetByNodeId(s.TargetId), "your target");
                    if (r == null)
                        r = NearestWhere(s, 2, o => o is HeroNPC, "nearest NPC (guess)");
                    s.InteractionTarget = r;
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ combat and kills

        private void ReadCombat(HeroLocalPlayer me, Snap prev, Snap s)
        {
            long now = s.Tick;
            IReadOnlyList<ulong> attackers = me.AttackerIds;
            if (attackers != null)
            {
                foreach (ulong id in attackers)
                    Track(id, now, s);
            }
            if (s.InCombat && s.TargetId != 0)
                Track(s.TargetId, now, s);

            if (s.InCombat && (prev == null || !prev.InCombat) && attackers != null)
            {
                s.CombatMobs = new List<ObjRef>();
                int n = 0;
                foreach (ulong id in attackers)
                {
                    if (++n > 6)
                        break;
                    ObjRef m = DescribeMob(HeroObjectManager.GetByNodeId(id) as HeroCharacter, "attacked you");
                    if (m != null)
                        s.CombatMobs.Add(m);
                }
            }

            if (_tracked.Count == 0)
                return;
            List<ulong> drop = null;
            foreach (KeyValuePair<ulong, TrackedMob> kv in _tracked)
            {
                TrackedMob t = kv.Value;
                var c = HeroObjectManager.GetByNodeId(kv.Key) as HeroCharacter;
                if (c == null || !c.IsValid || c is HeroPlayer)
                {
                    // despawned or out of range: not a kill
                    (drop ?? (drop = new List<ulong>())).Add(kv.Key);
                    continue;
                }
                bool dead = c.IsDead;
                if (!t.Checked)
                {
                    t.Checked = true;
                    t.WasDead = dead;
                    if (dead)
                        (drop ?? (drop = new List<ulong>())).Add(kv.Key);
                    continue;
                }
                if (dead && !t.WasDead)
                {
                    ObjRef mob = DescribeMob(c, "killed");
                    if (mob != null)
                    {
                        if (s.Kills == null)
                            s.Kills = new List<KillInfo>();
                        bool tapped = false;
                        try
                        {
                            tapped = c.TappedByOther;
                        }
                        catch
                        {
                            tapped = false;
                        }
                        s.Kills.Add(new KillInfo { Mob = mob, TappedByOther = tapped });
                    }
                    (drop ?? (drop = new List<ulong>())).Add(kv.Key);
                    continue;
                }
                t.WasDead = dead;
                if (!s.InCombat && now - t.LastListedTick > 20000)
                    (drop ?? (drop = new List<ulong>())).Add(kv.Key);
            }
            if (drop != null)
            {
                foreach (ulong id in drop)
                    _tracked.Remove(id);
            }
        }

        private void Track(ulong id, long now, Snap s)
        {
            if (id == 0 || id == s.PlayerId || id == s.CompanionId)
                return;
            TrackedMob t;
            if (_tracked.TryGetValue(id, out t))
            {
                t.LastListedTick = now;
                return;
            }
            if (_tracked.Count >= MaxTracked)
                return;
            _tracked[id] = new TrackedMob { LastListedTick = now };
        }

        private ObjRef DescribeMob(HeroCharacter c, string how)
        {
            if (c == null || !c.IsValid)
                return null;
            var r = new ObjRef();
            r.NodeId = c.NodeId;
            r.How = how;
            r.Kind = c is HeroNPC ? ObjKind.Npc : ObjKind.Other;
            r.Name = NameOf(c);
            Vector3 p;
            if (c.TryGetScenePosition(out p))
            {
                r.Pos = V(p);
                r.HasPos = true;
            }
            else
            {
                r.Pos = V(c.Location);
                r.HasPos = !r.Pos.IsZero;
            }
            try
            {
                r.Level = c.Level;
                r.Toughness = c.Toughness.ToString();
            }
            catch
            {
                // optional
            }
            return r;
        }

        // ------------------------------------------------------------------ casts and quest items

        private void ReadCast(HeroLocalPlayer me, Snap prev, Snap s)
        {
            if (s.CastSpec == 0)
                return;
            if (prev != null && prev.IsCasting && prev.CastSpec == s.CastSpec)
                return; // the same cast is still going
            string name;
            if (!_abilityNames.TryGetValue(s.CastSpec, out name))
            {
                HeroAbility a = me.CastingAbility;
                name = a != null ? Fmt.OneLine(a.Name) : string.Empty;
                if (_abilityNames.Count < 2000)
                    _abilityNames[s.CastSpec] = name;
            }
            s.CastName = name;

            bool stale = s.Tick - _questItemsTick > (s.InCombat ? 20000 : 2000);
            if (!_questItemAbilities.ContainsKey(s.CastSpec) && stale)
                RefreshQuestItems(me, s.Tick);
            string item;
            if (!_questItemAbilities.TryGetValue(s.CastSpec, out item) && name.Length > 0)
            {
                // The game can cast a mission item through a different ability id than the item lists.
                if (!_questItemByName.ContainsKey(name) && s.Tick - _questItemsTick > 500)
                    RefreshQuestItems(me, s.Tick);
                // the cast bar says "Using Rune of Passion..." for the mission item "Rune of Passion"
                string bare = name.StartsWith("Using ", StringComparison.Ordinal) ? name.Substring(6).TrimEnd('.', ' ') : name;
                if (_questItemByName.TryGetValue(name, out item) || _questItemByName.TryGetValue(bare, out item))
                    _questItemAbilities[s.CastSpec] = item;
                else if (!s.InCombat && _unmatchedLogged.Add(name))
                    RrLog.Debug("Cast \"{0}\" is not one of your mission items: {1}", name, _questItemList);
            }
            if (item != null)
            {
                s.QuestItemName = item;
                if (name.Length > 0 && _castNameItem.Count < 500)
                    _castNameItem[name] = item; // its cast bar text ("Using Detection...") - the combat log names it that way too
                s.CastTarget = ItemTarget(me, s, item);
            }
            else if (!s.InCombat && !(_baseAbilities != null && name.Length > 0 && !_baseAbilities.Contains(name) && AbilityManager.HasAbility(name)))
            {
                // Clicking a quest object usually starts a channel.
                // (not a quest ability from a special bar: that's recorded as its own use, not a use of an object nearby)
                s.CastTarget = CastObject(s);
            }
        }

        /// <summary>
        /// What a mission item was used on, as the game sees it: your target only if the item can be used on it;
        /// otherwise the nearest thing the item CAN be used on (Black Hole No Trace Left: the Radiation Sensor's spies,
        /// not General Konya you happened to have targeted). Null when the item isn't used on anything (used at a spot).
        /// </summary>
        private ObjRef ItemTarget(HeroLocalPlayer me, Snap s, string itemName)
        {
            HeroItem it = null;
            try
            {
                foreach (HeroItem q in me.QuestItems)
                    if (q != null && Fmt.OneLine(q.Name) == itemName)
                    {
                        it = q;
                        break;
                    }
                if (it == null)
                    foreach (HeroItem q in _bagQuestItems)
                        if (q != null && Fmt.OneLine(q.Name) == itemName)
                        {
                            it = q;
                            break;
                        }
            }
            catch
            {
                it = null;
            }
            HeroObject target = s.TargetId != 0 && s.TargetId != s.PlayerId && s.TargetId != s.CompanionId ? HeroObjectManager.GetByNodeId(s.TargetId) : null;
            if (it == null)
                return target != null ? Describe(target, "your target") : null;
            try
            {
                // an item that doesn't need a marked target is used where you stand, not on whoever you had targeted
                bool needsTarget = false;
                if (it.UseTargetMarkers != null)
                    foreach (var marker in it.UseTargetMarkers)
                    {
                        needsTarget = true;
                        break;
                    }
                if (!needsTarget)
                    // no marked target needed - but if the NPC you had targeted is one it works on, you used it on that NPC
                    return target != null && it.IsValidUseTarget(target) ? Describe(target, "your target (the item works on it)") : null;
                if (target != null && it.IsValidUseTarget(target))
                    return Describe(target, "your target (the item works on it)");
                foreach (KeyValuePair<float, HeroObject> kv in Nearby(s, 3f))
                {
                    try
                    {
                        if (kv.Value is HeroPlayer || kv.Value.NodeId == s.CompanionId)
                            continue;
                        if (it.IsValidUseTarget(kv.Value))
                            return Describe(kv.Value, "nearest thing the item works on");
                    }
                    catch
                    {
                        // skip what can't be checked
                    }
                }
                return null; // nothing near that the item accepts: the profile uses it at your spot
            }
            catch
            {
                return target != null ? Describe(target, "your target") : null;
            }
        }

        /// <summary>The object an out-of-combat cast was most likely used on.</summary>
        private ObjRef CastObject(Snap s)
        {
            ObjRef click = RecentClick(s, 3000, "you clicked it, then the cast started");
            if (click != null && click.Kind == ObjKind.Placeable)
                return click;
            // Left-click to select, then interact (key or double-click): the selected object is the one used .
            if (s.TargetId != 0 && s.TargetId != s.PlayerId && s.TargetId != s.CompanionId)
            {
                ObjRef t = Describe(HeroObjectManager.GetByNodeId(s.TargetId), "your target when the cast started");
                if (t != null && t.Kind == ObjKind.Placeable && (!t.HasPos || t.Pos.DistanceTo(s.Pos) <= 2f))
                    return t;
            }
            List<KeyValuePair<float, HeroObject>> near = Nearby(s, 3f);
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                var p = kv.Value as HeroPlaceable;
                if (p == null)
                    continue;
                bool usable;
                float range;
                try
                {
                    usable = p.IsUsableForActiveQuest;
                    range = p.UseRange;
                }
                catch
                {
                    continue;
                }
                float lim = Math.Max(range, 0.5f) + 0.5f; // game units (10 m): its use range plus 5 m
                if (usable && kv.Key <= lim * lim)
                    return Describe(p, "nearest object usable for a quest when the cast started (inferred from the cast)");
            }
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Value is HeroPlaceable && kv.Key <= 2.25f)
                    return Describe(kv.Value, "nearest object when the cast started (guess from the cast)");
            }
            // Last try: the nearest interaction prop, named after the placeable it belongs to.
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Key > 2.25f)
                    break;
                if (kv.Value is HeroNPC || kv.Value is HeroPlaceable)
                    continue;
                ObjRef r = Describe(kv.Value, "nearest object part when the cast started (guess from the cast)");
                if (r != null && r.Kind == ObjKind.Placeable)
                    return r;
            }
            return null;
        }

        /// <summary>
        /// Mission items that work instantly (no cast bar, like a scanner) can't be seen by their cast; their cooldown
        /// starting is the sign they were used.
        /// </summary>
        private void ReadInstantItems(HeroLocalPlayer me, Snap s)
        {
            if (s.Tick - _bagQuestItemsTick > 2000) // every 2 s, so a used-up item is noticed close to when you used it
                RefreshBagQuestItems(me, s.Tick);
            // the item being cast right now (its cast was already recorded): its cooldown / stack / leaving the bags at
            // the end of that cast is the same use, not a second one
            if (s.QuestItemName != null)
            {
                _castItemName = s.QuestItemName;
                _castItemSeen = s.Tick;
            }
            else if (_castItemName != null && s.IsCasting)
                _castItemSeen = s.Tick;

            // every mission item, counted by name (two stacks of one item are one item - comparing them one by one made
            // a fake "use" on every look)
            var quest = new Dictionary<string, ItemLook>(StringComparer.Ordinal);
            bool readAll = ReadItems(me.QuestItems, 60, quest);
            // (two empty reads in a row before believing the list really emptied: a zoning read comes back empty once)
            bool questOk = readAll && ConfirmEmpty(quest.Count, _questNames.Count, ref _questEmptyReads);
            foreach (KeyValuePair<string, ItemLook> kv in quest)
                CheckItemUsed(kv.Key, kv.Value, true, s);
            if (questOk)
            {
                // a mission item that was there a moment ago and is gone now: you used your last one (an injector), or the
                // game took it away (a click, a loot, a turn-in) - marked "item gone", the profile builder sorts out which
                foreach (string old in _questNames)
                    if (!quest.ContainsKey(old) && !_goneQueue.Contains(old))
                        _goneQueue.Add(old);
                _questNames = new HashSet<string>(quest.Keys, StringComparer.Ordinal);
            }

            // usable bag items (stims, plot items kept in the bags), counted by name too
            var bag = new Dictionary<string, ItemLook>(StringComparer.Ordinal);
            foreach (HeroItem item in _bagQuestItems)
                AddItem(item, bag);
            foreach (KeyValuePair<string, ItemLook> kv in bag)
                if (!quest.ContainsKey(kv.Key))
                    CheckItemUsed(kv.Key, kv.Value, false, s);
            if (_bagGone != null)
            {
                if (!_goneQueue.Contains(_bagGone))
                    _goneQueue.Add(_bagGone);
                _bagGone = null;
            }

            // one "gone" per look (the rest wait for the next look, none is lost)
            while (_goneQueue.Count > 0 && s.InstantItemName == null)
            {
                string gone = _goneQueue[0];
                _goneQueue.RemoveAt(0);
                int had;
                _itemStack.TryGetValue(gone, out had);
                _itemStack[gone] = 0;
                if (had <= 0 || IsSameCast(gone, s))
                    continue; // never had any, or the end of a cast already recorded
                s.InstantItemName = gone;
                _instName = gone;
                _instSeen = s.Tick;
                bool plot = _questItemByName.ContainsKey(gone) || _bagPlot.Contains(gone) || _bagPlotBefore.Contains(gone) || _wasQuestItem.Contains(gone);
                s.InstantItemBag = !plot; // a stim: kept only when a quest moves right after
                s.InstantItemGone = plot; // a mission item: the builder checks what made it go
                s.CastTarget = GoneTarget(s); // your target then, if it's an NPC right next to you
            }
        }

        private sealed class ItemLook
        {
            public int Stack;
            public float Cooldown;
            public string FirstName;
        }

        private static void AddItem(HeroItem item, Dictionary<string, ItemLook> into)
        {
            try
            {
                if (item == null)
                    return;
                string name = Fmt.OneLine(item.Name);
                if (name.Length == 0)
                    return;
                HeroAbility a = item.Ability;
                float cd = a != null ? a.CooldownRemaining : 0f;
                ItemLook look;
                if (!into.TryGetValue(name, out look))
                    into[name] = look = new ItemLook { FirstName = name };
                look.Stack += item.StackCount;
                if (cd > look.Cooldown)
                    look.Cooldown = cd;
            }
            catch
            {
                // skip this one
            }
        }

        private static bool ReadItems(IEnumerable<HeroItem> items, int max, Dictionary<string, ItemLook> into)
        {
            try
            {
                int n = 0;
                foreach (HeroItem item in items)
                {
                    if (item == null)
                        continue;
                    if (++n > max)
                        return false;
                    AddItem(item, into);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>An empty list after a non-empty one counts only on the second empty read in a row.</summary>
        private static bool ConfirmEmpty(int nowCount, int beforeCount, ref int emptyReads)
        {
            if (nowCount > 0 || beforeCount == 0)
            {
                emptyReads = 0;
                return true;
            }
            emptyReads++;
            return emptyReads >= 2;
        }

        /// <summary>The item whose cast was just recorded, still casting or ended under 4 s ago: the same use.</summary>
        private bool IsSameCast(string name, Snap s)
        {
            return (_castItemName != null && name == _castItemName && s.Tick - _castItemSeen < 4000) ||
                   (_logItemName != null && name == _logItemName && s.Tick - _logItemSeen < 4000); // (or the combat log already reported it)
        }

        /// <summary>Your target when an item left your bags, if it's an NPC within about 30 m (who you used it on).</summary>
        private ObjRef GoneTarget(Snap s)
        {
            try
            {
                if (s.TargetId == 0 || s.TargetId == s.CompanionId)
                    return null;
                var npc = HeroObjectManager.GetByNodeId(s.TargetId) as HeroNPC;
                if (npc == null || !npc.IsValid || V(npc.Location).DistanceTo(s.Pos) > 3f)
                    return null;
                return Describe(npc, "your target when the item left your bags");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// An item was used if its cooldown just started (a scanner: not the 1.5 s global cooldown), or its stack went down
        /// (collars, probes, charges). A stack drop with no cooldown can also be the game taking one away (a click, a
        /// turn-in), so that one is marked "item gone" and the profile builder checks what made it go.
        /// </summary>
        private void CheckItemUsed(string name, ItemLook look, bool fromQuestItems, Snap s)
        {
            float before;
            bool known = _itemCooldown.TryGetValue(name, out before);
            _itemCooldown[name] = look.Cooldown;
            int stackBefore;
            bool knownStack = _itemStack.TryGetValue(name, out stackBefore);
            _itemStack[name] = look.Stack;
            if (fromQuestItems)
                _wasQuestItem.Add(name);
            bool cooldownStarted = known && before <= 0.05f && look.Cooldown > 1.6f;
            bool usedUp = knownStack && look.Stack < stackBefore;
            if (!(cooldownStarted || usedUp) || s.QuestItemName == name || s.InstantItemName != null || IsSameCast(name, s))
                return;
            bool plot = fromQuestItems || _bagPlot.Contains(name);
            s.InstantItemName = name;
            _instName = name;
            _instSeen = s.Tick;
            s.InstantItemBag = !plot; // a usable bag item (a stim, a medpac), not a mission item
            s.InstantItemGone = plot && usedUp && !cooldownStarted;
            var me2 = Core.Me;
            if (me2 != null)
                s.CastTarget = ItemTarget(me2, s, name); // what the item works on, not just whoever you had targeted
        }

        /// <summary>Quest items in your bags: plot items you can right-click (not medpacs, gear or other stuff).</summary>
        private HashSet<string> _bagPlot = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _bagPlotBefore = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _bagNames = new HashSet<string>(StringComparer.Ordinal);
        private string _bagGone; // an item you just used your last one of
        private HashSet<string> _questNames = new HashSet<string>(StringComparer.Ordinal); // mission items at the last look

        private void RefreshBagQuestItems(HeroLocalPlayer me, long now)
        {
            _bagQuestItemsTick = now;
            var list = new List<HeroItem>();
            var plot = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                int n = 0;
                foreach (HeroItem item in me.Backpack)
                {
                    if (item == null || ++n > 300)
                        break;
                    try
                    {
                        // every item you can use from your bags (a stim that keeps you alive isn't always a "plot" item)
                        if (item.IsUsable && list.Count < 40)
                        {
                            list.Add(item);
                            if (item.IsPlotItem)
                                plot.Add(Fmt.OneLine(item.Name));
                        }
                    }
                    catch
                    {
                        // skip this one
                    }
                }
            }
            catch
            {
                // bags not readable right now
            }
            // gone since the last look, with some left then: used up
            var now2 = new HashSet<string>(StringComparer.Ordinal);
            foreach (HeroItem it2 in list)
            {
                try { now2.Add(Fmt.OneLine(it2.Name)); } catch { }
            }
            // (not on a bad read - an empty list once while zoning - and not when the list was cut at 40 items, where an
            // item can drop off the end after a bag sort without being used)
            if (!ConfirmEmpty(now2.Count, _bagNames.Count, ref _bagEmptyReads) || list.Count >= 40)
            {
                _bagQuestItems = list.Count > 0 ? list : _bagQuestItems;
                return;
            }
            foreach (string old in _bagNames)
            {
                int had;
                if (!now2.Contains(old) && _itemStack.TryGetValue(old, out had) && had > 0 && _bagGone == null)
                {
                    _bagGone = old;
                    // (its stack is zeroed when it's reported)
                }
            }
            _bagNames = now2;
            _bagPlotBefore = _bagPlot;
            _bagQuestItems = list;
            _bagPlot = plot;
        }

        private void RefreshQuestItems(HeroLocalPlayer me, long now)
        {
            _questItemsTick = now;
            var map = new Dictionary<ulong, string>();
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            int n = 0;
            var all = new List<HeroItem>();
            foreach (HeroItem item in me.QuestItems)
            {
                if (item == null || ++n > 60)
                    break;
                all.Add(item);
            }
            all.AddRange(_bagQuestItems); // a quest item kept in the bags is cast the same way
            foreach (HeroItem item in all)
            {
                string itemName = Fmt.OneLine(item.Name);
                ulong ab = item.AbilitySpecId;
                if (ab != 0 && !map.ContainsKey(ab))
                    map[ab] = itemName;
                string abName = string.Empty;
                try
                {
                    HeroAbility a = item.Ability;
                    if (a != null)
                        abName = Fmt.OneLine(a.Name);
                }
                catch
                {
                }
                if (itemName.Length > 0 && !byName.ContainsKey(itemName))
                    byName[itemName] = itemName;
                if (abName.Length > 0 && !byName.ContainsKey(abName))
                    byName[abName] = itemName;
                list.Add("\"" + itemName + "\" (ability \"" + abName + "\" " + Fmt.Hex(ab) + ")");
            }
            _questItemAbilities = map;
            _questItemByName = byName;
            _questItemList = list.Count > 0 ? string.Join(", ", list.ToArray()) : "none";
        }

        // ------------------------------------------------------------------ movement: mounts, taxis, elevators, ship

        private void ReadMovement(HeroLocalPlayer me, Snap prev, bool afterLoading, Snap s)
        {
            if (s.IsMounted && (prev == null || !prev.IsMounted))
            {
                long mid = me.ActiveMountId;
                s.MountName = mid != 0 ? Fmt.OneLine(MountManager.MountName(mid)) : string.Empty;
            }

            // Taxi: when the ride ends, the terminal you land at is the destination. It may stream in a moment later.
            if (prev != null && prev.IsInTaxi && !s.IsInTaxi)
            {
                _taxiPending = true;
                _taxiPendingUntil = s.Tick + 8000;
                _taxiLandPos = s.Pos;
                _taxiLandTime = s.Time;
            }
            if (_taxiPending && !s.IsInTaxi)
            {
                HeroObject destObj = NearestObject(s, 5, o => o.IsTaxiTerminal);
                ObjRef dest = destObj != null ? Describe(destObj, "taxi terminal where you landed") : null;
                if (dest != null || s.Tick > _taxiPendingUntil)
                {
                    var ta = new TaxiArrival { Dest = dest, LandPos = _taxiLandPos, LandTime = _taxiLandTime };
                    ResolveTaxiDestination(ta, destObj);
                    s.TaxiArrival = ta;
                    _taxiPending = false;
                }
            }

            // Ship: the galaxy-map area changes while the area (the ship interior) stays the same.
            if (prev != null && prev.PlayerId == s.PlayerId && prev.AreaId == s.AreaId &&
                prev.GalaxyAreaId != s.GalaxyAreaId && s.GalaxyAreaId != 0)
            {
                GalaxyMapDestination d = GalaxyMap.FindDestination(s.GalaxyAreaId);
                s.ShipDest = d != null ? Fmt.OneLine(d.Name) : string.Empty;
            }

            // Elevator / shuttle door: after its window opened, the next quick position change is the ride.
            if (_wonka != null)
            {
                if (s.Tick - _wonka.Tick > 120000)
                {
                    _wonka = null;
                }
                else if (prev != null)
                {
                    float dist = s.Pos.DistanceTo(prev.Pos);
                    double dt = Math.Max(0.05, (s.Tick - prev.Tick) / 1000.0);
                    bool moved = afterLoading || prev.AreaId != s.AreaId || (dist > 3f && dist / dt > 12.0);
                    // A short lift (CZ-198 Elevator 1 to Freight Depot) moves you less than 3 m.
                    if (!moved && Math.Abs(s.Pos.Y - prev.Pos.Y) > 0.8f && Math.Abs(s.Pos.Y - prev.Pos.Y) / dt > 2.0)
                        moved = true;
                    if (!moved)
                    {
                        try
                        {
                            var lift = HeroObjectManager.GetByNodeId(_wonka.Wonka.NodeId) as HeroPlaceable;
                            if (lift != null && lift.IsValid && lift.CurrentFloor > 0 && _wonka.Wonka.Floor > 0 && lift.CurrentFloor != _wonka.Wonka.Floor)
                                moved = true;
                        }
                        catch
                        {
                            // the lift is out of reach: keep watching your position
                        }
                    }
                    if (moved)
                    {
                        s.WonkaArrival = ResolveWonkaArrival(s);
                        _wonka = null;
                    }
                }
            }
        }

        private WonkaArrival ResolveWonkaArrival(Snap s)
        {
            WonkaMemo m = _wonka;
            var a = new WonkaArrival { From = m };
            string[] dests = m.Wonka.Destinations;
            var same = HeroObjectManager.GetByNodeId(m.Wonka.NodeId) as HeroPlaceable;
            if (same != null && same.IsValid && dests != null)
            {
                int f = same.CurrentFloor;
                if (f > 0 && f != m.Wonka.Floor && f <= dests.Length)
                {
                    a.Dest = dests[f - 1];
                    a.How = "the elevator now stands on that floor";
                    return a;
                }
            }
            foreach (KeyValuePair<float, HeroObject> kv in Nearby(s, 2))
            {
                var p = kv.Value as HeroPlaceable;
                if (p == null || !p.IsWonkavator)
                    continue;
                int f = p.CurrentFloor;
                IReadOnlyList<string> list = p.WonkavatorDestinations;
                if (f > 0 && list != null && f <= list.Count)
                {
                    a.Dest = Fmt.OneLine(list[f - 1]);
                    a.How = "floor of the elevator where you arrived";
                    return a;
                }
            }
            return a;
        }

        // ------------------------------------------------------------------ taxi map names

        /// <summary>While the taxi map is open, keep what it offers (destination spec ids).</summary>
        private void ReadTaxiMap(Snap s)
        {
            if ((chrInteractionType)s.Interaction != chrInteractionType.Taxi || _taxiNetReads >= 60)
                return;
            HeroGuiApiGfx gui = ExtraNodeManager.GuiApiGfx;
            if (gui == null || gui.TaxiSourceTerminalId == 0)
                return;
            _taxiNetReads++;
            Dictionary<ulong, ulong> net = gui.TaxiPrevTerminalNetwork;
            if (net != null && net.Count > 0)
                _taxiNet = new Dictionary<ulong, ulong>(net);
        }

        /// <summary>The destination's taxi-map name.</summary>
        private void ResolveTaxiDestination(TaxiArrival ta, HeroObject destObj)
        {
            ulong spec = 0;
            if (destObj != null)
            {
                try
                {
                    if (!destObj.TryGetField<ulong>(GomFieldId.plcTaxiTerminalSpec, out spec))
                        spec = 0;
                }
                catch
                {
                    spec = 0;
                }
            }
            if (spec != 0)
            {
                string n = TaxiMapName(spec, true);
                if (n.Length > 0)
                {
                    ta.MapName = n;
                    ta.MapNameSure = true;
                    ta.MapNameHow = "the taxi-map name of the terminal where you landed";
                }
            }

            if (_taxiNet != null)
            {
                var choices = new List<string>();
                foreach (KeyValuePair<ulong, ulong> kv in _taxiNet)
                {
                    if (kv.Value == 0 || choices.Count >= MaxTaxiChoices)
                        continue; // 0 = the terminal the map was opened at
                    string n = TaxiMapName(kv.Key, _readSw.Elapsed.TotalMilliseconds < ReadBudgetMs);
                    if (n.Length > 0 && !choices.Contains(n))
                        choices.Add(n);
                }
                choices.Sort(StringComparer.OrdinalIgnoreCase);
                if (choices.Count > 0)
                    ta.MapChoices = choices;
            }

            if (ta.MapNameSure || ta.MapChoices == null || ta.Dest == null || ta.Dest.Name.Length == 0)
                return;
            foreach (string c in ta.MapChoices)
            {
                if (string.Equals(c, ta.Dest.Name, StringComparison.Ordinal))
                {
                    ta.MapName = c;
                    ta.MapNameSure = true;
                    ta.MapNameHow = "the landed-at terminal's name is on the taxi map";
                    return;
                }
            }
            string want = Fmt.LooseName(ta.Dest.Name);
            string found = null;
            foreach (string c in ta.MapChoices)
            {
                if (Fmt.LooseName(c) != want)
                    continue;
                if (found != null)
                    return; // two alike: do not guess
                found = c;
            }
            if (found != null)
            {
                ta.MapName = found;
                ta.MapNameSure = false;
                ta.MapNameHow = "guessed: the taxi-map name most like \"" + ta.Dest.Name + "\"";
            }
        }

        /// <summary>A terminal spec's taxi-map name: the game's own (when <paramref name="mayAsk"/>), else BuddyCron's table.</summary>
        private string TaxiMapName(ulong spec, bool mayAsk)
        {
            string name;
            if (_taxiNames.TryGetValue(spec, out name))
                return name;
            name = string.Empty;
            if (mayAsk)
            {
                try
                {
                    HeroTaxiTerminal t = HeroGuiApiGfx.GetTerminalFromSpec(spec);
                    if (t != null)
                        name = Fmt.OneLine(t.Name);
                }
                catch
                {
                    name = string.Empty;
                }
            }
            if (name.Length == 0)
            {
                try
                {
                    string n = Fmt.OneLine(TaxiTerminalTable.NameOf(spec));
                    string hex = spec.ToString("X");
                    if (n.Length > 0 && n.IndexOf(hex, StringComparison.OrdinalIgnoreCase) < 0)
                        name = n;
                }
                catch
                {
                    name = string.Empty;
                }
            }
            if (name.StartsWith("#", StringComparison.Ordinal))
                name = string.Empty;
            if ((name.Length > 0 || mayAsk) && _taxiNames.Count < 500)
                _taxiNames[spec] = name;
            return name;
        }

        // ------------------------------------------------------------------ mission boards

        // The board that is open now.
        private BoardInfo _openBoard;
        private int _boardRetries;

        private void ReadBoard(HeroLocalPlayer me, Snap prev, Snap s)
        {
            if (!s.BoardOpen)
            {
                _openBoard = null;
                return;
            }
            if (prev != null && prev.BoardOpen)
            {
                if (_openBoard != null && _boardRetries < 10 && (_openBoard.Name.Length == 0 || _openBoard.Missions.Count == 0))
                {
                    _boardRetries++;
                    string n = Fmt.OneLine(me.MissionBoardName);
                    if (n.Length > 0)
                        _openBoard.Name = n;
                    if (_openBoard.Missions.Count == 0)
                        foreach (MissionBoardQuest mq in me.MissionBoardQuests)
                            _openBoard.Missions.Add(new KeyValuePair<ulong, string>(mq.QuestId, Fmt.OneLine(mq.InternalName)));
                }
                return;
            }
            var b = new BoardInfo();
            _openBoard = b;
            _boardRetries = 0;
            b.Name = Fmt.OneLine(me.MissionBoardName);
            foreach (MissionBoardQuest mq in me.MissionBoardQuests)
                b.Missions.Add(new KeyValuePair<ulong, string>(mq.QuestId, Fmt.OneLine(mq.InternalName)));
            b.Terminal = RecentClick(s, 10000, "you clicked it") ??
                         NearestWhere(s, 5, o => o is HeroPlaceable, "nearest object (guess)");
            s.Board = b;
        }

        // ------------------------------------------------------------------ areas

        private void EnsureArea(ulong areaId)
        {
            if (areaId == 0 || _areas.ContainsKey(areaId))
                return;
            var a = new AreaInfo { Id = areaId };
            try
            {
                HeroMapOracle map = ExtraNodeManager.MapOracle;
                if (map != null)
                    a.Display = Fmt.OneLine(map.GetEnglishAreaDisplayName(areaId));
            }
            catch
            {
                a.Display = string.Empty;
            }
            try
            {
                HeroBaseClient bc = ExtraNodeManager.BaseClient;
                if (bc != null && bc.AreaId == areaId)
                    a.Internal = Fmt.OneLine(bc.AreaName);
            }
            catch
            {
                a.Internal = string.Empty;
            }
            try
            {
                a.Planet = Fmt.OneLine(WorldManager.PlanetName);
            }
            catch
            {
                a.Planet = string.Empty;
            }
            _areas[areaId] = a;
        }

        // ------------------------------------------------------------------ object helpers

        /// <summary>Clicks often land on a placeable's interaction prop (a nameless "#3BC453FE" node), not the placeable itself.</summary>
        private HeroObject PropOwner(HeroObject o)
        {
            if (o == null || o is HeroNPC || o is HeroPlaceable || o is HeroPlayer)
                return o;
            ulong id = o.NodeId;
            HeroObject owner;
            if (_propOwners.TryGetValue(id, out owner) && owner != null && owner.IsValid)
                return owner;
            // The prop names its owner in the game field plcPropOwner.
            HeroObject byField = OwnerFromField(o);
            if (byField != null)
            {
                if (_propOwners.Count > 500)
                    _propOwners.Clear();
                _propOwners[id] = byField;
                return byField;
            }
            try
            {
                foreach (HeroPlaceable p in HeroObjectManager.GetObjects<HeroPlaceable>())
                {
                    try
                    {
                        if (p != null && PropIdOf(p) == id)
                        {
                            if (_propOwners.Count > 500)
                                _propOwners.Clear();
                            _propOwners[id] = p;
                            return p;
                        }
                    }
                    catch
                    {
                        // one unreadable placeable must not stop the search
                    }
                }
            }
            catch
            {
                // no placeable list right now: keep the prop
            }
            // Last resort: the nearest named placeable to the prop (its scene position, else where you stand).
            HeroObject near = NearestNamedPlaceable(o);
            if (near != null)
            {
                if (_propOwners.Count > 500)
                    _propOwners.Clear();
                _propOwners[id] = near;
                return near;
            }
            return o;
        }

        private static HeroObject NearestNamedPlaceable(HeroObject prop)
        {
            try
            {
                Vector3 at;
                float max;
                if (prop.TryGetScenePosition(out at) && at != Vector3.Zero)
                {
                    max = 1f; // game units: the clickable part sits on its object
                }
                else
                {
                    var me = Core.Me;
                    if (me == null)
                        return null;
                    at = me.Location;
                    max = 6f; // you click things within reach
                }
                HeroObject best = null;
                float bestD = max * max;
                foreach (HeroPlaceable p in HeroObjectManager.GetObjects<HeroPlaceable>())
                {
                    try
                    {
                        if (p == null || !p.IsValid)
                            continue;
                        string n = p.Name;
                        if (string.IsNullOrEmpty(n) || n.StartsWith("#", StringComparison.Ordinal))
                            continue;
                        Vector3 pp;
                        if (!p.TryGetScenePosition(out pp))
                            pp = p.Location;
                        float d = Vector3.DistanceSquared(pp, at);
                        if (d < bestD)
                        {
                            bestD = d;
                            best = p;
                        }
                    }
                    catch
                    {
                        // skip unreadable placeables
                    }
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        private readonly Dictionary<ulong, HeroObject> _propOwners = new Dictionary<ulong, HeroObject>();

        private static bool _ownerFieldLookedUp;
        private static bool _haveOwnerField;
        private static GomFieldId _ownerField;

        private static HeroObject OwnerFromField(HeroObject prop)
        {
            try
            {
                if (!_ownerFieldLookedUp)
                {
                    _ownerFieldLookedUp = true;
                    _haveOwnerField = Enum.TryParse("plcPropOwner", out _ownerField);
                }
                if (!_haveOwnerField)
                    return null;
                ulong ownerId = 0;
                try
                {
                    ulong u;
                    if (prop.TryGetField<ulong>(_ownerField, out u))
                        ownerId = u;
                }
                catch
                {
                    try
                    {
                        long l;
                        if (prop.TryGetField<long>(_ownerField, out l))
                            ownerId = unchecked((ulong)l);
                    }
                    catch
                    {
                        ownerId = 0;
                    }
                }
                if (ownerId == 0)
                    return null;
                HeroObject owner = HeroObjectManager.GetByNodeId(ownerId);
                return owner != null && owner.IsValid ? owner : null;
            }
            catch
            {
                return null;
            }
        }

        // HeroPlaceable.PropNodeId / plcProp are not public to plugins: read them through reflection (0 when missing).
        private static System.Reflection.PropertyInfo _propIdProp;
        private static System.Reflection.PropertyInfo _propNodeProp;
        private static bool _propLookedUp;

        private static ulong PropIdOf(HeroPlaceable p)
        {
            try
            {
                if (!_propLookedUp)
                {
                    _propLookedUp = true;
                    const System.Reflection.BindingFlags f = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    _propIdProp = typeof(HeroPlaceable).GetProperty("PropNodeId", f);
                    _propNodeProp = typeof(HeroPlaceable).GetProperty("plcProp", f);
                }
                if (_propIdProp != null)
                {
                    object v = _propIdProp.GetValue(p);
                    if (v != null)
                        return Convert.ToUInt64(v);
                }
                if (_propNodeProp != null)
                {
                    var node = _propNodeProp.GetValue(p) as HeroObject;
                    if (node != null)
                        return node.NodeId;
                }
            }
            catch
            {
                // unreadable: no prop
            }
            return 0;
        }

        private ObjRef Describe(HeroObject o, string how)
        {
            if (o == null || !o.IsValid)
                return null;
            o = PropOwner(o);
            var r = new ObjRef();
            r.NodeId = o.NodeId;
            r.How = how ?? string.Empty;
            if (o is HeroPlayer)
            {
                r.Kind = ObjKind.Player;
                return r;
            }
            var npc = o as HeroNPC;
            var plc = o as HeroPlaceable;
            r.Kind = npc != null ? ObjKind.Npc : (plc != null ? ObjKind.Placeable : ObjKind.Other);
            r.Name = NameOf(o);
            r.Pos = V(o.Location);
            r.HasPos = !r.Pos.IsZero;
            if (npc != null || plc != null)
            {
                try
                {
                    ulong spec;
                    if (o.TryGetField<ulong>(GomFieldId.spnSpawnedSpec, out spec))
                        r.Spec = spec; // its type: what quest tasks list
                }
                catch
                {
                    r.Spec = 0;
                }
            }
            try
            {
                cnvQuestIndication qi = o.QuestIndication;
                r.Indication = qi.ToString();
                r.OffersQuest = qi == cnvQuestIndication.Grant;
                r.TakesTurnIn = qi == cnvQuestIndication.End;
            }
            catch
            {
                r.Indication = string.Empty;
            }
            try
            {
                r.IsTaxiTerminal = o.IsTaxiTerminal;
            }
            catch
            {
                r.IsTaxiTerminal = false;
            }
            if (plc != null)
            {
                r.UseRange = plc.UseRange;
                r.IsWonkavator = plc.IsWonkavator;
                if (r.IsWonkavator)
                {
                    r.Floor = plc.CurrentFloor;
                    IReadOnlyList<string> d = plc.WonkavatorDestinations;
                    if (d != null)
                    {
                        r.Destinations = new string[d.Count];
                        for (int i = 0; i < d.Count; i++)
                            r.Destinations[i] = Fmt.OneLine(d[i]);
                    }
                }
                try
                {
                    r.UsableForQuest = plc.IsUsableForActiveQuest;
                }
                catch
                {
                    r.UsableForQuest = false;
                }
                try
                {
                    r.Anchor = plc.ParentAnchorId; // stable across respawns
                }
                catch
                {
                    r.Anchor = 0;
                }
            }
            if (npc != null)
            {
                r.Level = npc.Level;
                r.Toughness = npc.Toughness.ToString();
                r.IsVendor = npc.IsVendor;
                try
                {
                    r.Dead = npc.IsDead;
                    r.Health = npc.HealthPercent;
                }
                catch
                {
                    r.Dead = false;
                }
            }
            return r;
        }

        private ObjRef NearestWhere(Snap s, float radius, Func<HeroObject, bool> match, string how)
        {
            HeroObject o = NearestObject(s, radius, match);
            return o != null ? Describe(o, how) : null;
        }

        private HeroObject NearestObject(Snap s, float radius, Func<HeroObject, bool> match)
        {
            foreach (KeyValuePair<float, HeroObject> kv in Nearby(s, radius))
            {
                bool ok;
                try
                {
                    ok = match(kv.Value);
                }
                catch
                {
                    ok = false;
                }
                if (ok)
                    return kv.Value;
            }
            return null;
        }

        /// <summary>Objects within radius game units (1 unit = 10 m), nearest first, as (squared distance, object).</summary>
        private List<KeyValuePair<float, HeroObject>> Nearby(Snap s, float radius)
        {
            var list = new List<KeyValuePair<float, HeroObject>>();
            if (_readSw.IsRunning && _readSw.Elapsed.TotalMilliseconds > NearbyStopMs)
                return list; // this look already took long enough: the search waits (callers treat it as "nothing near")
            float r2 = radius * radius;
            Stopwatch sw = Stopwatch.StartNew();
            int n = 0;
            foreach (HeroObject o in HeroObjectManager.Objects)
            {
                if (++n > NearbyMaxObjects || sw.ElapsedMilliseconds > NearbyBudgetMs)
                    break;
                if (o == null || o is HeroPlayer || o.NodeId == s.CompanionId)
                    continue;
                Vector3 p;
                if (!o.TryGetScenePosition(out p))
                    continue;
                float d2 = V(p).DistanceSq(s.Pos);
                if (d2 <= r2)
                    list.Add(new KeyValuePair<float, HeroObject>(d2, o));
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }

        private string NameOf(HeroObject o)
        {
            string name;
            if (_names.TryGetValue(o.NodeId, out name))
                return name;
            try
            {
                name = Fmt.OneLine(o.Name);
            }
            catch
            {
                name = string.Empty;
            }
            if (_names.Count >= MaxNames)
                _names.Clear();
            if (!string.IsNullOrEmpty(name) && !name.StartsWith("#", StringComparison.Ordinal))
                _names[o.NodeId] = name;
            return name;
        }

        private static Vec3 V(Vector3 v)
        {
            return new Vec3(v.X, v.Y, v.Z);
        }
    }
}
