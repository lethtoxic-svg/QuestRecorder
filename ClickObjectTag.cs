// Quest Recorder - made by LethToxic.
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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
    /// &lt;ClickObject Name="..." XYZ="..." /&gt; clicks the ONE object with that name at that spot, once, then moves on.
    /// BuddyCron's Interact / UseObject take the object of that name nearest to the bot and skip one they clicked for
    /// 30 s, so a row of clicks on objects standing side by side (the Torvix Pipeline Controls) can't be done in your
    /// order with them. This one goes by the spot you clicked, like you did.
    /// </summary>
    [XmlElement("ClickObject")]
    public class ClickObjectTag : ProfileBehavior
    {
        private bool _done;

        [XmlAttribute("Name")]
        public string Name { get; set; } = string.Empty;

        [XmlAttribute("XYZ")]
        public Vector3 Location { get; set; }

        /// <summary>Extra wait after the click, in milliseconds.</summary>
        [XmlAttribute("WaitTime")]
        public int WaitTime { get; set; } = 1000;

        public override bool IsDone => _done;

        public override string StatusText => "Clicking " + Name;

        protected override void OnResetCachedDone()
        {
            _done = false;
        }

        private HeroPlaceable FindObject()
        {
            // the one of that name nearest the spot you clicked (within ~10 m of it)
            return HeroObjectManager.GetObjects<HeroPlaceable>()
                .Where(p => p != null && p.IsValid && p.Name == Name && Vector3.Distance(p.Location, Location) <= 1f)
                .OrderBy(p => Vector3.Distance(p.Location, Location))
                .FirstOrDefault();
        }

        protected override Composite CreateBehavior()
        {
            return new ActionRunCoroutine(ClickOnce);
        }

        private int _walks;

        private async Task<bool> ClickOnce(object arg)
        {
            var player = Core.Player!;
            // walk to the spot first: the object may only show up when you're close
            if (Vector3.Distance(player.Location, Location) > 2f)
            {
                if (++_walks > 5)
                {
                    Log("Couldn't get to " + Name + " - skipping it.");
                    _walks = 0;
                    _done = true;
                    return true;
                }
                await CommonTasks.MoveToAndStopAsync(Location, Name, false, () => FindObject() is { } near && near.Distance <= 0.4f, 0.3f);
                return true; // carry on next tick
            }
            _walks = 0;
            HeroPlaceable obj = FindObject();
            if (obj == null)
            {
                Log("No " + Name + " at the spot you clicked - skipping it.");
                _done = true;
                return true;
            }
            var walk = Stopwatch.StartNew();
            while (obj.IsValid && obj.Distance > 0.4f && walk.Elapsed.TotalSeconds < 20)
            {
                HeroPlaceable o = obj;
                await CommonTasks.MoveToAndStopAsync(o.Location, Name, false, () => !o.IsValid || o.Distance <= 0.4f, 0.3f);
                await Coroutine.Yield();
            }
            obj.Interact();
            Log("Clicked " + Name);
            if (await Coroutine.Wait(1000, () => player.IsCasting))
                await Coroutine.Wait(15000, () => !player.IsCasting);
            if (WaitTime > 0)
                await Coroutine.Sleep(WaitTime);
            _done = true;
            return true;
        }
    }
}
