// Quest Recorder - made by LethToxic.
using System;
using System.Text;
using System.Windows.Forms;
using System.Windows.Input;
using BuddyCron.Inheritables;
using BuddyCron.Profiles;
using Reborn.Utilities;

namespace QuestRecorder
{
    /// <summary>Quest Recorder: you play by hand.</summary>
    public class QuestRecorderPlugin : BotPlugin
    {
        /// <summary>The patch name, SWTOR style: 1.0, 1.1, 1.1a, 1.2.1 ...</summary>
        internal const string Patch = "1.4";

        internal static readonly Version PluginVersion = Updater.ToVersion(Patch);

        private const string HotkeyName = "Quest Recorder add marker";
        private const int StopWaitMs = 3000;

        /// <summary>The loaded plugin (the control panel uses it to start a recording).</summary>
        internal static QuestRecorderPlugin Current;

        private readonly object _formSync = new object();
        private RrForm _form;
        private bool _hotkeyRegistered;

        public override string Author
        {
            get { return "LethToxic"; }
        }

        public override string Name
        {
            get { return "Quest Recorder"; }
        }

        public override Version Version
        {
            get { return PluginVersion; }
        }

        public override string Description
        {
            get
            {
                return "Play it once and it makes the profile. Records what you do by hand and turns it into a questing profile, " +
                       "with a control panel to fix it - no XML needed. By LethToxic.";
            }
        }

        public override bool WantButton
        {
            get { return true; }
        }

        public override string ButtonText
        {
            get { return "Quest Recorder"; }
        }

        // ------------------------------------------------------------------ lifecycle

        public override void OnInitialize()
        {
            Current = this;
            RrLog.Debug("Quest Recorder {0} loaded.", Patch);
            StuckWatch.Start();
            Updater.CheckInBackground(); // check for updates as soon as BuddyCron starts, ticked or not
        }

        public override void OnEnabled()
        {
            try
            {
                RrLog.ResetRateLimits();
                RrLog.Verbose = RrSettings.Instance.VerboseLogging;
                // recording starts only when you ask for it (never by itself when BuddyCron opens: it recorded the bot)
                RrLog.Info("Quest Recorder {0} on. Click the Quest Recorder button to start recording (with the bot stopped).", Patch);
            }
            catch (Exception ex)
            {
                RrLog.Exception("Enabling Quest Recorder", ex);
            }
        }

        public override void OnDisabled()
        {
            // Called on BuddyCron's window thread: only ask the recording to stop.
            StopEverything("the plugin was disabled", false, false, 0);
        }

        public override void OnShutdown()
        {
            StuckWatch.Stop();
            StopEverything("BuddyCron is closing", true, true, StopWaitMs);
        }

        public override void Dispose()
        {
            StopEverything("the plugin was unloaded", true, true, StopWaitMs);
            base.Dispose();
        }

        /// <param name="waitMs">0 = do not wait; otherwise wait (bounded) for the recorder thread to finish.</param>
        private void StopEverything(string why, bool quick, bool closeForm, int waitMs)
        {
            try
            {
                UnregisterHotkey();
            }
            catch
            {
                // never throw from a lifecycle call
            }
            try
            {
                // On unload / shutdown wait.
                if (waitMs > 0)
                    Recorder.StopAndWait(waitMs, why, quick);
                else
                    Recorder.RequestStop(why, quick);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Stopping the recorder failed: {0}", ex.Message);
            }
            if (!closeForm)
                return;
            RrForm form;
            lock (_formSync)
            {
                form = _form;
                _form = null;
            }
            if (form == null)
                return;
            try
            {
                if (form.IsDisposed)
                    return;
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(form.Close));
                else
                    form.Close();
            }
            catch
            {
                // the window is already gone
            }
        }

        // ------------------------------------------------------------------ window

        public override void OnButtonPress()
        {
            try
            {
                RrForm form;
                lock (_formSync)
                {
                    if (_form == null || _form.IsDisposed)
                        _form = new RrForm(this);
                    form = _form;
                }
                form.Show();
                form.Activate();
            }
            catch (ArgumentOutOfRangeException)
            {
                // WinForms quirk when the window was on a monitor that is gone
            }
            catch (Exception ex)
            {
                RrLog.Exception("Opening the Quest Recorder window", ex);
            }
        }

        // ------------------------------------------------------------------ recording (UI thread)

