using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace VideoSyncPrototype.PlayerSearch;

internal sealed class PlayerProfileService
{
    private const string OpenCharaCardSignature = "48 85 D2 74 6D 48 89 5C 24";

    private readonly OpenCharaCardForObjectDelegate? openCharaCard;

    private delegate void OpenCharaCardForObjectDelegate(nint agent, nint gameObject);

    public PlayerProfileService()
    {
        if (Plugin.SigScanner.TryScanText(OpenCharaCardSignature, out var address))
        {
            this.openCharaCard = Marshal.GetDelegateForFunctionPointer<OpenCharaCardForObjectDelegate>(address);
        }

    }

    public bool CanOpenPlate => this.openCharaCard is not null;
    public bool CanExamine => Plugin.ChatSender.IsReady;

    public bool TryExamine(NearbyPlayer player, out string error)
    {
        error = string.Empty;
        if (!Plugin.ChatSender.IsReady)
        {
            error = "The game command service is unavailable.";
            return false;
        }

        var livePlayer = FindLivePlayer(player);
        if (livePlayer is null)
        {
            error = $"{player.Name} is no longer visible. Search again.";
            return false;
        }

        try
        {
            // Use the game's normal examine command. Changing target is intentional and
            // avoids invoking patch-sensitive native inspect functions.
            Plugin.TargetManager.Target = livePlayer;
            Plugin.ChatSender.ExecuteCommand("/check <t>");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not examine {player.Name}.");
            error = $"Could not examine {player.Name}.";
            return false;
        }
    }

    public unsafe bool TryOpenPlate(NearbyPlayer player, out string error)
    {
        error = string.Empty;
        if (this.openCharaCard is null)
        {
            error = "The adventurer plate opener is unavailable for this game version.";
            return false;
        }

        var livePlayer = FindLivePlayer(player);
        if (livePlayer is null)
        {
            error = $"{player.Name} is no longer visible. Search again.";
            return false;
        }

        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.CharaCard);
        if (agent is null)
        {
            error = "The adventurer plate window is not available right now.";
            return false;
        }

        try
        {
            this.openCharaCard((nint)agent, livePlayer.Address);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not open adventurer plate for {player.Name}.");
            error = $"Could not open {player.Name}'s adventurer plate.";
            return false;
        }
    }

    private static IPlayerCharacter? FindLivePlayer(NearbyPlayer player)
    {
        return Plugin.ObjectTable
            .OfType<IPlayerCharacter>()
            .FirstOrDefault(candidate => candidate.Address == player.Address);
    }

    public static bool TryOpenLodestone(NearbyPlayer player, out string error)
    {
        error = string.Empty;
        try
        {
            var url = "https://na.finalfantasyxiv.com/lodestone/character/" +
                      $"?search_type=character&q={Uri.EscapeDataString(player.Name)}";
            if (!string.IsNullOrWhiteSpace(player.HomeWorld))
            {
                url += $"&worldname={Uri.EscapeDataString(player.HomeWorld)}";
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not open Lodestone search for {player.Name}.");
            error = $"Could not open the Lodestone for {player.Name}.";
            return false;
        }
    }
}
