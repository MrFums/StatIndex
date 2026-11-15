using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StatIndex
{
    // Stats line drawn under the item name in grid cells.
    internal static class CellLines
    {
        private const string LineName = "StatIndexLine";
        private static readonly HashSet<Line> Lines = new HashSet<Line>();

        public static void Update(GridItemView cell)
        {
            if (cell == null || cell.Caption == null) return;
            Item item = cell.Item;
            string text = item == null || !Wanted(cell) ? null : Text(item);

            // the line may be parented to the caption's parent or the cell
            Line line = cell.GetComponentsInChildren<Line>(true).FirstOrDefault(l => l.Cell == cell);
            if (string.IsNullOrEmpty(text))
            {
                if (line != null) line.Show(null);
                return;
            }
            if (line == null) line = Create(cell);
            line.Show(text);
        }

        public static void RefreshAll()
        {
            foreach (var line in Lines.ToArray())
            {
                if (line == null) continue;
                try
                {
                    Update(line.Cell);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(e);
                }
            }
        }

        // TradingPlayerItemView is the player's own side of the trade screen, so it always shows.
        private static bool Wanted(GridItemView cell)
        {
            if (cell is RagfairOfferItemView) return Plugin.LineOnFlea.Value;
            if (cell is TradingItemView && !(cell is TradingPlayerItemView)) return Plugin.LineOnTraders.Value;
            return true;
        }

        private static string Text(Item item)
        {
            var template = item.Template;
            string ammo = Plugin.Ammo.OnCell.Value ? Stats.Ammo(template) : null;
            string armor = Plugin.Armor.OnCell.Value ? Stats.ItemArmor(item, Plugin.Armor.CellFormat) : null;
            string cells = Plugin.Containers.OnCell.Value ? Stats.Cells(template, Plugin.Containers.CellFormat) : null;
            string keys = Plugin.Keys.OnCell.Value ? Stats.Keys(template, Plugin.Keys.CellFormat) : null;
            string mods = Plugin.Attachments.OnCell.Value ? Stats.Attachment(template, Plugin.Attachments.CellFormat) : null;
            string collector = Plugin.Collector.OnCell.Value ? Stats.Collector(template, Plugin.Collector.CellFormat) : null;
            return Stats.Join(" ", ammo, armor, cells, keys, mods, collector);
        }

        private static Line Create(GridItemView cell)
        {
            TextMeshProUGUI caption = cell.Caption;
            TextMeshProUGUI label = UnityEngine.Object.Instantiate(caption, caption.transform.parent, false);
            label.name = LineName;
            foreach (Component extra in label.GetComponents<Component>()
                         .Where(c => !(c is RectTransform) && !(c is TextMeshProUGUI) && !(c is CanvasRenderer)))
                UnityEngine.Object.DestroyImmediate(extra);
            foreach (Transform child in label.transform.Cast<Transform>().ToArray())
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            label.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            label.enableAutoSizing = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.richText = true;

            var line = label.gameObject.AddComponent<Line>();
            line.Cell = cell;
            line.Caption = caption;
            line.Label = label;
            line.BaseSize = caption.fontSize;
            line.BaseAlignment = caption.alignment;
            line.BaseStyle = caption.fontStyle;
            Lines.Add(line);
            return line;
        }

        internal sealed class Line : MonoBehaviour
        {
            public GridItemView Cell;
            public TextMeshProUGUI Caption;
            public TextMeshProUGUI Label;
            public float BaseSize;
            public TextAlignmentOptions BaseAlignment;
            public FontStyles BaseStyle;
            private string _text;
            private bool _inCorner;
            private int _settle;
            private const int SettleFrames = 10;
            private const float CornerPad = 3f;

            public void Show(string text)
            {
                _text = text;
                if (text == null)
                {
                    gameObject.SetActive(false);
                    return;
                }
                Label.text = text;
                Label.fontSize = BaseSize * Plugin.LineSize.Value / 100f;
                Label.fontStyle = BaseStyle;
                Label.color = Plugin.LineColour.Value;
                _settle = SettleFrames;
                Place();
                gameObject.SetActive(Visible());
                transform.SetAsLastSibling();
            }

            // Under the caption, or at the top of the cell when the caption is hidden (trader stock, flea offers).
            private void Place()
            {
                RectTransform from = Caption.rectTransform;
                RectTransform to = Label.rectTransform;
                _inCorner = InCorner();
                Label.alignment = Align();
                if (_inCorner)
                {
                    var cellRect = (RectTransform)Cell.transform;
                    if (to.parent != cellRect) to.SetParent(cellRect, false);
                    to.anchorMin = to.anchorMax = new Vector2(0f, 1f);
                    to.pivot = new Vector2(0f, 1f);
                    float width = cellRect.rect.width - 2 * CornerPad;
                    to.sizeDelta = new Vector2(width, BaseSize * 2f);
                    float textWidth = Mathf.Min(width, Label.GetPreferredValues(_text ?? "").x);
                    Rect cell = cellRect.rect;
                    bool right = Label.alignment == TextAlignmentOptions.TopRight;
                    float from_ = right ? cell.xMax - CornerPad - textWidth : cell.xMin + CornerPad;
                    // drop below anything already in that span (trader price, container tag)
                    to.anchoredPosition = new Vector2(CornerPad, -(CornerPad + TopTaken(cellRect, from_, from_ + textWidth)));
                }
                else
                {
                    if (to.parent != from.parent) to.SetParent(from.parent, false);
                    to.anchorMin = from.anchorMin;
                    to.anchorMax = from.anchorMax;
                    to.pivot = from.pivot;
                    to.sizeDelta = from.sizeDelta;
                    to.anchoredPosition = from.anchoredPosition
                                          + new Vector2(0f, -BaseSize);
                }
            }

            private TextAlignmentOptions Align()
            {
                if (!_inCorner) return BaseAlignment;
                return Plugin.LinePosition.Value == LinePlace.TopLeft ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight;
            }

            private bool InCorner() => Plugin.LinePosition.Value == LinePlace.TopLeft || NameHidden();

            // Lowest point reached by visible text or the tag in the top half, between xFrom and xTo.
            // Uses textBounds, since TMP rects often span the whole cell.
            private float TopTaken(RectTransform cellRect, float xFrom, float xTo)
            {
                Rect cell = cellRect.rect;
                float taken = 0f;
                var corners = new Vector3[4];
                foreach (var graphic in Cell.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    if (graphic == Label || graphic == Caption || !graphic.enabled || graphic.color.a <= 0f) continue;
                    Vector3 low, high;
                    if (graphic is TextMeshProUGUI text)
                    {
                        if (string.IsNullOrEmpty(text.text)) continue;
                        Bounds bounds = text.textBounds;
                        low = cellRect.InverseTransformPoint(text.rectTransform.TransformPoint(bounds.min));
                        high = cellRect.InverseTransformPoint(text.rectTransform.TransformPoint(bounds.max));
                    }
                    else
                    {
                        if (graphic != (UnityEngine.UI.Graphic)Cell._tagColor) continue;
                        graphic.rectTransform.GetWorldCorners(corners);
                        // corners: 0 bottom-left, 2 top-right, in the cell's own space
                        low = cellRect.InverseTransformPoint(corners[0]);
                        high = cellRect.InverseTransformPoint(corners[2]);
                    }
                    if (Mathf.Max(low.y, high.y) < cell.yMax - cell.height / 2f) continue;
                    if (Mathf.Min(low.x, high.x) > xTo || Mathf.Max(low.x, high.x) < xFrom) continue;
                    taken = Mathf.Max(taken, cell.yMax - Mathf.Min(low.y, high.y));
                }
                return Mathf.Min(taken, cell.height / 2f);
            }

            private bool NameHidden() =>
                Caption != null && !Caption.gameObject.activeSelf && Cell != null && !Cell.IsBeingExamined.Value;

            private bool Visible() =>
                _text != null && Cell != null && Cell.Examined && Cell.IsSearched && !Cell.IsBeingExamined.Value;

            private void LateUpdate()
            {
                if (!Visible()) gameObject.SetActive(false);
                // the flea hides the caption and traders fill the price a few frames late, so re-place for a bit
                else if (InCorner() != _inCorner || _settle > 0)
                {
                    if (_settle > 0) _settle--;
                    Place();
                }
            }

            private void OnDestroy() => Lines.Remove(this);
        }
    }

    [HarmonyPatch(typeof(GridItemView), nameof(GridItemView.UpdateItemName))]
    internal static class UpdateItemNamePatch
    {
        [HarmonyPostfix]
        private static void Postfix(GridItemView __instance)
        {
            try
            {
                CellLines.Update(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }

    // cells are pooled and reused
    [HarmonyPatch(typeof(GridItemView), nameof(GridItemView.NewGridItemView))]
    internal static class NewGridItemViewPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GridItemView __instance)
        {
            try
            {
                CellLines.Update(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }

    [HarmonyPatch(typeof(GridItemView), nameof(GridItemView.OnBeingExaminedChanged))]
    internal static class ExaminedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(GridItemView __instance)
        {
            try
            {
                CellLines.Update(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }
}
