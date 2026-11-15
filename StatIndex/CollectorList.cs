using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using HarmonyLib;

namespace StatIndex
{
    // Collector (Kappa) hand-over items: the player's quest list once Collector is in it, else statindex-data.json.
    internal static class CollectorList
    {
        private static HashSet<string> _fromQuests;

        public static int Version { get; private set; }

        public static bool Has(string templateId) => (_fromQuests ?? Shipped.Collector).Contains(templateId);

        // Collector = quest rewarding the biggest secure container; ties go to the most hand-over items.
        public static void FromQuests(IEnumerable<QuestTemplate> quests)
        {
            if (!Singleton<ItemFactory>.Instantiated) return;
            var templates = Singleton<ItemFactory>.Instance.ItemTemplates;
            int bestCells = 0;
            HashSet<string> best = null;
            foreach (var quest in quests)
            {
                if (quest?._rewards == null || !quest._rewards.TryGetValue(EQuestStatus.Success, out var rewards) || rewards == null) continue;
                int cells = 0;
                foreach (var reward in rewards)
                    foreach (var item in reward?.items ?? new JsonType.FlatItem[0])
                        if (item != null && templates.TryGetValue(item._tpl, out var template) && template is MobContainerTemplate container)
                            cells = Math.Max(cells, Cells(container));
                if (cells == 0) continue;

                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (quest._conditions != null && quest._conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions))
                    foreach (var condition in conditions.OfType<ConditionHandoverItem>())
                        foreach (var target in condition.target ?? new string[0])
                            if (!string.IsNullOrEmpty(target)) wanted.Add(target);

                if (cells > bestCells || cells == bestCells && best != null && wanted.Count > best.Count)
                {
                    bestCells = cells;
                    best = wanted;
                }
            }
            if (best == null || best.Count == 0 || _fromQuests != null && _fromQuests.SetEquals(best)) return;
            _fromQuests = best;
            Version++;
            Plugin.Log.LogInfo($"Collector asks for {best.Count} items (from your quest list)");
        }

        private static int Cells(CompoundItemTemplate template)
        {
            int cells = 0;
            foreach (var grid in template.Grids ?? new Grid[0])
                if (grid != null) cells += grid.GridWidth * grid.GridHeight;
            return cells;
        }
    }

    [HarmonyPatch(typeof(ConditionalTemplatesStorage), nameof(ConditionalTemplatesStorage.AddGlobalQuestTemplates))]
    internal static class QuestTemplatesPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ConditionalTemplatesStorage __instance)
        {
            try
            {
                CollectorList.FromQuests(__instance.GlobalQuestTemplates);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }
}
