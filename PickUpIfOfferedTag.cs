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

        public override bool IsDone => _gaveUp || IsQuestComplete || HasQuest;

        public override string StatusText => "Picking up " + QuestName + " from " + ObjectName + " (if it's offered)";

        protected override void OnResetCachedDone()
        {
            _gaveUp = false;
            _atGiver = null;
            _pickUp = null;
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
                };
            return _pickUp;
        }

        protected override Composite CreateBehavior()
        {
            return new PrioritySelector(
                // the give-up: at the giver, no conversation going on, and still no quest after the tries -> move on
                new Decorator(ret => ShouldGiveUp(), new ActionRunCoroutine(GiveUp)),
                // everything else is BuddyCron's PickUpQuest, as it is
                PickUpBehavior());
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
            return _atGiver.ElapsedMilliseconds > 10000L * Tries;
        }

        private Task<bool> GiveUp(object arg)
        {
            Log(ObjectName + " isn't giving " + QuestName + " right now (done already today?) - skipping it.");
            _gaveUp = true;
            return Task.FromResult(true);
        }
    }
}
