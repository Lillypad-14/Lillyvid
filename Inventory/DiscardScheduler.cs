using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace VideoSyncPrototype.Inventory;

/// <summary>
/// Discards a queue of items one at a time, driving the game's "Discard this item?"
/// yes/no dialog automatically. The sequence is a small timed state machine ticked
/// from <see cref="IFramework.Update"/>.
///
/// Every item is re-checked against the hard safety floor immediately before it is
/// discarded, so nothing protected can slip through even if the list is stale.
/// </summary>
public sealed class DiscardScheduler : IDisposable
{
    private enum Phase
    {
        Idle,
        StartItem,
        AwaitConfirm,
        AwaitClose,
    }

    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoDialogGiveUp = TimeSpan.FromSeconds(3);

    private readonly List<InventoryItemInfo> queue = [];
    private HashSet<uint> blacklist = [];
    private Phase phase = Phase.Idle;
    private int index;
    private DateTime nextAction;
    private DateTime itemStarted;
    private bool subscribed;

    public bool IsRunning => this.phase != Phase.Idle;

    public int Progress => this.index;

    public int Total => this.queue.Count;

    public string? Error { get; private set; }

    /// <summary>Queues the given items for discard and begins the automated sequence.</summary>
    public void Start(IEnumerable<InventoryItemInfo> items, HashSet<uint> userBlacklist)
    {
        if (this.IsRunning)
        {
            return;
        }

        this.queue.Clear();
        this.queue.AddRange(items);
        this.blacklist = userBlacklist;
        this.index = 0;
        this.Error = null;

        if (this.queue.Count == 0)
        {
            return;
        }

        this.phase = Phase.StartItem;
        this.nextAction = DateTime.Now;
        if (!this.subscribed)
        {
            Plugin.Framework.Update += this.OnUpdate;
            this.subscribed = true;
        }
    }

    public void Cancel()
    {
        this.queue.Clear();
        this.index = 0;
        this.phase = Phase.Idle;
        if (this.subscribed)
        {
            Plugin.Framework.Update -= this.OnUpdate;
            this.subscribed = false;
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (this.phase == Phase.Idle || DateTime.Now < this.nextAction)
        {
            return;
        }

        switch (this.phase)
        {
            case Phase.StartItem:
                this.StartNextItem();
                break;
            case Phase.AwaitConfirm:
                this.AwaitConfirm();
                break;
            case Phase.AwaitClose:
                this.AwaitClose();
                break;
        }
    }

    private void StartNextItem()
    {
        if (this.index >= this.queue.Count)
        {
            Plugin.ChatGui.Print("[Lillypad] Finished discarding items.");
            this.Cancel();
            return;
        }

        var item = this.queue[this.index];

        // Re-verify against the hard floor right before acting — never trust a stale list.
        if (!InventoryReader.IsSafeToDiscard(item, this.blacklist))
        {
            Plugin.Log.Warning($"[Inventory] Skipping protected item {item.Name}");
            this.index++;
            this.nextAction = DateTime.Now;
            return;
        }

        try
        {
            InventoryReader.DiscardItem(item);
            this.itemStarted = DateTime.Now;
            this.phase = Phase.AwaitConfirm;
            this.nextAction = DateTime.Now.AddMilliseconds(400);
        }
        catch (Exception ex)
        {
            this.Error = $"Failed to discard {item.Name}: {ex.Message}";
            Plugin.Log.Error(ex, $"[Inventory] Failed to discard {item.Name}");
            this.Cancel();
        }
    }

    private unsafe void AwaitConfirm()
    {
        var addon = FindDiscardDialog();
        if (addon != null)
        {
            var yesNo = (AddonSelectYesno*)addon;
            if (yesNo->YesButton != null)
            {
                yesNo->YesButton->AtkComponentBase.SetEnabledState(true);
            }

            addon->FireCallbackInt(0);
            this.phase = Phase.AwaitClose;
            this.nextAction = DateTime.Now.AddMilliseconds(300);
            return;
        }

        // Some discards complete without a confirmation. If none shows up in time,
        // assume this one went through and move on.
        if (DateTime.Now - this.itemStarted > NoDialogGiveUp)
        {
            this.index++;
            this.phase = Phase.StartItem;
            this.nextAction = DateTime.Now.AddMilliseconds(200);
            return;
        }

        this.nextAction = DateTime.Now.AddMilliseconds(100);
    }

    private unsafe void AwaitClose()
    {
        if (FindDiscardDialog() != null)
        {
            this.nextAction = DateTime.Now.AddMilliseconds(100);
            return;
        }

        this.index++;
        this.phase = Phase.StartItem;
        this.nextAction = DateTime.Now.AddMilliseconds(200);
    }

    /// <summary>
    /// Finds a visible "SelectYesno" addon whose prompt actually mentions discarding, so
    /// we never click Yes on an unrelated confirmation dialog.
    /// </summary>
    private static unsafe AtkUnitBase* FindDiscardDialog()
    {
        for (var i = 1; i < 100; i++)
        {
            var ptr = Plugin.GameGui.GetAddonByName("SelectYesno", i);
            if (ptr.IsNull)
            {
                continue;
            }

            var addon = (AtkUnitBase*)ptr.Address;
            if (addon == null || !addon->IsVisible || addon->UldManager.LoadedState != AtkLoadState.Loaded)
            {
                continue;
            }

            var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
            if (textNode == null)
            {
                continue;
            }

            var text = textNode->NodeText.ToString();
            if (text.Contains("discard", StringComparison.OrdinalIgnoreCase))
            {
                return addon;
            }
        }

        return null;
    }

    public void Dispose() => this.Cancel();
}
