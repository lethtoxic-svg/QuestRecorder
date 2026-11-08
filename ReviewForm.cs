// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace QuestRecorder
{
    /// <summary>
    /// After Stop: your profile as a list of steps in plain words. Untick a step to leave it out, move it up or down, or
    /// mark it "do by hand" (the bot walks there and waits for you). Steps with no proof (the game moved on but nothing
    /// you did was seen) are yellow. Save writes the profile - no XML for you to touch.
    /// </summary>
    internal sealed class ReviewForm : Form
    {
        private sealed class Item
        {
            public XmlComment Comment;
            public XmlElement Element;
            public bool NoProof;
            public bool Keep = true;
        }

        private readonly string _path;
        private readonly XmlDocument _doc = new XmlDocument();
        private readonly XmlElement _questing;
        private readonly List<Item> _items = new List<Item>();
        private readonly ListView _list;
        private bool _changed;
        private bool _filling;
        private readonly ICollection<string> _unsure; // labels of the steps the recorder isn't sure about (from the task tracker)

        public ReviewForm(string profilePath, string headline, ICollection<string> unsure)
        {
            _path = profilePath;
            _unsure = unsure;
            _doc.PreserveWhitespace = false;
            _loadedStamp = Stamp(profilePath);
            _doc.Load(profilePath);
            _questing = _doc.SelectSingleNode("/Profile/Questing") as XmlElement;

            Text = "Quest Recorder - your profile, step by step";
            Width = 900;
            Height = 620;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;

            var top = new Label
            {
                Dock = DockStyle.Top,
                Height = 58,
                Padding = new Padding(8, 6, 8, 0),
                Text = headline + "\r\nUntick a step to leave it out. Up / Down to move it. \"Do by hand\" = the bot walks there and waits for you. " +
                       "Yellow = no proof: the game moved on but nothing you did was seen there (the bot will only walk there). Save when you're done.",
            };
            _list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            _list.Columns.Add("Step", 430);
            _list.Columns.Add("What the bot does", 420);
            _list.ItemChecked += (s, e) =>
            {
                if (_filling)
                    return;
                ((Item)e.Item.Tag).Keep = e.Item.Checked;
                _changed = true;
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(Btn("Up", (s, e) => Move(-1)));
            buttons.Controls.Add(Btn("Down", (s, e) => Move(1)));
            buttons.Controls.Add(Btn("Do by hand", (s, e) => ByHand()));
            buttons.Controls.Add(Btn("Save", (s, e) => Save()));
            buttons.Controls.Add(Btn("Close", (s, e) => Close()));

            Controls.Add(_list);
            Controls.Add(buttons);
            Controls.Add(top);
            Load_();
            FormClosing += OnClosing;
        }

        private static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(4) };
            b.Click += click;
            return b;
        }

        private void Load_()
        {
            _items.Clear();
            if (_questing == null)
                return;
            XmlComment pending = null;
            foreach (XmlNode n in _questing.ChildNodes)
            {
                if (n is XmlComment)
                    pending = (XmlComment)n;
                else if (n is XmlElement)
                {
                    var el = (XmlElement)n;
                    // yellow: nothing you did was seen for it, or the recorder marked it "not sure" (the game doesn't
                    // count what it would do)
                    bool unsure = pending != null && (pending.Value.IndexOf("(not sure", StringComparison.OrdinalIgnoreCase) >= 0 || IsUnsure(pending.Value.Trim()));
                    _items.Add(new Item { Comment = pending, Element = el, NoProof = unsure || NoProof(el) });
                    pending = null;
                }
            }
            Fill();
        }

        /// <summary>The recorder marked this step's label "not sure" in the task tracker.</summary>
        private bool IsUnsure(string label)
        {
            if (_unsure == null)
                return false;
            foreach (string u in _unsure)
                if (label == u || label == Fmt.XmlComment(u))
                    return true;
            return false;
        }

        /// <summary>A step whose only action is walking to where the game moved on: nothing you did was seen.</summary>
        private static bool NoProof(XmlElement el)
        {
            // (a step that ticked when you arrived is a real "go there" step - only a count with no action is unproven)
            if (el.GetAttribute("Condition").IndexOf("IsTaskValueGreaterThan", StringComparison.Ordinal) < 0)
                return false;
            var moves = el.SelectNodes(".//MoveTo[@Name='Where it ticked']");
            if (moves == null || moves.Count == 0)
                return false;
            foreach (XmlNode n in el.SelectNodes(".//*"))
                if (n.Name != "MoveTo" && n.Name != "If")
                    return false;
            return true;
        }

        private void Fill()
        {
            _filling = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (Item it in _items)
            {
                string name = it.Comment != null ? it.Comment.Value.Trim() : it.Element.Name;
                var row = new ListViewItem(name) { Checked = it.Keep, Tag = it };
                row.SubItems.Add(Describe(it.Element));
                if (it.NoProof)
                    row.BackColor = Color.LightYellow;
                _list.Items.Add(row);
            }
            _list.EndUpdate();
            _filling = false;
        }

        /// <summary>What the bot does in this step, in plain words.</summary>
        private static string Describe(XmlElement el)
        {
            var parts = new List<string>();
            foreach (XmlNode n in el.SelectNodes(".//*"))
            {
                var e = n as XmlElement;
                if (e == null)
                    continue;
                string name = e.GetAttribute("Name");
                switch (e.Name)
                {
                    case "PickUpQuest":
                    case "PickUpIfOffered": parts.Add("pick up from " + e.GetAttribute("GiverName")); break;
                    case "TurnInQuest": parts.Add("hand in"); break;
                    case "CompleteQuest": parts.Add("take the reward"); break;
                    case "UseObject": parts.Add("click " + e.GetAttribute("ObjectName")); break;
                    case "Interact":
                    case "ClickObject": parts.Add("click " + name); break;
                    case "TalkTo": parts.Add("talk to " + name); break;
                    case "TalkAndReply": parts.Add("talk to " + name + ", replies " + e.GetAttribute("Replies")); break;
                    case "UseItem":
                    case "UseMissionItemOnce": parts.Add("use " + (e.GetAttribute("AbilityName").Length > 0 ? e.GetAttribute("AbilityName") : e.GetAttribute("ItemName"))); break;
                    case "UseQuestAbility": parts.Add("use ability " + e.GetAttribute("AbilityName")); break;
                    case "EquipItem": parts.Add("put on " + e.GetAttribute("ItemName")); break;
                    case "While": parts.Add("wait here until it moves on"); break;
                    case "Grind": parts.Add("fight"); break;
                    case "UseTransport": parts.Add("lift to " + e.GetAttribute("Option")); break;
                    case "UserDialog": parts.Add("PAUSE: you do it"); break;
                    case "WaitTimer": parts.Add("wait " + e.GetAttribute("WaitTime") + " s"); break;
                    case "MoveTo": if (name == "Where it ticked") parts.Add("walk to where it moved on"); break;
                }
            }
            if (parts.Count == 0)
                parts.Add(el.Name);
            var seen = new List<string>();
            foreach (string p in parts)
                if (!seen.Contains(p))
                    seen.Add(p);
            return string.Join(", ", seen.ToArray());
        }

        private void Move(int by)
        {
            if (_list.SelectedIndices.Count == 0)
                return;
            int i = _list.SelectedIndices[0];
            int j = i + by;
            if (j < 0 || j >= _items.Count)
                return;
            Item t = _items[i];
            _items[i] = _items[j];
            _items[j] = t;
            Fill();
            _list.Items[j].Selected = true;
            _list.EnsureVisible(j);
            _changed = true;
        }

        /// <summary>The bot walks to this step's spot and waits for you to do it, then you press Continue.</summary>
        private void ByHand()
        {
            if (_list.SelectedIndices.Count == 0)
                return;
            int i = _list.SelectedIndices[0];
            Item it = _items[i];
            XmlElement spotEl = it.Element.SelectSingleNode(".//*[@XYZ]") as XmlElement;
            string xyz = spotEl != null ? spotEl.GetAttribute("XYZ") : string.Empty;
            string what = it.Comment != null ? it.Comment.Value.Trim() : "this step";
            XmlElement target = it.Element.Name == "If" ? it.Element : null;
            if (target == null)
            {
                MessageBox.Show(this, "This step can't be done by hand (it isn't a quest step).", "Quest Recorder");
                return;
            }
            target.RemoveAll_KeepAttributes();
            if (xyz.Length > 0)
            {
                XmlElement mv = _doc.CreateElement("MoveTo");
                mv.SetAttribute("QuestId", "1");
                mv.SetAttribute("Name", "Do it by hand here");
                mv.SetAttribute("XYZ", xyz);
                target.AppendChild(mv);
            }
            XmlElement ud = _doc.CreateElement("UserDialog");
            ud.SetAttribute("Title", "Quest Recorder");
            ud.SetAttribute("Message", "Do this by hand: " + what + " - then press Continue.");
            target.AppendChild(ud);
            it.NoProof = false;
            Fill();
            _list.Items[i].Selected = true;
            _changed = true;
        }

        // when the file was read (time + size): Save never writes over a newer file
        private string _loadedStamp;

        private static string Stamp(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.LastWriteTimeUtc.Ticks + "/" + fi.Length : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void Save()
        {
            if (_questing == null)
                return;
            // the file changed after this list read it (the recorder finished saving, or another review saved): writing
            // this list's copy would throw the newer steps away (Black Hole: supplies 4-5 and two rewards lost)
            if (Stamp(_path) != _loadedStamp)
            {
                MessageBox.Show(this, "Your profile file changed after this list opened (the recorder saved newer steps).\r\n\r\n" +
                                      "Nothing was saved, so nothing is lost. Close this list and open the review again to see the newest version.",
                                "Quest Recorder");
                return;
            }
            try
            {
                // keep a copy of what it was
                string dir = Path.Combine(Path.GetDirectoryName(_path) ?? ".", "_fixer backups");
                Directory.CreateDirectory(dir);
                File.Copy(_path, Path.Combine(dir, Path.GetFileNameWithoutExtension(_path) + " (before review " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ").xml"), true);

                foreach (Item it in _items)
                {
                    if (it.Comment != null && it.Comment.ParentNode == _questing)
                        _questing.RemoveChild(it.Comment);
                    if (it.Element.ParentNode == _questing)
                        _questing.RemoveChild(it.Element);
                }
                for (int i = 0; i < _items.Count; i++)
                {
                    if (!_items[i].Keep)
                        continue; // left out
                    if (_items[i].Comment != null)
                        _questing.AppendChild(_items[i].Comment);
                    _questing.AppendChild(_items[i].Element);
                }
                var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", NewLineChars = "\r\n", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true };
                using (XmlWriter w = XmlWriter.Create(_path, settings))
                    _doc.Save(w);
                _loadedStamp = Stamp(_path); // our own save: a second Save from this list is fine
                _changed = false;
                RrLog.Info("Profile saved from your review: {0}", _path);
                MessageBox.Show(this, "Saved. Load it in BuddyCron with Load Profile.", "Quest Recorder");
                Load_(); // what's in the file now
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save: " + ex.Message, "Quest Recorder");
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_changed && MessageBox.Show(this, "You changed steps but didn't save. Close anyway?", "Quest Recorder", MessageBoxButtons.YesNo) != DialogResult.Yes)
                e.Cancel = true;
        }
    }

    internal static class XmlExtras
    {
        /// <summary>Removes the children, keeps the attributes (the step's condition).</summary>
        public static void RemoveAll_KeepAttributes(this XmlElement el)
        {
            while (el.HasChildNodes)
                el.RemoveChild(el.FirstChild);
        }
    }
}
