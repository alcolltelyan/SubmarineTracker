using SubmarineTracker.Data;
using SubmarineTracker.Resources;

namespace SubmarineTracker.Windows.Helpy;

public partial class HelpyWindow
{
    private static readonly Vector2 IconSize = new(32, 32);

    private void StorageTab()
    {
        using var tabItem = ImRaii.TabItem($"{Language.HelpyTabStorage}##Storage");
        if (!tabItem.Success)
            return;

        ImGuiHelpers.ScaledDummy(5.0f);
        if (!Storage.HasStorageData())
        {
            Helper.TextColored(ImGuiColors.ParsedOrange, Language.HelpyStorageTabWarning);
            return;
        }

        foreach (var key in Plugin.GetFCOrderWithoutHidden())
        {
            if (!Plugin.DatabaseCache.TryGetFC(key, out var fc))
                continue;

            Helper.TextColored(ImGuiColors.DalamudViolet, $"{Plugin.NameConverter.GetName(fc)}:");

            using var indent = ImRaii.PushIndent(10.0f);
            using var table = ImRaii.Table($"##SubmarineOverview{key}", 3);
            if (!table.Success)
                continue;

            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthStretch, 0.1f);
            ImGui.TableSetupColumn("##count", ImGuiTableColumnFlags.WidthStretch, 0.15f);
            ImGui.TableSetupColumn("##item");

            foreach (var item in ItemExtensions.ImportantItems)
            {
                var itemRow = item.GetItem();
                var ok = Storage.TryGetStorageCount((uint)Items.Tanks, fc, out var storageCount);

                ImGui.TableNextColumn();
                Helper.DrawScaledIcon(itemRow.Icon, IconSize);

                var count = ok ? $"{storageCount}x" : Language.WarningNoStorageCount;
                ImGui.TableNextColumn();
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(count).X);
                ImGui.TextUnformatted(count);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(itemRow.Name.ToString());

                ImGui.TableNextRow();
            }

            ImGuiHelpers.ScaledDummy(10.0f);
        }
    }
}