        internal string StartRecording()
        {
            string msg;
            // a new recording: the tracker, the review list and the control panel never show the last one's profile
            if (!Recorder.IsRecording)
                RecordingSession.ClearLive();
            if (!Recorder.Start(out msg))
                return msg;
            ApplyHotkey();
            bool botRunning = false;
            try
            {
                botRunning = Recorder.BotOwnsGame();
            }
            catch
            {
                botRunning = false;
            }
            return botRunning
                ? "Recording started. The bot is running: the recorder waits until you stop it."
                : "Recording started. Play by hand; click Stop recording when you are done.";
        }

        internal void StopRecording(string why)
        {
            Recorder.RequestStop(why, false);
            UnregisterHotkey();
        }

        /// <summary>The window saw the recording end (for example after an error): the hotkey goes too.</summary>
        internal void OnRecordingEnded()
        {
            UnregisterHotkey();
        }

        // ------------------------------------------------------------------ hotkey (only while recording)

        /// <summary>(Re)registers the Add marker hotkey from the settings. Call from the UI thread.</summary>
        internal void ApplyHotkey()
        {
            UnregisterHotkey();
            if (!Recorder.IsRecording)
                return;
            ApplyUndoHotkey();
            ApplySpotHotkeys();
            Keys combo;
            try
            {
                combo = RrSettings.Instance.MarkerHotkey;
            }
            catch
            {
                return;
            }
            if (combo == Keys.None)
                return;
            Keys key = combo & Keys.KeyCode;
            Keys mods = combo & Keys.Modifiers;
            if (key == Keys.None || mods == Keys.None)
            {
                RrLog.Warn("Marker hotkey {0} ignored: use Ctrl, Alt or Shift together with a key.", combo);
                return;
            }
            ModifierKeys modifiers = ModifierKeys.None;
            if ((mods & Keys.Control) == Keys.Control)
                modifiers |= ModifierKeys.Control;
            if ((mods & Keys.Shift) == Keys.Shift)
                modifiers |= ModifierKeys.Shift;
            if ((mods & Keys.Alt) == Keys.Alt)
                modifiers |= ModifierKeys.Alt;
            try
            {
                HotkeyManager.Register(HotkeyName, key, modifiers, hk => OnHotkey());
                _hotkeyRegistered = true;
                RrLog.Info("Press {0} to add a marker at your position (the window's note box names it).", KeyText(combo));
            }
            catch (Exception ex)
            {
                RrLog.Exception("Registering the marker hotkey", ex);
            }
        }

        private const string UndoHotkeyName = "Quest Recorder undo last action";
        private bool _undoRegistered;

        /// <summary>"Undo last action": right after a misclick, the profile forgets your last object click.</summary>
        private void ApplyUndoHotkey()
        {
            Keys combo;
            try
            {
                combo = RrSettings.Instance.UndoHotkey;
            }
            catch
            {
                return;
            }
            Keys key = combo & Keys.KeyCode;
            Keys mods = combo & Keys.Modifiers;
            if (combo == Keys.None || key == Keys.None || mods == Keys.None)
                return;
            ModifierKeys modifiers = ModifierKeys.None;
            if ((mods & Keys.Control) == Keys.Control)
                modifiers |= ModifierKeys.Control;
            if ((mods & Keys.Shift) == Keys.Shift)
                modifiers |= ModifierKeys.Shift;
            if ((mods & Keys.Alt) == Keys.Alt)
                modifiers |= ModifierKeys.Alt;
            try
            {
                HotkeyManager.Register(UndoHotkeyName, key, modifiers, hk => OnUndoHotkey());
                _undoRegistered = true;
                RrLog.Info("Press {0} right after a misclick to undo your last object click.", KeyText(combo));
            }
            catch (Exception ex)
            {
                RrLog.Exception("Registering the undo hotkey", ex);
            }
        }

        private const string PauseHotkeyName = "Quest Recorder pause here";
        private const string WaitHotkeyName = "Quest Recorder wait here";
        private bool _spotKeysRegistered;

