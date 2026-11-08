// Quest Recorder - made by LethToxic.
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using BuddyCron;
using BuddyCron.Managers;
using BuddyCron.Objects.Oracle;
using BuddyCron.Profiles;
using Reborn.Behaviors.Coroutines;
using Reborn.Behaviors.Treesharp;
using Reborn.Utilities.Math;
using Reborn.Utilities.XmlEngine;

namespace QuestBehaviors
{
    /// <summary>
    /// &lt;PickUpIfOffered QuestId="..." GiverName="..." XYZ="..." /&gt; runs BuddyCron's own PickUpQuest (so boards, NPC
    /// quest windows and conversations are handled exactly the way BuddyCron does it) and only adds one thing: if the
    /// giver doesn't give the quest (a daily or heroic you already did today), it gives up instead of clicking forever.
    /// </summary>
    [XmlElement("PickUpIfOffered")]
    public class PickUpIfOfferedTag : ProfileBehavior
    {
        private PickupQuestTag _pickUp;
        private bool _gaveUp;
        private Stopwatch _atGiver;

        [XmlAttribute("GiverName")]
        [XmlAttribute("Name")]
        public string ObjectName { get; set; } = string.Empty;

        [XmlAttribute("XYZ")]
        public Vector3 Position { get; set; }

        [XmlAttribute("UseRange")]
        [DefaultValue(20f)]
        public float ProfileUseRange { get; set; } = 20f;

        [XmlAttribute("SearchRadius")]
        [DefaultValue(20f)]
        public float ProfileSearchRadius { get; set; } = 20f;

        /// <summary>How many goes at the giver before giving up (about 10 seconds each, never during a conversation).</summary>
        [XmlAttribute("Tries")]
        public int Tries { get; set; } = 3;

        /// <summary>Kept so older profiles still load. Every pickup now works this way: if the giver offers the mission, it's
        /// taken - weekly or daily, whatever the game says about "complete" from an earlier run.</summary>
        [XmlAttribute("Repeatable")]
        public bool Repeatable { get; set; }

        // Done = in the log, or the giver doesn't offer it. "Complete" from an earlier run doesn't count (a weekly stays
        // "complete" all week after the first run, but the board offers it again).
        public override bool IsDone => _gaveUp || HasQuest;

        public override string StatusText => "Picking up " + QuestName + " from " + ObjectName + " (if it's offered)";

        protected override void OnResetCachedDone()
        {
            _gaveUp = false;
            _atGiver = null;
            _pickUp = null;
            _sinceClick = null;
        }

        /// <summary>BuddyCron's own PickUpQuest, set up with this step's giver and quest.</summary>
        private PickupQuestTag PickUp()
        {
            if (_pickUp == null)
                _pickUp = new PickupQuestTag
                {
                    ObjectName = ObjectName,
                    Position = Position,
                    ProfileUseRange = ProfileUseRange,
                    ProfileSearchRadius = ProfileSearchRadius,
                    QuestId = QuestId,
                    IgnoreLOS = true, // BuddyCron's default for PickUpQuest in a profile
                };
            return _pickUp;
        }

        protected override Composite CreateBehavior()
        {
            return new PrioritySelector(
                // BuddyCron's PickUpQuest looks the giver up once and keeps it. Looked up from the other side of a lift
                // (Black Hole: the terminal floor), that copy goes stale and the bot stands next to the real Recon Droid
                // doing nothing. Forget a stale or far-away copy so it looks again from here.
                new Action(ret => { ForgetStaleGiver(); return RunStatus.Failure; }),
                // the give-up: at the giver, no conversation going on, and still no quest after the tries -> move on
                new Decorator(ret => ShouldGiveUp(), new ActionRunCoroutine(GiveUp)),
                // at the giver: one click, then up to 3 s for the board to open. BuddyCron's PickUpQuest clicks again
                // every 0.3 s, and a mission board takes almost a second to open, so each click cancelled the one before
                // and the board never opened (Black Hole terminal, BuddyCron 1.0.76: 27 clicks in 10 s).
                new Decorator(ret => WaitForBoard(), new Action(ret => RunStatus.Success)),
                // the board is open and this mission isn't on it (done today, or this week's runs used up): skip it now
                new Decorator(ret => BoardOpenWithout(), new ActionRunCoroutine(GiveUp)),
                // everything else is BuddyCron's PickUpQuest, as it is
                PickUpBehavior());
        }

