// Quest Recorder - made by LethToxic.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BuddyCron;
using BuddyCron.Objects;
using BuddyCron.Profiles;
using Reborn.Behaviors.Coroutines;
using Reborn.Behaviors.Treesharp;
using Reborn.Utilities.XmlEngine;

namespace QuestBehaviors
{
    /// <summary>
    /// &lt;UseMissionItemOnce ItemName="..." /&gt; uses a mission item (or bag item) one time and moves on. For quests that need
    /// several items used in order with nothing ticking in between (the Sith runes in the Shadow Spawn heroic).
    /// </summary>
    [XmlElement("UseMissionItemOnce")]
    public class UseMissionItemOnceTag : ProfileBehavior
    {
        private bool _done;

        [XmlAttribute("ItemName")]
        public string ItemName { get; set; } = string.Empty;

        /// <summary>Extra wait after the use, in milliseconds.</summary>
        [XmlAttribute("WaitTime")]
        public int WaitTime { get; set; } = 1000;

        public override bool IsDone => _done;

        public override string StatusText => "Using " + ItemName + " once";

        protected override void OnResetCachedDone()
        {
            _done = false;
        }

        private HeroItem FindItem()
        {
            HeroItem item = Core.Me!.QuestItems.FirstOrDefault(i => i.Name == ItemName);
            return item ?? Core.Me!.Backpack.FirstOrDefault(i => i.Name == ItemName);
        }

        protected override Composite CreateBehavior()
        {
            return new ActionRunCoroutine(UseOnce);
        }

        private async Task<bool> UseOnce(object arg)
        {
            HeroItem item = FindItem();
            if (item == null)
            {
                Log("Item " + ItemName + " isn't in your mission items or bags - skipping it.");
                _done = true;
                return true;
            }
            var player = Core.Player!;
            for (int press = 0; press < 3; press++)
            {
                ulong before = player.CastingAbilitySpecId;
                item.Use();
                Log("Using item " + ItemName);
                if (item.Ability is { } ability && ability.CastingTime <= 0f && ability.ChannelingTime <= 0f)
                    break; // instant: nothing to wait for
                if (await Coroutine.Wait(1500, () => player.CastingAbilitySpecId != 0 && player.CastingAbilitySpecId != before))
                {
                    ulong cast = player.CastingAbilitySpecId;
                    await Coroutine.Wait(15000, () => player.CastingAbilitySpecId != cast);
                    break;
                }
                Log("Using " + ItemName + " didn't start; pressing again.");
            }
            if (WaitTime > 0)
                await Coroutine.Sleep(WaitTime);
            _done = true;
            return true;
        }
    }
}
