// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using BuddyCron;
using BuddyCron.Objects;

namespace QuestRecorder
{
    /// <summary>Profile control panel: fix or build a profile without opening the XML.</summary>
    internal sealed class ProfileFixerForm : Form
    {
        // what an action needs
        [Flags]
        private enum Need
        {
            None = 0,
            Target = 1,       // something targeted in the game
            Object = 2,       //...and it must be an object
            Lift = 4,         //...and it must be an elevator
            Quest = 8,        // a quest from your log (pick-up / hand-in / reward)
            Step = 16,        // "until this quest step is done" (required)
            StepOrOnce = 32,  // a quest step, or "just once"
            Detail = 64,      // the details box
        }

        private sealed class Act
        {
            public string Label;
            public Need Needs;
            public string DetailLabel;
            public bool Header;

            public override string ToString()
            {
                return Header ? "----- " + Label + " -----" : "   " + Label;
            }
        }

        private static readonly Act PickUp = new Act { Label = "Pick up a quest from my target (NPC or mission board)", Needs = Need.Target | Need.Quest };
        private static readonly Act HandIn = new Act { Label = "Hand in a quest to my target and take the reward", Needs = Need.Target | Need.Quest };
        private static readonly Act Reward = new Act { Label = "Take the reward of a finished quest", Needs = Need.Quest };
        private static readonly Act Holo = new Act { Label = "Answer a holocom call", Needs = Need.Quest };
        private static readonly Act Use = new Act { Label = "Click / use my target until the quest step is done", Needs = Need.Target | Need.Object | Need.Step };
        private static readonly Act Once = new Act { Label = "Click my target once (door, console, starts a fight)", Needs = Need.Target | Need.Object };
        private static readonly Act Talk = new Act { Label = "Talk to my target", Needs = Need.Target | Need.StepOrOnce };
        private static readonly Act Fight = new Act { Label = "Fight mobs like my target, here, until the quest step is done", Needs = Need.Target | Need.Step };
        private static readonly Act ItemHere = new Act { Label = "Use a mission item where I am standing", Needs = Need.StepOrOnce | Need.Detail, DetailLabel = "Mission item:" };
        private static readonly Act ItemOn = new Act { Label = "Use a mission item on mobs/NPCs like my target", Needs = Need.Target | Need.Step | Need.Detail, DetailLabel = "Mission item:" };
        private static readonly Act Walk = new Act { Label = "Walk to where I am standing", Needs = Need.StepOrOnce | Need.Detail, DetailLabel = "On the way:" };
        private static readonly Act Planet = new Act { Label = "Fly to another planet", Needs = Need.Detail, DetailLabel = "Planet:" };
        private static readonly Act Wait = new Act { Label = "Wait some seconds", Needs = Need.Detail, DetailLabel = "Seconds:" };
        private static readonly Act Msg = new Act { Label = "Pause and show a message (player presses Continue)", Needs = Need.Detail, DetailLabel = "Message:" };
        private static readonly Act Stop = new Act { Label = "Stop the bot here (end of the profile)", Needs = Need.Detail, DetailLabel = "Message:" };
        private static readonly Act LoadAct = new Act { Label = "Load another profile (pick the file when you add it)", Needs = Need.None };

        private static readonly Act[] All =
        {
            new Act { Label = "Quests", Header = true }, PickUp, HandIn, Reward, Holo,
            new Act { Label = "Doing things", Header = true }, Use, Once, Talk, Fight, ItemHere, ItemOn,
            new Act { Label = "Moving (taxis, elevators and quick travel are done by BuddyCron itself)", Header = true }, Walk, Planet,
            new Act { Label = "Bot control", Header = true }, Wait, Msg, Stop, LoadAct,
        };

        private const string WalkFight = "Fight anything that attacks";
        private const string WalkNoFight = "Don't stop to fight";

        private static readonly string[] Planets =
        {
            "Imperial Fleet", "Republic Fleet", "Korriban", "Tython", "Hutta", "Ord Mantell", "Dromund Kaas", "Coruscant",
            "Nar Shaddaa", "Tatooine", "Alderaan", "Balmorra", "Taris", "Quesh", "Hoth", "Belsavis", "Voss", "Corellia",
            "Ilum", "Makeb", "Oricon", "Rishi", "Yavin 4", "Ziost", "Odessen", "Iokath", "Ossus", "Onderon", "Mek-Sha",
            "Dantooine", "Manaan", "Elom", "Galactic Republic Base", "Imperial Base",
        };

        private sealed class QuestChoice
        {
            public ulong Id;
            public ulong Branch;
            public ulong Step;
            public string Name = string.Empty;
            public string Objective = string.Empty;