        /// <summary>Pause here (default Ctrl+Shift+P): the profile pauses here for you. Wait here (default Ctrl+Shift+W):
        /// the bot waits here 10 s (a respawn, a door). Each from the settings, each on its own (one taken by another
        /// program never stops the other).</summary>
        private void ApplySpotHotkeys()
        {
            Keys pause = Keys.Control | Keys.Shift | Keys.P;
            Keys wait = Keys.Control | Keys.Shift | Keys.W;
            try
            {
                pause = RrSettings.Instance.PauseHotkey;
                wait = RrSettings.Instance.WaitHotkey;
            }
            catch
            {
                // the defaults
            }
            var said = new System.Collections.Generic.List<string>();
            if (RegisterSpot(PauseHotkeyName, pause, () => Spot(ReplayBuilder.PauseNote, "Pause added here - the bot will stop here for you.")))
                said.Add(KeyText(pause) + " = pause here (you do it by hand)");
            if (RegisterSpot(WaitHotkeyName, wait, () => Spot(ReplayBuilder.WaitNote, "Wait added here - the bot will wait here 10 seconds.")))
                said.Add(KeyText(wait) + " = wait here 10 s");
            if (said.Count > 0 && _undoRegistered)
                said.Add(KeyText(RrSettings.Instance.UndoHotkey) + " = ignore your last action");
            if (said.Count > 0)
                RrLog.Info("{0}.", string.Join(", ", said.ToArray()));
        }

        private bool RegisterSpot(string name, Keys combo, Action pressed)
        {
            Keys key = combo & Keys.KeyCode;
            Keys mods = combo & Keys.Modifiers;
            if (combo == Keys.None || key == Keys.None)
                return false;
            if (mods == Keys.None)
            {
                RrLog.Warn("Hotkey {0} ignored: use Ctrl, Alt or Shift together with a key.", combo);
                return false;
            }
            ModifierKeys modifiers = ModifierKeys.None;
            if ((mods & Keys.Control) == Keys.Control)
                modifiers |= ModifierKeys.Control;
            if ((mods & Keys.Shift) == Keys.Shift)
                modifiers |= ModifierKeys.Shift;
            if ((mods & Keys.Alt) == Keys.Alt)
                modifiers |= ModifierKeys.Alt;
            try
            {
                HotkeyManager.Register(name, key, modifiers, hk => pressed());
                _spotKeysRegistered = true;
                return true;
            }
            catch (Exception ex)
            {
                RrLog.Warn("{0}: {1} can't be used ({2}). Pick another key in the settings.", name, KeyText(combo), ex.Message);
                return false;
            }
        }

        private static void Spot(string note, string said)
        {
            try
            {
                if (Recorder.AddMarker(note, "hotkey"))
                    RrLog.Info("{0}", said);
            }
            catch (Exception ex)
            {
                RrLog.Debug("{0} hotkey failed: {1}", note, ex.Message);
            }
        }

        private static void OnUndoHotkey()
        {
            try
            {
                if (Recorder.AddMarker(ReplayBuilder.UndoNote, "hotkey"))
                    RrLog.Info("Ignored - your last action (click, item or NPC) won't be in the profile.");
            }
            catch (Exception ex)
            {
                RrLog.Debug("Undo hotkey failed: {0}", ex.Message);
            }
        }

        private void UnregisterHotkey()
        {
            if (_undoRegistered)
            {
                _undoRegistered = false;
                try
                {
                    HotkeyManager.Unregister(UndoHotkeyName);
                }
                catch (Exception ex)
                {
                    RrLog.Debug("Unregistering the undo hotkey failed: {0}", ex.Message);
                }
            }
            if (_spotKeysRegistered)
            {
                _spotKeysRegistered = false;
                foreach (string n in new[] { PauseHotkeyName, WaitHotkeyName })
                {
                    try
                    {
                        HotkeyManager.Unregister(n);
                    }
                    catch (Exception ex)
                    {
                        RrLog.Debug("Unregistering {0} failed: {1}", n, ex.Message);
                    }
                }
            }
            if (!_hotkeyRegistered)
                return;
            _hotkeyRegistered = false;
            try
            {
                HotkeyManager.Unregister(HotkeyName);
            }
            catch (Exception ex)
            {
                RrLog.Debug("Unregistering the marker hotkey failed: {0}", ex.Message);
            }
        }

        private static void OnHotkey()
        {
            try
            {
                string note = RrForm.TakeNoteForHotkey();
                if (!Recorder.AddMarker(note, "hotkey"))
                    RrLog.Info("Not recording: the marker hotkey does nothing.");
            }
            catch (Exception ex)
            {
                RrLog.Debug("Marker hotkey failed: {0}", ex.Message);
            }
        }

        private static string KeyText(Keys combo)
        {
            var sb = new StringBuilder();
            if ((combo & Keys.Control) == Keys.Control)
                sb.Append("Ctrl+");
            if ((combo & Keys.Alt) == Keys.Alt)
                sb.Append("Alt+");
            if ((combo & Keys.Shift) == Keys.Shift)
                sb.Append("Shift+");
            sb.Append((combo & Keys.KeyCode).ToString());
            return sb.ToString();
        }
    }
}