        private Stopwatch _sinceClick;
        private Stopwatch _boardOpen;

        /// <summary>True when a mission board is open (for a second, so its list has filled in) and this mission isn't on it.</summary>
        private bool BoardOpenWithout()
        {
            try
            {
                if (HasQuest || Core.Player == null || !Core.Player.MissionBoardOpen)
                {
                    _boardOpen = null;
                    return false;
                }
                if (_boardOpen == null)
                    _boardOpen = Stopwatch.StartNew();
                if (_boardOpen.ElapsedMilliseconds < 1000)
                    return false;
                var list = Core.Player.MissionBoardQuests;
                if (list == null)
                    return false;
                int n = 0;
                foreach (var mq in list)
                {
                    if (mq == null)
                        continue;
                    ulong id = BoardId(mq);
                    if (id == 0)
                        continue; // can't tell what this entry is
                    n++;
                    if (id == QuestId)
                        return false; // it's there: PickUpQuest takes it
                }
                return n > 0; // a list we could read, without this mission (an unreadable list never skips)
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The quest id of a board entry (its own 0xE000... id field; the definition's id as a fallback).</summary>
        private static ulong BoardId(object mq)
        {
            ulong id = 0;
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (System.Reflection.FieldInfo fi in mq.GetType().GetFields(all))
                if (fi.FieldType == typeof(ulong)) { ulong v = (ulong)fi.GetValue(mq); if ((v >> 48) == 0xE000) id = v; }
            foreach (System.Reflection.PropertyInfo pi in mq.GetType().GetProperties(all))
                if (pi.PropertyType == typeof(ulong) && pi.GetIndexParameters().Length == 0) { try { ulong v = (ulong)pi.GetValue(mq); if ((v >> 48) == 0xE000) id = v; } catch { } }
            return id;
        }

        /// <summary>True while a click at the giver is still waiting for its board / quest window (don't click again yet).</summary>
        private bool WaitForBoard()
        {
            if (Core.Me == null || Core.Player == null)
                return false;
            if (Core.Player.MissionBoardOpen || Core.Player.LightweightQuestOffer != null)
                return false; // open: let PickUpQuest take the mission
            HeroCnvOracle cnv = ExtraNodeManager.ConvoOracle;
            if (cnv != null && cnv.IsInConversation)
                return false; // a conversation: PickUpQuest handles it
            // only once a click can really happen: the giver found and within the game's interact range (the same test
            // BuddyCron's walk-and-interact uses) - before that, never hold up the walk
            bool inReach = false;
            try
            {
                var giver = PickUp().Object;
                inReach = giver != null && giver.IsValid && giver.IsWithinInteractRange;
            }
            catch
            {
                inReach = false;
            }
            if (!inReach)
            {
                _sinceClick = null; // still walking there
                return false;
            }
            if (_sinceClick != null && _sinceClick.ElapsedMilliseconds < 3000)
                return true; // clicked just now: give the board time to open
            _sinceClick = Stopwatch.StartNew(); // this pass is the click
            return false;
        }

        /// <summary>
        /// BuddyCron's PickUpQuest behaviour tree, built by its own CreateBehavior (its Behavior property is only filled
        /// in when BuddyCron starts that tag itself, so asking for it here gave nothing and the bot stood still).
        /// </summary>
        private Composite PickUpBehavior()
        {
            var make = typeof(PickupQuestTag).GetMethod("CreateBehavior", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var tree = make != null ? make.Invoke(PickUp(), null) as Composite : null;
            return tree ?? PickUp().Behavior;
        }

        private static System.Reflection.FieldInfo _giverField;

        private void ForgetStaleGiver()
        {
            try
            {
                if (_pickUp == null)
                    return;
                if (_giverField == null)
                    foreach (System.Reflection.FieldInfo f in typeof(PickupQuestTag).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                        if (f.Name.IndexOf("<Object>", System.StringComparison.Ordinal) >= 0 && typeof(BuddyCron.Objects.HeroObject).IsAssignableFrom(f.FieldType))
                            _giverField = f;
                if (_giverField == null)
                    return;
                var kept = _giverField.GetValue(_pickUp) as BuddyCron.Objects.HeroObject;
                // stale (gone, or not where the giver stands) -> look again
                if (kept != null && (!kept.IsValid || kept.Location.DistanceSqr(Position) > 4f)) // more than 20 m from the giver's spot
                {
                    _giverField.SetValue(_pickUp, null);
                    kept = null;
                }
                // Two givers with the same name at the same spot (Black Hole: two "Black Hole Mission Terminal"s 7 cm
                // apart, only one has missions): BuddyCron takes whichever it finds first. Take the one the game marks
                // as giving something, nearest the recorded spot.
                BuddyCron.Objects.HeroObject best = BestGiver();
                if (best != null && (kept == null || kept.NodeId != best.NodeId))
                    _giverField.SetValue(_pickUp, best);
            }
            catch
            {
                // leave BuddyCron's lookup as it is
            }
        }

        /// <summary>Of the objects with the giver's name within 30 m of the recorded spot: the one the game marks as
        /// giving a mission (any quest marker), else the one nearest the recorded spot. Null when there's only one or none.</summary>
        private BuddyCron.Objects.HeroObject BestGiver()
        {
            BuddyCron.Objects.HeroObject marked = null, nearest = null;
            int count = 0;
            foreach (BuddyCron.Objects.HeroObject o in HeroObjectManager.GetObjects<BuddyCron.Objects.HeroObject>())
            {
                if (o == null || !o.IsValid || o.Name != ObjectName || o.Location.DistanceSqr(Position) > 9f)
                    continue;
                count++;
                if (nearest == null || o.Location.DistanceSqr(Position) < nearest.Location.DistanceSqr(Position))
                    nearest = o;
                string ind = string.Empty;
                try { ind = o.QuestIndication.ToString(); } catch { }
                if (ind.Length > 0 && ind != "None" && (marked == null || o.Location.DistanceSqr(Position) < marked.Location.DistanceSqr(Position)))
                    marked = o;
            }
            if (count < 2)
                return null;
            return marked ?? nearest;
        }

        private bool ShouldGiveUp()
        {
            if (HasQuest || Core.Me == null)
                return false;
            bool atGiver = Core.Me.Location.DistanceSqr(Position) <= (ProfileUseRange / 10f + 3f) * (ProfileUseRange / 10f + 3f);
            HeroCnvOracle cnv = ExtraNodeManager.ConvoOracle;
            bool talking = cnv != null && cnv.IsInConversation;
            if (!atGiver || talking)
            {
                if (talking && _atGiver != null)
                    _atGiver.Restart(); // a conversation gets all the time it needs
                return false;
            }
            if (_atGiver == null)
                _atGiver = Stopwatch.StartNew();
            // at least 45 s: BuddyCron's PickUpQuest waits up to 30 s at the spot for the giver to show up before it
            // looks again - giving up sooner (as before) cut it off just before it found the Recon Droid
            return _atGiver.ElapsedMilliseconds > System.Math.Max(10000L * Tries, 45000L);
        }

        private Task<bool> GiveUp(object arg)
        {
            Log(ObjectName + " isn't giving " + QuestName + " right now (done already today?) - skipping it.");
            _gaveUp = true;
            return Task.FromResult(true);
        }
    }
}