            public override string ToString()
            {
                if (Id == 0)
                    return "Just once / every time it gets here";
                return Name + " - until this step is done" + (Objective.Length > 0 ? " (" + Objective + ")" : " (step " + Step + ")");
            }
        }

        private readonly Label _file;
        private readonly ListBox _steps;
        private readonly ComboBox _action;
        private readonly Label _questLabel;
        private readonly ComboBox _quest;
        private readonly Label _extraLabel;
        private readonly ComboBox _extra;
        private readonly Label _game;
        private readonly Label _preview;
        private readonly Label _status;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly FlowLayoutPanel _simple;
        private readonly Control[] _advanced;
        private ProfileEditor _ed;
        private List<ProfileEditor.StepView> _views = new List<ProfileEditor.StepView>();
        private GameView _last = new GameView();
        private int _lastAction = -1;

        public ProfileFixerForm()
        {
            Text = "Quest Recorder - Profile Control Panel";
            ClientSize = new Size(820, 680);
            MinimumSize = new Size(640, 520);
            StartPosition = FormStartPosition.CenterScreen;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = true };
            top.Controls.Add(MakeButton("Open profile...", OnOpen));
            top.Controls.Add(MakeButton("Undo last fix", OnUndo));
            _file = new Label { AutoSize = true, Margin = new Padding(8, 8, 3, 3), Text = "No profile open." };
            top.Controls.Add(_file);

            _steps = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, Font = new Font(FontFamily.GenericMonospace, 8.5f) };

