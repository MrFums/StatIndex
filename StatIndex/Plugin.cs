using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StatIndex
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.statindex";
        public const string Name = "Fums-StatIndex";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        internal static Kind Ammo;
        internal static Kind Armor;
        internal static Kind Containers;
        internal static Kind Keys;
        internal static Kind Attachments;
        internal static Kind Collector;

        internal static ConfigEntry<LinePlace> LinePosition;
        internal static ConfigEntry<int> LineSize;
        internal static ConfigEntry<Color> LineColour;
        internal static ConfigEntry<bool> LineOnFlea;
        internal static ConfigEntry<bool> LineOnTraders;

        private void Awake()
        {
            Log = Logger;

            Ammo = new Kind(Config, "1. Ammo", "[{pen}/{dmg}]", "[{pen}/{dmg}]", "penetration/damage, [53/42]");
            Armor = new Kind(Config, "2. Armor", "[Lv{class}{plates}]", "[Lv{class}]",
                "armor class, [Lv3+] (+ takes plates); on the cell each plate fitted, front first, - for an empty slot");
            Containers = new Kind(Config, "3. Containers", "[{cells}]", "[{cells}]", "grid cells inside, [35]");
            Keys = new Kind(Config, "4. Keys", "[{maps}]", "[{maps}]",
                "the map its lock is on, [CST]; [CASE] for an item's lock, [NO USE] when no lock uses it");
            Attachments = new Kind(Config, "5. Attachments", "[{stats}]", "[{stats}]", "ergonomics and recoil change, [E+2 R-2%]");
            Collector = new Kind(Config, "6. Collector", "[K]", "[K]", "[K] on items Fence's Collector quest (Kappa) asks for");

            const string line = "7. Cell line";
            LinePosition = Config.Bind(line, "Position", LinePlace.UnderName,
                "Where the stats line sits in the cell: just under the item's name, or in the cell's top-left corner.");
            LineSize = Config.Bind(line, "Text size %", 85,
                new ConfigDescription("Size of the stats line, as a percent of the item name's size.", new AcceptableValueRange<int>(30, 200)));
            LineColour = Config.Bind(line, "Colour", new Color(0.83f, 0.78f, 0.58f, 1f),
                new ConfigDescription("Colour of the stats line. Click the swatch for sliders, or type a hex code.", null,
                    new ConfigurationManagerAttributes { CustomDrawer = ColourField.Draw }));
            LineOnFlea = Config.Bind(line, "Show on flea offers", true,
                "Show the stats line on the flea market's offer pictures (the offer's name beside them has the stats too).");
            LineOnTraders = Config.Bind(line, "Show on trader stock", true,
                "Show the stats line on the items a trader sells. Your own items on the sell screen always show it.");

            Shipped.Load();
            KeyMaps.Load();
            Config.SettingChanged += (_, e) =>
            {
                // line settings don't need the name table rebuilt
                if (e?.ChangedSetting?.Definition.Section == line)
                {
                    CellLines.RefreshAll();
                    return;
                }
                Names.Reset();
                CellLines.RefreshAll();
            };

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("StatIndex loaded");
        }
    }

    internal enum LinePlace
    {
        UnderName,
        TopLeft
    }

    internal sealed class Kind
    {
        public readonly ConfigEntry<bool> OnName;
        public readonly ConfigEntry<bool> OnCell;
        public readonly string NameFormat;
        public readonly string CellFormat;

        public Kind(ConfigFile config, string section, string nameFormat, string cellFormat, string what)
        {
            OnName = config.Bind(section, "Show on full names", true,
                "Put it in front of the item's name (inspect window, trader and flea lists, tooltips): " + what + ".");
            OnCell = config.Bind(section, "Show on stash cell", true,
                "Show it on its own line in the item's cell: " + what + ".");
            NameFormat = nameFormat;
            CellFormat = cellFormat;
        }
    }
}
