using System;
using System.Collections.Generic;
using System.Threading;
using Comfort.Common;
using EFT;
using HarmonyLib;
using JsonType;

namespace StatIndex
{
    // Prefix table for item names, built from the server's templates.
    internal static class Names
    {
        private const string NameSuffix = " Name";
        private const string ShortNameSuffix = " ShortName";
        private const int IdLength = 24;

        private sealed class Table
        {
            public ItemTemplates Source;
            public int Count;
            public int KeyVersion;
            public int CollectorVersion;
            public Dictionary<string, string> Prefixes;
        }

        private static Table _table;
        // building looks up map names, which re-enters the lookup patch
        [ThreadStatic] private static bool _building;

        public static void Reset() => Volatile.Write(ref _table, null);

        // hot path: every locale lookup
        public static string Prefix(string key)
        {
            int length = key.Length;
            if (length != IdLength + NameSuffix.Length && length != IdLength + ShortNameSuffix.Length) return null;
            // both " Name" and " ShortName" end in "Name"
            if (_building || !key.EndsWith("Name", StringComparison.Ordinal)) return null;
            var table = Current();
            return table != null && table.Prefixes.TryGetValue(key, out var prefix) ? prefix : null;
        }

        private static Table Current()
        {
            if (!Singleton<ItemFactory>.Instantiated) return null;
            var templates = Singleton<ItemFactory>.Instance.ItemTemplates;
            if (templates == null || templates.Count == 0) return null;
            if (!KeyMaps.Ready)
            {
                _building = true;
                try
                {
                    KeyMaps.Poke();
                }
                finally
                {
                    _building = false;
                }
            }
            var table = Volatile.Read(ref _table);
            if (table != null && ReferenceEquals(table.Source, templates) && table.Count == templates.Count
                && table.KeyVersion == KeyMaps.Version && table.CollectorVersion == CollectorList.Version) return table;
            _building = true;
            try
            {
                table = Build(templates);
            }
            finally
            {
                _building = false;
            }
            Volatile.Write(ref _table, table);
            return table;
        }

        private static Table Build(ItemTemplates templates)
        {
            var table = new Table
            {
                Source = templates,
                Count = templates.Count,
                KeyVersion = KeyMaps.Version,
                CollectorVersion = CollectorList.Version,
                Prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            int ammo = 0, armor = 0, cells = 0, keys = 0, mods = 0;
            foreach (var pair in templates)
            {
                string id = pair.Key.ToString();
                if (id.Length != IdLength || pair.Value == null) continue;
                var template = pair.Value;

                string a = Stats.Ammo(template, Plugin.Ammo.NameFormat);
                string r = Stats.TemplateArmor(template, templates, Plugin.Armor.NameFormat);
                string c = Stats.Cells(template, Plugin.Containers.NameFormat);
                string k = Stats.Keys(template, Plugin.Keys.NameFormat);
                string m = Stats.Attachment(template, Plugin.Attachments.NameFormat);
                if (m != null) mods++;
                string q = Stats.Collector(template, Plugin.Collector.NameFormat);
                if (a != null) ammo++;
                if (k != null) keys++;
                if (r != null) armor++;
                if (c != null) cells++;

                string name = Stats.Join(" ", Plugin.Ammo.OnName.Value ? a : null, Plugin.Armor.OnName.Value ? r : null,
                    Plugin.Containers.OnName.Value ? c : null, Plugin.Keys.OnName.Value ? k : null,
                    Plugin.Attachments.OnName.Value ? m : null, Plugin.Collector.OnName.Value ? q : null);
                if (name != null) table.Prefixes[id + NameSuffix] = name + " ";
            }
            Plugin.Log.LogInfo($"Name stats ready: {ammo} rounds, {armor} armor items, {cells} containers, {keys} keys, {mods} attachments");
            return table;
        }
    }

    // Only the returned string changes; the locale table is never modified.
    [HarmonyPatch(typeof(LocalizationManager), nameof(LocalizationManager.TryGetLocalization))]
    internal static class TryGetLocalizationPatch
    {
        [HarmonyPostfix]
        private static void Postfix(string id, bool __result, ref string localizedValue)
        {
            if (!__result || id == null) return;
            try
            {
                var prefix = Names.Prefix(id);
                if (prefix != null) localizedValue = prefix + localizedValue;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }
}
