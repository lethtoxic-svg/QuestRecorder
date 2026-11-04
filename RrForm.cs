// Quest Recorder - made by LethToxic.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace QuestRecorder
{
    /// <summary>The Quest Recorder window.</summary>
    internal sealed class RrForm : Form
    {
        // The hotkey callback may come on any thread: it reads the note from here, and the window clears the box.
        private static volatile string _noteForHotkey = string.Empty;
        private static int _noteTaken;

        private readonly QuestRecorderPlugin _plugin;
        private readonly Label _status;
        private readonly TextBox _note;
        private readonly Button _start;
        private readonly Button _stop;
        private readonly Button _marker;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly TextBox _tracker;
        private readonly TextBox _actions;
        private string[] _shownActions;
        private string[] _shownTrace;
        private bool _wasRunning;

        public RrForm(QuestRecorderPlugin plugin)
        {
            _plugin = plugin;
            Text = "Quest Recorder " + QuestRecorderPlugin.Patch;
            ClientSize = new Size(640, 620);
            MinimumSize = new Size(400, 460);
            StartPosition = FormStartPosition.CenterScreen;
            
            _status = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 96,
                Padding = new Padding(6, 6, 6, 2),
                Text = "Not recording.",
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(4),
            };
            _start = MakeButton("Start recording", OnStartClick);
            _stop = MakeButton("Stop recording", OnStopClick);
            _marker = MakeButton("Add marker", OnMarkerClick);
            buttons.Controls.Add(_start);
            buttons.Controls.Add(_stop);
            buttons.Controls.Add(_marker);
            buttons.Controls.Add(MakeButton("Open recordings folder", OnFolderClick));
            buttons.Controls.Add(MakeButton("Make profile from a recording", OnMakeProfileClick));
            buttons.Controls.Add(MakeButton("Control panel", OnFixProfileClick));
            buttons.Controls.Add(MakeButton("Send report", OnReportClick));
            buttons.Controls.Add(MakeButton("Settings", OnSettingsClick));
            buttons.Controls.Add(MakeButton("Updates", OnUpdatesClick));
            buttons.Controls.Add(MakeButton("Undo update", OnUndoUpdateClick));

            var noteRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(4, 0, 4, 4),
            };
            noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            var noteLabel = new Label { Text = "Marker note:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            _note = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3) };
            _note.TextChanged += OnNoteChanged;
            _note.KeyDown += OnNoteKeyDown;
            noteRow.Controls.Add(noteLabel, 0, 0);
            noteRow.Controls.Add(_note, 1, 0);

            // Docking lays out from the last added control.
            _tracker = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                Text = "Task tracker: each quest task you complete shows here with what the profile will do for it.\r\n" +
                       "A line with !! means no action was caught for that task - redo it or press Add marker there.",
            };
            _tracker.DoubleClick += OnTrackerDoubleClick;
            var trackerLabel = new Label { Dock = DockStyle.Top, Height = 18, Padding = new Padding(6, 2, 6, 0), Text = "Task tracker (live) - double-click a line to fix that step" };
            _actions = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                Text = "What you did: every click, item, reply and window shows here as you play, with what it did in your quest log.\r\n" +
                       "OK = the bot does it by itself.  !! = it can't, and the line says why - so you know while you're still there.",
            };
            // two tabs: what you did (every action), and the task tracker (what the profile does per quest task)
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var actionsPage = new TabPage("What you did (live)");
            actionsPage.Controls.Add(_actions);
            var trackerPage = new TabPage("Task tracker");
            trackerPage.Controls.Add(_tracker);
            trackerPage.Controls.Add(trackerLabel);
            _tracker.BringToFront();
            tabs.TabPages.Add(actionsPage);
            tabs.TabPages.Add(trackerPage);

            Controls.Add(tabs);
            Controls.Add(noteRow);
            Controls.Add(buttons);
            Controls.Add(_status);
            tabs.BringToFront();
            Shown += OnFirstShown;

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += OnTick;
            _timer.Start();
            FormClosed += OnFormClosed;
            RefreshStatus();
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3) };
            b.Click += click;
            return b;
        }

        /// <summary>Called by the marker hotkey (any thread): the note typed in the window, which is then cleared.</summary>
        internal static string TakeNoteForHotkey()
        {
            string n = _noteForHotkey ?? string.Empty;
            if (n.Length > 0)
            {
                _noteForHotkey = string.Empty;
                Interlocked.Exchange(ref _noteTaken, 1);
            }
            return n;
        }

        private void OnNoteChanged(object sender, EventArgs e)
        {
            _noteForHotkey = _note.Text ?? string.Empty;
        }

        private void OnNoteKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && Recorder.IsRecording)
            {
                e.SuppressKeyPress = true;
                OnMarkerClick(sender, EventArgs.Empty);
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                _timer.Stop();
                _timer.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (Interlocked.Exchange(ref _noteTaken, 0) == 1)
                    _note.Clear();
                RefreshStatus();
            }
            catch (Exception ex)
            {
                if (RrLog.Every("form-tick", TimeSpan.FromMinutes(5)))
                    RrLog.Debug("Window refresh failed: {0}", ex.Message);
            }
        }

        private void RefreshStatus()
        {
            RecorderStatus st = Recorder.Status;
            bool running = Recorder.IsRecording;
            if (_wasRunning && !running)
                _plugin.OnRecordingEnded(); // the recording ended by itself or from elsewhere: drop the hotkey
            _wasRunning = running;

            _start.Enabled = !running;
            _stop.Enabled = running && !st.Stopping;
            _marker.Enabled = running && !st.Stopping;

            var sb = new StringBuilder();
            if (running)
            {
                sb.Append(st.Stopping ? "Stopping, writing the files..." : "Recording " + Fmt.Duration(st.Seconds));
                if (st.Place.Length > 0)
                    sb.Append(" on ").Append(st.Place);
                sb.Append(" - ").Append(st.StateText).Append("\r\n");
                sb.Append(Fmt.Format("{0} events: {1} quest updates, {2} kills, {3} markers{4}\r\n", st.Events, st.QuestEvents, st.Kills,
                    st.Markers, st.Warnings > 0 ? ", " + st.Warnings.ToString() + " warnings" : string.Empty));
                if (st.LastEvent.Length > 0)
                    sb.Append("Last: ").Append(st.LastEvent).Append("\r\n");
                if (st.BaseName.Length > 0)
                    sb.Append("Files: ").Append(Path.Combine(st.Folder, st.BaseName)).Append(" (.txt, .csv)");
            }
            else
            {
                sb.Append("Not recording. Stop the bot, click Start recording, then play by hand.\r\n");
                if (st.Result.Length > 0)
                    sb.Append("Last recording: ").Append(st.Result);
            }
            if (Updater.UpdateAvailable)
                sb.Append("\r\nUpdate available: Quest Recorder ").Append(Updater.LatestVersion).Append(" - click Updates.");
            string text = sb.ToString();
            if (_status.Text != text)
                _status.Text = text;

            string[] acts = RecordingSession.LiveActions;
            if (acts != null && acts.Length > 0 && !ReferenceEquals(acts, _shownActions))
            {
                _shownActions = acts;
                _actions.Text = string.Join("\r\n", acts);
                _actions.SelectionStart = _actions.TextLength;
                _actions.ScrollToCaret();
            }

            string[] trace = RecordingSession.LiveTrace;
            if (trace != null && trace.Length > 0 && !ReferenceEquals(trace, _shownTrace))
            {
                _shownTrace = trace;
                _tracker.Text = string.Join("\r\n", trace);
                _tracker.SelectionStart = _tracker.TextLength;
                _tracker.ScrollToCaret();
            }
        }

        private void OnStartClick(object sender, EventArgs e)
        {
            try
            {
                string msg = _plugin.StartRecording();
                if (!string.IsNullOrEmpty(msg))
                    RrLog.Info("{0}", msg);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                RrLog.Exception("Starting the recording", ex);
            }
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            try
            {
                bool addingBit = Merge.TargetPath != null;
                _plugin.StopRecording("you clicked Stop");
                RefreshStatus();
                if (addingBit)
                    return; // "Record from here": the bit goes into the other profile by itself
                ShowSummary();
            }
            catch (Exception ex)
            {
                RrLog.Exception("Stopping the recording", ex);
            }
        }

        private void OnMarkerClick(object sender, EventArgs e)
        {
            try
            {
                string note = _note.Text ?? string.Empty;
                if (Recorder.AddMarker(note, "button"))
                {
                    _note.Clear();
                }
                else
                {
                    RrLog.Info("Not recording: click Start recording first.");
                }
            }
            catch (Exception ex)
            {
                RrLog.Exception("Adding the marker", ex);
            }
        }

        private void OnFolderClick(object sender, EventArgs e)
        {
            string dir = null;
            try
            {
                dir = RecordingSession.ResolveFolder();
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                RrLog.Warn("Could not open {0}: {1}", dir ?? "the recordings folder", ex.Message);
            }
        }

        /// <summary>Updates: check, show what's new, install only when the player says yes.</summary>
        private async void OnUpdatesClick(object sender, EventArgs e)
        {
            try
            {
                if (!Updater.UpdateAvailable)
                {
                    Updater.CheckInBackground();
                    await System.Threading.Tasks.Task.Delay(3000);
                }
                if (!Updater.UpdateAvailable)
                {
                    string why = Updater.Problem.Length > 0 ? "\r\n\r\n(Could not reach the update page: " + Updater.Problem + ")" : string.Empty;
                    MessageBox.Show(this, "You have the latest Quest Recorder (" + QuestRecorderPlugin.Patch + ")." + why, "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string news = Updater.WhatsNew.Length > 0 ? "\r\n\r\nWhat's new:\r\n" + Updater.WhatsNew : string.Empty;
                if (MessageBox.Show(this, "Quest Recorder " + Updater.LatestVersion + " is available (you have " + QuestRecorderPlugin.Patch + ")." + news +
                                          "\r\n\r\nInstall it now? Your current version is backed up first.", "Quest Recorder update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                string msg = await Updater.InstallAsync();
                RrLog.Info(msg.Replace("\r\n", " "));
                MessageBox.Show(this, msg, "Quest Recorder update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                RrLog.Exception("Updating Quest Recorder", ex);
                MessageBox.Show(this, "The update did not work: " + ex.Message + "\r\nNothing was lost; your current version is still there.", "Quest Recorder update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnUndoUpdateClick(object sender, EventArgs e)
        {
            try
            {
                if (MessageBox.Show(this, "Go back to the Quest Recorder version you had before the last update?", "Quest Recorder", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                string msg = Updater.UndoLast();
                RrLog.Info(msg);
                MessageBox.Show(this, msg, "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                RrLog.Exception("Undoing the update", ex);
            }
        }

        private ProfileFixerForm _fixer;

        /// <summary>Profile Fixer: fix a profile without opening the XML.</summary>
        private void OnFixProfileClick(object sender, EventArgs e)
        {
            try
            {
                if (_fixer == null || _fixer.IsDisposed)
                    _fixer = new ProfileFixerForm();
                _fixer.Show();
                _fixer.Activate();
            }
            catch (Exception ex)
            {
                RrLog.Exception("Opening the Profile Fixer", ex);
            }
        }

        /// <summary>Pick a recording (.csv): the plugin builds the ready profile from it, no need to play it again.</summary>
        private void OnMakeProfileClick(object sender, EventArgs e)
        {
            try
            {
                string dir = RecordingSession.ResolveFolder();
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Pick a recording (.csv) to make a profile from";
                    dlg.Filter = "Quest Recorder recordings (*.csv)|*.csv";
                    if (Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                    ReplayBuilder.CreditName = RrSettings.Instance.CreditName;
                    ReplayBuilder.WalkSpacing = RrSettings.Instance.WalkPointMeters;
                    string path = RecordingLoader.MakeProfile(dlg.FileName, AppDomain.CurrentDomain.BaseDirectory, RrSettings.Instance.ProfileFolder);
                    RrLog.Info("Made a profile from {0}: {1}", Path.GetFileName(dlg.FileName), path);
                    MessageBox.Show(this, "Profile made from your recording:\r\n\r\n" + path + "\r\n\r\nLoad it in BuddyCron to run it.", "Quest Recorder",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                RrLog.Exception("Making a profile from a recording", ex);
                MessageBox.Show(this, "Could not make the profile: " + ex.Message, "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Double-click a tracker line: open the control panel at that step.</summary>
        private void OnTrackerDoubleClick(object sender, EventArgs e)
        {
            try
            {
                int line = _tracker.GetLineFromCharIndex(_tracker.SelectionStart);
                if (line < 0 || line >= _tracker.Lines.Length)
                    return;
                string text = _tracker.Lines[line];
                int dot = text.IndexOf(". ", StringComparison.Ordinal);
                int arrow = text.IndexOf("  ->  ", StringComparison.Ordinal);
                if (dot < 0 || arrow <= dot)
                    return;
                string label = text.Substring(dot + 2, arrow - dot - 2).Trim();
                string profile = RecordingSession.LiveProfile;
                if (string.IsNullOrEmpty(profile) || !File.Exists(profile))
                {
                    MessageBox.Show(this, "There's no profile for these lines yet. Record a bit more first.", "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (_fixer == null || _fixer.IsDisposed)
                    _fixer = new ProfileFixerForm();
                _fixer.Show();
                _fixer.Activate();
                _fixer.OpenAt(profile, label);
            }
            catch (Exception ex)
            {
                RrLog.Exception("Opening that step", ex);
            }
        }

        /// <summary>Send report: pack a recording, its profile and the log into one zip for Discord.</summary>
        private void OnReportClick(object sender, EventArgs e)
        {
            try
            {
                string dir = RecordingSession.ResolveFolder();
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Pick the recording (.csv) you want to report";
                    dlg.Filter = "Quest Recorder recordings (*.csv)|*.csv";
                    if (Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                    string zip = Reports.MakeReport(dlg.FileName, "What went wrong: (write it in Discord with this file)");
                    RrLog.Info("Report saved: {0}", zip);
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + zip + "\"", UseShellExecute = true });
                    MessageBox.Show(this, "Report saved:\r\n\r\n" + zip + "\r\n\r\nIt has the recording, the profile and your BuddyCron log, with your character name taken out. " +
                                          "Drop it in Discord and say what went wrong.", "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                RrLog.Exception("Making the report", ex);
                MessageBox.Show(this, "Couldn't make the report: " + ex.Message, "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>After Stop: how many steps were caught, what was missed, and a way straight to the fix.</summary>
        private void ShowSummary()
        {
            string profile = RecordingSession.LiveProfile;
            if (string.IsNullOrEmpty(profile) || !File.Exists(profile))
                return;
            string[] trace = RecordingSession.LiveTrace ?? new string[0];
            var missed = new System.Collections.Generic.List<string>();
            foreach (string line in trace)
            {
                if (line.IndexOf("!!", StringComparison.Ordinal) < 0)
                    continue;
                int dot = line.IndexOf(". ", StringComparison.Ordinal);
                int arrow = line.IndexOf("  ->  ", StringComparison.Ordinal);
                missed.Add(dot >= 0 && arrow > dot ? line.Substring(dot + 2, arrow - dot - 2).Trim() : line.Trim());
            }
            int caught = trace.Length - missed.Count;
            // the review list: every step in plain words - untick, reorder, "do by hand", save
            try
            {
                string head = missed.Count == 0
                    ? "Nice - " + caught + " step(s) caught, nothing missed. Your profile: " + Path.GetFileName(profile)
                    : caught + " step(s) caught, " + missed.Count + " with no proof (yellow). Your profile: " + Path.GetFileName(profile);
                var review = new ReviewForm(profile, head);
                review.Show(this);
                return;
            }
            catch (Exception ex)
            {
                RrLog.Debug("Review list: {0}", ex.Message); // fall back to the old summary below
            }
            string where = "\r\n\r\nYour profile: " + profile;
            if (missed.Count == 0)
            {
                if (MessageBox.Show(this, "Nice - " + caught + " step(s) caught, nothing missed." + where + "\r\n\r\nLoad it in BuddyCron with Load Profile. Show you the file?",
                        "Quest Recorder", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + profile + "\"", UseShellExecute = true });
                return;
            }
            string list = string.Join("\r\n- ", missed.GetRange(0, Math.Min(6, missed.Count)).ToArray()) + (missed.Count > 6 ? "\r\n- ..." : string.Empty);
            if (MessageBox.Show(this, caught + " step(s) caught, " + missed.Count + " missed:\r\n- " + list + where +
                                      "\r\n\r\nGo back and do those again while recording, or fix them now. Fix the first one now?",
                    "Quest Recorder", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            if (_fixer == null || _fixer.IsDisposed)
                _fixer = new ProfileFixerForm();
            _fixer.Show();
            _fixer.Activate();
            _fixer.OpenAt(profile, missed[0]);
        }

        /// <summary>First time the window opens: three lines on how it works.</summary>
        private void OnFirstShown(object sender, EventArgs e)
        {
            try
            {
                if (RrSettings.Instance.SeenWelcome)
                    return;
                RrSettings.Instance.SeenWelcome = true;
                MessageBox.Show(this,
                    "Welcome to Quest Recorder!\r\n\r\n" +
                    "1. Stop the bot and just play. It records what you do (Start / Stop recording).\r\n\r\n" +
                    "2. When you stop, your profile is ready in Profiles\\Recorded. Load it in BuddyCron and let it run.\r\n\r\n" +
                    "3. Bot missed something? Double-click that line in the task tracker, target the thing in the game and press a button.\r\n\r\n" +
                    "Ctrl+Shift+Z undoes a misclick while you record. Stuck? Press Send report and drop it in Discord.",
                    "Quest Recorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                RrLog.Exception("Showing the welcome", ex);
            }
        }

        private Form _settings;

        /// <summary>The settings, in their own window so the main one stays simple.</summary>
        private void OnSettingsClick(object sender, EventArgs e)
        {
            try
            {
                if (_settings == null || _settings.IsDisposed)
                {
                    _settings = new Form { Text = "Quest Recorder - Settings", ClientSize = new Size(520, 560), StartPosition = FormStartPosition.CenterParent };
                    var grid = new PropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false, PropertySort = PropertySort.Categorized, HelpVisible = true };
                    grid.PropertyValueChanged += OnPropertyValueChanged;
                    grid.SelectedObject = RrSettings.Instance;
                    _settings.Controls.Add(grid);
                }
                _settings.Show(this);
                _settings.Activate();
            }
            catch (Exception ex)
            {
                RrLog.Exception("Opening the settings", ex);
            }
        }

        private void OnPropertyValueChanged(object s, PropertyValueChangedEventArgs e)
        {
            try
            {
                if (e.ChangedItem == null || e.ChangedItem.PropertyDescriptor == null)
                    return;
                string name = e.ChangedItem.PropertyDescriptor.Name;
                if (name == nameof(RrSettings.MarkerHotkey))
                {
                    if (Recorder.IsRecording)
                        _plugin.ApplyHotkey();
                }
                else if (name == nameof(RrSettings.VerboseLogging))
                {
                    RrLog.Verbose = RrSettings.Instance.VerboseLogging;
                }
            }
            catch (Exception ex)
            {
                RrLog.Exception("Applying the setting", ex);
            }
        }
    }
}
