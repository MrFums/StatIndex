using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StatIndex
{
    // Which maps a key's locks are on. Locks live in map scenes, not server data, so:
    // - statindex-data.json: scanned from the game files by tools/build_data.py
    // - learned-locks.json: the last raid scan of each map version (covers modded maps)
    // Which list counts for a map, checked on every boot:
    // 1. scans of installed backports (from a map version picker), so the backport wins while it's installed
    // 2. else the vanilla scan
    // 3. else the shipped list
    // A backport's scan is kept when it's uninstalled, and counts again if it comes back.
    internal static class KeyMaps
    {
        private const string LearnedFile = "learned-locks.json";
        private const string CaseWord = "CASE";
        private const string NoUseWord = "NO USE";
        private const string LetterVowels = "AEIOUY";
        // a key on this many vehicle models is the models' default, not a real lock
        private const int VehicleModels = 3;

        private static readonly object Gate = new object();
        // map id -> map version -> key ids, from raids
        private static Dictionary<string, Dictionary<string, HashSet<string>>> _learned =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        // map id -> key ids that count now; built by Rebuild
        private static Dictionary<string, HashSet<string>> _active = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // map id -> code; null until the location list arrives
        private static Dictionary<string, string> _codes;
        private static HashSet<string> _playable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // key id -> maps it grants entry to (AccessKeys, e.g. Labs keycard)
        private static Dictionary<string, List<string>> _entry = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _itemKeys;
        private static object _itemKeysFrom;
        private static int _nextTry;

        public static int Version { get; private set; }

        public static void Load()
        {
            try
            {
                string learned = Path.Combine(Shipped.Folder, LearnedFile);
                if (File.Exists(learned))
                {
                    var read = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var map in JObject.Parse(File.ReadAllText(learned)).Properties())
                    {
                        var versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                        // older files: one list per map, counted as vanilla
                        if (map.Value is JArray list)
                            versions[Variants.Original] = Ids(list);
                        else if (map.Value is JObject byVersion)
                            foreach (var version in byVersion.Properties())
                                if (version.Value is JArray ids) versions[version.Name] = Ids(ids);
                        if (versions.Count > 0) read[map.Name] = versions;
                    }
                    _learned = read;
                }
                Plugin.Log.LogInfo($"Key maps: {_learned.Count} maps learned in raids");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Couldn't read {LearnedFile}: {e}");
            }
        }

        private static HashSet<string> Ids(JArray list) =>
            new HashSet<string>(list.Values<string>().Where(id => !string.IsNullOrEmpty(id)), StringComparer.OrdinalIgnoreCase);

        // Call under Gate. Picks each map's list (see the top of the class).
        private static void Rebuild()
        {
            var active = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Shipped.Maps)
                foreach (var map in pair.Value)
                {
                    if (!active.TryGetValue(map, out var keys)) active[map] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    keys.Add(pair.Key);
                }
            var picked = new List<string>();
            foreach (var map in _learned)
            {
                var lists = map.Value.Where(v => !Variants.IsOriginal(v.Key) && Variants.Installed(map.Key, v.Key)).ToList();
                if (lists.Count == 0) lists = map.Value.Where(v => Variants.IsOriginal(v.Key)).ToList();
                // only scans of uninstalled backports: the shipped list stays
                if (lists.Count == 0) continue;
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var list in lists) keys.UnionWith(list.Value);
                active[map.Key] = keys;
                picked.Add(map.Key + "=" + string.Join("+", lists.Select(v => v.Key)));
            }
            _active = active;
            Version++;
            Plugin.Log.LogInfo("Key maps from raids: " + (picked.Count > 0 ? string.Join(", ", picked) : "none"));
        }


        public static bool Ready => _codes != null;

        public static void Poke() => Codes();

        // Null until the location list arrives, so nothing reads NO USE early.
        public static string For(string keyId, JsonType.ItemTemplates templates)
        {
            var codes = Codes();
            if (codes == null) return null;
            var maps = new List<string>();
            lock (Gate)
            {
                foreach (var pair in _active)
                    if (pair.Value.Contains(keyId)) maps.Add(pair.Key);
                if (_entry.TryGetValue(keyId, out var into))
                    maps.AddRange(into);
            }
            // prefer playable maps, fall back to disabled ones
            var found = Named(maps.Where(m => _playable.Contains(m)), codes);
            if (found.Count == 0) found = Named(maps, codes);
            if (found.Count > 0) return string.Join("/", found);
            if (OpensItem(keyId, templates)) return CaseWord;
            return NoUseWord;
        }

        private static List<string> Named(IEnumerable<string> maps, Dictionary<string, string> codes)
        {
            var found = new List<string>();
            foreach (var map in maps)
                if (codes.TryGetValue(map, out var code) && !found.Contains(code)) found.Add(code);
            return found;
        }

        private static bool OpensItem(string keyId, JsonType.ItemTemplates templates)
        {
            if (templates == null) return false;
            var keys = _itemKeys;
            if (keys == null || !ReferenceEquals(_itemKeysFrom, templates))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var template in templates.Values)
                    if (template is EFT.InventoryLogic.ILockableComponentTemplate lockable && lockable.KeyIds != null)
                        foreach (var id in lockable.KeyIds)
                            if (!string.IsNullOrEmpty(id)) keys.Add(id);
                _itemKeys = keys;
                _itemKeysFrom = templates;
            }
            return keys.Contains(keyId);
        }

        private static Dictionary<string, string> Codes()
        {
            var codes = _codes;
            if (codes != null) return codes;
            if (Environment.TickCount - _nextTry < 0) return null;
            _nextTry = Environment.TickCount + 1000;

            if (!TarkovApplication.Exist(out var app)) return null;
            var locations = app.Session?.LocationSettings?.locations;
            if (locations == null || locations.Count == 0) return null;

            codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var playable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entry = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var location in locations.Values)
            {
                if (location == null || location.IsHideout || string.IsNullOrEmpty(location.Id)) continue;
                string name = Shown(location);
                codes[location.Id] = Code(name);
                bool visited;
                lock (Gate) visited = _learned.ContainsKey(location.Id);
                if (location.Enabled || visited) playable.Add(location.Id);
                foreach (var key in location.AccessKeys ?? new string[0])
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!entry.TryGetValue(key, out var list)) entry[key] = list = new List<string>();
                    list.Add(location.Id);
                }
            }
            lock (Gate)
            {
                _playable = playable;
                _entry = entry;
                // every plugin has loaded by now, so the picker knows which backports are installed
                Rebuild();
                _codes = codes;
            }
            Plugin.Log.LogInfo("Map codes: " + string.Join(", ", codes.Select(p => p.Key + "=" + p.Value)));
            return codes;
        }

        // localized name (Ground Zero, not Sandbox)
        private static string Shown(JsonType.LocationSettings.Location location)
        {
            if (!string.IsNullOrEmpty(location._Id)
                && LocalizationManager.Instance.TryGetLocalization(location._Id + " Name", LocalizationManager.Instance.Culture, out var shown)
                && !string.IsNullOrEmpty(shown))
                return shown;
            return string.IsNullOrEmpty(location.Name) ? location.Id : location.Name;
        }


        // Woods WDS, Customs CST, Ground Zero GZR, Streets of Tarkov STR, The Lab LAB.
        public static string Code(string name)
        {
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())
                .Where(w => w.Length > 0).ToList();
            var main = words.Where(w => w != "THE" && w != "OF").ToList();
            if (main.Count == 0) main = words;
            if (main.Count == 0) return name;

            string last = main[main.Count - 1];
            var code = new StringBuilder();
            foreach (var word in main)
                if (code.Length < 3) code.Append(word[0]);
            foreach (char c in last.Skip(1))
                if (code.Length < 3 && LetterVowels.IndexOf(c) < 0) code.Append(c);
            if (code.Length < 3 && main.Count == 1) return last.Length > 3 ? last.Substring(0, 3) : last;
            return code.ToString();
        }

        private static bool VehicleDefault(string key, HashSet<string> models) =>
            Shipped.VehicleDefaults.Contains(key) || models.Count >= VehicleModels;

        // Kamaz_4310_on (1) -> kamaz_4310
        private static readonly System.Text.RegularExpressions.Regex ModelState = new System.Text.RegularExpressions.Regex(
            @"(\s*\(\d+\))+$|_(on|off|open|closed)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static string Model(string name)
        {
            string previous = null;
            while (previous != name)
            {
                previous = name;
                name = ModelState.Replace(name, "");
            }
            return name.ToLowerInvariant();
        }

        public static void Learn(GameWorld world)
        {
            string map = world?.LocationId;
            if (string.IsNullOrEmpty(map)) return;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var onDoors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // inactive too: the scan replaces the map's list, so a lock off at raid start must still count
            foreach (var thing in UnityEngine.Object.FindObjectsOfType<WorldInteractiveObject>(true))
            {
                if (string.IsNullOrEmpty(thing.KeyId)) continue;
                if (thing is Trunk)
                {
                    if (!models.TryGetValue(thing.KeyId, out var set)) models[thing.KeyId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(Model(thing.transform.parent != null ? thing.transform.parent.name : ""));
                }
                else onDoors.Add(thing.KeyId);
            }
            foreach (var pair in models)
                if (!VehicleDefault(pair.Key, pair.Value)) keys.Add(pair.Key);
            keys.UnionWith(onDoors);
            if (keys.Count == 0) return;

            string version = Variants.Current(map);
            if (version == null)
            {
                Plugin.Log.LogWarning($"Found {keys.Count} locks on {map}, not saved: the map version picker hasn't said which version of the map loaded");
                return;
            }

            int shipped, unseen, added, removed;
            lock (Gate)
            {
                var onMap = Shipped.Maps.Where(p => p.Value.Contains(map, StringComparer.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
                shipped = onMap.Count;
                unseen = onMap.Count(k => !keys.Contains(k));
                if (!_learned.TryGetValue(map, out var versions))
                    _learned[map] = versions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                versions.TryGetValue(version, out var known);
                added = known == null ? keys.Count : keys.Count(k => !known.Contains(k));
                removed = known == null ? 0 : known.Count(k => !keys.Contains(k));
                // this scan is now this map version's whole list
                versions[version] = keys;
            }
            Plugin.Log.LogInfo($"Found {keys.Count} locks on {map} ({version}): {added} new, {removed} gone since last raid, {unseen} of the {shipped} shipped for it not seen");
            if (added == 0 && removed == 0) return;

            lock (Gate)
            {
                Rebuild();
                try
                {
                    var save = _learned.ToDictionary(p => p.Key, p => p.Value.ToDictionary(v => v.Key,
                        v => v.Value.OrderBy(k => k, StringComparer.Ordinal).ToArray()));
                    File.WriteAllText(Path.Combine(Shipped.Folder, LearnedFile), JsonConvert.SerializeObject(save, Formatting.Indented));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Couldn't save {LearnedFile}: {e}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
    internal static class GameStartedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GameWorld __instance)
        {
            try
            {
                KeyMaps.Learn(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }
}
