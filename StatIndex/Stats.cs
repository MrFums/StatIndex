using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EFT.InventoryLogic;
using JsonType;

namespace StatIndex
{
    internal static class Stats
    {
        // Format from the Ammo style setting.
        public static string Ammo(ItemTemplate template)
        {
            if (!(template is AmmoTemplate ammo)) return null;
            if (ammo.PenetrationPower == 0 && ammo.Damage == 0) return null;
            string dmg = ammo.ProjectileCount > 1 ? ammo.ProjectileCount + "x" + ammo.Damage : ammo.Damage.ToString();
            string format;
            switch (Plugin.AmmoLook.Value)
            {
                case AmmoStyle.Letters: format = "[P{pen} D{dmg}]"; break;
                case AmmoStyle.Slash: format = "[{pen}/{dmg}]"; break;
                default: format = "[P:{pen} D:{dmg}]"; break;
            }
            return format.Replace("{pen}", ammo.PenetrationPower.ToString()).Replace("{dmg}", dmg);
        }

        // Built-in class (own or locked inserts); {plates} becomes + if it takes plates.
        public static string TemplateArmor(ItemTemplate template, ItemTemplates templates, string format)
        {
            int best = OwnClass(template);
            bool plates = false;
            if (template is CompoundItemTemplate compound && compound.Slots != null)
            {
                foreach (var slot in compound.Slots)
                {
                    if (slot == null || !IsArmorSlot(slot)) continue;
                    if (!slot.Locked)
                    {
                        plates = true;
                        continue;
                    }
                    foreach (var filter in slot.Filters)
                    {
                        if (filter?.Filter == null) continue;
                        foreach (var id in filter.Filter)
                            if (templates.TryGetValue(id, out var insert)) best = Math.Max(best, OwnClass(insert));
                    }
                }
            }
            return best > 0 ? Fill(format).Replace("{class}", best.ToString()).Replace("{plates}", plates ? "+" : "") : null;
        }

        // One class per plate slot (front, back, sides), - for empty; no plate slots = built-in class.
        // Armored add-ons (face shields) come first: Killa + C6 shield = 6/4.
        public static string ItemArmor(Item item, string format)
        {
            int builtIn = OwnClass(item.Template);
            var plates = new List<int>();
            var addOns = new List<int>();
            if (item is CompoundItem compound && compound.Slots != null)
            {
                foreach (var slot in compound.Slots)
                {
                    if (slot == null) continue;
                    if (!IsArmorSlot(slot))
                    {
                        AddOns(slot.ContainedItem, addOns);
                        continue;
                    }
                    int level = slot.ContainedItem != null ? OwnClass(slot.ContainedItem.Template) : 0;
                    if (slot.Locked) builtIn = Math.Max(builtIn, level);
                    else plates.Add(level);
                }
            }
            // front/back always shown; sides only up to the last fitted
            int keep = Math.Max(plates.FindLastIndex(level => level > 0) + 1, Math.Min(2, plates.Count));
            plates = plates.GetRange(0, keep);
            string shown;
            if (plates.Count > 0)
                shown = string.Join("/", plates.Select(level => level > 0 ? level.ToString() : "-"));
            else
                shown = builtIn > 0 ? builtIn.ToString() : null;
            var extra = addOns.Where(level => level.ToString() != shown).Distinct().Select(level => level.ToString()).ToList();
            if (extra.Count > 0) shown = string.Join("/", shown != null ? extra.Concat(new[] { shown }) : extra);
            return shown != null ? Fill(format).Replace("{class}", shown).Replace("{plates}", "") : null;
        }

        private static void AddOns(Item child, List<int> found)
        {
            if (child == null) return;
            int level = OwnClass(child.Template);
            if (level > 0)
            {
                found.Add(level);
                return;
            }
            if (child is CompoundItem compound && compound.Slots != null)
                foreach (var slot in compound.Slots)
                    if (slot != null) AddOns(slot.ContainedItem, found);
        }

        public static string Attachment(ItemTemplate template, string format)
        {
            if (!(template is ModTemplate mod)) return null;
            if (mod.Ergonomics == 0f && mod.Recoil == 0f) return null;
            string ergo = Signed(mod.Ergonomics), recoil = Signed(mod.Recoil) + "%";
            string stats = Join(" ", mod.Ergonomics != 0f ? "E" + ergo : null, mod.Recoil != 0f ? "R" + recoil : null);
            return Fill(format).Replace("{stats}", stats).Replace("{ergo}", ergo).Replace("{recoil}", recoil);
        }

        private static string Signed(float value) =>
            (value > 0f ? "+" : "") + value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        public static string Collector(ItemTemplate template, string format) =>
            CollectorList.Has(template._id) ? Fill(format) : null;

        public static string Keys(ItemTemplate template, string format)
        {
            if (!(template is KeyTemplate)) return null;
            var templates = Comfort.Common.Singleton<EFT.ItemFactory>.Instantiated
                ? Comfort.Common.Singleton<EFT.ItemFactory>.Instance.ItemTemplates
                : null;
            string maps = KeyMaps.For(template._id.ToString(), templates);
            return maps != null ? Fill(format).Replace("{maps}", maps) : null;
        }

        public static string Cells(ItemTemplate template, string format)
        {
            if (!(template is CompoundItemTemplate compound) || compound.Grids == null) return null;
            // not carryable containers
            if (template is LootContainerTemplate || template is StashTemplate || template is PocketsTemplate
                || template is StationaryContainerTemplate || template is HideoutAreaContainerTemplate) return null;
            int cells = 0;
            foreach (var grid in compound.Grids)
                if (grid != null) cells += grid.GridWidth * grid.GridHeight;
            return cells > 0 ? Fill(format).Replace("{cells}", cells.ToString()) : null;
        }

        public static string Join(string between, params string[] parts)
        {
            var text = new StringBuilder();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (text.Length > 0) text.Append(between);
                text.Append(part);
            }
            return text.Length > 0 ? text.ToString() : null;
        }

        private static int OwnClass(ItemTemplate template) =>
            template is IArmorComponentTemplate armor ? armor.ArmorClass : 0;

        private static bool IsArmorSlot(Slot slot)
        {
            if (slot.Filters == null) return false;
            foreach (var filter in slot.Filters)
                if (filter != null && (filter.armorColliders?.Length > 0 || filter.armorPlateColliders?.Length > 0))
                    return true;
            return false;
        }

        private static string Fill(string format) => (format ?? string.Empty).Trim();
    }
}
