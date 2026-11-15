using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace StatIndex
{
    // statindex-data.json, built by tools/build_data.py.
    internal static class Shipped
    {
        public const string FileName = "statindex-data.json";

        // key id -> map ids its locks are on
        public static Dictionary<string, string[]> Maps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // keys whose vehicle locks are model defaults, not real
        public static HashSet<string> VehicleDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // fallback only, see CollectorList
        public static HashSet<string> Collector = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string Folder => Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";

        private sealed class Data
        {
            public Dictionary<string, string[]> maps { get; set; }
            public string[] vehicleDefaults { get; set; }
            public string[] collector { get; set; }
        }

        public static void Load()
        {
            string file = Path.Combine(Folder, FileName);
            if (!File.Exists(file))
            {
                Plugin.Log.LogWarning($"{FileName} is missing: keys get a map only after you've loaded that map in a raid.");
                return;
            }
            try
            {
                var data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(file)) ?? new Data();
                Maps = new Dictionary<string, string[]>(data.maps ?? new Dictionary<string, string[]>(), StringComparer.OrdinalIgnoreCase);
                VehicleDefaults = new HashSet<string>(data.vehicleDefaults ?? new string[0], StringComparer.OrdinalIgnoreCase);
                Collector = new HashSet<string>(data.collector ?? new string[0], StringComparer.OrdinalIgnoreCase);
                Plugin.Log.LogInfo($"Shipped data: {Maps.Count} keys' locks, {Collector.Count} Collector items");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Couldn't read {FileName}: {e}");
            }
        }
    }
}
