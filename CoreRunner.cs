namespace QuestRecorder
{
    /// <summary>The step runner every profile the new core writes carries, as its QrRunner Definition chunk (from core/runner.cs.txt).</summary>
    internal static class CoreRunner
    {
        public const string Text = @"// Quest Recorder step runner. Every mission step of this profile runs through QrRun(step number): it checks the
// mission tracker, does the step's action, counts tries, caps the time, and writes one line when the step ends.
// It steps aside while you are dead, fighting or talking, so BuddyCron's own handling runs.
sealed class QrStepDef
{
	public int No, Kind, Task, MaxTries = 6;
	public string Label = """";
	public ulong Quest, Branch, Step, CountTo;
	public ulong[] Specs = new ulong[0], Nodes = new ulong[0];
	public string[] Names = new string[0]; // only for a recording with no type ids: the names it saw
	public System.Numerics.Vector3[] Spots = new System.Numerics.Vector3[0];
	public ulong Ability, Template, Area; public string Item = """", Cast = """";
	public bool DeadOnly, InFight, Loot;
	public bool Once; // one of the actions you did before a step changed, not the last: done after one go (a later action moves the step)
	public float HealthAt, Range = 0.4f;
	public double NoProgressCap = 300, WallCap = 900, WaitAtSpot = 8;
	public QrStepDef[] Row = new QrStepDef[0];
}
sealed class QrStepState
{
	public System.DateTime Start, Last, Next, BlipSince = System.DateTime.MinValue, PressedAt, WalkFailAt, ArrivedAt = System.DateTime.MinValue, OfferAt, PoiAt;
	public double SinceProgress, Active;
	public int Tries, WalkFails, SpotAt, RowAt, MissingSecs;
	public ulong LastCount = ulong.MaxValue, Current, PoiNode;
	public bool Done, GaveUp, Pressed, SawCast, MoveOn, NoMount;
	public System.DateTime SteerSince = System.DateTime.MinValue, WalkLast = System.DateTime.MinValue, WalkMovedAt;
	public System.Numerics.Vector3 WalkPos;
	public System.Collections.Generic.Dictionary<ulong, int> PerNode = new System.Collections.Generic.Dictionary<ulong, int>();
	public System.Collections.Generic.Dictionary<ulong, System.DateTime> SkipUntil = new System.Collections.Generic.Dictionary<ulong, System.DateTime>();
	// one press (a click or an item use) as BuddyCron's own UseObject / UseItem follow it: the cast, how much of it was
	// left when it ended, a fight starting on it, the loot window, a conversation
	public ulong PressNode, CastBefore, PressSpec, CastSpec, CloseIn;
	public float CastLeft;
	public string LastRefusal = """";
	public bool PressInFight, LootTaken, SawConvo, UsableBefore, AskedDone, SaidDone;
	public int Duds;
	public System.DateTime LootSince = System.DateTime.MinValue, LootTry = System.DateTime.MinValue, SightSince = System.DateTime.MinValue, RefusedSince = System.DateTime.MinValue;
	// what an item already went on in this step (BuddyCron's UseItem never uses one twice); kept when the count goes up
	public System.Collections.Generic.HashSet<ulong> Used = new System.Collections.Generic.HashSet<ulong>();
}
const int QrKill = 1, QrUse = 2, QrTalk = 3, QrItem = 4, QrRow = 5, QrAbility = 6, QrEquip = 7, QrDefend = 8, QrWalkWait = 9,
	QrPickup = 10, QrHandIn = 11, QrHolocom = 12, QrWalk = 13, QrHop = 14, QrCollect = 15;
const float QrArrive = 0.5f, QrClose = 0.15f; // the navigator's own arrival distance (0.5 units, about 5 m); walking up to an object
static System.Collections.Generic.Dictionary<int, QrStepDef> _qrDefs;
static readonly System.Collections.Generic.Dictionary<int, QrStepState> _qrStates = new System.Collections.Generic.Dictionary<int, QrStepState>();

static QrStepDef QrDef(int n)
{
	if (_qrDefs == null)
	{
		var all = new System.Collections.Generic.Dictionary<int, QrStepDef>();
		foreach (QrStepDef d in QrSteps())
			all[d.No] = d;
		_qrDefs = all;
	}
	QrStepDef x;
	return _qrDefs.TryGetValue(n, out x) ? x : null;
}

static void QrSay(QrStepDef d, string what)
{
	try { Reborn.Utilities.Logging.Write(""[Quest Recorder] step "" + d.No + "" "" + what + "" ("" + d.Label + "")""); } catch { }
}

// how = ""done"", ""gave up"" or ""stopped""; one line per step, the score sheet of a run
static void QrEnd(QrStepDef d, QrStepState s, string how, string why)
{
	if (how == ""gave up"") s.GaveUp = true; else s.Done = true;
	try { BuddyCron.Navigation.Navigator.PlayerMover.MoveStop(); } catch { }
	QrSay(d, how + "" after "" + s.Active.ToString(""0"") + "" s, "" + s.Tries + "" tries"" + (why.Length > 0 ? "": "" + why : """"));
}

static ulong QrSpec(BuddyCron.Objects.HeroObject o)
{
	ulong spec = 0;
	try { o.TryGetField<ulong>(BuddyCron.GomFieldId.spnSpawnedSpec, out spec); } catch { }
	return spec;
}

static bool QrHas(ulong[] list, ulong v)
{
	if (v == 0) return false;
	for (int i = 0; i < list.Length; i++) if (list[i] == v) return true;
	return false;
}

// a recording that has no type id for the step's targets: one of the names it saw (never used when there are type ids)
static bool QrNamed(QrStepDef d, BuddyCron.Objects.HeroObject o)
{
	if (d.Specs.Length > 0 || d.Names.Length == 0) return false;
	string n = """";
	try { n = o.Name ?? """"; } catch { }
	for (int i = 0; i < d.Names.Length; i++) if (n.Length > 0 && d.Names[i] == n) return true;
	return false;
}

static float QrSpotDist(QrStepDef d, System.Numerics.Vector3 p)
{
	float best = float.MaxValue;
	for (int i = 0; i < d.Spots.Length; i++) best = System.Math.Min(best, System.Numerics.Vector3.Distance(d.Spots[i], p));
	return d.Spots.Length == 0 ? 0f : best;
}

static bool QrSkipped(QrStepState s, ulong node, System.DateTime now)
{
	System.DateTime until;
	return s.SkipUntil.TryGetValue(node, out until) && now < until;
}

static void QrCountTry(QrStepState s, ulong node, int most, double skipSecs, System.DateTime now)
{
	int k; s.PerNode.TryGetValue(node, out k); s.PerNode[node] = ++k;
	if (k >= most) { s.SkipUntil[node] = now.AddSeconds(skipSecs); s.PerNode[node] = 0; } // no progress on it: another one first
}

// where a walk stood still with the mount (it walks without it within about 80 m of those places, for every step)
static readonly System.Collections.Generic.List<System.Numerics.Vector3> _qrNoMount = new System.Collections.Generic.List<System.Numerics.Vector3>();

static bool QrNoMountNear(System.Numerics.Vector3 p)
{
	foreach (var z in _qrNoMount) if (System.Numerics.Vector3.Distance(z, p) < 8f) return true;
	return false;
}

static bool QrNearElevator(BuddyCron.Objects.HeroLocalPlayer me)
{
	try
	{
		foreach (var p in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroPlaceable>())
			if (p != null && p.IsValid && p.IsWonkavator && System.Numerics.Vector3.Distance(p.Location, me.Location) < 3f) return true;
	}
	catch { }
	return false;
}

// one step of walking; true once there. A spot the navigator can't find a way to counts as a walk failure (at most one a second).
// tol: how close counts as there (a spot: the navigator's own 0.5; an object: closer, the caller stops once the game says
// it is in range)
static bool QrWalkTo(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.Numerics.Vector3 to, System.DateTime now, float tol = QrArrive)
{
	if (System.Numerics.Vector3.Distance(me.Location, to) <= tol) return true;
	// standing still for 8 s while it should walk (Section X: BuddyCron stopped to mount where mounts aren't allowed
	// and never moved again): stop, drop the path, and walk on without the mount. A cast (quick travel, the mount
	// itself) stands still too: up to 20 s of it is waited out; so is up to 60 s next to an elevator (the navigator
	// waits there for the car)
	if ((now - s.WalkLast).TotalSeconds > 2 || System.Numerics.Vector3.Distance(me.Location, s.WalkPos) > 0.1f) { s.WalkPos = me.Location; s.WalkMovedAt = now; }
	s.WalkLast = now;
	bool casting = false;
	try { casting = me.IsCasting; } catch { }
	double still = (now - s.WalkMovedAt).TotalSeconds;
	if (still > (casting ? 20 : 8) && (still > 60 || !QrNearElevator(me)))
	{
		BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
		try { BuddyCron.Navigation.Navigator.Clear(); } catch { }
		if (!s.NoMount) QrSay(d, ""stood still on the way: walking on without the mount"");
		s.NoMount = true;
		// (every later step walks without the mount near here too)
		if (_qrNoMount.Count < 50 && !QrNoMountNear(me.Location)) _qrNoMount.Add(me.Location);
		s.WalkFails++;
		s.WalkMovedAt = now;
		return false;
	}
	var mp = new BuddyCron.Navigation.MoveToParameters(to, ""Quest Recorder step "" + d.No) { DistanceTolerance = tol };
	if (s.NoMount || QrNoMountNear(me.Location)) mp.UseMount = false;
	var r = BuddyCron.Navigation.Navigator.MoveTo(mp);
	if (r == BuddyCron.Navigation.MoveResult.ReachedDestination) return true;
	if ((r == BuddyCron.Navigation.MoveResult.PathGenerationFailed || r == BuddyCron.Navigation.MoveResult.Failed) && (now - s.WalkFailAt).TotalSeconds >= 1)
	{
		s.WalkFails++;
		s.WalkFailAt = now;
	}
	return false;
}

// walk up to an object or an NPC until the game says it is in reach: the navigator to about 5 m of it, then straight
// at it. An object often stands where nobody can stand (a console against a wall, a machine on a table), and asking the
// navigator for its own spot fails (Section X Science Station: ""the way there could not be found"", while you had
// stood 3 m from it and used it)
static void QrApproach(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, BuddyCron.Objects.HeroObject o, System.DateTime now)
{
	if (System.Numerics.Vector3.Distance(me.Location, o.Location) > QrArrive + 0.3f)
	{
		s.SteerSince = System.DateTime.MinValue;
		QrWalkTo(d, s, me, o.Location, now, QrArrive);
		return;
	}
	if (s.SteerSince == System.DateTime.MinValue) s.SteerSince = now;
	if ((now - s.SteerSince).TotalSeconds > 8)
	{
		// 8 s walking straight at it and still out of reach: leave that one a while (another of its kind, or later)
		BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
		s.SteerSince = System.DateTime.MinValue;
		s.SkipUntil[o.NodeId] = now.AddSeconds(30);
		s.Tries++;
		return;
	}
	BuddyCron.Navigation.Navigator.PlayerMover.MoveTowards(o.Location);
}

// 1 = this step is over (done, or the mission moved past it), 0 = still to do, -1 = the mission is not in your log
static int QrTracker(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, double dt)
{
	if (d.Quest == 0) return 0; // a walk between missions: no tracker
	var st = me.Quests.GetState(d.Quest);
	if (d.Kind == QrPickup)
	{
		if (st != null) return 1;
		s.SinceProgress += dt;
		return 0;
	}
	if (d.Kind == QrHandIn || (d.Kind == QrHolocom && d.Step == 0))
	{
		if (st == null) return 1;
		foreach (var r in me.Quests.PendingQuestRewards) if (r != null && r.QuestId == d.Quest) return 1;
		s.SinceProgress += dt;
		return 0;
	}
	if (st == null) return -1;
	if (d.Branch != 0 && st.BranchId != d.Branch) return 1;
	if (st.StepId != d.Step) return 1;
	if (d.Kind == QrWalk) return 0;
	ulong c = d.Task >= 0 && d.Task < st.TaskCount.Count ? st.TaskCount[d.Task] : 0UL;
	if (d.CountTo > 0 && c >= d.CountTo) return 1;
	try
	{
		var sd = st.StepDefinition;
		if (sd != null && d.Task >= 0 && d.Task < sd.Tasks.Count && sd.Tasks[d.Task].IsTaskComplete((uint)System.Math.Min(c, (ulong)uint.MaxValue))) return 1;
	}
	catch { }
	if (s.LastCount != ulong.MaxValue && c > s.LastCount)
	{
		// the count went up: progress, so the tries and the no-progress clock start over
		s.SinceProgress = 0;
		s.Tries = 0;
		s.PerNode.Clear();
		s.SkipUntil.Clear();
	}
	else
		s.SinceProgress += dt;
	s.LastCount = c;
	return 0;
}

static bool QrRun(int n)
{
	QrStepDef d = QrDef(n);
	if (d == null) return false;
	var now = System.DateTime.Now;
	QrStepState s;
	if (!_qrStates.TryGetValue(n, out s) || ((s.Done || s.GaveUp) && (now - s.Last).TotalSeconds > 30))
	{
		// first time here, or this step ended more than 30 s ago and the profile came back to it (a restart, a loop): a fresh go
		s = new QrStepState { Start = now, Next = now };
		_qrStates[n] = s;
	}
	double dt = (now - s.Last).TotalSeconds;
	if (dt < 0 || dt > 5) dt = 0; // time the hook did not run (a fight, a stop) is not counted
	s.Last = now;
	if (s.Done || s.GaveUp) return false;
	var me = BuddyCron.Core.Me;
	if (me == null || !me.IsValid)
	{
		// you can't be read (a loading screen): hold the step up to 30 s rather than let the profile go past it
		if (s.BlipSince == System.DateTime.MinValue) s.BlipSince = now;
		return (now - s.BlipSince).TotalSeconds < 30;
	}
	try
	{
		if (me.IsDead) { s.Pressed = false; return false; }
		if (me.IsInConversation) { if (s.Pressed) s.SawConvo = true; return false; }
		bool fightingStep = d.Kind == QrKill || d.Kind == QrDefend;
		if (me.InCombat && !d.InFight)
		{
			if (s.Pressed) s.PressInFight = true; // a fight started on a click or an item use: it may have broken the cast off
			if (fightingStep) return false;
			var foe = BuddyCron.Profiles.CombatTargeting.Instance.FirstUnit;
			if (foe != null)
			{
				s.BlipSince = System.DateTime.MinValue;
				return false;
			}
			// in combat with nothing to fight: hold a moment, then go on with the step (walking away usually ends it)
			if (s.BlipSince == System.DateTime.MinValue) s.BlipSince = now;
			if ((now - s.BlipSince).TotalSeconds < 5) return true;
		}
		else
			s.BlipSince = System.DateTime.MinValue;
		s.Active += dt;
		int t = QrTracker(d, s, me, dt);
		if (t == 1) { QrEnd(d, s, ""done"", """"); return false; }
		if (t == -1) { QrEnd(d, s, ""stopped"", ""the mission is not in your log""); return false; }
		int most = d.MaxTries;
		double wall = d.WallCap;
		if (d.Kind == QrPickup)
		{
			// a mission the game says you already did (today's daily): one look at the giver, not the full tries (its
			// ""already done"" has read true for missions the giver offered again, so it still gets that look)
			if (!s.AskedDone)
			{
				s.AskedDone = true;
				try { s.SaidDone = me.Quests.IsQuestCompleted(d.Quest); } catch { }
				if (s.SaidDone) QrSay(d, ""the game says you already did this mission: one look at the giver"");
			}
			if (s.SaidDone) { most = System.Math.Min(most, 2); wall = System.Math.Min(wall, 45); }
		}
		// (the last try's own outcome is waited for first: its cast may still move the count)
		if (s.Tries >= most && !s.Pressed) { QrEnd(d, s, ""gave up"", ""no progress after "" + most + "" tries""); return false; }
		if (s.SinceProgress > d.NoProgressCap) { QrEnd(d, s, ""gave up"", ""no progress for "" + d.NoProgressCap.ToString(""0"") + "" s""); return false; }
		if (s.Active > wall) { QrEnd(d, s, ""gave up"", ""over the step's time limit of "" + wall.ToString(""0"") + "" s""); return false; }
		if (s.WalkFails >= 10) { QrEnd(d, s, ""gave up"", ""the way there could not be found""); return false; }
		if (QrTakeLoot(s, now)) return true;
		return QrAct(d, s, me, now);
	}
	catch (System.Exception ex)
	{
		QrEnd(d, s, ""gave up"", ex.GetType().Name + "": "" + ex.Message);
		return false;
	}
}

// the same step from inside a fight: BuddyCron ticks PreCombatLogic hooks before the routine's own move each fight
// tick (its hunt tags use it for ""use the item once its health is down""); true keeps the routine out for that tick
static bool QrFight(int n)
{
	QrStepDef d = QrDef(n);
	if (d == null || !d.InFight) return false;
	return QrRun(n);
}

static bool QrAct(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	switch (d.Kind)
	{
		case QrKill: return QrDoKill(d, s, me, now);
		case QrUse: case QrTalk: case QrPickup: case QrHandIn: return QrDoInteract(d, s, me, now);
		case QrItem: case QrAbility: case QrEquip: return QrDoItem(d, d, s, me, now);
		case QrRow: return QrDoRow(d, s, me, now);
		case QrHolocom: return QrDoHolocom(d, s, me, now);
		case QrDefend: case QrWalkWait: case QrWalk: return QrDoStand(d, s, me, now);
		case QrHop: return QrDoHop(d, s, me, now);
		case QrCollect: return s.Pressed || QrFindObject(d, s, now) != null ? QrDoInteract(d, s, me, now) : QrDoKill(d, s, me, now);
	}
	QrEnd(d, s, ""gave up"", ""unknown step kind "" + d.Kind);
	return false;
}

// walk the step's spots one by one, waiting a little at each (mobs respawn, objects come back); a full round is one try
static bool QrRoam(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	if (d.Spots.Length == 0)
	{
		if (s.ArrivedAt == System.DateTime.MinValue) s.ArrivedAt = now;
		if ((now - s.ArrivedAt).TotalSeconds >= d.WaitAtSpot) { s.Tries++; s.ArrivedAt = System.DateTime.MinValue; }
		return true;
	}
	var to = d.Spots[s.SpotAt % d.Spots.Length];
	if (!QrWalkTo(d, s, me, to, now))
	{
		s.ArrivedAt = System.DateTime.MinValue;
		return true;
	}
	BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
	if (s.ArrivedAt == System.DateTime.MinValue) s.ArrivedAt = now;
	if ((now - s.ArrivedAt).TotalSeconds >= d.WaitAtSpot)
	{
		s.SpotAt++;
		s.ArrivedAt = System.DateTime.MinValue;
		if (s.SpotAt % d.Spots.Length == 0) s.Tries++;
	}
	return true;
}

static bool QrDoKill(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	var cur = BuddyCron.Helpers.Poi.Current;
	if (cur != null && cur.Type == BuddyCron.Helpers.PoiType.Kill)
	{
		var u = cur.HeroCharacter;
		if (u != null && u.IsValid && !u.IsDead)
		{
			// BuddyCron is on a kill: let it run, unless it has got nowhere with our mob for 20 s
			if (u.NodeId != s.PoiNode || me.InCombat || u.HealthPercent < 100f || (now - s.PoiAt).TotalSeconds < 20) return false;
			s.SkipUntil[u.NodeId] = now.AddSeconds(60);
			s.PoiNode = 0;
			s.Tries++;
			BuddyCron.Helpers.Poi.Clear(""Quest Recorder step "" + d.No + "": could not get to it"");
			return true;
		}
	}
	// BuddyCron loots a body before it starts the next fight (""There's stuff to loot. Avoiding chain-pulling.""): let it
	// loot, and don't count that as a failed try (Section X: the step gave up after 12 such ""tries"" in 4 s)
	if (cur != null && cur.Type == BuddyCron.Helpers.PoiType.Loot) return false;
	try { if (BuddyCron.Profiles.LootTargeting.Instance.FirstEntity != null) return false; } catch { }
	BuddyCron.Objects.HeroCharacter best = null, corpse = null;
	foreach (var c in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroCharacter>())
	{
		if (c == null || !c.IsValid || !(c is BuddyCron.Objects.HeroNPC) || c.Distance > 5f || QrSkipped(s, c.NodeId, now)) continue;
		if (!QrHas(d.Specs, QrSpec(c)) && !QrNamed(d, c)) continue;
		if (c.IsDead)
		{
			if (d.Loot && c.IsLootable && (corpse == null || c.Distance < corpse.Distance)) corpse = c;
			else if (d.Once && c.NodeId == s.Current && !me.InCombat) { QrEnd(d, s, ""done"", ""did it once (a later step moves the mission on)""); return false; }
			continue;
		}
		if (c.TappedByOther || c.IsFriendly) continue;
		if (best == null || c.Distance < best.Distance) best = c;
	}
	if (corpse != null)
	{
		// kill and loot: open the body of one you killed
		if (!corpse.IsWithinInteractRange) { QrApproach(d, s, me, corpse, now); return true; }
		if (now < s.Next) return true;
		BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
		corpse.Interact();
		QrCountTry(s, corpse.NodeId, 3, 300, now);
		s.Next = now.AddSeconds(2);
		return true;
	}
	if (best != null)
	{
		if (best.NodeId == s.PoiNode && (now - s.PoiAt).TotalSeconds < 3 && !me.InCombat && best.HealthPercent >= 100f)
		{
			// handed to BuddyCron a moment ago and it did nothing with it: leave that one for now
			s.SkipUntil[best.NodeId] = now.AddSeconds(60);
			s.PoiNode = 0;
			s.Tries++;
			return true;
		}
		if (s.Current != best.NodeId) { s.Current = best.NodeId; s.Tries++; }
		s.PoiNode = best.NodeId;
		s.PoiAt = now;
		// let BuddyCron pull and fight it (the same way its own hunt tags start a fight)
		BuddyCron.Helpers.Poi.Current = new BuddyCron.Helpers.Poi(best, BuddyCron.Helpers.PoiType.Kill);
		return false;
	}
	s.Current = 0;
	return QrRoam(d, s, me, now);
}

static bool QrMarker(BuddyCron.Objects.HeroObject o, BuddyCron.cnvQuestIndication want)
{
	try { return o.QuestIndication == want; } catch { return false; }
}

// the NPC or object for a click, a talk, a pick-up or a hand-in: the exact one you used if it is there, else one of the
// same type near where you used it. A click wants one the game says is usable for a mission; a pick-up prefers one
// showing a mission-offer marker, a hand-in one showing a hand-in marker.
static BuddyCron.Objects.HeroObject QrFindObject(QrStepDef d, QrStepState s, System.DateTime now)
{
	BuddyCron.Objects.HeroObject best = null;
	int bestRank = int.MaxValue;
	float bestDist = float.MaxValue;
	foreach (var o in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroObject>())
	{
		if (o == null || !o.IsValid || o is BuddyCron.Objects.HeroPlayer || QrSkipped(s, o.NodeId, now)) continue;
		var ch = o as BuddyCron.Objects.HeroCharacter;
		if (ch != null && (ch.IsDead || (ch.IsHostile && (d.Kind == QrUse || d.Kind == QrCollect)))) continue; // a hostile mob is fought, not clicked
		int at = System.Array.IndexOf(d.Nodes, o.NodeId);
		// (a click the recording has no type id for: any object at the spot the game marks usable for a mission)
		bool anyAtSpot = d.Specs.Length == 0 && d.Nodes.Length == 0 && d.Names.Length == 0 && d.Spots.Length > 0 && (d.Kind == QrUse || d.Kind == QrCollect) && ch == null;
		if (at < 0 && ((!anyAtSpot && !QrHas(d.Specs, QrSpec(o)) && !QrNamed(d, o)) || (d.Spots.Length > 0 && QrSpotDist(d, o.Location) > 3f))) continue;
		// rank: 0 = the right marker / usable, 1 = no sign either way; exact ones before same-type ones
		int rank = 1;
		if (d.Kind == QrUse || d.Kind == QrCollect)
		{
			bool usable = false;
			try { usable = BuddyCron.Core.Me.Quests.CheckQuestUsability(o.NodeId); } catch { }
			if (usable) rank = 0; else if (at < 0) continue;
		}
		else if (d.Kind == QrPickup && QrMarker(o, BuddyCron.cnvQuestIndication.Grant)) rank = 0;
		else if (d.Kind == QrHandIn && QrMarker(o, BuddyCron.cnvQuestIndication.End)) rank = 0;
		rank = rank * 2 + (at >= 0 ? 0 : 1);
		float dist = at >= 0 ? at : o.Distance;
		if (rank < bestRank || (rank == bestRank && dist < bestDist)) { best = o; bestRank = rank; bestDist = dist; }
	}
	return best;
}

// an open loot window (after a click, a body you opened, a chest): take everything, the way BuddyCron's UseObject and its
// own looting do (give the window 0.4 s to fill, then PlayerTakeAllLoot); at most 5 s on one window (bags full)
static bool QrTakeLoot(QrStepState s, System.DateTime now)
{
	bool open = false;
	try { open = BuddyCron.Objects.HeroGuiApiGfx.Instance.IsLootWindowOpen; } catch { }
	if (!open) { s.LootSince = System.DateTime.MinValue; return false; }
	var cur = BuddyCron.Helpers.Poi.Current;
	if (cur != null && cur.Type == BuddyCron.Helpers.PoiType.Loot) return false; // BuddyCron is looting this one itself
	if (s.LootSince == System.DateTime.MinValue) { s.LootSince = now; s.LootTry = now.AddSeconds(0.4); }
	if ((now - s.LootSince).TotalSeconds > 5) return false;
	if (now >= s.LootTry)
	{
		try { BuddyCron.HeroScrpt.Automaton.PlayerTakeAllLoot(); } catch { }
		s.LootTaken = true;
		s.LootTry = now.AddSeconds(1);
	}
	return true;
}

// a try that a fight or a broken-off cast cut short is not a failed try
static void QrRefund(QrStepState s, ulong node)
{
	if (s.Tries > 0) s.Tries--;
	int k;
	if (node != 0 && s.PerNode.TryGetValue(node, out k) && k > 0) s.PerNode[node] = k - 1;
}

// a hostile standing at the object (within about 12 m of it, and within 50 m of you): fought first, the way BuddyCron's
// own UseObject and Interact tags clear the spot before they use anything (HuntBehavior: EnemiesNearObject)
static BuddyCron.Objects.HeroCharacter QrFoeAt(QrStepState s, BuddyCron.Objects.HeroObject o, System.DateTime now)
{
	BuddyCron.Objects.HeroCharacter best = null;
	float bestD = float.MaxValue;
	foreach (var c in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroCharacter>())
	{
		if (c == null || !c.IsValid || c is BuddyCron.Objects.HeroPlayer || c.NodeId == o.NodeId || c.IsDead || !c.IsHostile || c.TappedByOther || c.Distance >= 5f || QrSkipped(s, c.NodeId, now)) continue;
		float dd = System.Numerics.Vector3.DistanceSquared(c.Location, o.Location);
		if (dd < 1.5f && dd < bestD) { best = c; bestD = dd; }
	}
	return best;
}

// the object a press went on has gone, or the game no longer marks it usable for the mission (it was before the press)
static bool QrUsedUp(QrStepState s, int kind)
{
	if (s.PressNode == 0) return false;
	BuddyCron.Objects.HeroObject o = null;
	try { o = BuddyCron.Managers.HeroObjectManager.GetByNodeId(s.PressNode); } catch { }
	if (o == null || !o.IsValid) return true;
	if ((kind == QrUse || kind == QrCollect) && s.UsableBefore)
	{
		bool usable = true;
		try { usable = BuddyCron.Core.Me.Quests.CheckQuestUsability(s.PressNode); } catch { }
		return !usable;
	}
	return false;
}

static bool QrDoInteract(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	if (d.Kind == QrPickup && (me.MissionBoardOpen || me.LightweightQuestOffer != null))
	{
		// the mission is offered: take it (the way BuddyCron's PickUpQuest does)
		if ((now - s.OfferAt).TotalSeconds >= 2)
		{
			BuddyCron.Objects.HeroGuiApiGfx.Instance.AcceptMission(d.Quest);
			s.OfferAt = now;
			s.Tries++;
		}
		return true;
	}
	if (me.IsCasting)
	{
		if (s.Pressed) { s.SawCast = true; try { s.CastLeft = me.CastTimeRemaining; } catch { } }
		return true;
	}
	if (s.Pressed)
	{
		// a cast that ended with more than 1 s of it left, or a fight that started on the click before any cast: broken
		// off (BuddyCron's own UseItem rule), so not a failed try; the same object again once the fight is over
		if (!s.LootTaken && !s.SawConvo && (s.SawCast ? s.CastLeft > 1f : s.PressInFight))
		{
			s.Pressed = false;
			QrRefund(s, s.PressNode);
			s.Next = now.AddSeconds(1);
			QrSay(d, ""a click was broken off"" + (s.SawCast ? "" with "" + s.CastLeft.ToString(""0.0"") + "" s of the cast left"" : "" by a fight"") + "": not counted, trying again"");
			return true;
		}
		// the cast starts within a second when there is one (BuddyCron's UseObject waits 1 s for it)
		if (!s.SawCast && (now - s.PressedAt).TotalSeconds < 1.0) return true;
		// then give the game a moment (the count, the object going away) before judging the press
		if (now < s.Next) return true;
		s.Pressed = false;
		bool usedUp = QrUsedUp(s, d.Kind);
		bool did = s.SawCast || s.LootTaken || s.SawConvo || usedUp;
		// used up: leave it 30 s (BuddyCron's UseObject blacklists an object that is no longer usable after its cast)
		if (usedUp) s.SkipUntil[s.PressNode] = now.AddSeconds(30);
		if (d.Once && d.Kind != QrPickup && d.Kind != QrHandIn)
		{
			// one go is enough when something showed it happened (a cast, loot, a talk, the object used up); a press with
			// nothing to show gets one more go
			if (did) { QrEnd(d, s, ""done"", ""did it once (a later step moves the mission on)""); return false; }
			if (s.Duds >= 1) { QrEnd(d, s, ""done"", ""pressed it twice with nothing to show (a later step moves the mission on)""); return false; }
			s.Duds++;
		}
		return true; // (the step's limits are looked at before the next press)
	}
	if (now < s.Next) return true;
	var o = QrFindObject(d, s, now);
	if (o == null)
		return QrRoam(d, s, me, now);
	s.ArrivedAt = System.DateTime.MinValue;
	if (d.Kind == QrUse || d.Kind == QrCollect || d.Kind == QrTalk)
	{
		// BuddyCron is busy on a kill or a body: let it finish first
		var cur = BuddyCron.Helpers.Poi.Current;
		if (cur != null && cur.Type == BuddyCron.Helpers.PoiType.Loot) return false;
		if (cur != null && cur.Type == BuddyCron.Helpers.PoiType.Kill && cur.HeroCharacter != null && cur.HeroCharacter.IsValid && !cur.HeroCharacter.IsDead) return false;
		var foe = QrFoeAt(s, o, now);
		if (foe != null)
		{
			if (foe.NodeId == s.PoiNode && (now - s.PoiAt).TotalSeconds < 3 && !me.InCombat && foe.HealthPercent >= 100f)
				s.SkipUntil[foe.NodeId] = now.AddSeconds(60); // handed to BuddyCron a moment ago and it would not take it: use the object anyway
			else
			{
				s.PoiNode = foe.NodeId;
				s.PoiAt = now;
				BuddyCron.Helpers.Poi.Current = new BuddyCron.Helpers.Poi(foe, BuddyCron.Helpers.PoiType.Kill);
				return false;
			}
		}
	}
	if (!o.IsWithinInteractRange)
	{
		s.SightSince = System.DateTime.MinValue;
		QrApproach(d, s, me, o, now);
		return true;
	}
	bool sight = true;
	try { sight = o.InLineOfSight; } catch { }
	if (!sight)
	{
		// in reach but not in sight (BuddyCron's MoveToInteract wants both): step towards it a moment, then press anyway
		if (s.SightSince == System.DateTime.MinValue) s.SightSince = now;
		if ((now - s.SightSince).TotalSeconds < 1.5) { BuddyCron.Navigation.Navigator.PlayerMover.MoveTowards(o.Location); return true; }
	}
	s.SightSince = System.DateTime.MinValue;
	s.SteerSince = System.DateTime.MinValue;
	BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
	s.UsableBefore = false;
	if (d.Kind == QrUse || d.Kind == QrCollect) { try { s.UsableBefore = me.Quests.CheckQuestUsability(o.NodeId); } catch { } }
	o.Interact();
	s.Tries++;
	s.Pressed = true;
	s.SawCast = false;
	s.CastLeft = 0f;
	s.PressInFight = false;
	s.LootTaken = false;
	s.SawConvo = false;
	s.PressNode = o.NodeId;
	s.PressedAt = now;
	s.Next = now.AddSeconds(d.Kind == QrUse || d.Kind == QrCollect ? 2 : 4);
	QrCountTry(s, o.NodeId, 3, 30, now);
	return true;
}

// the item: by its use ability's id, else its type id; a recording with no ids finds it by its use ability's name
// (the name the combat log gave its cast, the one sure way when two items have like names), then by its own name
static BuddyCron.Objects.HeroItem QrFindItem(QrStepDef p)
{
	BuddyCron.Objects.HeroItem byTemplate = null, byCast = null, byName = null;
	foreach (var list in new System.Collections.Generic.IEnumerable<BuddyCron.Objects.HeroItem>[] { BuddyCron.Core.Me.QuestItems, BuddyCron.Core.Me.Backpack })
		foreach (var it in list)
		{
			if (it == null || !it.IsValid) continue;
			ulong ab = 0, tpl = 0;
			try { ab = it.AbilitySpecId; tpl = it.TemplateId; } catch { }
			if (p.Ability != 0 && ab == p.Ability) return it;
			if (byTemplate == null && p.Template != 0 && tpl == p.Template) byTemplate = it;
			if (byCast == null && p.Cast.Length > 0)
			{
				string an = """", ln = """";
				try { var a = it.Ability; if (a != null) { an = a.Name ?? """"; ln = a.LocalizedName ?? """"; } } catch { }
				if (an == p.Cast || ln == p.Cast) byCast = it;
			}
			if (byName == null && p.Item.Length > 0 && it.Name == p.Item) byName = it;
		}
	if (byTemplate != null) return byTemplate;
	if (p.Ability != 0 || p.Template != 0) return null;
	return byCast ?? byName;
}

// what the item goes on: the exact one, else one of the recorded type near the spot; when the game knows the item's
// target rule, only a target the game accepts for it. One the item already went on in this step comes last (BuddyCron's
// UseItem never uses an item on the same one twice), but is still taken when it is the only one there.
static BuddyCron.Objects.HeroObject QrFindTarget(QrStepDef p, QrStepState s, BuddyCron.Objects.HeroItem item, System.DateTime now)
{
	if (p.Specs.Length == 0 && p.Nodes.Length == 0 && p.Names.Length == 0) return null;
	bool checkRule = false;
	try { checkRule = item != null && item.UseTargetMarkers.Count > 0; } catch { }
	BuddyCron.Objects.HeroObject best = null, bestUsed = null;
	foreach (var o in BuddyCron.Managers.HeroObjectManager.GetObjects<BuddyCron.Objects.HeroObject>())
	{
		if (o == null || !o.IsValid || o is BuddyCron.Objects.HeroPlayer || QrSkipped(s, o.NodeId, now)) continue;
		var c = o as BuddyCron.Objects.HeroCharacter;
		if (c != null && c.IsDead != p.DeadOnly) continue;
		if (System.Array.IndexOf(p.Nodes, o.NodeId) < 0 && !QrHas(p.Specs, QrSpec(o)) && !QrNamed(p, o)) continue;
		if (p.Spots.Length > 0 && QrSpotDist(p, o.Location) > 3f) continue;
		if (checkRule)
		{
			bool ok = true;
			try { ok = item.IsValidUseTarget(o); } catch { }
			if (!ok) continue;
		}
		if (s.Used.Contains(o.NodeId)) { if (bestUsed == null || o.Distance < bestUsed.Distance) bestUsed = o; }
		else if (best == null || o.Distance < best.Distance) best = o;
	}
	return best ?? bestUsed;
}

// a refusal that only says ""not now"" (the global cooldown, the item's own cooldown, a cast or a move in progress)
static bool QrBusyRefusal(BuddyCron.effResult r)
{
	return r == BuddyCron.effResult.NotReady || r == BuddyCron.effResult.IsCasting || r == BuddyCron.effResult.IsChanneling
		|| r == BuddyCron.effResult.IsMoving || r == BuddyCron.effResult.CasterStunned;
}

// one item use (or a quest ability, or putting an item on): find it, find what it goes on, walk there, press it and follow
// the press the way BuddyCron's own UseItem does: a press the game refuses is pressed again (every 0.15 s for 2 s), an
// accepted press must start the item's cast within 1 s, and a cast that ends with more than 1 s left was broken off (not a
// failed try). Returns true while it is busy. p is the part (the step itself, or one part of an item row).
static bool QrDoItem(QrStepDef d, QrStepDef p, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	if (me.IsCasting)
	{
		ulong spec = 0;
		bool looting = false;
		try { spec = me.CastingAbilitySpecId; looting = me.IsLooting; } catch { }
		// a cast that started late, after the press was already judged as starting none: it is that press after all
		if (!s.Pressed && s.PressSpec != 0 && spec == s.PressSpec && (now - s.PressedAt).TotalSeconds < 5) { s.Pressed = true; s.SawCast = false; }
		if (s.Pressed)
		{
			// the item's cast: its own ability, or any cast that replaced what was in flight at the press (not looting)
			if (!s.SawCast && spec != 0 && !looting && (spec == s.PressSpec || spec != s.CastBefore)) { s.SawCast = true; s.CastSpec = spec; }
			if (s.SawCast && spec == s.CastSpec) { try { s.CastLeft = me.CastTimeRemaining; } catch { } }
		}
		return true;
	}
	if (s.Pressed)
	{
		ulong on = s.PressNode;
		if (s.SawCast && s.CastLeft > 1f || !s.SawCast && s.PressInFight)
		{
			// broken off (a stun, a knock-back, a fight that started on it): not a failed try, the same target again
			s.Pressed = false;
			QrRefund(s, on);
			s.Next = now.AddSeconds(1);
			QrSay(d, ""an item use was broken off"" + (s.SawCast ? "" with "" + s.CastLeft.ToString(""0.0"") + "" s of the cast left"" : "" by a fight"") + "": not counted, trying again"");
			return true;
		}
		if (!s.SawCast)
		{
			if ((now - s.PressedAt).TotalSeconds < 1.0) return true;
			// the game took the press but no cast started (live: a press right as the global cooldown ended): press again;
			// the first two such presses are not counted
			s.Pressed = false;
			if (s.Duds < 2) QrRefund(s, on);
			s.Duds++;
			if (d.Once && d.Kind != QrRow && s.Duds >= 3) { QrEnd(d, s, ""done"", ""pressed it three times and no cast started (a later step moves the mission on)""); return false; }
			s.Next = now.AddSeconds(0.5);
			return true;
		}
		// used
		s.Pressed = false;
		s.Duds = 0;
		s.PressSpec = 0;
		if (on != 0) s.Used.Add(on);
		if (d.Once && d.Kind != QrRow) { QrEnd(d, s, ""done"", ""did it once (a later step moves the mission on)""); return false; }
		if (d.Kind == QrRow) s.RowAt++;
		if (s.MoveOn) { s.SpotAt++; s.MoveOn = false; } // used where you stood: the next use at the next spot
		s.Next = now.AddSeconds(d.Kind == QrRow ? 1.5 : 3);
		return true;
	}
	if (now < s.Next) return true;
	BuddyCron.Objects.HeroItem item = null;
	BuddyCron.Objects.HeroAbility ability = null;
	if (p.Kind == QrAbility)
	{
		bool have = false;
		try
		{
			// by its id; a recording that has only the combat log's name for it: by that name (an exact match only)
			if (p.Ability != 0) have = BuddyCron.Managers.AbilityManager.HasAbility(p.Ability, out ability);
			else if (p.Cast.Length > 0) have = BuddyCron.Managers.AbilityManager.HasAbility(p.Cast, out ability, true);
		}
		catch { ability = null; have = false; }
		if (!have || ability == null)
		{
			// the bar is not open yet: its mission item opens it
			ability = null;
			var opener = p.Item.Length > 0 || p.Template != 0 ? QrFindItem(new QrStepDef { Item = p.Item, Template = p.Template, Cast = p.Cast }) : null;
			if (opener != null) { opener.Use(); s.Tries++; s.Next = now.AddSeconds(3); return true; }
			s.MissingSecs++;
			s.Next = now.AddSeconds(1);
			if (s.MissingSecs > 20) { QrEnd(d, s, ""gave up"", ""the ability is not on your bars""); return false; }
			return true;
		}
	}
	else
	{
		item = QrFindItem(p);
		if (item == null)
		{
			// not in your bags (yet): wait a little for it, then give up
			s.MissingSecs++;
			s.Next = now.AddSeconds(1);
			if (s.MissingSecs > 20) { QrEnd(d, s, ""gave up"", ""the item is not in your mission items or bags""); return false; }
			return true;
		}
		if (p.Kind == QrEquip && item.IsEquipped) { QrEnd(d, s, ""done"", ""it is on""); return false; }
	}
	s.MissingSecs = 0;
	var target = QrFindTarget(p, s, item, now);
	if ((p.Specs.Length > 0 || p.Nodes.Length > 0 || p.Names.Length > 0) && target == null)
		return QrRoam(p.Spots.Length > 0 ? p : d, s, me, now);
	// used where you stood (no target): at each of your spots in turn
	int here = d.Kind == QrRow || p.Spots.Length == 0 ? 0 : s.SpotAt % p.Spots.Length;
	if (target == null && p.Spots.Length > 0 && !QrWalkTo(d, s, me, p.Spots[here], now))
		return true;
	var ch = target as BuddyCron.Objects.HeroCharacter;
	if (target != null)
	{
		if (p.HealthAt > 0f && ch != null && ch.HealthPercent > p.HealthAt)
		{
			// weaken it first: BuddyCron fights it, the item goes on once its health is down
			if (!me.InCombat) BuddyCron.Helpers.Poi.Current = new BuddyCron.Helpers.Poi(ch, BuddyCron.Helpers.PoiType.Kill);
			return false;
		}
		bool inRange = target.Distance <= p.Range;
		try { if (item != null && item.CanUseOn(target)) inRange = true; } catch { }
		// the game said ""too far"" or ""can't see it"" for this one: walk right up to it
		if (s.CloseIn == target.NodeId) inRange = target.Distance <= System.Math.Min(p.Range, 0.3f);
		if (!inRange) { QrApproach(d, s, me, target, now); return true; }
	}
	BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
	ulong before = 0;
	try { before = me.CastingAbilitySpecId; } catch { }
	ulong pressSpec = 0;
	bool accepted = true;
	BuddyCron.effResult result = BuddyCron.effResult.Ok;
	if (ability != null)
	{
		try { pressSpec = ability.SpecId; } catch { }
		BuddyCron.Managers.AbilityManager.Cast(ability, ch ?? (BuddyCron.Objects.HeroCharacter)me);
	}
	else
	{
		try { pressSpec = item.AbilitySpecId; } catch { }
		if (ch != null)
		{
			var r = item.UseAbility(ch);
			accepted = r.Item1;
			result = r.Item2;
		}
		else
			item.Use(); // the game reports nothing back for this one: the cast decides
	}
	if (!accepted)
	{
		string on = """";
		try { on = target != null ? target.Name ?? """" : """"; } catch { }
		string why = (on.Length > 0 ? "" on "" + on : """") + "" ("" + result + "")"";
		if (s.LastRefusal != why) { s.LastRefusal = why; QrSay(d, ""the game refused the item"" + why); }
		if (result == BuddyCron.effResult.NotFacing) { try { me.Face(target.Location); } catch { } }
		if (s.RefusedSince == System.DateTime.MinValue) s.RefusedSince = now;
		double refusedFor = (now - s.RefusedSince).TotalSeconds;
		// pressed again every 0.15 s for 2 s, as BuddyCron's UseItem does (most refusals are the global cooldown)
		if (refusedFor < 2) { s.Next = now.AddSeconds(0.15); return true; }
		if (QrBusyRefusal(result))
		{
			// still ""not now"" after 2 s (a long cooldown): keep pressing once a second, one try per 10 s of it
			s.Next = now.AddSeconds(1);
			if (refusedFor >= 12) { s.Tries++; s.RefusedSince = now.AddSeconds(-2); }
			return true;
		}
		s.RefusedSince = System.DateTime.MinValue;
		s.Tries++;
		s.Next = now.AddSeconds(1);
		if (target != null)
		{
			if ((result == BuddyCron.effResult.TooFar || result == BuddyCron.effResult.TargetViewBlocked || result == BuddyCron.effResult.NotFacing) && s.CloseIn != target.NodeId)
				s.CloseIn = target.NodeId; // walk right up to it and press again
			else
				s.SkipUntil[target.NodeId] = now.AddSeconds(30); // the game won't take the item on this one: another first
		}
		return true;
	}
	s.RefusedSince = System.DateTime.MinValue;
	s.LastRefusal = """";
	s.Tries++;
	s.Pressed = true;
	s.SawCast = false;
	s.CastLeft = 0f;
	s.CastSpec = 0;
	s.CastBefore = before;
	s.PressSpec = pressSpec;
	s.PressInFight = false;
	s.PressNode = target != null ? target.NodeId : 0;
	s.PressedAt = now;
	s.MoveOn = target == null && d.Kind != QrRow && p.Spots.Length > 1;
	bool instant = false;
	try { var ab = ability ?? item.Ability; instant = ab != null && ab.CastingTime <= 0f && ab.ChannelingTime <= 0f; } catch { }
	if (instant) s.SawCast = true; // no cast to watch: the accepted press is the use
	if (target != null && d.Kind != QrRow) QrCountTry(s, target.NodeId, 2, 30, now);
	return true;
}

static bool QrDoRow(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	if (d.Row.Length == 0) { QrEnd(d, s, ""gave up"", ""nothing in the row""); return false; }
	if (s.RowAt >= d.Row.Length)
	{
		// the whole row is done: wait for the count, then go round again
		if (s.ArrivedAt == System.DateTime.MinValue) s.ArrivedAt = now;
		if ((now - s.ArrivedAt).TotalSeconds < 8) return true;
		s.ArrivedAt = System.DateTime.MinValue;
		s.RowAt = 0;
		return true;
	}
	return QrDoItem(d, d.Row[s.RowAt], s, me, now);
}

static bool QrDoHolocom(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	var st = me.Quests.GetState(d.Quest);
	if (st != null && st.HolocomIsWaiting && now >= s.Next)
	{
		me.Quests.AnswerHolocomCall(d.Quest);
		s.Tries++;
		s.Next = now.AddSeconds(5);
		return true;
	}
	// no call yet: each 30 s without one is a try
	if (s.ArrivedAt == System.DateTime.MinValue) s.ArrivedAt = now;
	if ((now - s.ArrivedAt).TotalSeconds > 30) { s.Tries++; s.ArrivedAt = System.DateTime.MinValue; }
	return true;
}

// a mission transport: press it (found by its use ability id, else its cast's or its own name) until you are where it took
// you when you recorded it: in its area (Area, when it took you to another one), else within Range of Spots[0]
static bool QrDoHop(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	ulong area = 0;
	try { area = me.AreaId; } catch { }
	bool there = d.Area != 0 ? area == d.Area : d.Spots.Length > 0 && QrSpotDist(d, me.Location) <= d.Range;
	if (there) { QrEnd(d, s, ""done"", ""landed""); return false; }
	if (me.IsCasting) { s.SawCast = true; return true; }
	if (now < s.Next) return true;
	if (d.Spots.Length == 0 && s.Tries > 0) { QrEnd(d, s, ""done"", ""used once (where it takes you was not recorded)""); return false; }
	var item = QrFindItem(d);
	if (item == null)
	{
		s.MissingSecs++;
		s.Next = now.AddSeconds(1);
		if (s.MissingSecs > 20) { QrEnd(d, s, ""gave up"", ""the transport is not in your mission items or bags""); return false; }
		return true;
	}
	s.MissingSecs = 0;
	BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
	item.Use();
	s.Tries++;
	s.Next = now.AddSeconds(8); // the cast, then the jump
	return true;
}

// defend a spot, walk somewhere, or (a step the recording couldn't tie to anything you did) wait where it ticked
static bool QrDoStand(QrStepDef d, QrStepState s, BuddyCron.Objects.HeroLocalPlayer me, System.DateTime now)
{
	if (d.Spots.Length == 0)
	{
		if (d.Kind == QrWalk) { QrEnd(d, s, ""done"", ""nowhere to walk""); return false; }
		if (s.ArrivedAt == System.DateTime.MinValue) s.ArrivedAt = now;
	}
	else
	{
		int last = d.Spots.Length - 1;
		int at = d.Kind == QrWalk ? System.Math.Min(s.SpotAt, last) : 0;
		if (!QrWalkTo(d, s, me, d.Spots[at], now))
		{
			s.ArrivedAt = System.DateTime.MinValue;
			return true;
		}
		if (d.Kind == QrWalk)
		{
			if (s.SpotAt < last) { s.SpotAt++; return true; }
			QrEnd(d, s, ""done"", """");
			return false;
		}
		BuddyCron.Navigation.Navigator.PlayerMover.MoveStop();
		if (s.ArrivedAt == System.DateTime.MinValue) s.ArrivedAt = now;
	}
	if ((now - s.ArrivedAt).TotalSeconds > d.WaitAtSpot)
	{
		QrEnd(d, s, ""gave up"", ""waited "" + d.WaitAtSpot.ToString(""0"") + "" s "" + (d.Kind == QrDefend ? ""defending the spot"" : ""where it ticked"") + "" and the mission did not move on"");
		return false;
	}
	return true;
}";
    }
}