            var edit = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4), WrapContents = true };
            edit.Controls.Add(MakeButton("Remove step", OnRemove));
            edit.Controls.Add(MakeButton("Move up", (s, e) => OnMove(-1)));
            edit.Controls.Add(MakeButton("Move down", (s, e) => OnMove(1)));
            edit.Controls.Add(MakeButton("Replace step", OnReplace));
            edit.Controls.Add(MakeButton("Record from here", OnRecordFromHere));
            edit.Controls.Add(MakeButton("Check my profile", OnCheck));
            edit.Controls.Add(MakeButton("Share", OnShare));
            edit.Controls.Add(new Label { AutoSize = true, Margin = new Padding(12, 9, 3, 3), Text = "New steps go before the step you picked. Replace swaps it for what's set below." });

            var add = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
            add.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            add.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            add.Controls.Add(new Label { Text = "What should the bot do?", AutoSize = true, Margin = new Padding(3, 7, 3, 3), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
            _action = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 30 };
            _action.Items.AddRange(All);
            _action.SelectedIndexChanged += (s, e) => OnActionChanged();
            add.Controls.Add(_action, 1, 0);

            _questLabel = new Label { Text = "Until:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
            add.Controls.Add(_questLabel, 0, 1);
            _quest = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 20 };
            _quest.DropDown += (s, e) => LoadQuests();
            _quest.SelectedIndexChanged += (s, e) => UpdatePreview();
            add.Controls.Add(_quest, 1, 1);

            _extraLabel = new Label { Text = "Details:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
            add.Controls.Add(_extraLabel, 0, 2);
            _extra = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxDropDownItems = 20 };
            _extra.DropDown += (s, e) => FillDetails(false);
            _extra.TextChanged += (s, e) => UpdatePreview();
            add.Controls.Add(_extra, 1, 2);

            add.Controls.Add(new Label { Text = "In the game:", AutoSize = true, Margin = new Padding(3, 5, 3, 3) }, 0, 3);
            _game = new Label { AutoSize = true, Margin = new Padding(3, 5, 3, 3), ForeColor = Color.DimGray, Text = "-" };
            add.Controls.Add(_game, 1, 3);

            add.Controls.Add(new Label { Text = "The bot will:", AutoSize = true, Margin = new Padding(3, 7, 3, 3), Font = new Font(Font, FontStyle.Bold) }, 0, 4);
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var addBtn = MakeButton("Add step", OnAdd);
            addBtn.Font = new Font(Font, FontStyle.Bold);
            row.Controls.Add(addBtn);
            _preview = new Label { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(6, 8, 3, 3), Text = "-" };
            row.Controls.Add(_preview);
            add.Controls.Add(row, 1, 4);

            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(6, 4, 6, 4),
                Text = "1. Open a profile.  2. Pick a step in the list.  3. Remove / move it, or pick what the bot should do and click Add step.\r\n" +
                       "Every change is saved straight away with a backup: \"Undo last fix\" puts the previous version back.",
            };

            // Simple view: big buttons for the usual fixes. The full list is under "More options".
            _simple = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), WrapContents = true };
            _simple.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(780, 0),
                Margin = new Padding(3, 3, 3, 6),
                Font = new Font(Font.FontFamily, 10f),
                Text = "Pick the step in the list where something's missing. Then target the thing in the game and press a button - it goes in before that step.",
            });
            _simple.SetFlowBreak(_simple.Controls[0], true);
            _simple.Controls.Add(BigButton("Click this", (s, e) => QuickAdd(Use)));
            _simple.Controls.Add(BigButton("Talk to this", (s, e) => QuickAdd(Talk)));
            _simple.Controls.Add(BigButton("Fight these", (s, e) => QuickAdd(Fight)));
            _simple.Controls.Add(BigButton("Use my mission item", (s, e) => QuickAdd(ItemHere)));
            _simple.Controls.Add(BigButton("Walk here", (s, e) => QuickAdd(Walk)));
            _simple.Controls.Add(BigButton("Wait 5 sec", (s, e) => QuickAdd(Wait)));
            _simple.Controls.Add(BigButton("Record from here", OnRecordFromHere));
            _simple.Controls.Add(BigButton("Remove step", OnRemove));
            _simple.Controls.Add(BigButton("Undo", OnUndo));
            _simple.Controls.Add(BigButton("More options", (s, e) => ShowAdvanced(true)));
            _advanced = new Control[] { edit, add };
            edit.Controls.Add(MakeButton("Simple view", (s, e) => ShowAdvanced(false)));

            Controls.Add(_steps);
            Controls.Add(edit);
            Controls.Add(add);
            Controls.Add(_simple);
            Controls.Add(_status);
            Controls.Add(top);
            _steps.BringToFront();
            ShowAdvanced(false);

            LoadQuests();
            _action.SelectedIndex = 1;
            Say("Start with Open profile. Every change is saved straight away, and Undo puts it back.");

            // the game is read every second: target, position, items - no refresh button needed
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) => OnTick();
            _timer.Start();
            FormClosed += (s, e) => _timer.Stop();
        }

        private Button BigButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = false, Size = new Size(170, 40), Margin = new Padding(4), Font = new Font(Font.FontFamily, 10f, FontStyle.Bold) };
            b.Click += click;
            return b;
        }

        private void ShowAdvanced(bool on)
        {
            SuspendLayout();
            if (on)
                Say(Help(Current() ?? PickUp));
            foreach (Control c in _advanced)
                c.Visible = on;
            _simple.Visible = !on;
            ResumeLayout();
        }

        /// <summary>The quest of the picked step, where you are in it right now (from your quest log), or null.</summary>
        private QuestChoice QuestOfSelected()
        {
            ProfileEditor.StepView s = Selected();
            if (s == null || s.Node == null)
                return null;
            var ids = new List<ulong>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s.Node.OuterXml, @"0x([0-9A-Fa-f]{16})"))
            {
                ulong id;
                if (ulong.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id) && !ids.Contains(id))
                    ids.Add(id);
            }
            if (ids.Count == 0)
                return null;
            foreach (QuestChoice q in ReadGame(true).Quests)
                if (ids.Contains(q.Id))
                    return q;
            return null;
        }

        /// <summary>One big button: picks the action, the quest step and the details by itself, then adds the step.</summary>
        private void QuickAdd(Act a)
        {
            if (!Ready())
                return;
            if (Selected() == null && _steps.SelectedIndex < 0)
            {
                Say("Pick the step in the list first - the new one goes in before it.");
                return;
            }
            QuestChoice q = QuestOfSelected();
            if (q == null && (a == Use || a == Fight))
            {
                if (a == Fight)
                {
                    Say("I can't tell which quest this fight is for. Pick a step of that quest in the list (or use More options).");
                    return;
                }
                a = Once; // no quest to wait for: just click it once
            }
            _action.SelectedItem = a;
            LoadQuests();
            _quest.SelectedIndex = 0;
            if (q != null)
            {
                for (int i = 0; i < _quest.Items.Count; i++)
                {
                    var c = _quest.Items[i] as QuestChoice;
                    if (c != null && c.Id == q.Id)
                        _quest.SelectedIndex = i;
                }
            }
            if (a == Wait)
                _extra.Text = "5";
            else if (a == Walk)
                _extra.Text = WalkFight;
            else if (a == ItemHere)
            {
                FillDetails(false);
                if (_extra.Items.Count == 0)
                {
                    Say("You don't have any mission items right now.");
                    return;
                }
                if (_extra.Items.Count > 1 && q != null)
                {
                    // the mission item named after this quest if there is one (Heroic Transport: <mission>)
                    foreach (object it in _extra.Items)
                        if (q.Name.IndexOf(it.ToString().Replace("Heroic Transport: ", string.Empty), StringComparison.OrdinalIgnoreCase) >= 0)
                            _extra.Text = it.ToString();
                }
                if (_extra.Text.Length == 0)
                    _extra.Text = _extra.Items[0].ToString();
            }
            OnAdd(this, EventArgs.Empty);
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3) };
            b.Click += click;
            return b;
        }

        private void Say(string text)
        {
            _status.Text = text;
        }

        private Act Current()
        {
            return _action.SelectedItem as Act;
        }

        // ------------------------------------------------------------------ profile

        private void OnOpen(object sender, EventArgs e)
        {
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Pick the profile to fix";
                    dlg.Filter = "Profiles (*.xml)|*.xml";
                    string start = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", "Recorded");
                    if (Directory.Exists(start))
                        dlg.InitialDirectory = start;
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                    _ed = ProfileEditor.Open(dlg.FileName);
                    _file.Text = Path.GetFileName(dlg.FileName);
                    ShowSteps(0);
                    Say("Opened. Pick a step. Tip: load this profile again in BuddyCron after fixing it (BuddyCron only reads it when you load it).");
                }
            }
            catch (Exception ex)
            {
                Say("Could not open that profile: " + ex.Message);
            }
        }

        private void ShowSteps(int select)
        {
            _views = _ed != null ? _ed.Steps() : new List<ProfileEditor.StepView>();
            _steps.BeginUpdate();
            _steps.Items.Clear();
            for (int i = 0; i < _views.Count; i++)
                _steps.Items.Add((i + 1).ToString().PadLeft(3) + ". " + _views[i].Text);
            _steps.Items.Add("     (end of the profile)");
            _steps.EndUpdate();
            if (_steps.Items.Count > 0)
                _steps.SelectedIndex = Math.Max(0, Math.Min(select, _steps.Items.Count - 1));
        }

        private ProfileEditor.StepView Selected()
        {
            int i = _steps.SelectedIndex;
            return i >= 0 && i < _views.Count ? _views[i] : null;
        }

        private bool Ready()
        {
            if (_ed != null)
                return true;
            Say("Open a profile first.");
            return false;
        }

        private void OnRemove(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            ProfileEditor.StepView s = Selected();
            if (s == null)
            {
                Say("Pick the step to remove.");
                return;
            }
            if (MessageBox.Show(this, "Remove this step?\r\n\r\n" + s.Text, "Profile Control Panel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            int at = _steps.SelectedIndex;
            _ed.Remove(s);
            SaveAndShow(at, "Step removed.");
        }

        private void OnMove(int dir)
        {
            if (!Ready())
                return;
            ProfileEditor.StepView s = Selected();
            if (s == null)
            {
                Say("Pick the step to move.");
                return;
            }
            int at = _steps.SelectedIndex;
            _ed.Move(s, dir);
            SaveAndShow(at + dir, dir < 0 ? "Step moved up." : "Step moved down.");
        }

        private void OnUndo(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            try
            {
                string path = _ed.Path;
                if (ProfileEditor.UndoLast(path) == null)
                {
                    Say("Nothing to undo for this profile.");
                    return;
                }
                _ed = ProfileEditor.Open(path);
                ShowSteps(_steps.SelectedIndex);
                Say("Undone: the profile is back to how it was before the last fix.");
            }
            catch (Exception ex)
            {
                Say("Undo failed: " + ex.Message);
            }
        }

        private void SaveAndShow(int select, string what)
        {
            try
            {
                _ed.Save();
                string path = _ed.Path;
                _ed = ProfileEditor.Open(path); // re-read what was written
                ShowSteps(select);
                Say(what + " Saved (a backup was kept - \"Undo last fix\" puts it back). Load the profile again in BuddyCron to use it.");
            }
            catch (Exception ex)
            {
                Say("Saving failed: " + ex.Message);
            }
        }

        /// <summary>Opens a profile and picks the step whose label starts with this text (from the task tracker).</summary>
        public void OpenAt(string path, string label)
        {
            try
            {
                _ed = ProfileEditor.Open(path);
                _file.Text = Path.GetFileName(path);
                ShowSteps(0);
                for (int i = 0; i < _views.Count; i++)
                {
                    if (_views[i].Comment != null && _views[i].Comment.Value.Trim().StartsWith(label, StringComparison.Ordinal))
                    {
                        _steps.SelectedIndex = i;
                        _steps.TopIndex = Math.Max(0, i - 3);
                        Say("This is the step from the tracker. Replace it, or add what's missing before it.");
                        return;
                    }
                }
                Say("Opened the profile, but couldn't find that exact step - pick it from the list.");
            }
            catch (Exception ex)
            {
                Say("Could not open that profile: " + ex.Message);
            }
        }

        /// <summary>Record just the missing bit: what you play next goes into this profile before the picked step.</summary>
        private void OnRecordFromHere(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            QuestRecorderPlugin plugin = QuestRecorderPlugin.Current;
            if (plugin == null)
            {
                Say("Tick Quest Recorder in BuddyCron's plugin list first.");
                return;
            }
            ProfileEditor.StepView at = Selected();
            if (MessageBox.Show(this, "Record just the bit the bot got wrong?\r\n\r\nPlay only that part, then press Stop recording in the Quest Recorder window. " +
                                      "What you did goes into this profile " + (at != null ? "before:\r\n" + at.Text : "at the end") + ".",
                    "Record from here", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;
            try
            {
                Merge.Begin(_ed.Path, at != null ? at.Node.OuterXml : null, at != null && at.Comment != null ? at.Comment.Value.Trim() : null);
                if (Recorder.IsRecording)
                    Recorder.StopAndWait(4000, "starting Record from here", false);
                string msg = plugin.StartRecording();
                if (!Recorder.IsRecording)
                {
                    Merge.TargetPath = null;
                    Say("Couldn't start recording: " + msg);
                    return;
                }
                Say("Recording just this bit - play the missing part, then press Stop recording. It goes in by itself.");
            }
            catch (Exception ex)
            {
                Merge.TargetPath = null;
                Say("Couldn't start recording: " + ex.Message);
            }
        }

        private void OnReplace(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            ProfileEditor.StepView old = Selected();
            if (old == null)
            {
                Say("Pick the step to replace.");
                return;
            }
            try
            {
                GameView g = ReadGame(false);
                _last = g;
                string xml, what, problem;
                if (!Build(g, true, out xml, out what, out problem) || xml == null)
                {
                    Say(problem ?? "Something is missing.");
                    return;
                }
                if (MessageBox.Show(this, "Replace this step:\r\n\r\n" + old.Text + "\r\n\r\nwith: the bot will " + what, "Profile Control Panel", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                int at = _steps.SelectedIndex;
                _ed.Add(xml, "Changed with the control panel: " + what, old);
                _ed.Remove(old);
                SaveAndShow(at, "Replaced: the bot will " + what);
            }
            catch (Exception ex)
            {
                Say("Could not replace it: " + ex.Message);
            }
        }

        private void OnCheck(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            try
            {
                List<string> found = ProfileCheck.Check(_ed.Path);
                string msg = found.Count == 0
                    ? "Looks good - nothing obvious that would go wrong."
                    : "Things that will probably go wrong:\r\n\r\n- " + string.Join("\r\n- ", found.ToArray()) + "\r\n\r\nPick the step in the list and replace or remove it.";
                MessageBox.Show(this, msg, "Check my profile", MessageBoxButtons.OK, found.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Say("Could not check it: " + ex.Message);
            }
        }

        private void OnShare(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            try
            {
                string zip = Reports.Share(_ed.Path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + zip + "\"", UseShellExecute = true });
                Say("Ready to share: " + zip);
            }
            catch (Exception ex)
            {
                Say("Could not pack it: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ game reads (read only)

        private sealed class GameView
        {
            public bool InGame;
            public Vector3 Me;
            public bool HasTarget;
            public string TargetName = string.Empty;
            public Vector3 TargetPos;
            public bool TargetIsObject;
            public bool TargetIsLift;
            public List<string> Floors = new List<string>();
            public List<string> Items = new List<string>();
            public List<string> Bag = new List<string>();
            public List<QuestChoice> Quests = new List<QuestChoice>();
        }

        private static GameView ReadGame(bool withLists)
        {
            var g = new GameView();
            if (!Core.IsInitialized)
                return g;
            using (Core.Memory.AcquireFrame(true))
            {
                var me = Core.Me;
                if (me == null)
                    return g;
                g.InGame = true;
                g.Me = me.Location;
                // your target can be an NPC or an object: look it up by id (Core.Target only gives characters)
                HeroObject target = me.TargetId != 0 ? global::BuddyCron.Managers.HeroObjectManager.GetByNodeId(me.TargetId) : null;
                if (target != null && target.IsValid && target.Name != null)
                {
                    g.HasTarget = true;
                    g.TargetName = target.Name;
                    g.TargetPos = target.Location;
                    var plc = target as HeroPlaceable;
                    g.TargetIsObject = plc != null;
                    if (plc != null && plc.IsWonkavator)
                    {
                        g.TargetIsLift = true;
                        IReadOnlyList<string> d = plc.WonkavatorDestinations;
                        if (d != null)
                            foreach (string f in d)
                                g.Floors.Add(Fmt.OneLine(f));
                    }
                }
                try
                {
                    foreach (HeroItem item in me.QuestItems)
                    {
                        if (item == null || g.Items.Count >= 40)
                            continue;
                        string n = Fmt.OneLine(item.Name);
                        if (n.Length > 0 && !g.Items.Contains(n))
                            g.Items.Add(n);
                    }
                }
                catch
                {
                    // no mission items
                }
                if (!withLists)
                    return g;
                try
                {
                    foreach (HeroItem item in me.Backpack)
                    {
                        if (item == null || g.Bag.Count >= 120)
                            continue;
                        string n = Fmt.OneLine(item.Name);
                        if (n.Length > 0 && !g.Bag.Contains(n))
                            g.Bag.Add(n);
                    }
                    g.Bag.Sort(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    // bags not readable
                }
                foreach (QuestState st in me.Quests.ActiveQuestStates)
                {
                    if (st == null || st.QuestId == 0 || st.IsHidden)
                        continue;
                    g.Quests.Add(new QuestChoice { Id = st.QuestId, Branch = st.BranchId, Step = st.StepId, Name = Fmt.OneLine(st.DisplayName) });
                }
            }
            return g;
        }

        private void OnTick()
        {
            try
            {
                if (!Visible)
                    return;
                _last = ReadGame(false);
                if (!_last.InGame)
                    _game.Text = "Not in the game (or BuddyCron is not attached).";
                else
                    _game.Text = "Target: " + (_last.HasTarget ? _last.TargetName + (_last.TargetIsLift ? " (elevator)" : _last.TargetIsObject ? " (object)" : " (NPC / mob)") : "nothing") +
                                 "     You: " + Fmt.Format("{0:0},{1:0},{2:0}", _last.Me.X, _last.Me.Y, _last.Me.Z) +
                                 "     Mission items: " + _last.Items.Count;
                UpdatePreview();
            }
            catch
            {
                // reading the game can fail during loading screens; try again next second
            }
        }

        private void LoadQuests()
        {
            try
            {
                GameView g = ReadGame(true);
                object keep = _quest.SelectedItem;
                _quest.Items.Clear();
                Act a = Current();
                bool questOnly = a != null && (a.Needs & Need.Quest) != 0;
                if (!questOnly)
                    _quest.Items.Add(new QuestChoice());
                foreach (QuestChoice q in g.Quests)
                    _quest.Items.Add(q);
                if (_quest.Items.Count > 0)
                    _quest.SelectedIndex = 0;
                if (keep is QuestChoice)
                {
                    for (int i = 0; i < _quest.Items.Count; i++)
                        if (((QuestChoice)_quest.Items[i]).Id == ((QuestChoice)keep).Id)
                            _quest.SelectedIndex = i;
                }
            }
            catch (Exception ex)
            {
                Say("Could not read your quests from the game: " + ex.Message);
            }
        }

        private void OnActionChanged()
        {
            Act a = Current();
            if (a == null)
                return;
            if (a.Header)
            {
                // a group title: move to the first action under it
                int i = _action.SelectedIndex;
                int next = _lastAction > i && i > 0 ? i - 1 : i + 1;
                if (next >= 0 && next < _action.Items.Count)
                    _action.SelectedIndex = next;
                return;
            }
            _lastAction = _action.SelectedIndex;
            bool quest = (a.Needs & Need.Quest) != 0;
            bool step = (a.Needs & (Need.Step | Need.StepOrOnce)) != 0;
            _questLabel.Text = quest ? "Which quest:" : "Until:";
            _quest.Enabled = quest || step;
            _questLabel.Enabled = _quest.Enabled;
            LoadQuests();
            if ((a.Needs & Need.Step) != 0 && _quest.SelectedItem is QuestChoice && ((QuestChoice)_quest.SelectedItem).Id == 0 && _quest.Items.Count > 1)
                _quest.SelectedIndex = 1; // this action needs a quest step
            bool detail = (a.Needs & Need.Detail) != 0;
            _extraLabel.Text = detail ? a.DetailLabel : "Details:";
            _extra.Enabled = detail;
            _extraLabel.Enabled = detail;
            FillDetails(true);
            Say(Help(a));
            UpdatePreview();
        }

        private void FillDetails(bool reset)
        {
            Act a = Current();
            if (a == null)
                return;
            string keep = _extra.Text;
            _extra.Items.Clear();
            try
            {
                if (a == ItemHere || a == ItemOn)
                {
                    foreach (string it in ReadGame(false).Items)
                        _extra.Items.Add(it);
                }
                else if (a == Walk)
                {
                    _extra.Items.AddRange(new object[] { WalkFight, WalkNoFight });
                }
                else if (a == Planet)
                {
                    _extra.Items.AddRange(Planets);
                }
                else if (a == Wait)
                {
                    _extra.Items.AddRange(new object[] { "3", "5", "10", "20", "30", "60" });
                }
            }
            catch (Exception ex)
            {
                Say("Could not read the game: " + ex.Message);
            }
            if (!reset)
            {
                _extra.Text = keep;
                return;
            }
            if (a == Msg)
                _extra.Text = "Do this part by hand, then press Continue.";
            else if (a == Stop)
                _extra.Text = "This profile is finished.";
            else if (a == Wait)
                _extra.Text = "5";
            else if (_extra.Items.Count > 0)
                _extra.SelectedIndex = 0;
            else
                _extra.Text = string.Empty;
        }

        private static string Help(Act a)
        {
            if (a == PickUp)
                return "Pick up the quest in the game first (so it is in your log), target the giver or board, then choose the quest here.";
            if (a == HandIn)
                return "Target who you hand it in to and choose the quest. The bot hands it in and accepts the reward.";
            if (a == Reward)
                return "For quests that finish without a hand-in (heroics, bonus missions): the bot accepts the reward window.";
            if (a == Use)
                return "Target the object. The bot keeps using it until the quest step is done (and waits if it has to respawn).";
            if (a == Once)
                return "Target the object. The bot clicks it one time - for doors, consoles and things that start a fight.";
            if (a == Fight)
                return "Target one of the mobs. The bot fights that kind of mob around here until the quest step is done.";
            if (a == ItemOn)
                return "Target one of the mobs/NPCs, pick the mission item. The bot uses it on them until the quest step is done.";
            if (a == Walk)
                return "Stand where the bot should go. You don't need walk steps to reach a quest - BuddyCron finds the way (taxis, elevators and quick travel included). Use this for an exact spot, like a door or a corner.";
            if (a == Stop)
                return "Put this at the end: the bot shows the message and stops when the player presses Continue.";
            if (a == LoadAct)
                return "Click Add step, then pick the profile file. The bot switches to it when it gets here.";
            return "Fill in the details, then click Add step.";
        }

        // ------------------------------------------------------------------ preview + add

        private void UpdatePreview()
        {
            try
            {
                string xml, what, problem;
                Build(_last, false, out xml, out what, out problem);
                _preview.ForeColor = problem != null ? Color.Firebrick : SystemColors.ControlText;
                _preview.Text = problem ?? what;
            }
            catch (Exception ex)
            {
                _preview.ForeColor = Color.Firebrick;
                _preview.Text = ex.Message;
            }
        }

        /// <summary>Builds the step for the chosen action. Returns false with a reason when something is missing.</summary>
        private bool Build(GameView g, bool forReal, out string xml, out string what, out string problem)
        {
            xml = null;
            what = null;
            problem = null;
            Act a = Current();
            if (a == null || a.Header)
            {
                problem = "Pick what the bot should do.";
                return false;
            }
            var q = _quest.SelectedItem as QuestChoice ?? new QuestChoice();
            string detail = _extra.Text.Trim();
            string until = q.Id != 0 ? ", until \"" + q.Name + "\" step " + q.Step + " is done" : string.Empty;
            if ((a.Needs & Need.Target) != 0 && !g.HasTarget)
            {
                problem = "Target it in the game first.";
                return false;
            }
            if ((a.Needs & Need.Object) != 0 && !g.TargetIsObject)
            {
                problem = "Your target is an NPC or mob, not an object. Use \"" + Talk.Label + "\" or \"" + Fight.Label + "\".";
                return false;
            }
            if (((a.Needs & Need.Quest) != 0 || (a.Needs & Need.Step) != 0) && q.Id == 0)
            {
                problem = (a.Needs & Need.Quest) != 0 ? "Choose the quest (it has to be in your quest log)." : "Choose the quest step, so the bot knows when to stop.";
                return false;
            }
            if ((a.Needs & Need.Detail) != 0 && detail.Length == 0)
            {
                problem = "Fill in: " + a.DetailLabel.TrimEnd(':').ToLowerInvariant() + ".";
                return false;
            }
            float tx = g.TargetPos.X, ty = g.TargetPos.Y, tz = g.TargetPos.Z, mx = g.Me.X, my = g.Me.Y, mz = g.Me.Z;
            if (a == PickUp)
            {
                xml = ProfileEditor.PickUp(q.Name, q.Id, g.TargetName, g.TargetIsObject, tx, ty, tz);
                what = "pick up \"" + q.Name + "\" from " + g.TargetName + " (skipped if you already have it or did it).";
            }
            else if (a == HandIn)
            {
                xml = ProfileEditor.HandIn(q.Name, q.Id, g.TargetName, g.TargetIsObject, tx, ty, tz);
                what = "hand in \"" + q.Name + "\" to " + g.TargetName + " and take the reward.";
            }
            else if (a == Reward)
            {
                xml = ProfileEditor.Reward(q.Id);
                what = "wait for the reward window of \"" + q.Name + "\" and accept it.";
            }
            else if (a == Holo)
            {
                xml = ProfileEditor.Holocom(q.Name, q.Id);
                what = "answer the holocom call for \"" + q.Name + "\".";
            }
            else if (a == Use)
            {
                xml = ProfileEditor.ClickObject(g.TargetName, tx, ty, tz, q.Id, q.Branch, q.Step);
                what = "use " + g.TargetName + until + ".";
            }
            else if (a == Once)
            {
                xml = ProfileEditor.ClickObject(g.TargetName, tx, ty, tz, 0, 0, 0);
                what = "click " + g.TargetName + " once.";
            }
            else if (a == Talk)
            {
                xml = ProfileEditor.TalkTo(g.TargetName, tx, ty, tz, q.Id, q.Branch, q.Step);
                what = "talk to " + g.TargetName + until + ".";
            }
            else if (a == Fight)
            {
                xml = ProfileEditor.Fight(g.TargetName, tx, ty, tz, q.Id, q.Branch, q.Step);
                what = "fight " + g.TargetName + " mobs around here" + until + ".";
            }
            else if (a == ItemHere)
            {
                xml = ProfileEditor.UseMissionItem(detail, mx, my, mz, q.Id, q.Branch, q.Step);
                what = "use " + detail + " where you are standing" + until + ".";
            }
            else if (a == ItemOn)
            {
                xml = ProfileEditor.UseItemOn(detail, g.TargetName, mx, my, mz, q.Id, q.Branch, q.Step);
                what = "use " + detail + " on " + g.TargetName + " around here" + until + ".";
            }
            else if (a == Walk && detail == WalkNoFight)
            {
                xml = ProfileEditor.WalkNoFight(mx, my, mz, q.Id, q.Branch, q.Step);
                what = "walk to where you are standing without stopping to fight" + until + ".";
            }
            else if (a == Walk)
            {
                xml = ProfileEditor.WalkHere(mx, my, mz, q.Id, q.Branch, q.Step);
                what = "walk to where you are standing" + until + ".";
            }
            else if (a == Planet)
            {
                xml = ProfileEditor.FlyTo(detail);
                what = "fly to " + detail + ".";
            }
            else if (a == Wait)
            {
                int sec;
                if (!int.TryParse(detail, out sec) || sec <= 0)
                {
                    problem = "Type how many seconds to wait.";
                    return false;
                }
                xml = ProfileEditor.Wait(sec);
                what = "wait " + sec + " seconds.";
            }
            else if (a == Msg)
            {
                xml = ProfileEditor.Message(detail);
                what = "pause and show \"" + detail + "\" until the player presses Continue.";
            }
            else if (a == Stop)
            {
                xml = ProfileEditor.StopBot(detail);
                what = "show \"" + detail + "\" and stop the bot.";
            }
            else if (a == LoadAct)
            {
                what = "switch to the profile you pick.";
                if (forReal)
                {
                    string path = PickProfile();
                    if (path == null)
                    {
                        problem = "No profile picked.";
                        return false;
                    }
                    xml = ProfileEditor.LoadOther(path);
                    what = "switch to " + path + ".";
                }
            }
            else
            {
                problem = "Pick what the bot should do.";
                return false;
            }
            return true;
        }

        private string PickProfile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Pick the profile to switch to";
                dlg.Filter = "Profiles (*.xml)|*.xml";
                if (_ed != null)
                    dlg.InitialDirectory = Path.GetDirectoryName(_ed.Path);
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return null;
                // same folder: just the file name, so the pair of profiles can be moved together
                if (_ed != null && string.Equals(Path.GetDirectoryName(dlg.FileName), Path.GetDirectoryName(_ed.Path), StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dlg.FileName);
                return dlg.FileName;
            }
        }

        private void OnAdd(object sender, EventArgs e)
        {
            if (!Ready())
                return;
            try
            {
                GameView g = ReadGame(false);
                _last = g;
                string xml, what, problem;
                if (!Build(g, true, out xml, out what, out problem) || xml == null)
                {
                    Say(problem ?? "Something is missing.");
                    return;
                }
                var q = _quest.SelectedItem as QuestChoice ?? new QuestChoice();
                int at = _steps.SelectedIndex;
                _ed.Add(xml, "Added with the control panel: " + what + (q.Id != 0 && what.IndexOf(q.Name, StringComparison.Ordinal) < 0 ? " (" + q.Name + ")" : string.Empty), Selected());
                SaveAndShow(at, "Added: the bot will " + what);
            }
            catch (Exception ex)
            {
                Say("Could not add it: " + ex.Message);
            }
        }
    }
}
