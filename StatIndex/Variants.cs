using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace StatIndex
{
    // Map versions (vanilla Customs or a backport) from a map version picker, read by reflection
    // so StatIndex runs without it. No picker: every map has only "original".
    internal static class Variants
    {
        public const string Original = "original";

        private static bool _looked;
        private static PropertyInfo _current, _location;
        private static MethodInfo _variants, _for;
        private static object _set;

        public static bool IsOriginal(string variant) => string.Equals(variant, Original, StringComparison.OrdinalIgnoreCase);

        private static bool Find()
        {
            if (_looked) return _current != null;
            _looked = true;
            try
            {
                var api = AccessTools.TypeByName("MapVariants.Client.Api.Maps");
                if (api == null) return false;
                _current = AccessTools.Property(api, "CurrentVariant");
                _location = AccessTools.Property(api, "CurrentLocationId");
                _variants = AccessTools.Method(api, "Variants", new[] { typeof(string) });
                // which plugin a maps.json backport needs; internal, so optional
                var maps = AccessTools.TypeByName("MapVariants.Client.ClientMaps");
                _set = maps == null ? null : AccessTools.Field(maps, "Set")?.GetValue(null);
                _for = _set == null ? null : AccessTools.Method(_set.GetType(), "For", new[] { typeof(string) });
                if (_current == null || _variants == null) _current = null;
                Plugin.Log.LogInfo(_current != null ? "Map version picker found: key maps are kept per map version" : "Map version picker not recognised: one key list per map");
            }
            catch (Exception e)
            {
                _current = null;
                Plugin.Log.LogWarning($"Couldn't read the map version picker, one key list per map: {e.Message}");
            }
            return _current != null;
        }

        // The version loaded for this raid; null when the picker hasn't decided, so the scan isn't filed wrong.
        public static string Current(string map)
        {
            if (!Find() || !Has(map)) return Original;
            // backport plugin gone: the picker sets no choice and vanilla loads
            if (!AnyBackport(map)) return Original;
            try
            {
                string current = _current.GetValue(null) as string;
                string location = _location?.GetValue(null) as string;
                if (string.IsNullOrEmpty(current)) return null;
                if (!string.IsNullOrEmpty(location) && !string.Equals(location, map, StringComparison.OrdinalIgnoreCase)) return null;
                return current;
            }
            catch
            {
                return null;
            }
        }

        // Vanilla always counts; a backport only while the picker offers it and its plugin is loaded.
        public static bool Installed(string map, string variant)
        {
            if (IsOriginal(variant)) return true;
            if (!Find()) return false;
            try
            {
                var offered = _variants.Invoke(null, new object[] { map }) as string[];
                if (offered == null || !offered.Contains(variant, StringComparer.OrdinalIgnoreCase)) return false;
                var managed = _for?.Invoke(_set, new object[] { map });
                string guid = managed == null ? null : AccessTools.Property(managed.GetType(), "PluginGuid")?.GetValue(managed) as string;
                return string.IsNullOrEmpty(guid) || Chainloader.PluginInfos.ContainsKey(guid);
            }
            catch
            {
                return false;
            }
        }

        private static bool AnyBackport(string map)
        {
            try
            {
                var offered = _variants.Invoke(null, new object[] { map }) as string[] ?? new string[0];
                return offered.Any(v => !IsOriginal(v) && Installed(map, v));
            }
            catch
            {
                return false;
            }
        }

        private static bool Has(string map)
        {
            try
            {
                var offered = _variants.Invoke(null, new object[] { map }) as string[];
                return offered != null && offered.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
