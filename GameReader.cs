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
        private const double NearbyStopMs = 15.0;
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
        }

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
            Part("taxi map", () => ReadTaxiMap(s));
            Part("movement", () => ReadMovement(me, prev, afterLoading, s));
            Part("mission board", () => ReadBoard(me, prev, s));
            return s;
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
                    si.Tasks.Add(ti);
                }
            }
            si.Resolved = true;
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
                    if (pos.IsZero || pos.DistanceTo(clicked.Pos) > 80f)
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
            s.ConvoPartner = GuessPartner(s, cfg.PartnerRadius);
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
                if (t != null && (t.Kind == ObjKind.Npc || t.Kind == ObjKind.Placeable) && (!t.HasPos || t.Pos.DistanceTo(s.Pos) <= 15f))
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
                if (kv.Value is HeroNPC && kv.Key <= 25f)
                    return Describe(kv.Value, "nearest NPC (guess)");
            }
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Value is HeroPlaceable && kv.Key <= 16f)
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
                        r = NearestWhere(s, 8, o => o.IsTaxiTerminal, "nearest taxi terminal (guess)");
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
                        r = NearestWhere(s, 8, o => { var p = o as HeroPlaceable; return p != null && p.IsWonkavator; }, "nearest elevator (guess)") ?? r;
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
                        r = NearestWhere(s, 6, o => o is HeroNPC, "nearest NPC (guess)");
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
                if (s.TargetId != 0 && s.TargetId != s.PlayerId && s.TargetId != s.CompanionId)
                    s.CastTarget = Describe(HeroObjectManager.GetByNodeId(s.TargetId), "your target");
            }
            else if (!s.InCombat)
            {
                // Clicking a quest object usually starts a channel.
                s.CastTarget = CastObject(s);
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
                if (t != null && t.Kind == ObjKind.Placeable && (!t.HasPos || t.Pos.DistanceTo(s.Pos) <= 10f))
                    return t;
            }
            List<KeyValuePair<float, HeroObject>> near = Nearby(s, 8f);
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
                float lim = Math.Max(range, 3f) + 1.5f;
                if (usable && kv.Key <= lim * lim)
                    return Describe(p, "nearest object usable for a quest when the cast started (inferred from the cast)");
            }
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Value is HeroPlaceable && kv.Key <= 25f)
                    return Describe(kv.Value, "nearest object when the cast started (guess from the cast)");
            }
            // Last try: the nearest interaction prop, named after the placeable it belongs to.
            foreach (KeyValuePair<float, HeroObject> kv in near)
            {
                if (kv.Key > 25f)
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
            if (s.Tick - _bagQuestItemsTick > 5000)
                RefreshBagQuestItems(me, s.Tick);
            int n = 0;
            foreach (HeroItem item in me.QuestItems)
            {
                if (item == null || ++n > 60)
                    break;
                CheckItemUsed(item, s);
            }
            foreach (HeroItem item in _bagQuestItems)
                CheckItemUsed(item, s);
        }

        /// <summary>
        /// An item was used if its cooldown just started (a scanner), or its stack just went down while you still
        /// have some (collars, probes, charges that get used up).
        /// </summary>
        private void CheckItemUsed(HeroItem item, Snap s)
        {
            string name;
            float cd;
            int stack;
            try
            {
                name = Fmt.OneLine(item.Name);
                HeroAbility a = item.Ability;
                cd = a != null ? a.CooldownRemaining : 0f;
                stack = item.StackCount;
            }
            catch
            {
                return;
            }
            if (name.Length == 0)
                return;
            float before;
            bool known = _itemCooldown.TryGetValue(name, out before);
            _itemCooldown[name] = cd;
            int stackBefore;
            bool knownStack = _itemStack.TryGetValue(name, out stackBefore);
            _itemStack[name] = stack;
            bool cooldownStarted = known && before <= 0.05f && cd > 0.5f;
            bool usedUp = knownStack && stack > 0 && stack < stackBefore;
            if ((cooldownStarted || usedUp) && s.QuestItemName != name && s.InstantItemName == null)
            {
                s.InstantItemName = name;
                if (s.TargetId != 0 && s.TargetId != s.PlayerId && s.TargetId != s.CompanionId)
                    s.CastTarget = Describe(HeroObjectManager.GetByNodeId(s.TargetId), "your target");
            }
        }

        /// <summary>Quest items in your bags: plot items you can right-click (not medpacs, gear or other stuff).</summary>
        private void RefreshBagQuestItems(HeroLocalPlayer me, long now)
        {
            _bagQuestItemsTick = now;
            var list = new List<HeroItem>();
            try
            {
                int n = 0;
                foreach (HeroItem item in me.Backpack)
                {
                    if (item == null || ++n > 300)
                        break;
                    try
                    {
                        if (item.IsPlotItem && item.IsUsable && list.Count < 30)
                            list.Add(item);
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
            _bagQuestItems = list;
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
                HeroObject destObj = NearestObject(s, 40, o => o.IsTaxiTerminal);
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
            foreach (KeyValuePair<float, HeroObject> kv in Nearby(s, 10))
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
                    max = 4f;
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
            }
            if (npc != null)
            {
                r.Level = npc.Level;
                r.Toughness = npc.Toughness.ToString();
                r.IsVendor = npc.IsVendor;
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

        /// <summary>Objects within radius metres, nearest first, as (squared distance, object).</summary>
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
