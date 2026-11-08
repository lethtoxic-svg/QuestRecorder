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

        /// <summary>A weekly (3 times a week since 7.0): the game calls it "complete" after the first one that week, so
        /// "complete" doesn't count as done - it's picked up whenever the giver offers it (and given up on when it doesn't).</summary>
        [XmlAttribute("Repeatable")]
        public bool Repeatable { get; set; }

        public override bool IsDone => _gaveUp || HasQuest || (IsQuestComplete && !Repeatable);

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
                // everything else is BuddyCron's PickUpQuest, as it is
                PickUpBehavior());
        }

        private Stopwatch _sinceClick;

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
                if (kept == null)
                    return;
                // stale (gone, or not where the giver stands) -> look again
                if (!kept.IsValid || kept.Location.DistanceSqr(Position) > 4f) // more than 20 m from the giver's spot
                    _giverField.SetValue(_pickUp, null);
            }
            catch
            {
                // leave BuddyCron's lookup as it is
            }
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
