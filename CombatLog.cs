// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuestRecorder
{
    /// <summary>Who a combat log field names.</summary>
    internal enum LogWho
    {
        None = 0,
        Me = 1,
        Companion = 2,
        Npc = 3,
        OtherPlayer = 4,
    }

    /// <summary>One side of a combat log line: "@Name#id|(x,y,z,h)|(hp/max)", "Mob {template}:instance|...".</summary>
    internal sealed class LogEnt
    {
        public LogWho Who;
        /// <summary>The name as the log writes it (never written to a profile: ids only).</summary>
        public string Name = string.Empty;
        /// <summary>The NPC's template id in the log's own numbering (not the spawner spec the bot reads).</summary>
        public ulong Template;
        /// <summary>This one NPC: the same number as the recording's target_id (HeroObject.NodeId).</summary>
        public ulong Instance;
        /// <summary>Where it was, in the recording's units (the log writes metres with y and z swapped).</summary>
        public Vec3 Pos;
        public bool HasPos;
        public long Hp = -1;
        public long HpMax = -1;
    }

    /// <summary>One line of the game's combat log.</summary>
    internal sealed class LogLine
    {
        public DateTime Time;
        public LogEnt Src;
        public LogEnt Tgt;
        public string Ability = string.Empty;
        public ulong AbilityId;
        /// <summary>The line's kind: Event, ApplyEffect, RemoveEffect, AreaEntered ...</summary>
        public ulong KindId;
        /// <summary>For an Event: AbilityActivate, Death, TargetSet ...; for an effect: the effect.</summary>
        public ulong WhatId;
        public string What = string.Empty;
        public int Line;

        public bool IsEvent(ulong what)
        {
            return KindId == CombatLog.KEvent && WhatId == what;
        }
    }

    /// <summary>
    /// Reads the game's combat log (Documents\Star Wars - The Old Republic\CombatLogs, written when combat logging is
    /// on in the game's settings). It is the game's own record of what you did: every ability you activated (mission
    /// items too, even with no cast bar), every death and who killed it, every fight, every area you entered. All by the
    /// game's ids, the same in every client language. The mission tracker says what counted; this says what you did.
    /// </summary>
    internal static class CombatLog
    {
        // line kinds
        public const ulong KEvent = 836045448945472UL;
        public const ulong KSpend = 836045448945473UL;
        public const ulong KRestore = 836045448945476UL;
        public const ulong KApplyEffect = 836045448945477UL;
        public const ulong KRemoveEffect = 836045448945478UL;
        public const ulong KAreaEntered = 836045448953664UL;
        public const ulong KDisciplineChanged = 836045448953665UL;
        public const ulong KModifyCharges = 836045448953666UL;

        // events
        public const ulong AbilityActivate = 836045448945479UL;
        public const ulong AbilityDeactivate = 836045448945480UL;
        public const ulong AbilityCancel = 836045448945481UL;
        public const ulong AbilityInterrupt = 836045448945482UL;
        public const ulong EnterCombat = 836045448945489UL;
        public const ulong ExitCombat = 836045448945490UL;
        public const ulong Death = 836045448945493UL;
        public const ulong TargetSet = 836045448953668UL;
        public const ulong TargetCleared = 836045448953669UL;

        // effects
        public const ulong Damage = 836045448945501UL;
        public const ulong Heal = 836045448945500UL;

        /// <summary>The combat log folder of this PC (it may not exist: combat logging was never on).</summary>
        public static string Folder()
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Star Wars - The Old Republic", "CombatLogs");
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>What the recorder says when the game writes no combat log (combat logging is off in the game).</summary>
        public const string OffText = "The game isn't writing a combat log. Turn on \"Enable Combat Logging\" in the game's Preferences (Combat Logging) " +
                                      "before you record: the profile comes out much better with it.";

        /// <summary>Null when the game is writing a combat log (a log file written in the last 2 hours), else OffText.</summary>
        public static string OffWarning()
        {
            try
            {
                string dir = Folder();
                DateTime newest = DateTime.MinValue;
                if (dir.Length > 0 && Directory.Exists(dir))
                    foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("combat_*.txt"))
                        if (f.LastWriteTimeUtc > newest)
                            newest = f.LastWriteTimeUtc;
                return newest != DateTime.MinValue && DateTime.UtcNow - newest < TimeSpan.FromHours(2) ? null : OffText;
            }
            catch
            {
                return null; // (the folder can't be read: say nothing)
            }
        }

        /// <summary>When a log file was started, from its name "combat_2026-11-07_14_32_30_194881.txt".</summary>
        public static bool FileStart(string path, out DateTime start)
        {
            start = DateTime.MinValue;
            string n = Path.GetFileNameWithoutExtension(path);
            if (!n.StartsWith("combat_", StringComparison.OrdinalIgnoreCase) || n.Length < 26)
                return false;
            return DateTime.TryParseExact(n.Substring(7, 19), "yyyy-MM-dd_HH_mm_ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
        }

        /// <summary>
        /// The log files that cover [from, to]: each file runs from its start until the next file starts. Files from
        /// the folders given, newest name wins when two folders hold the same file.
        /// </summary>
        public static List<string> FilesFor(DateTime from, DateTime to, IEnumerable<string> folders)
        {
            var byName = new SortedDictionary<DateTime, string>();
            foreach (string dir in folders)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                        continue;
                    foreach (string f in Directory.GetFiles(dir, "combat_*.txt"))
                    {
                        DateTime s;
                        if (FileStart(f, out s) && !byName.ContainsKey(s))
                            byName[s] = f;
                    }
                }
                catch
                {
                    // a folder we may not read: the others still count
                }
            }
            var starts = new List<DateTime>(byName.Keys);
            var list = new List<string>();
            for (int i = 0; i < starts.Count; i++)
            {
                DateTime s = starts[i];
                DateTime e = i + 1 < starts.Count ? starts[i + 1] : DateTime.MaxValue;
                if (s <= to && e >= from)
                    list.Add(byName[s]);
            }
            return list;
        }

        /// <summary>
        /// The lines of one log file between from and to (a minute of slack each side) that concern you or your
        /// companion, plus every death and every area change. The file's owner is the character whose area change
        /// opens it.
        /// </summary>
        public static List<LogLine> Read(string path, DateTime from, DateTime to)
        {
            var list = new List<LogLine>();
            DateTime day;
            if (!FileStart(path, out day))
                return list;
            day = day.Date;
            string[] lines = ReadLines(path);
            string owner = null;
            TimeSpan lastTod = TimeSpan.MinValue;
            DateTime lo = from.AddMinutes(-1), hi = to.AddMinutes(1);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length < 16 || line[0] != '[')
                    continue;
                List<string> f = Fields(line);
                if (f.Count < 5)
                    continue;
                TimeSpan tod;
                if (!TimeSpan.TryParseExact(f[0], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out tod))
                    continue;
                if (lastTod != TimeSpan.MinValue && tod < lastTod - TimeSpan.FromHours(12))
                    day = day.AddDays(1); // past midnight
                lastTod = tod;
                DateTime when = day + tod;
                ulong kind, what;
                string whatName;
                Kind(f[4], out kind, out what, out whatName);
                if (kind == KAreaEntered)
                    owner = PlayerId(f[1]) ?? owner;
                if (when < lo || when > hi)
                    continue;
                if (kind == KSpend || kind == KRestore || kind == KModifyCharges || kind == KDisciplineChanged)
                    continue;
                var l = new LogLine { Time = when, KindId = kind, WhatId = what, What = whatName, Line = i + 1 };
                l.Src = Ent(f[1], owner, null);
                l.Tgt = f[2] == "=" ? l.Src : Ent(f[2], owner, l.Src);
                Named(f[3], out l.Ability, out l.AbilityId);
                bool mine = l.Src.Who == LogWho.Me || l.Src.Who == LogWho.Companion;
                bool onMine = l.Tgt.Who == LogWho.Me || l.Tgt.Who == LogWho.Companion;
                // (a mob's own activations are kept when they are aimed at you or your companion: who you fought)
                if (kind == KAreaEntered || (kind == KEvent && what == Death) || mine ||
                    (onMine && kind == KEvent && what == AbilityActivate))
                    list.Add(l);
            }
            return list;
        }

        private static string[] ReadLines(string path)
        {
            byte[] raw;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                raw = new byte[fs.Length];
                int got = 0;
                while (got < raw.Length)
                {
                    int n = fs.Read(raw, got, raw.Length - got);
                    if (n <= 0)
                        break;
                    got += n;
                }
            }
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(raw); // an older client writes Windows-1252
            }
            return text.Split('\n');
        }

        /// <summary>The [..] fields of a line: time, source, target, ability, what happened.</summary>
        public static List<string> Fields(string line)
        {
            var list = new List<string>(6);
            int i = 0;
            while (i < line.Length && list.Count < 5)
            {
                int a = line.IndexOf('[', i);
                if (a < 0)
                    break;
                int b = line.IndexOf(']', a + 1);
                if (b < 0)
                    break;
                list.Add(line.Substring(a + 1, b - a - 1).Trim());
                i = b + 1;
            }
            return list;
        }

        /// <summary>"Event {836..472}: AbilityActivate {836..479}", "ApplyEffect {..}: Damage {..}", "AreaEntered {..}: Section X {..}".</summary>
        private static void Kind(string field, out ulong kind, out ulong what, out string whatName)
        {
            kind = 0;
            what = 0;
            whatName = string.Empty;
            int colon = field.IndexOf("}:", StringComparison.Ordinal);
            string head = colon >= 0 ? field.Substring(0, colon + 1) : field;
            string tail = colon >= 0 ? field.Substring(colon + 2).Trim() : string.Empty;
            string n;
            Named(head, out n, out kind);
            Named(tail, out whatName, out what);
        }

        /// <summary>"Name {123}" -> Name, 123. Text after the closing brace (a "/Spec {..}") is dropped.</summary>
        private static void Named(string field, out string name, out ulong id)
        {
            name = field;
            id = 0;
            int a = field.IndexOf('{');
            if (a < 0)
                return;
            int b = field.IndexOf('}', a + 1);
            if (b < 0)
                return;
            name = field.Substring(0, a).Trim();
            ulong.TryParse(field.Substring(a + 1, b - a - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        /// <summary>"@Name#689880479018865|..." -> "689880479018865" (the character's own number), or null.</summary>
        private static string PlayerId(string field)
        {
            if (!field.StartsWith("@", StringComparison.Ordinal))
                return null;
            int bar = field.IndexOf('|');
            string head = bar >= 0 ? field.Substring(0, bar) : field;
            int hash = head.IndexOf('#');
            if (hash < 0)
                return null;
            int slash = head.IndexOf('/', hash);
            return slash >= 0 ? head.Substring(hash + 1, slash - hash - 1) : head.Substring(hash + 1);
        }

        private static LogEnt Ent(string field, string owner, LogEnt same)
        {
            var e = new LogEnt();
            if (field.Length == 0)
                return e;
            string[] parts = field.Split('|');
            string head = parts[0];
            if (head.StartsWith("@", StringComparison.Ordinal))
            {
                int slash = head.IndexOf('/');
                string pid = PlayerId(field);
                bool own = owner != null && pid == owner;
                if (slash >= 0)
                {
                    // "@Name#id/Companion {template}:instance": a player's companion
                    e.Who = own ? LogWho.Companion : LogWho.OtherPlayer;
                    NpcHead(head.Substring(slash + 1), e);
                }
                else
                {
                    e.Who = own ? LogWho.Me : LogWho.OtherPlayer;
                    int hash = head.IndexOf('#');
                    e.Name = hash > 1 ? head.Substring(1, hash - 1) : head.Substring(1);
                }
            }
            else
            {
                e.Who = LogWho.Npc;
                NpcHead(head, e);
            }
            if (parts.Length > 1)
                Where(parts[1], e);
            if (parts.Length > 2)
                Health(parts[2], e);
            return e;
        }

        /// <summary>"Mutated Muckwing {2955607514546176}:78205000142023".</summary>
        private static void NpcHead(string head, LogEnt e)
        {
            int a = head.IndexOf('{');
            int b = a >= 0 ? head.IndexOf('}', a + 1) : -1;
            if (a < 0 || b < 0)
            {
                e.Name = head.Trim();
                return;
            }
            e.Name = head.Substring(0, a).Trim();
            ulong.TryParse(head.Substring(a + 1, b - a - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Template);
            int colon = head.IndexOf(':', b);
            if (colon >= 0)
                ulong.TryParse(head.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out e.Instance);
        }

        /// <summary>"(722.44,-2051.72,135.32,79.44)": metres x, z, y, heading -> the recording's x, y, z.</summary>
        private static void Where(string part, LogEnt e)
        {
            string p = part.Trim('(', ')', ' ');
            string[] v = p.Split(',');
            if (v.Length < 3)
                return;
            float x, y, z;
            if (!float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z) ||
                !float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return;
            e.Pos = new Vec3(x / 10f, y / 10f, z / 10f);
            e.HasPos = true;
        }

        /// <summary>"(4055/4055)".</summary>
        private static void Health(string part, LogEnt e)
        {
            string p = part.Trim('(', ')', ' ');
            int s = p.IndexOf('/');
            if (s < 0)
                return;
            long hp, max;
            if (long.TryParse(p.Substring(0, s), NumberStyles.Integer, CultureInfo.InvariantCulture, out hp) &&
                long.TryParse(p.Substring(s + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
            {
                e.Hp = hp;
                e.HpMax = max;
            }
        }
    }
}
