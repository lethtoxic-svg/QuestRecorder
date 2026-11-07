// Quest Recorder - made by LethToxic.
using System;
using System.Linq;
using System.Threading.Tasks;
using BuddyCron;
using BuddyCron.Objects;
using BuddyCron.Profiles;
using BuddyCron.Scripting;
using Reborn.Behaviors.Coroutines;
using Reborn.Behaviors.Treesharp;
using Reborn.Utilities.XmlEngine;

namespace QuestBehaviors
{
    /// <summary>
    /// &lt;EquipItem ItemName="..." /&gt; puts on an item from your bags (a quest disguise), like you did: right-click
    /// equips it. Done when it's on, or when it isn't in your bags.
    /// </summary>
    [XmlElement("EquipItem")]
    public class EquipItemTag : ProfileBehavior
    {
        private bool _done;

        [XmlAttribute("ItemName")]
        public string ItemName { get; set; } = string.Empty;

        [XmlAttribute("WaitTime")]
        public int WaitTime { get; set; } = 1500;

        public override bool IsDone => _done;

        public override string StatusText => "Putting on " + ItemName;

        protected override void OnResetCachedDone()
        {
            _done = false;
        }

        protected override Composite CreateBehavior()
        {
            return new ActionRunCoroutine(EquipOnce);
        }

        private async Task<bool> EquipOnce(object arg)
        {
            if (ConditionParser.HasItemEquipped(ItemName))
            {
                _done = true;
                return true;
            }
            HeroItem item = Core.Me!.Backpack.FirstOrDefault(i => i.Name == ItemName);
            if (item == null)
            {
                Log("Item " + ItemName + " isn't in your bags - skipping it.");
                _done = true;
                return true;
            }
            Log("Putting on " + ItemName);
            item.Interact();
            await Coroutine.Wait(Math.Max(1000, WaitTime), () => ConditionParser.HasItemEquipped(ItemName));
            _done = true;
            return true;
        }
    }
}
