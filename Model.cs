// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;

namespace QuestRecorder
{
    // Plain C# copies of game state.

    internal struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float DistanceTo(Vec3 o)
        {
            return (float)Math.Sqrt(DistanceSq(o));
        }

        public float DistanceSq(Vec3 o)
        {
            float dx = X - o.X;
            float dy = Y - o.Y;
            float dz = Z - o.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public bool IsZero
        {
            get { return X == 0f && Y == 0f && Z == 0f; }
        }

        public override string ToString()
        {
            return Fmt.Xyz(this);
        }
    }

    internal enum ObjKind
    {
        None = 0,
        Npc = 1,
        Placeable = 2,
        Player = 3,
        Other = 4,
    }

    /// <summary>A world object as it was seen at one moment (name exactly as the game names it).</summary>
    internal sealed class ObjRef
    {
        public string Name = string.Empty;
        public ulong NodeId;
        public ObjKind Kind;
        public Vec3 Pos;
        public bool HasPos;
        public string Indication = string.Empty;
        public bool OffersQuest;
        public bool TakesTurnIn;
        public string How = string.Empty;
        public bool IsWonkavator;
        public bool IsTaxiTerminal;
        public bool IsVendor;
        public bool UsableForQuest;
        public float UseRange;
        public string[] Destinations;
        public int Floor;
        public ulong Level;
        public string Toughness = string.Empty;

