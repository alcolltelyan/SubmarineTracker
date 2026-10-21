using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using SubmarineTracker.Resources;

namespace SubmarineTracker.Windows.Config;

public partial class ConfigWindow
{
    private int CurrentAccountId;
    private string NewAccountName = string.Empty;

    private ulong SelectedAccountId;

    private void Manage()
    {
        using var tabItem = ImRaii.TabItem($"{Language.ConfigTabManage}##Manage");
        if (!tabItem.Success)
            return;

        using var child = ImRaii.Child("FCContent", Vector2.Zero);
        if (!child.Success)
            return;

        AccountManagement();

        ImGuiHelpers.ScaledDummy(20.0f);

        FCManagingTable();

        ImGuiHelpers.ScaledDummy(5.0f);

        CharacterManagingTable();
    }

    private void AccountManagement()
    {
        ImGuiHelpers.ScaledDummy(5.0f);
        Helper.TextColored(ImGuiColors.DalamudViolet, "Accounts:");
        var combo = Plugin.Configuration.AccountFCs.Keys.ToArray();
        Helper.DrawComboWithArrows("##CollectionSelector", ref CurrentAccountId, ref combo);

        ImGui.SameLine();

        using (ImRaii.Disabled(combo.Length == 0))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
            {
                Plugin.Configuration.AccountFCs.Remove(combo[CurrentAccountId]);
                CurrentAccountId = 0;
            }
        }

        ImGui.InputTextWithHint("##AccountNameInput", "New account name ...", ref NewAccountName, 32);
        ImGui.SameLine();

        using (ImRaii.Disabled(NewAccountName.Length <= 3))
        {
            if (ImGuiComponents.IconButton(2, FontAwesomeIcon.Plus))
            {
                if (!Plugin.Configuration.AccountFCs.TryAdd(NewAccountName, []))
                    Utils.AddNotification(Language.ErrorCollectionExists, NotificationType.Error, false);

                combo = Plugin.Configuration.AccountFCs.Keys.ToArray();
                CurrentAccountId = Array.FindIndex(combo, s => s == NewAccountName);
                if (CurrentAccountId == -1)
                    CurrentAccountId = 0;

                NewAccountName = string.Empty;
                Plugin.Configuration.Save();
            }
        }

        ImGuiHelpers.ScaledDummy(5.0f);

        using var savesTable = ImRaii.Table("##AccountTable", 5, ImGuiTableFlags.BordersH);
        if (savesTable.Success)
        {
            ImGui.TableSetupColumn("Accounts");
            ImGui.TableSetupColumn("##OrderUp", ImGuiTableColumnFlags.WidthStretch, 0.07f);
            ImGui.TableSetupColumn("##OrderDown", ImGuiTableColumnFlags.WidthStretch, 0.07f);
            ImGui.TableSetupColumn("##Hidden", ImGuiTableColumnFlags.WidthStretch, 0.07f);
            ImGui.TableSetupColumn("##Del", ImGuiTableColumnFlags.WidthStretch, 0.07f);

            ImGui.TableHeadersRow();

            Plugin.EnsureFCOrderSafety();

            var allFCs = Plugin.DatabaseCache.GetFreeCompanies();
            foreach (var (key, fcList) in Plugin.Configuration.AccountFCs.ToArray())
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.HealerGreen, key);

                var remove = 0ul;
                var hideFc = 0ul;
                (int OrgIdx, int NewIdx) changedOrder = (-1, -1);

