// Quest Recorder - made by LethToxic.
using System;
using System.Reflection;

namespace QuestRecorder
{
    /// <summary>Shows a message on top of the game through BuddyCron's overlay toasts.</summary>
    internal static class GameToast
    {
        private static MethodInfo _add;
        private static object _target;
        private static bool _looked;
        private static PropertyInfo _dispatcher;

        public static bool Show(string text, int durationMs)
        {
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    Find();
                }
                if (_add == null)
                    return false;
                Action show = () =>
                {
                    try
                    {
                        _add.Invoke(_target, new object[] { text, durationMs });
                    }
                    catch (Exception ex)
                    {
                        if (RrLog.Every("toast", TimeSpan.FromMinutes(10)))
                            RrLog.Debug("Could not show the in-game message: {0}", ex.Message);
                    }
                };
                // the overlay is a WPF window: run it on its own thread when BuddyCron says which one
                object dispatcher = _dispatcher != null ? _dispatcher.GetValue(_dispatcher.GetGetMethod(true).IsStatic ? null : _target, null) : null;
                MethodInfo begin = dispatcher != null ? dispatcher.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate), typeof(object[]) }) : null;
                if (begin != null)
                    begin.Invoke(dispatcher, new object[] { show, new object[0] });
                else
                    show();
                return true;
            }
            catch (Exception ex)
            {
                if (RrLog.Every("toast", TimeSpan.FromMinutes(10)))
                    RrLog.Debug("Could not show the in-game message: {0}", ex.Message);
                return false;
            }
        }

        private static void Find()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try
                {
                    t = a.GetType("Reborn.Utilities.Overlay.OverlayManager", false);
                }
                catch
                {
                    continue;
                }
                if (t == null)
                    continue;
                MethodInfo m = t.GetMethod("AddToast", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, new[] { typeof(string), typeof(int) }, null);
                if (m == null)
                    continue;
                _dispatcher = t.GetProperty("Dispatcher", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (m.IsStatic)
                {
                    _add = m;
                    return;
                }
                // an instance: look for a static property or field holding it
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (p.PropertyType == t)
                    {
                        _target = p.GetValue(null, null);
                        break;
                    }
                }
                if (_target == null)
                {
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (f.FieldType == t)
                        {
                            _target = f.GetValue(null);
                            break;
                        }
                    }
                }
                if (_target != null)
                    _add = m;
                return;
            }
        }
    }
}