        public ObjRef Clone(string how)
        {
            var c = (ObjRef)MemberwiseClone();
            if (how != null)
                c.How = how;
            return c;
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case ObjKind.Npc:
                        return "NPC";
                    case ObjKind.Placeable:
                        return "object";
                    case ObjKind.Player:
                        return "player";
                    case ObjKind.Other:
                        return "thing";
                    default:
                        return "?";
                }
            }
        }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? "(no name)" : Name; }
        }
    }

    /// <summary>One active quest as seen in one snapshot.</summary>
    internal sealed class QSnap
    {
        public ulong QuestId;
        public ulong Branch;
        public ulong Step;
        public ulong[] Counts = new ulong[0];
        public bool Holocom;
        public bool Hidden;
    }

    /// <summary>Cached quest identity (one engine call to resolve, then kept for the session).</summary>
    internal sealed class QuestInfo
    {
        public ulong QuestId;
        public string Name = string.Empty;
        public string Fqn = string.Empty;
        public bool Resolved;
        public int Attempts;
    }

    internal sealed class TaskInfo
    {
        public int Index;
        public ulong TaskId;
        public ulong Max;
        public string Text = string.Empty;
    }

    /// <summary>Cached task list of one quest step.</summary>
    internal sealed class StepInfo
    {
        public ulong QuestId;
        public ulong Branch;
        public ulong Step;
        public readonly List<TaskInfo> Tasks = new List<TaskInfo>();
        public bool Resolved;
        public int Attempts;

        public TaskInfo Task(int index)
        {
            return index >= 0 && index < Tasks.Count ? Tasks[index] : null;
        }
    }

    internal sealed class AreaInfo
    {
        public ulong Id;
        public string Display = string.Empty;
        public string Internal = string.Empty;
        public string Planet = string.Empty;

        /// <summary>The best human name: English display name, else planet, else internal name, else the id.</summary>
        public string Label
        {
            get
            {
                if (!string.IsNullOrEmpty(Display))
                    return Display;
                if (!string.IsNullOrEmpty(Planet))
                    return Planet;
                if (!string.IsNullOrEmpty(Internal))
                    return Internal;
                return Fmt.Hex(Id);
            }
        }
    }

    /// <summary>One player reply on a conversation choice wheel.</summary>
    internal sealed class ReplyOpt
    {
        /// <summary>The engine's 1-based choice-wheel number (what SelectConversationOption takes).</summary>
        public long Option;
        /// <summary>The reply's node number in the dialog tree (the conversation moves to it when this reply is picked).</summary>
        public long Node;
        /// <summary>"dark", "light" or "neutral": the Force alignment picking it grants.</summary>
        public string Align = "neutral";

        public override string ToString()
        {
            return Option.ToString() + " " + Align;
        }
    }

    internal sealed class KillInfo
    {
        public ObjRef Mob;
        public bool TappedByOther;
    }

    internal sealed class TaxiArrival
    {
        /// <summary>The terminal object where you landed (its world name can differ from its taxi-map name).</summary>
        public ObjRef Dest;
        public Vec3 LandPos;
        public DateTime LandTime;

        /// <summary>The destination's name as the taxi map shows it (what UseTransport Option needs), or empty.</summary>
        public string MapName = string.Empty;
        /// <summary>True when <see cref="MapName"/> was read from the game, false when it is a guess.</summary>
        public bool MapNameSure;
        public string MapNameHow = string.Empty;
        /// <summary>The destination names the taxi map offered (read while it was open), or null.</summary>
        public List<string> MapChoices;
    }

    internal sealed class WonkaMemo
    {
        public ObjRef Wonka;
        public Vec3 Stand;
        public ulong AreaId;
        public long Tick;
    }

    internal sealed class WonkaArrival
    {
        public WonkaMemo From;
        public string Dest;
        public string How = string.Empty;
    }

    internal sealed class BoardInfo
    {
        public string Name = string.Empty;
        public ObjRef Terminal;
        public readonly List<KeyValuePair<ulong, string>> Missions = new List<KeyValuePair<ulong, string>>();
    }

    /// <summary>Live answer of BuddyCron's own condition functions right after a step change.</summary>
    internal sealed class StepCheck
    {
        public bool OldDone;
        public bool NewDone;
        public bool Ran;
    }

    internal sealed class RemovedInfo
    {
        public bool Known;
        public bool Completed;
        public bool Abandoned;
    }

    /// <summary>Everything the recorder saw in one look at the game.</summary>
    internal sealed class Snap
    {
        public DateTime Time;
        public long Tick;
        public ulong PlayerId;
        public string PlayerName = string.Empty;
        public ulong CompanionId;
        public ulong Level;
        public Vec3 Pos;
        public float Heading;
        public ulong AreaId;
        public ulong GalaxyAreaId;
        public bool InCombat;
        public bool IsDead;
        public bool IsMounted;
        public bool IsInTaxi;
        public bool InConvo;
        public bool IsCasting;
        public bool BoardOpen;
        public int Interaction;
        public string InteractionName = string.Empty;
        public ulong CastSpec;
        public ulong TargetId;

        /// <summary>The last good quest list (carried over from the previous look when it could not be read now).</summary>
        public List<QSnap> Quests = new List<QSnap>();
        public bool QuestsAvailable;
        public HashSet<ulong> PendingRewards = new HashSet<ulong>();

        // Extras read only on the sample where something started or changed.
        public ObjRef Click;
        /// <summary>Other objects with the same name near the one you clicked (the scanner).</summary>
        public List<ObjRef> SameKind;
        public string ClickCommand = string.Empty;
        /// <summary>A command other than "use" (right-click) started with the mouse over an object: not a click.</summary>
        public string OtherCommand;
        public ObjRef OtherCommandTarget;
        public ObjRef ConvoPartner;
        public bool ConvoIsComlink;
        public string ConvoTypeName = string.Empty;
        public ulong OfferedQuestId;
        /// <summary>The conversation's current dialog node number (-1 when not talking or unknown).</summary>
        public long ConvoNode = -1;
        /// <summary>The node whose choice wheel is on screen now (-1 when no wheel is shown).</summary>
        public long ReplyNode = -1;
        /// <summary>The wheel's replies, read only on the look where a new wheel appeared (null otherwise).</summary>
        public List<ReplyOpt> Replies;
        public List<KillInfo> Kills;
        public List<ObjRef> CombatMobs;
        public Dictionary<ulong, RemovedInfo> Removed;
        public Dictionary<ulong, StepCheck> StepChecks;
        public Dictionary<string, bool> TaskChecks;
        public BoardInfo Board;
        public ObjRef InteractionTarget;
        public TaxiArrival TaxiArrival;
        public WonkaArrival WonkaArrival;
        public string MountName;
        public string ShipDest;
        public string CastName;
        public string QuestItemName;
        /// <summary>A mission item used without a cast bar (seen by its cooldown starting), or null.</summary>
        public string InstantItemName;
        public ObjRef CastTarget;
    }

    internal enum EvKind
    {
        Start,
        Stop,
        QuestLog,
        QuestAdded,
        QuestStep,
        QuestTask,
        QuestCompleted,
        QuestAbandoned,
        QuestRemoved,
        HolocomWaiting,
        HolocomAnswered,
        RewardWindow,
        ConvoStart,
        ConvoEnd,
        ConvoChoice,
        ObjectClick,
        NpcClick,
        QuestItem,
        Cast,
        BoardOpen,
        BoardClose,
        InteractionOpen,
        TaxiOpen,
        TaxiStart,
        TaxiEnd,
        ElevatorOpen,
        ElevatorRide,
        CombatStart,
        CombatEnd,
        Kill,
        Died,
        Revived,
        Mounted,
        Dismounted,
        AreaChange,
        Loading,
        Jump,
        ShipTravel,
        Marker,
        Breadcrumb,
        CharacterChange,
        Paused,
        Resumed,
        Warning,
        Command,
        /// <summary>Another object like the one you clicked, nearby (the scanner): more spots for "click any of these".</summary>
        Spot,
    }

    internal sealed class QuestLine
    {
        public ulong QuestId;
        public string Name = string.Empty;
        public ulong Branch;
        public ulong Step;
        public int TaskTotal;
        public string Counts = string.Empty;
        public string Text = string.Empty;
        public bool Hidden;
    }

    /// <summary>One line of the timeline (one row of the CSV).</summary>
    internal sealed class RecEvent
    {
        public int Seq;
        public DateTime Time;
        public double Elapsed;
        public EvKind Kind;

        // where you were
        public Vec3 Pos;
        public float Heading;
        public ulong AreaId;
        public string AreaName = string.Empty;

        // quest
        public ulong QuestId;
        public string QuestName = string.Empty;
        public string QuestFqn = string.Empty;
        public bool QuestHidden;
        public bool HasStep;
        public ulong Branch;
        public ulong Step;
        public bool HasOldStep;
        public ulong OldBranch;
        public ulong OldStep;
        public int TaskIndex = -1;
        public ulong TaskId;
        public ulong Count;
        public ulong Max;
        public int TaskTotal = -1;
        public string Text = string.Empty;

        // what you interacted with
        public ObjRef Target;
        /// <summary>A second object (the terminal where a taxi ride ended).</summary>
        public ObjRef Target2;

        // jumps and rides
        public bool HasFrom;
        public Vec3 FromPos;
        public ulong FromArea;
        public string FromAreaName = string.Empty;
        public bool HasStand;
        public Vec3 Stand;

        public string Option = string.Empty;
        /// <summary>True when <see cref="Option"/> is a guess (for example a taxi-map name matched by similarity).</summary>
        public bool OptionGuessed;
        /// <summary>The choices the game offered (taxi-map destinations), when known.</summary>
        public List<string> Choices;
        public string Note = string.Empty;
        public string Check = string.Empty;
        public bool CheckFailed;
        public long DurationMs;
        public List<QuestLine> Quests;
        public List<string> Details;

        /// <summary>Headline written to the .txt and the log.</summary>
        public string Summary = string.Empty;

        public void AddDetail(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            if (Details == null)
                Details = new List<string>();
            Details.Add(line);
        }
    }
}
