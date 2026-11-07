// Quest Recorder - made by LethToxic.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BuddyCron;
using BuddyCron.Behaviors;
using BuddyCron.Managers;
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
    /// With Npc="name" it is used ON that NPC (the nearest one of that name), from Range metres like you did;
    /// DeadOnly="True" for a corpse.
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

        /// <summary>The NPC you used it on (empty = on yourself / where you stand).</summary>
        [XmlAttribute("Npc")]
        public string Npc { get; set; } = string.Empty;

        /// <summary>How close you stood to that NPC, in metres (like UseItem's Range).</summary>
        [XmlAttribute("Range")]
        public float RangeMetres { get; set; } = 15f;

        /// <summary>The NPC was a corpse.</summary>
        [XmlAttribute("DeadOnly")]
        public bool DeadOnly { get; set; }

        public override bool IsDone => _done;

        public override string StatusText => "Using " + ItemName + " once" + (Npc.Length > 0 ? " on " + Npc : string.Empty);

        protected override void OnResetCachedDone()
        {
            _done = false;
        }

        private HeroItem FindItem()
        {
            HeroItem item = Core.Me!.QuestItems.FirstOrDefault(i => i.Name == ItemName);
            return item ?? Core.Me!.Backpack.FirstOrDefault(i => i.Name == ItemName);
        }

        private HeroCharacter FindNpc()
        {
            return HeroObjectManager.GetObjects<HeroCharacter>()
                .Where(c => c != null && c.IsValid && c.Name == Npc && c.IsDead == DeadOnly)
                .OrderBy(c => c.DistanceSqr)
                .FirstOrDefault();
        }

        protected override Composite CreateBehavior()
        {
            return new ActionRunCoroutine(UseOnce);
        }

        /// <summary>A quest ability from a special / temporary bar instead of an item (in a row of uses).</summary>
        [XmlAttribute("AbilityName")]
        public string AbilityName { get; set; } = string.Empty;

        /// <summary>The mission item that opens that ability's bar, used first when the ability isn't there.</summary>
        [XmlAttribute("OpenWith")]
        public string OpenWith { get; set; } = string.Empty;

        private async Task<bool> CastOnce()
        {
            var player = Core.Player!;
            if (!AbilityManager.HasAbility(AbilityName) && OpenWith.Length > 0)
            {
                HeroItem opener = Core.Me!.QuestItems.FirstOrDefault(i => i.Name == OpenWith) ?? Core.Me!.Backpack.FirstOrDefault(i => i.Name == OpenWith);
                if (opener != null)
                {
                    opener.Use();
                    await Coroutine.Wait(5000, () => AbilityManager.HasAbility(AbilityName));
                }
            }
            if (!AbilityManager.HasAbility(AbilityName))
            {
                Log(AbilityName + " isn't on your bars - skipping it.");
                _done = true;
                return true;
            }
            HeroCharacter target = null;
            if (Npc.Length > 0)
            {
                target = FindNpc();
                if (target == null)
                {
                    Log("No " + Npc + " near you to use " + AbilityName + " on - skipping it.");
                    _done = true;
                    return true;
                }
                float range = RangeMetres >= 1f ? RangeMetres / 10f : RangeMetres;
                var walk = Stopwatch.StartNew();
                while (target.IsValid && target.Distance > range && walk.Elapsed.TotalSeconds < 30)
                {
                    HeroCharacter t = target;
                    await CommonTasks.MoveToAndStopAsync(t.Location, Npc, false, () => !t.IsValid || t.Distance <= range, Math.Max(0.1f, range * 0.8f));
                    await Coroutine.Yield();
                }
            }
            ulong before = player.CastingAbilitySpecId;
            var r = AbilityManager.Cast(AbilityName, target ?? (HeroCharacter)player, false);
            Log(r.Success ? "Using " + AbilityName + (target != null ? " on " + Npc : string.Empty) : "Using " + AbilityName + " refused: " + r.Result + ".");
            if (r.Success && await Coroutine.Wait(1000, () => player.CastingAbilitySpecId != 0 && player.CastingAbilitySpecId != before))
            {
                ulong cast = player.CastingAbilitySpecId;
                await Coroutine.Wait(15000, () => player.CastingAbilitySpecId != cast);
            }
            if (WaitTime > 0)
                await Coroutine.Sleep(WaitTime);
            _done = true;
            return true;
        }

        private async Task<bool> UseOnce(object arg)
        {
            if (AbilityName.Length > 0)
                return await CastOnce();
            HeroItem item = FindItem();
            if (item == null)
            {
                Log("Item " + ItemName + " isn't in your mission items or bags - skipping it.");
                _done = true;
                return true;
            }
            HeroCharacter target = null;
            if (Npc.Length > 0)
            {
                target = FindNpc();
                if (target == null)
                {
                    Log("No " + Npc + (DeadOnly ? " corpse" : string.Empty) + " near you to use " + ItemName + " on - skipping it.");
                    _done = true;
                    return true;
                }
                // get as close as you stood (game units: 1 = 10 m)
                float range = RangeMetres >= 1f ? RangeMetres / 10f : RangeMetres;
                var walk = Stopwatch.StartNew();
                while (target.IsValid && target.Distance > range && walk.Elapsed.TotalSeconds < 30)
                {
                    HeroCharacter t = target;
                    await CommonTasks.MoveToAndStopAsync(t.Location, Npc, false, () => !t.IsValid || t.Distance <= range, Math.Max(0.1f, range * 0.8f));
                    await Coroutine.Yield();
                }
            }
            var player = Core.Player!;
            for (int press = 0; press < 3; press++)
            {
                ulong before = player.CastingAbilitySpecId;
                if (target != null)
                {
                    var (accepted, result) = item.UseAbility(target);
                    if (!accepted)
                    {
                        Log("Using " + ItemName + " on " + Npc + " refused: " + result + ".");
                        await Coroutine.Sleep(300);
                        continue;
                    }
                    Log("Using item " + ItemName + " on " + Npc);
                }
                else
                {
                    item.Use();
                    Log("Using item " + ItemName);
                }
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
