// Quest Recorder - made by LethToxic.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using BuddyCron;
using BuddyCron.Behaviors;
using BuddyCron.Managers;
using BuddyCron.Navigation;
using BuddyCron.Objects;
using BuddyCron.Objects.Oracle;
using BuddyCron.Objects.Static;
using BuddyCron.Profiles;
using Reborn.Behaviors.Coroutines;
using Reborn.Behaviors.Treesharp;
using Reborn.Utilities.Math;
using Reborn.Utilities.XmlEngine;

namespace QuestBehaviors
{
    /// <summary>
    /// &lt;TalkAndReply Name="NPC" Replies="2,1" XYZ="..." /&gt; talks to an NPC and picks the reply-wheel numbers you
    /// picked when you recorded it, in order (ConvoOracle.SelectConversationOption). Lines in between are skipped.
    /// </summary>
    [XmlElement("TalkAndReply")]
    public class TalkAndReplyTag : ProfileBehavior
    {
        private bool _done;
        private int _tries;

        [XmlAttribute("Name")]
        public string NpcName { get; set; } = string.Empty;

        [XmlAttribute("XYZ")]
        public Vector3 Position { get; set; }

        /// <summary>The wheel numbers to pick, in order ("2,1,3").</summary>
        [XmlAttribute("Replies")]
        public string Replies { get; set; } = string.Empty;

        [XmlAttribute("UseRange")]
        [DefaultValue(4f)]
        public float UseRange { get; set; } = 4f;

        public override bool IsDone => _done;

        public override string StatusText => "Talking to " + NpcName + " (replies " + Replies + ")";

        protected override void OnResetCachedDone()
        {
            _done = false;
            _tries = 0;
        }

        private HeroObject Npc()
        {
            return (from c in HeroObjectManager.GetObjects<HeroCharacter>() orderby c.DistanceSqr select c).FirstOrDefault(c => c.Name == NpcName);
        }

        private static HeroCnvOracle Convo()
        {
            HeroCnvOracle cnv = ExtraNodeManager.ConvoOracle;
            return cnv != null && cnv.IsInConversation ? cnv : null;
        }

        protected override Composite CreateBehavior()
        {
            return new PrioritySelector(ctx => Npc(),
                new Decorator(ret => ret == null && Core.Me!.Location.DistanceSqr(Position) <= UseRange * UseRange, new ActionRunCoroutine(NoNpc)),
                CommonBehaviors.MoveToInteract(ret => ret as HeroObject, ret => Position, ret => UseRange, true, ret => "TalkAndReply: " + NpcName),
                new ActionRunCoroutine(Talk));
        }

        private async Task<bool> NoNpc(object arg)
        {
            await Coroutine.Sleep(1000);
            if (++_tries >= 30)
            {
                Log(NpcName + " isn't here - skipping the talk.");
                _done = true;
            }
            return true;
        }

        private async Task<bool> Talk(object arg)
        {
            var npc = arg as HeroObject;
            if (npc == null || !npc.IsValid)
                return true;
            int[] picks = Replies.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out int n) ? n : 0).Where(n => n > 0).ToArray();
            npc.Interact();
            if (!await Coroutine.Wait(5000, () => Convo() != null))
            {
                if (++_tries >= 3)
                {
                    Log(NpcName + " didn't start a conversation - skipping the talk.");
                    _done = true;
                }
                return true;
            }
            int next = 0;
            long lastNode = -2;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 90000)
            {
                HeroCnvOracle cnv = Convo();
                if (cnv == null)
                    break; // the conversation is over
                long node = Convert.ToInt64(cnv.CurrentNodeNumber);
                if (cnv.IsWaitingForChoice && cnv.ChoicesShown)
                {
                    if (node != lastNode)
                    {
                        lastNode = node; // one pick per wheel
                        if (next < picks.Length)
                        {
                            Log("Reply " + picks[next] + " to " + NpcName);
                            cnv.SelectConversationOption(picks[next]);
                            next++;
                        }
                        else
                        {
                            cnv.SelectReplyByAlignment(cnvForceRewardTypeEnum.None); // more wheels than you picked: the neutral one
                        }
                    }
                }
                else
                {
                    cnv.SelectConversationSkip();
                }
                await Coroutine.Sleep(400);
            }
            _done = true;
            return true;
        }
    }
}
