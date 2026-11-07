// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BuddyCron;
using BuddyCron.Behaviors;
using BuddyCron.Helpers;
using BuddyCron.Managers;
using BuddyCron.Objects;
using BuddyCron.Profiles;
using Reborn.Behaviors.Coroutines;
using Reborn.Behaviors.Treesharp;
using Reborn.Utilities.XmlEngine;

namespace QuestBehaviors
{
    /// <summary>
    /// &lt;UseQuestAbility AbilityName="..." /&gt; - like UseItem, for an ability a quest gives you on a special / temporary
    /// bar (Macrobinoculars, the Seeker Droid): at the spots you used it, on the NPCs you used it on (&lt;Npcs&gt;), from
    /// Range metres, until the mission moves. OpenWith="item" uses that mission item first when the bar isn't up.
    /// </summary>
    [XmlElement("UseQuestAbility")]
    public class UseQuestAbilityTag : HuntBehavior
    {
        [XmlAttribute("AbilityName")]
        public string AbilityName { get; set; } = string.Empty;

        /// <summary>The mission item that opens the bar (Macrobinoculars), used first when the ability isn't there.</summary>
        [XmlAttribute("OpenWith")]
        public string OpenWith { get; set; } = string.Empty;

        [XmlAttribute("DeadOnly")]
        public bool DeadOnly { get; set; }

        public override bool IsDone
        {
            get
            {
                if (IsQuestComplete)
                    return true;
                if (IsTaskComplete)
                    return true;
                return IsStepComplete;
            }
        }

        public override string StatusText => "Using " + AbilityName + " for quest " + QuestName;

        private const BlacklistFlags UseFlag = BlacklistFlags.UseItem;

        protected override HeroObject? GetTargetObject()
        {
            if (Names.Count == 0)
                return null;
            float num = Radius == 0f ? 0.5f : Radius / 10f;
            foreach (HeroCharacter c in HeroObjectManager.GetObjects<HeroCharacter>().OrderBy(c => c.DistanceSqr))
            {
                if (!Names.Contains(c.Name) || Blacklist.Contains(c.NodeId, UseFlag) || c.IsDead != DeadOnly)
                    continue;
                if (!DeadOnly && !(c.HealthPercent <= UseHealthPercent || (InCombat && c.Demeanor != cbtDemeanorEnum.Friendly)))
                    continue;
                if (c.Distance < 1f || System.Numerics.Vector3.Distance(c.Location, Position) < num)
                    return c;
            }
            return null;
        }

        protected override Composite DoHuntThing()
        {
            return new ActionRunCoroutine(ctx => UseOn(ctx as HeroCharacter));
        }

        protected override Composite? AtHotspot()
        {
            return new Decorator(ret => Names.Count == 0, new ActionRunCoroutine(ctx => UseOn(null)));
        }

        private async Task<bool> OpenBar()
        {
            if (AbilityManager.HasAbility(AbilityName) || OpenWith.Length == 0)
                return AbilityManager.HasAbility(AbilityName);
            HeroItem item = Core.Me!.QuestItems.FirstOrDefault(i => i.Name == OpenWith) ?? Core.Me!.Backpack.FirstOrDefault(i => i.Name == OpenWith);
            if (item == null)
            {
                Log("Item " + OpenWith + " (it opens " + AbilityName + ") isn't in your mission items or bags.");
                return false;
            }
            Log("Using " + OpenWith + " to get " + AbilityName);
            item.Use();
            await Coroutine.Wait(5000, () => AbilityManager.HasAbility(AbilityName));
            return AbilityManager.HasAbility(AbilityName);
        }

        private async Task<bool> UseOn(HeroCharacter? target)
        {
            if (target == null && Names.Count > 0)
                return false;
            if (!await OpenBar())
            {
                Log(AbilityName + " isn't on your bars.");
                await Coroutine.Sleep(1000);
                return false;
            }
            var player = Core.Player!;
            ulong before = player.CastingAbilitySpecId;
            var r = AbilityManager.Cast(AbilityName, target ?? (HeroCharacter)player, false);
            if (!r.Success)
            {
                Log("Using " + AbilityName + (target != null ? " on " + target : string.Empty) + " refused: " + r.Result + ".");
                await Coroutine.Sleep(300);
                return false;
            }
            Log(target != null ? "Using " + AbilityName + " on " + target : "Using " + AbilityName);
            if (await Coroutine.Wait(1000, () => player.CastingAbilitySpecId != 0 && player.CastingAbilitySpecId != before))
            {
                ulong cast = player.CastingAbilitySpecId;
                await Coroutine.Wait(15000, () => player.CastingAbilitySpecId != cast);
            }
            if (target != null)
                Blacklist.Add(target, UseFlag, TimeSpan.FromDays(60.0));
            if (WaitTime > 0)
                await Coroutine.Sleep(WaitTime);
            return true;
        }
    }
}
