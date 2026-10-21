using Dalamud.Utility;
using SubmarineTracker.Data;
using SubmarineTracker.Resources;

namespace SubmarineTracker.Windows.Main;

public partial class MainWindow
{
    private void All()
    {
        var numberText = "1. ";
        var rankText = $"{Language.TermsRank} 999 ";
        var identifierText = "(WWWW++)";
        var extraText = "[";
        if (Plugin.Configuration.ShowDateInAll)
            extraText += " 24/12/2000 23:59:59 ";
        else
            extraText += " 123:59:59 ";

        if (Plugin.Configuration.ShowRouteInAll)
            extraText += " AA->AB->AC->W->Z ";
        extraText += "]";

        var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
        var indentWidth = 10.0f * ImGuiHelpers.GlobalScale;
        var secondRowWidth = ImGui.CalcTextSize(numberText).X + itemSpacing + ImGui.CalcTextSize(rankText).X + itemSpacing;
        var thirdRowWidth = ImGui.CalcTextSize(identifierText).X + itemSpacing;
        var extraTextWidth =  ImGui.CalcTextSize(extraText).X;

        var numberOfRows = (int)(ImGui.GetContentRegionAvail().X / (indentWidth + secondRowWidth + thirdRowWidth + extraTextWidth + 20.0f * ImGuiHelpers.GlobalScale));

        // Ensure that atleast 1 column is requested
        numberOfRows = Math.Max(1, numberOfRows);

        var allFCs = Plugin.DatabaseCache.GetFreeCompanies();
        foreach (var (name, fcLists) in Plugin.Configuration.AccountFCs)
        {
            if (!ImGui.CollapsingHeader($"{name}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            using var allTable = ImRaii.Table($"##allTable{name}", numberOfRows);
            if (!allTable.Success)
                return;

            foreach (var id in fcLists)
            {
                ShowFCInfo(allFCs[id], indentWidth, secondRowWidth, thirdRowWidth);

                ImGuiHelpers.ScaledDummy(5.0f);
            }
        }

        var unassignedFCs = Plugin.GetManagedFCs(false);
        if (unassignedFCs.Length == 0)
            return;

        if (!ImGui.CollapsingHeader($"Not Assigned", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        using var notAssignedTable = ImRaii.Table($"##allTableNotAssigned", numberOfRows);
        if (!notAssignedTable.Success)
            return;

        foreach (var (id, _) in Plugin.GetManagedFCs(false))
        {
            ShowFCInfo(allFCs[id], indentWidth, secondRowWidth, thirdRowWidth);

            ImGuiHelpers.ScaledDummy(5.0f);
        }
    }

    private void ShowFCInfo(FreeCompany fc, float indentWidth, float secondRowWidth, float thirdRowWidth)
    {
        ImGui.TableNextColumn();
        Helper.TextColored(ImGuiColors.DalamudViolet, $"{Plugin.NameConverter.GetName(fc)}:");

        var subs = Plugin.DatabaseCache.GetSubmarines(fc.FreeCompanyId);

        if (Plugin.Configuration.ShowResourcesInAll)
        {
            using var indent = ImRaii.PushIndent(10.0f);

            var hasTanks = Storage.TryGetStorageCount((uint)Items.Tanks, fc, out var tankCount);
            var hasKits = Storage.TryGetStorageCount((uint)Items.Kits, fc, out var kitCount);

            var leftover = Storage.CheckLeftoversFromStorage(subs, tankCount, kitCount);
            if (leftover.Voyages == -1 || leftover.Repairs == -1)
            {
                Helper.TextColored(ImGuiColors.ErrorForeground, Language.ErrorLeftoverCalculationFail);
            }
            else
            {
                Vector4 color;
                string leftoverText;
                var storageText = $"{Language.TermsTanks} {(hasTanks ? $"x{tankCount:N0}" : Language.WarningNoStorageCount)} & {Language.TermsKits} {(hasKits ? $"x{kitCount:N0}" : Language.WarningNoStorageCount)}";

                if (leftover is { Voyages: 0, Repairs: 0 })
                {
                    leftoverText = Language.StorageBoth;
                    color = ImGuiColors.ErrorForeground;
                }
                else if (leftover.Voyages == 0)
                {
                    leftoverText = Language.StorageNoTanks;
                    color = ImGuiColors.AttentionForeground;
                }
                else if (leftover.Repairs == 0)
                {
                    leftoverText = Language.StorageNoKits;
                    color = ImGuiColors.AttentionForeground;
                }
                else
                {
                    leftoverText = Language.StorageAllOkayShort.Format(leftover.Voyages, leftover.Repairs);
                    color = ImGuiColors.SuccessForeground;
                }


                if (!Plugin.Configuration.SwapResourcesInAll)
                    ColoredTextWithHover(storageText, leftoverText, color);
                else
                    ColoredTextWithHover(leftoverText, storageText, color);
            }
        }

        foreach (var (sub, idx) in subs.WithIndex())
        {
            using var indent = ImRaii.PushIndent(10.0f);
            var begin = ImGui.GetCursorScreenPos();

            Helper.TextColored(ImGuiColors.HealerGreen, $"{idx + 1}. ");
            ImGui.SameLine();

            var condition = sub.PredictDurability() > 0;
            var color = condition ? ImGuiColors.TankBlue : ImGuiColors.DalamudYellow;
            Helper.TextColored(color, $"{Language.TermsRank} {sub.Rank}");
            ImGui.SameLine(indentWidth + secondRowWidth);
            Helper.TextColored(color, $"({sub.Build.FullIdentifier()})");

            ImGui.SameLine(indentWidth + secondRowWidth + thirdRowWidth);

            var route = "";
            var time = $" {Language.TermsNoVoyage} ";
            if (sub.IsOnVoyage())
            {
                route = Utils.SectorsToPath(" -> ", sub.Points);

                time = $" {Language.TermsDone} ";
                var returnTime = sub.ReturnTime - DateTime.Now.ToUniversalTime();
                if (returnTime.TotalSeconds > 0)
                    time = !Plugin.Configuration.ShowDateInAll ? $" {Utils.ToTime(returnTime)} " : $" {sub.ReturnTime.ToLocalTime()}";
            }

            var fullText = $"[ {time}{(Plugin.Configuration.ShowRouteInAll ? $"   {route}" : "")} ]";
            Helper.TextColored(ImGuiColors.ParsedOrange, fullText);

            var textSize = ImGui.CalcTextSize(fullText);
            var end = new Vector2(begin.X + textSize.X + indentWidth + secondRowWidth + thirdRowWidth, begin.Y + textSize.Y + ImGui.GetStyle().ItemSpacing.Y);
            if (ImGui.IsMouseHoveringRect(begin, end))
            {
                var tooltip = condition ? "" : $"{Language.ReturnOverlayTooltipRepairNeeded}\n";
                tooltip += $"{Language.TermsRank} {sub.Rank}    ({sub.Build.FullIdentifier()})\n";

                var predictedExp = sub.PredictExpGrowth();
                tooltip += $"{Language.TermsRoute}: {route}\n";
                tooltip += $"{Language.TermsEXPAfter}: {predictedExp.Rank} ({predictedExp.Exp:##0.00}%)\n";
                tooltip += $"{Language.TermsRepair}: {Language.MainWindowTooltipRepair.Format(sub.Build.RepairCosts, sub.CalculateUntilRepair())}";

                Helper.Tooltip(tooltip);
            }
        }
    }

    private void ColoredTextWithHover(string text, string hover, Vector4 color)
    {
        Helper.TextColored(color, text);
        if (ImGui.IsItemHovered())
        {
            using var tooltip = ImRaii.Tooltip();
            Helper.TextColored(color, hover);
        }
    }
}
