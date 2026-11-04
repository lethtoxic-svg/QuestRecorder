Quest Recorder - BuddyCron plugin by LethToxic
==============================================

You play it once, it makes the profile. That's the idea.

Do a quest area by hand with the recorder on, and you get a profile that does it the same way you did.
Same stuff clicked, same order, same spots, same fights. The recorder only watches, it never touches
your character.


INSTALL
-------
Put the QuestRecorder folder in BuddyCron\Plugins and restart BuddyCron. Tick Quest Recorder in the
plugin list. Done.

If it doesn't show up, check the log for "Compiler Error" and send it to me.


RECORDING
---------
Stop the bot, tick Quest Recorder and it starts recording on its own. Then just play.

Ctrl+Shift+Z  - ignore your last action (a misclick, a wrong item - do it straight away)
Ctrl+Shift+P  - pause here: the bot will stop at this spot and let you do it by hand
Ctrl+Shift+W  - wait here: the bot waits 10 seconds at this spot (respawns, doors)
Ctrl+Shift+M  - drop a marker where you're standing (good for doors, corners, phase edges)

Hit "Stop recording" when you're done - it tells you what it caught and anything it missed.
Logged off halfway? Your next recording of the same place carries on in the same profile. If the bot runs for a bit (or you use Combat Assist) it just pauses,
it only records what you do yourself. Loading screens are fine.


YOUR PROFILE
------------
It gets saved while you play, in Profiles\Recorded\<planet>. Load that one in BuddyCron.

Some things it does for you:
- uses every object at the spot you used it, in your order, and never re-uses one that already counted
- fights where you fought and stops at the counts you got to
- talks, pick-ups, hand-ins, holocoms and rewards where you did them
- elevators to the floor you picked
- Heroic Transport shuttles, used the same way you did
- leaves the walking to BuddyCron (it finds the way between steps by itself)
- after a restart it carries on from where it was

Want your name on it? Set "Your name for credits" in the settings and every profile you make says
"Recorded by <you>".


TASK TRACKER
------------
The window shows each quest task as it ticks and what the profile will do for it. If you see "!!" the
recorder missed what you did there - just redo that bit while you're still there. With BuddyCron's
overlay on you get a heads-up in game as well.


OLD RECORDINGS
--------------
"Make profile from a recording" rebuilds a profile from any old recording (.csv) with the newest version
of the plugin. No need to play it again.


CONTROL PANEL
-------------
Fix or build a profile without touching the XML. Click "Control panel" and open a profile.

Pick the step where something's missing, target the thing in the game and press a big button:
Click this, Talk to this, Fight these, Use my mission item, Walk here or Wait. It works out the quest step by itself.

More options gives you everything else: move steps, and pick what the bot should do from the full list
(quests, clicking, fighting, mission items, walking, planets, waiting, stopping and so on).
No need to add taxis, elevators, quick travel, vendor runs or looting - BuddyCron does those on its own.
It reads your target and quests from the game, and tells you what the bot will do before you add it.

Replace step swaps the picked step for a new one. Check my profile tells you about steps that will probably
go wrong. Share packs the profile into a zip you can post.

Record from here: pick a step, press it, play just the missing bit, press Stop. It slots in before that step.

Tip: double-click a line in the task tracker to jump straight to that step. And if the bot gets stuck while
running a profile, a pop-up offers to take you right there.

Everything saves straight away and keeps a backup, so "Undo last fix" always gets you back.
Reload the profile in BuddyCron after changing it.


FILES
-----
"Open recordings folder" shows the raw recordings: a .txt you can read and a .csv the plugin uses.
The .txt has your character name in it, the profiles don't.


UPDATES
-------
When there's a new version you get a pop-up when BuddyCron starts. Click Yes, restart BuddyCron, done.
Your old version is kept in PluginBackups and "Undo update" puts it back.


TIPS
----
- It copies your mistakes too, so walk the route you actually want.
- If someone else just used "your" object, the bot waits for it to come back.
- Run a new profile once and watch it before you share it.
- Problems? Click "Send report" and drop the zip in Discord.