                var arrowsDisabled = fcList.Count < 2;
                var firstFC = !arrowsDisabled ? fcList.First() : 0;
                var lastFc = !arrowsDisabled ? fcList.Last() : 0;
                foreach (var (idx, fcId) in fcList.Index())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Plugin.NameConverter.GetCombinedName(allFCs[fcId]));

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Up", FontAwesomeIcon.ArrowUp, arrowsDisabled || firstFC == fcId))
                        changedOrder = (idx, idx - 1);

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Down", FontAwesomeIcon.ArrowDown, arrowsDisabled || lastFc == fcId))
                        changedOrder = (idx, idx + 1);

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Hide", FontAwesomeIcon.Eye))
                        hideFc = fcId;

                    if (ImGui.IsItemHovered())
                        Helper.Tooltip("Hide this FC from all UIs.");

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Remove", FontAwesomeIcon.Times))
                        remove = fcId;

                    if (ImGui.IsItemHovered())
                        Helper.Tooltip("Remove this FC from the account.");
                }

                if (changedOrder.OrgIdx != -1)
                {
                    Plugin.Configuration.AccountFCs[key].Swap(changedOrder.OrgIdx, changedOrder.NewIdx);
                    Plugin.Configuration.Save();
                }

                if (hideFc != 0)
                {
                    Plugin.Configuration.AccountFCs[key].Remove(hideFc);

                    var idx = Plugin.Configuration.ManagedFCs.FindIndex(status => status.Id == hideFc);
                    if (idx != -1)
                        Plugin.Configuration.ManagedFCs[idx] = (hideFc, true);

                    Plugin.Configuration.Save();
                }

                if (remove != 0)
                {
                    Plugin.Configuration.AccountFCs[key].Remove(remove);
                    Plugin.Configuration.Save();
                }
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGuiHelpers.ScaledDummy(20.0f);

            var noAccountFCs = Plugin.GetManagedFCs(false);
            if (noAccountFCs.Length > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.HealerGreen, "Not Assigned");

                (int HideIdx, ulong FCId) hide = (-1, 0);
                (int OrgIdx, int NewIdx) changedOrder = (-1, -1);

                var plusDisabled = Plugin.Configuration.AccountFCs.Count == 0;
                var arrowsDisabled = noAccountFCs.Length < 2;
                var firstFC = !arrowsDisabled ? noAccountFCs.First().Id : 0;
                var lastFc = !arrowsDisabled ? noAccountFCs.Last().Id : 0;
                foreach (var (fcId, _) in noAccountFCs)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Plugin.NameConverter.GetCombinedName(allFCs[fcId]));

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Add", FontAwesomeIcon.Plus, plusDisabled))
                    {
                        SelectedAccountId = fcId;
                        ImGui.OpenPopup("##accountPopup");
                    }

                    if (ImGui.IsItemHovered())
                        Helper.Tooltip("Add this FC to an account.");

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Up", FontAwesomeIcon.ArrowUp, arrowsDisabled || firstFC == fcId))
                    {
                        var idx = Plugin.Configuration.ManagedFCs.FindIndex(status => status.Id == fcId);
                        changedOrder = (idx, idx - 1);
                    }

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Down", FontAwesomeIcon.ArrowDown, arrowsDisabled || lastFc == fcId))
                    {
                        var idx = Plugin.Configuration.ManagedFCs.FindIndex(status => status.Id == fcId);
                        changedOrder = (idx, idx + 1);
                    }

                    ImGui.TableNextColumn();
                    if (Helper.Button($"##{fcId}Hide", FontAwesomeIcon.Eye))
                    {
                        var idx = Plugin.Configuration.ManagedFCs.FindIndex(status => status.Id == fcId);
                        hide = (idx, fcId);
                    }

                    if (ImGui.IsItemHovered())
                        Helper.Tooltip("Hide this FC from all UIs.");
                }

                if (changedOrder.OrgIdx != -1)
                {
                    Plugin.Configuration.ManagedFCs.Swap(changedOrder.OrgIdx, changedOrder.NewIdx);
                    Plugin.Configuration.Save();
                }

                if (hide.HideIdx != -1)
                {
                    Plugin.Configuration.ManagedFCs[hide.HideIdx] = (hide.FCId, true);
                    Plugin.Configuration.Save();
                }
            }

            var hiddenAccountFCs = Plugin.GetManagedFCs(true);
            if (hiddenAccountFCs.Length > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.HealerGreen, "Hidden");

                (int HideIdx, ulong FCId) hide = (-1, 0);
                foreach (var (fcId, _) in hiddenAccountFCs)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Plugin.NameConverter.GetCombinedName(allFCs[fcId]));

                    ImGui.TableSetColumnIndex(4);
                    if (Helper.Button($"##{fcId}Hide", FontAwesomeIcon.EyeSlash))
                    {
                        var idx = Plugin.Configuration.ManagedFCs.FindIndex(status => status.Id == fcId);
                        hide = (idx, fcId);
                    }

                    if (ImGui.IsItemHovered())
                        Helper.Tooltip("Unhide this FC.");
                }

                if (hide.HideIdx != -1)
                {
                    Plugin.Configuration.ManagedFCs[hide.HideIdx] = (hide.FCId, false);
                    Plugin.Configuration.Save();
                }
            }
        }

        AddToAccount();
    }

    private void AddToAccount()
    {
        ImGui.SetNextWindowSize(new Vector2(200 * ImGuiHelpers.GlobalScale, 90 * ImGuiHelpers.GlobalScale));
        using var context = ImRaii.ContextPopupItem("##accountPopup", ImGuiPopupFlags.None);
        if (!context.Success)
            return;

        using var child = ImRaii.Child("AccountChild", Vector2.Zero, false);
        if (!child.Success)
            return;

        var ret = false;
        foreach (var key in Plugin.Configuration.AccountFCs.Keys)
        {
            if (ImGui.Selectable(key))
            {
                Plugin.Configuration.AccountFCs[key].Add(SelectedAccountId);
                Plugin.Configuration.Save();

                SelectedAccountId = 0;
                ret = true;
            }
        }

        // ImGui issue #273849, children keep popups from closing automatically
        if (ret)
            ImGui.CloseCurrentPopup();
    }

    private void FCManagingTable()
    {
        using var savesTable = ImRaii.Table("##DeleteSavesTable", 5, ImGuiTableFlags.BordersH);
        if (savesTable.Success)
        {
            ImGui.TableSetupColumn(Language.TermsSavedFCs);
            ImGui.TableSetupColumn("##OrderUp", ImGuiTableColumnFlags.WidthStretch, 0.07f);
            ImGui.TableSetupColumn("##OrderDown", ImGuiTableColumnFlags.WidthStretch, 0.07f);
            ImGui.TableSetupColumn("##Hidden", ImGuiTableColumnFlags.WidthStretch, 0.07f);
            ImGui.TableSetupColumn("##Del", ImGuiTableColumnFlags.WidthStretch, 0.09f);

            ImGui.TableHeadersRow();

            Plugin.EnsureFCOrderSafety();
            if (Plugin.Configuration.ManagedFCs.Count == 0)
                return;

            (int DelIdx, ulong FCId) deletion = (-1, 0);
            (int OrgIdx, int NewIdx) changedOrder = (-1, -1);
            (int Idx, (ulong, bool) Status) changedStatus = (-1, (0, false));

            var firstFC = Plugin.Configuration.ManagedFCs.First();
            var lastFC = Plugin.Configuration.ManagedFCs.Last();
            foreach (var ((id, hidden), idx) in Plugin.Configuration.ManagedFCs.WithIndex())
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Plugin.NameConverter.GetCombinedName(Plugin.DatabaseCache.GetFreeCompanies()[id]));

                ImGui.TableNextColumn();
                if (Helper.Button($"##{id}Up", FontAwesomeIcon.ArrowUp, firstFC.Id == id))
                    changedOrder = (idx, idx - 1);

                ImGui.TableNextColumn();
                if (Helper.Button($"##{id}Down", FontAwesomeIcon.ArrowDown, lastFC.Id == id))
                    changedOrder = (idx, idx + 1);

                ImGui.TableNextColumn();
                if (Helper.Button($"##{id}Hide", hidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye))
                    changedStatus = (idx, (id, !hidden));

                ImGui.TableNextColumn();
                if (Helper.Button($"##{id}Del", FontAwesomeIcon.Trash, !ImGui.GetIO().KeyCtrl))
                    deletion = (idx, id);

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    Helper.Tooltip(Language.ConfigTabTooltipSavedFCsDeletion);

                if (lastFC.Id != id)
                    ImGui.TableNextRow();
            }

            if (changedOrder.OrgIdx != -1)
            {
                Plugin.Configuration.ManagedFCs.Swap(changedOrder.OrgIdx, changedOrder.NewIdx);
                Plugin.Configuration.Save();
            }

            if (changedStatus.Idx != -1)
            {
                Plugin.Configuration.ManagedFCs[changedStatus.Idx] = changedStatus.Status;
                Plugin.Configuration.Save();
            }

            if (deletion.DelIdx != -1)
            {
                Plugin.Configuration.ManagedFCs.RemoveAt(deletion.DelIdx);
                Plugin.Configuration.Save();

                if (!Plugin.DatabaseCache.Database.DeleteFreeCompany(deletion.FCId))
                    Utils.AddNotification(Language.ErrorDeletionFailed, NotificationType.Error, false);
            }
        }
    }

    private void CharacterManagingTable()
    {
        using var charactersTable = ImRaii.Table("##IgnoredCharacters", 2, ImGuiTableFlags.BordersH);
        if (charactersTable.Success)
        {
            ImGui.TableSetupColumn(Language.TermsIgnoredCharacters);
            ImGui.TableSetupColumn("##CharacterDel", ImGuiTableColumnFlags.WidthStretch, 0.07f);

            ImGui.TableHeadersRow();
            foreach (var (id, name) in Plugin.Configuration.IgnoredCharacters.ToArray())
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Plugin.NameConverter.GetCharacterName(name));

                ImGui.TableNextColumn();
                if (Helper.Button($"##{id}CharacterDel", FontAwesomeIcon.Trash, !ImGui.GetIO().KeyCtrl))
                {
                    Plugin.Configuration.IgnoredCharacters.Remove(id);
                    Plugin.Configuration.Save();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    Helper.Tooltip(Language.ConfigTabTooltipIgnoredCharacterDelete);

                ImGui.TableNextRow();
            }

            ImGui.TableNextColumn();
            if (ImGui.Button(Language.TermsAddCurrentCharacter))
            {
                var local = Plugin.ObjectTable.LocalPlayer;
                if (local != null)
                {
                    var name = local.Name.TextValue;
                    var tag = local.CompanyTag.TextValue;
                    var world = local.HomeWorld.Value.Name.ExtractText();

                    Plugin.Configuration.IgnoredCharacters.Add(Plugin.PlayerState.ContentId, $"({tag}) {name}@{world}");
                    Plugin.Configuration.Save();
                }
            }
        }
    }
}
