// Quest Recorder - made by LethToxic.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using Reborn.Utilities;

namespace QuestRecorder
{
    /// <summary>Prefixed log lines ("[Quest Recorder] ...").</summary>
    internal static class RrLog
    {
        public const string Prefix = "[Quest Recorder] ";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, long> LastByKey = new Dictionary<string, long>();

        /// <summary>When true, Debug lines are written at Normal level so they show in the main log.</summary>
        public static bool Verbose { get; set; }

        public static void Info(string format, params object[] args)
        {
            Write(LogLevel.Normal, null, format, args);
        }

        public static void Good(string format, params object[] args)
        {
            Write(LogLevel.Normal, Colors.LightGreen, format, args);
        }

        public static void Event(string format, params object[] args)
        {
            Write(LogLevel.Normal, Colors.LightSkyBlue, format, args);
        }

        public static void Warn(string format, params object[] args)
        {
            Write(LogLevel.Normal, Colors.Orange, format, args);
        }

        public static void Error(string format, params object[] args)
        {
            Write(LogLevel.Quiet, Colors.OrangeRed, format, args);
        }

        public static void Debug(string format, params object[] args)
        {
            try
            {
                string msg = Prefix + Format(format, args);
                if (Verbose)
                    Logging.Write(LogLevel.Normal, Colors.Gray, msg);
                else
                    Logging.WriteDiagnostic(msg);
            }
            catch
            {
                // logging must never throw into the caller
            }
        }

        /// <summary>Logs a short warning plus the full exception at diagnostic level.</summary>
        public static void Exception(string what, Exception ex)
        {
            if (ex == null)
                return;
            Warn("{0} failed: {1}: {2}", what, ex.GetType().Name, ex.Message);
            try
            {
                Logging.WriteException(ex);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>True at most once per <paramref name="every"/> for a given key (keeps repeats out of the log).</summary>
        public static bool Every(string key, TimeSpan every)
        {
            long now = Environment.TickCount64;
            lock (Sync)
            {
                long last;
                if (LastByKey.TryGetValue(key, out last) && now - last < (long)every.TotalMilliseconds)
                    return false;
                LastByKey[key] = now;
                return true;
            }
        }

        public static void ResetRateLimits()
        {
            lock (Sync)
                LastByKey.Clear();
        }

        private static void Write(LogLevel level, Color? color, string format, object[] args)
        {
            try
            {
                string msg = Prefix + Format(format, args);
                if (color.HasValue)
                    Logging.Write(level, color.Value, msg);
                else
                    Logging.Write(level, msg);
            }
            catch
            {
                // logging must never throw into the caller
            }
        }

        private static string Format(string format, object[] args)
        {
            if (format == null)
                return string.Empty;
            if (args == null || args.Length == 0)
                return format;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }
    }
}
