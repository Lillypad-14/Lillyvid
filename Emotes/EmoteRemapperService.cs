using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace VideoSyncPrototype.Emotes;

internal sealed class EmoteRemapperService : IDisposable
{
    private const string SwapModName = "_LillypadEmoteSwap";
    private const int SwapPriority = 200;
    private const float MaxAlignDistance = 2f;

    private static readonly string[] RaceIds =
    [
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601",
        "c0701", "c0801", "c0901", "c1001", "c1101", "c1201",
        "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    ];

    private static readonly string[] ResolveSubfolders =
    [
        string.Empty, "resident/", "nonresident/", "bt_common/",
        "bt_swd_sld/", "bt_clw_clw/", "bt_2ax_emp/", "bt_2sp_emp/",
        "bt_2bw_emp/", "bt_stf_sld/", "bt_jst_sld/", "bt_2bk_emp/",
        "bt_dgr_dgr/", "bt_2gn_emp/", "bt_2sw_emp/", "bt_2gl_emp/",
        "bt_2kt_emp/", "bt_2rp_emp/", "bt_rod_emp/", "bt_2gb_emp/",
        "bt_chk_chk/", "bt_2km_emp/", "bt_2ff_emp/", "bt_bld_bld/",
        "bt_brs_plt/",
    ];

    private static readonly string[] CarrierSubfolders = [string.Empty, "bt_common/", "resident/", "nonresident/"];

    private static readonly HashSet<string> NoCarrierCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/sit", "/groundsit", "/doze", "/changepose", "/cpose", "/hug", "/pet", "/handover", "/embrace", "/dote",
        "/showleft", "/showright", "/savortea", "/hildy", "/songbird",
    };

    private static readonly string[] PreferredOneShotCarriers =
    [
        "/no", "/me", "/bow", "/clap", "/yes", "/wave", "/cheer", "/dance", "/laugh",
    ];

    private readonly Configuration config;
    private readonly PenumbraService penumbra = new();
    private readonly Dictionary<string, ushort> commandToEmoteId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, List<TimelineEntry>> emoteTimelines = [];
    private readonly HashSet<string> registeredTriggerCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> registeredOneOffCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ushort> usedCarrierIds = [];
    private readonly List<string> emoteCommands = [];
    private int swapCounter;
    private bool swapRegistered;
    private bool swapDirty;
    private Guid activeCollectionId = Guid.Empty;

    public EmoteRemapperService(Configuration config)
    {
        this.config = config;
        this.penumbra.Initialize();
        this.BuildEmoteLookup();
        this.RegisterOneOffCommands();
        this.RefreshCommandHandlers();
    }

    public string Status { get; private set; } = "Ready.";

    public bool PenumbraAvailable => this.penumbra.IsAvailable;

    public IReadOnlyList<string> EmoteCommands => this.emoteCommands;

    public string OneOffCommandHint => this.registeredOneOffCommands.Contains("/vanilla")
        ? "/vanilla <emote>"
        : "/lillyemote <emote>";

    public void NotifyTargetChanged()
    {
        this.swapDirty = true;
        this.usedCarrierIds.Clear();
    }

    public unsafe void AlignToTarget()
    {
        var target = Plugin.TargetManager.Target ?? Plugin.TargetManager.SoftTarget;
        if (target is not IPlayerCharacter targetPlayer)
        {
            this.Status = "Target a nearby player first.";
            return;
        }

        var player = (Character*)(Plugin.ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player is null)
        {
            this.Status = "Your character is not available.";
            return;
        }

        if (player->Mode != CharacterModes.Normal)
        {
            this.Status = player->Mode switch
            {
                CharacterModes.Mounted => "Dismount before aligning.",
                CharacterModes.EmoteLoop => "Stop your current emote before aligning.",
                CharacterModes.InPositionLoop => "Stand up before aligning.",
                CharacterModes.Performance => "Stop performing before aligning.",
                _ => "Return to a normal standing state before aligning.",
            };
            return;
        }

        var playerPosition = new System.Numerics.Vector3(
            player->GameObject.Position.X,
            player->GameObject.Position.Y,
            player->GameObject.Position.Z);
        var distance = System.Numerics.Vector3.Distance(playerPosition, targetPlayer.Position);
        if (distance > MaxAlignDistance)
        {
            this.Status = $"Move closer to {targetPlayer.Name.TextValue}; align works within {MaxAlignDistance:0} yalms.";
            return;
        }

        var targetPosition = targetPlayer.Position;
        player->GameObject.SetPosition(targetPosition.X, targetPosition.Y, targetPosition.Z);
        player->GameObject.SetRotation(targetPlayer.Rotation);
        this.Status = $"Aligned to {targetPlayer.Name.TextValue}.";
    }

    public void RefreshCommandHandlers()
    {
        foreach (var command in this.registeredTriggerCommands)
        {
            Plugin.CommandManager.RemoveHandler(command);
        }

        this.registeredTriggerCommands.Clear();

        if (!this.config.EmoteRemapperEnabled)
        {
            return;
        }

        foreach (var entry in this.config.EmoteRemaps.Where(e => e.Enabled))
        {
            var trigger = NormalizeCommand(entry.TriggerCommand);
            if (string.IsNullOrWhiteSpace(trigger) || trigger is "/lilly" or "/pad")
            {
                continue;
            }

            if (!this.registeredTriggerCommands.Add(trigger))
            {
                continue;
            }

            try
            {
                Plugin.CommandManager.AddHandler(trigger, new CommandInfo((_, _) => this.Execute(entry))
                {
                    HelpMessage = $"Play Lillypad emote remap: {entry.Name}",
                });
            }
            catch (Exception ex)
            {
                this.registeredTriggerCommands.Remove(trigger);
                Plugin.Log.Warning($"Could not register emote trigger {trigger}: {ex.Message}");
            }
        }
    }

    public bool Execute(EmoteRemapEntry entry)
    {
        if (!this.config.EmoteRemapperEnabled)
        {
            this.Status = "Emote remapper is disabled.";
            return false;
        }

        var target = NormalizeCommand(entry.TargetEmoteCommand);
        var carrier = NormalizeCommand(entry.CarrierEmoteCommand);
        if (string.IsNullOrWhiteSpace(target) || (!entry.AutoCarrier && string.IsNullOrWhiteSpace(carrier)))
        {
            this.Status = "Set both a modded emote and a carrier emote.";
            return false;
        }

        if (!this.penumbra.IsAvailable)
        {
            this.penumbra.Initialize();
            if (!this.penumbra.IsAvailable)
            {
                this.Status = "Penumbra IPC is not available.";
                return false;
            }
        }

        if (this.swapDirty)
        {
            if (this.activeCollectionId != Guid.Empty)
            {
                this.penumbra.RemoveTemporaryModSettings(this.activeCollectionId, SwapModName);
                this.activeCollectionId = Guid.Empty;
            }

            // Re-registering makes Penumbra rebuild redirects instead of retaining the
            // previous target animation under the same generated mod.
            this.swapRegistered = false;
            this.swapDirty = false;
        }

        if (!this.TryGetTimelinePair(
                target,
                carrier,
                entry.AutoCarrier,
                out var resolvedCarrier,
                out var resolvedCarrierId,
                out var pairs))
        {
            return false;
        }

        if (!this.SetupSwapMod(pairs))
        {
            return false;
        }

        if (entry.AutoCarrier)
        {
            this.usedCarrierIds.Add(resolvedCarrierId);
        }

        this.Status = $"Swap ready for {target} through {resolvedCarrier}.";
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    Plugin.ChatSender.ExecuteCommand(resolvedCarrier);
                    this.Status = $"Playing {target} through {resolvedCarrier}.";
                }
                catch (Exception ex)
                {
                    this.Status = $"Swap is active, but the carrier command failed: {ex.Message}";
                }
            }).ConfigureAwait(false);
        });
        return true;
    }

    public void ClearSwap()
    {
        if (this.penumbra.IsAvailable && this.activeCollectionId != Guid.Empty)
        {
            this.penumbra.RemoveTemporaryModSettings(this.activeCollectionId, SwapModName);
        }

        this.WriteEmptyRealMod();
        this.activeCollectionId = Guid.Empty;
        this.swapRegistered = false;
        this.usedCarrierIds.Clear();
        this.Status = "Cleared emote swap.";
    }

    public void Dispose()
    {
        foreach (var command in this.registeredTriggerCommands)
        {
            Plugin.CommandManager.RemoveHandler(command);
        }

        this.registeredTriggerCommands.Clear();

        foreach (var command in this.registeredOneOffCommands)
        {
            Plugin.CommandManager.RemoveHandler(command);
        }

        this.registeredOneOffCommands.Clear();
    }

    private void RegisterOneOffCommands()
    {
        foreach (var command in new[] { "/lillyemote", "/vanilla" })
        {
            try
            {
                Plugin.CommandManager.AddHandler(command, new CommandInfo(this.OnOneOffCommand)
                {
                    HelpMessage = "Play a locked or modded emote through Lillypad's Penumbra bypass.",
                });
                this.registeredOneOffCommands.Add(command);
            }
            catch (Exception ex)
            {
                Plugin.Log.Information($"Could not register optional emote command {command}: {ex.Message}");
            }
        }
    }

    private void OnOneOffCommand(string command, string args)
    {
        var target = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        target = NormalizeCommand(target);
        if (string.IsNullOrWhiteSpace(target))
        {
            this.Status = $"Usage: {command} <emote>, for example {command} golddance";
            return;
        }

        this.Execute(new EmoteRemapEntry
        {
            Name = $"One-off {target}",
            TargetEmoteCommand = target,
            AutoCarrier = true,
            Enabled = true,
        });
    }

    public static string NormalizeCommand(string value)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith("/", StringComparison.Ordinal) ? value.ToLowerInvariant() : "/" + value.ToLowerInvariant();
    }

    private void BuildEmoteLookup()
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet is null)
            {
                this.Status = "Could not load the emote sheet.";
                return;
            }

            foreach (var row in sheet)
            {
                var emoteId = (ushort)row.RowId;
                var entries = new List<TimelineEntry>();
                for (var i = 0; i < 7; i++)
                {
                    var timelineRef = row.ActionTimeline[i];
                    if (timelineRef.RowId == 0 || !timelineRef.IsValid)
                    {
                        continue;
                    }

                    var key = timelineRef.Value.Key.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                            var isLoop = key.Contains("loop", StringComparison.OrdinalIgnoreCase);
                            entries.Add(new TimelineEntry(i, key, isLoop, timelineRef.Value.LoadType));
                    }
                }

                if (entries.Count > 0)
                {
                    this.emoteTimelines[emoteId] = entries;
                }

                var textCommand = row.TextCommand.ValueNullable;
                if (textCommand is null)
                {
                    continue;
                }

                this.AddCommand(textCommand.Value.Command.ToString(), emoteId);
                this.AddCommand(textCommand.Value.Alias.ToString(), emoteId);
                this.AddCommand(textCommand.Value.ShortCommand.ToString(), emoteId);
                this.AddCommand(textCommand.Value.ShortAlias.ToString(), emoteId);
            }

            this.emoteCommands.Clear();
            this.emoteCommands.AddRange(this.commandToEmoteId.Keys
                .Where(command => command.StartsWith("/", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(command => command, StringComparer.OrdinalIgnoreCase));

            Plugin.Log.Information($"Emote remapper lookup ready: {this.commandToEmoteId.Count} commands, {this.emoteTimelines.Count} timeline sets.");
        }
        catch (Exception ex)
        {
            this.Status = $"Could not build emote lookup: {ex.Message}";
            Plugin.Log.Warning(ex, "Could not build emote remapper lookup.");
        }
    }

    private void AddCommand(string command, ushort emoteId)
    {
        command = NormalizeCommand(command);
        if (!string.IsNullOrWhiteSpace(command))
        {
            this.commandToEmoteId.TryAdd(command, emoteId);
            this.commandToEmoteId.TryAdd(command.TrimStart('/'), emoteId);
        }
    }

    private bool TryGetTimelinePair(
        string targetCommand,
        string carrierCommand,
        bool autoCarrier,
        out string resolvedCarrierCommand,
        out ushort resolvedCarrierId,
        out List<(string CarrierKey, string TargetKey)> pairs)
    {
        pairs = [];
        resolvedCarrierCommand = carrierCommand;
        resolvedCarrierId = 0;
        if (!this.commandToEmoteId.TryGetValue(targetCommand, out var targetId))
        {
            this.Status = $"Unknown target emote: {targetCommand}";
            return false;
        }

        if (!this.emoteTimelines.TryGetValue(targetId, out var targetEntries) || targetEntries.Count == 0)
        {
            this.Status = $"No animation timelines found for {targetCommand}.";
            return false;
        }

        ushort carrierId;
        List<TimelineEntry> carrierEntries;
        if (autoCarrier)
        {
            var carrier = this.FindCarrier(targetId, targetEntries);
            if (carrier is null)
            {
                this.Status = "Could not find an unlocked carrier emote. Try setting one manually.";
                return false;
            }

            carrierId = carrier.Value.Id;
            resolvedCarrierCommand = carrier.Value.Command;
            carrierEntries = carrier.Value.Entries;
        }
        else
        {
            if (!this.commandToEmoteId.TryGetValue(carrierCommand, out carrierId))
            {
                this.Status = $"Unknown carrier emote: {carrierCommand}";
                return false;
            }

            if (!IsEmoteUnlocked(carrierId))
            {
                this.Status = $"{carrierCommand} is not unlocked. Use auto carrier or pick an emote you own.";
                return false;
            }

            if (!this.emoteTimelines.TryGetValue(carrierId, out var manualCarrierEntries) || manualCarrierEntries.Count == 0)
            {
                this.Status = $"No animation timelines found for {carrierCommand}.";
                return false;
            }

            carrierEntries = manualCarrierEntries;
        }

        resolvedCarrierId = carrierId;

        var targetMain = GetMainTimeline(targetEntries);
        if (string.IsNullOrWhiteSpace(targetMain.Key))
        {
            targetMain = targetEntries[0];
        }

        var carrierMain = GetMainTimeline(carrierEntries);
        if (string.IsNullOrWhiteSpace(carrierMain.Key))
        {
            carrierMain = carrierEntries[0];
        }

        pairs.Add((carrierMain.Key, targetMain.Key));

        foreach (var target in targetEntries)
        {
            if (target.Slot == targetMain.Slot)
            {
                continue;
            }

            var carrier = carrierEntries.FirstOrDefault(e => e.Slot == target.Slot);
            if (!string.IsNullOrWhiteSpace(carrier.Key))
            {
                pairs.Add((carrier.Key, target.Key));
            }
        }

        this.Status = autoCarrier
            ? $"Auto carrier selected: {resolvedCarrierCommand}."
            : $"Carrier selected: {resolvedCarrierCommand}.";
        return true;
    }

    private CarrierInfo? FindCarrier(ushort targetId, List<TimelineEntry> targetEntries)
    {
        var carrier = this.FindCarrierCore(targetId, targetEntries);
        if (carrier is not null || this.usedCarrierIds.Count == 0)
        {
            return carrier;
        }

        // Once every suitable carrier has been used, start a fresh rotation. Reusing
        // the same animation key immediately is what leaves the game showing a stale swap.
        this.usedCarrierIds.Clear();
        return this.FindCarrierCore(targetId, targetEntries);
    }

    private CarrierInfo? FindCarrierCore(ushort targetId, List<TimelineEntry> targetEntries)
    {
        var targetMain = GetMainTimeline(targetEntries);
        var needsLoop = targetMain.IsLoop;
        var targetHasIntro = targetEntries.Any(e => e.Slot == 1 && !string.IsNullOrWhiteSpace(e.Key));

        if (!needsLoop)
        {
            foreach (var command in PreferredOneShotCarriers)
            {
                var preferred = this.TryGetCarrierInfo(command, targetId, needsLoop: false, requireSlot1: targetHasIntro);
                if (preferred is not null)
                {
                    return preferred;
                }
            }
        }

        var matching = this.ScanForCarrier(targetId, loopType: needsLoop, requireSlot1: targetHasIntro);
        if (matching is not null)
        {
            return matching;
        }

        if (targetHasIntro)
        {
            matching = this.ScanForCarrier(targetId, loopType: needsLoop, requireSlot1: false);
            if (matching is not null)
            {
                return matching;
            }
        }

        foreach (var command in PreferredOneShotCarriers)
        {
            var preferred = this.TryGetCarrierInfo(command, targetId, needsLoop: null, requireSlot1: false);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return this.ScanForCarrier(targetId, loopType: null, requireSlot1: false);
    }

    private CarrierInfo? TryGetCarrierInfo(string command, ushort targetId, bool? needsLoop, bool requireSlot1)
    {
        command = NormalizeCommand(command);
        if (!this.commandToEmoteId.TryGetValue(command, out var id))
        {
            return null;
        }

        return this.GetCarrierInfo(id, targetId, command, needsLoop, requireSlot1);
    }

    private CarrierInfo? ScanForCarrier(ushort targetId, bool? loopType, bool requireSlot1)
    {
        foreach (var (id, entries) in this.emoteTimelines)
        {
            var command = this.GetShortestCommand(id);
            if (command is null)
            {
                continue;
            }

            var info = this.GetCarrierInfo(id, targetId, command, loopType, requireSlot1);
            if (info is not null)
            {
                return info;
            }
        }

        return null;
    }

    private CarrierInfo? GetCarrierInfo(ushort id, ushort targetId, string command, bool? loopType, bool requireSlot1)
    {
        if (id == targetId ||
            this.usedCarrierIds.Contains(id) ||
            NoCarrierCommands.Contains(command) ||
            !IsEmoteUnlocked(id) ||
            !this.emoteTimelines.TryGetValue(id, out var entries))
        {
            return null;
        }

        var main = GetMainTimeline(entries);
        if (string.IsNullOrWhiteSpace(main.Key))
        {
            return null;
        }

        if (loopType.HasValue && main.IsLoop != loopType.Value)
        {
            return null;
        }

        if (requireSlot1 && !entries.Any(e => e.Slot == 1 && !string.IsNullOrWhiteSpace(e.Key)))
        {
            return null;
        }

        return new CarrierInfo(id, command, entries);
    }

    private string? GetShortestCommand(ushort emoteId)
    {
        string? result = null;
        foreach (var (command, id) in this.commandToEmoteId)
        {
            if (id != emoteId || !command.StartsWith("/", StringComparison.Ordinal) || NoCarrierCommands.Contains(command))
            {
                continue;
            }

            if (result is null || command.Length < result.Length)
            {
                result = command;
            }
        }

        return result;
    }

    private static TimelineEntry GetMainTimeline(List<TimelineEntry> entries)
    {
        var slot0 = entries.FirstOrDefault(e => e.Slot == 0);
        if (!string.IsNullOrWhiteSpace(slot0.Key))
        {
            return slot0;
        }

        var slot4 = entries.FirstOrDefault(e => e.Slot == 4);
        return !string.IsNullOrWhiteSpace(slot4.Key) ? slot4 : entries[0];
    }

    private static unsafe bool IsEmoteUnlocked(ushort emoteId)
    {
        try
        {
            var uiState = UIState.Instance();
            return uiState is not null && uiState->IsEmoteUnlocked(emoteId);
        }
        catch
        {
            return false;
        }
    }

    private bool SetupSwapMod(List<(string CarrierKey, string TargetKey)> pairs)
    {
        var collection = this.penumbra.GetCurrentCollection();
        if (!collection.Success)
        {
            this.Status = "Could not find your current Penumbra collection.";
            return false;
        }

        var penumbraRoot = this.penumbra.GetModDirectory();
        if (string.IsNullOrWhiteSpace(penumbraRoot))
        {
            this.Status = "Could not find Penumbra's mod directory.";
            return false;
        }

        var tempDir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "emote-remapper");
        Directory.CreateDirectory(tempDir);
        var realModDir = Path.Combine(penumbraRoot, SwapModName);
        Directory.CreateDirectory(realModDir);

        var race = this.GetPlayerRaceCode();
        var jobSubfolder = this.GetPlayerJobSubfolder();
        var swapId = (++this.swapCounter).ToString();
        var mappings = new Dictionary<string, string>();
        var successfulPairs = 0;

        foreach (var (carrierKey, targetKey) in pairs)
        {
            var targetPaths = this.ResolveEmotePaths(targetKey);
            var carrierPaths = this.ResolveEmotePaths(carrierKey);
            if (targetPaths.Count == 0 || carrierPaths.Count == 0)
            {
                continue;
            }

            var targetPath = PickBestPath(targetPaths, race, jobSubfolder);
            var carrierPath = PickBestPath(carrierPaths, race, jobSubfolder);
            var targetData = this.ReadTargetData(targetPath);
            var carrierData = Plugin.DataManager.GetFile(carrierPath)?.Data;
            if (targetData is null || carrierData is null)
            {
                continue;
            }

            var targetName = ReadPapAnimationName(targetData);
            var carrierName = ReadPapAnimationName(carrierData);
            if (targetName is null || carrierName is null)
            {
                continue;
            }

            var rewritten = RewriteAnimationName(targetData, targetName, carrierName);
            if (rewritten is null)
            {
                continue;
            }

            var fileName = $"swap_{successfulPairs}_{swapId}.pap";
            File.WriteAllBytes(Path.Combine(tempDir, fileName), rewritten);
            File.Copy(Path.Combine(tempDir, fileName), Path.Combine(realModDir, fileName), true);

            foreach (var carrierGamePath in GenerateAllCarrierPaths(carrierKey))
            {
                mappings[carrierGamePath] = fileName;
            }

            var targetTmbPath = $"chara/action/{targetKey}.tmb";
            var carrierTmbPath = $"chara/action/{carrierKey}.tmb";
            var targetTmb = this.ReadTargetData(targetTmbPath);
            if (targetTmb is not null)
            {
                var tmbData = RewriteAnimationName(targetTmb, targetName, carrierName) ?? targetTmb;
                var tmbName = $"swap_tmb_{successfulPairs}_{swapId}.tmb";
                File.WriteAllBytes(Path.Combine(tempDir, tmbName), tmbData);
                File.Copy(Path.Combine(tempDir, tmbName), Path.Combine(realModDir, tmbName), true);
                mappings[carrierTmbPath] = tmbName;
            }

            successfulPairs++;
        }

        if (mappings.Count == 0)
        {
            this.Status = "No usable PAP mappings were generated. Is the target mod enabled in Penumbra?";
            return false;
        }

        WriteMeta(realModDir);
        WriteDefaultMod(realModDir, mappings);

        if (this.activeCollectionId != Guid.Empty)
        {
            this.penumbra.RemoveTemporaryModSettings(this.activeCollectionId, SwapModName);
            this.activeCollectionId = Guid.Empty;
        }

        if (!this.swapRegistered)
        {
            this.swapRegistered = this.penumbra.AddMod(SwapModName);
            Thread.Sleep(50);
        }

        this.penumbra.ReloadMod(SwapModName);
        if (!this.penumbra.SetTemporaryModSettings(collection.Id, SwapModName, true, SwapPriority))
        {
            this.Status = "Generated the swap mod, but Penumbra would not enable it.";
            return false;
        }

        this.activeCollectionId = collection.Id;
        this.Status = $"Swap active: {successfulPairs} animation pair(s).";
        return true;
    }

    private byte[]? ReadTargetData(string gamePath)
    {
        var resolved = this.penumbra.ResolvePlayerPath(gamePath);
        if (!string.IsNullOrWhiteSpace(resolved) &&
            !string.Equals(resolved, gamePath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(resolved))
        {
            try
            {
                return File.ReadAllBytes(resolved);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Could not read resolved Penumbra path '{resolved}': {ex.Message}");
            }
        }

        return Plugin.DataManager.GetFile(gamePath)?.Data;
    }

    private List<string> ResolveEmotePaths(string timelineKey)
    {
        var paths = new List<string>();
        var globalPath = $"chara/animation/{timelineKey}.pap";
        if (Plugin.DataManager.FileExists(globalPath))
        {
            paths.Add(globalPath);
        }

        foreach (var race in RaceIds)
        {
            foreach (var layer in new[] { "a0001", "a0002" })
            {
                foreach (var subfolder in ResolveSubfolders)
                {
                    var path = $"chara/human/{race}/animation/{layer}/{subfolder}{timelineKey}.pap";
                    if (Plugin.DataManager.FileExists(path))
                    {
                        paths.Add(path);
                    }
                }
            }
        }

        return paths;
    }

    private static IEnumerable<string> GenerateAllCarrierPaths(string timelineKey)
    {
        yield return $"chara/animation/{timelineKey}.pap";
        foreach (var race in RaceIds)
        {
            foreach (var layer in new[] { "a0001", "a0002" })
            {
                foreach (var subfolder in CarrierSubfolders)
                {
                    yield return $"chara/human/{race}/animation/{layer}/{subfolder}{timelineKey}.pap";
                }
            }
        }
    }

    private static string PickBestPath(List<string> paths, string? raceCode, string? jobSubfolder)
    {
        if (raceCode is not null && jobSubfolder is not null)
        {
            var match = paths.FirstOrDefault(p => p.Contains($"/{raceCode}/", StringComparison.OrdinalIgnoreCase) &&
                                                  p.Contains($"/{jobSubfolder}", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (raceCode is not null)
        {
            var match = paths.FirstOrDefault(p => p.Contains($"/{raceCode}/", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return paths[0];
    }

    private string? GetPlayerRaceCode()
    {
        if (Plugin.ObjectTable.LocalPlayer is not ICharacter character)
        {
            return null;
        }

        var customize = character.Customize;
        var race = customize[0];
        var gender = customize[1];
        var tribe = customize[4];
        var modelBase = race switch
        {
            1 when tribe == 1 => 1,
            1 => 3,
            2 => 5,
            3 => 11,
            4 => 7,
            5 => 9,
            6 => 13,
            7 => 15,
            8 => 17,
            _ => 0,
        };

        return modelBase == 0 ? null : $"c{(modelBase + gender):D2}01";
    }

    private string? GetPlayerJobSubfolder()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
        {
            return null;
        }

        return player.ClassJob.RowId switch
        {
            1 or 19 => "bt_swd_sld/",
            2 or 20 => "bt_clw_clw/",
            3 or 21 => "bt_2ax_emp/",
            4 or 22 => "bt_2sp_emp/",
            5 or 23 => "bt_2bw_emp/",
            6 or 24 => "bt_stf_sld/",
            7 or 25 => "bt_jst_sld/",
            26 or 27 or 28 => "bt_2bk_emp/",
            29 or 30 => "bt_dgr_dgr/",
            31 => "bt_2gn_emp/",
            32 => "bt_2sw_emp/",
            33 => "bt_2gl_emp/",
            34 => "bt_2kt_emp/",
            35 => "bt_2rp_emp/",
            36 => "bt_rod_emp/",
            37 => "bt_2gb_emp/",
            38 => "bt_chk_chk/",
            39 => "bt_2km_emp/",
            40 => "bt_2ff_emp/",
            41 => "bt_bld_bld/",
            42 => "bt_brs_plt/",
            _ => null,
        };
    }

    private static string? ReadPapAnimationName(byte[] papData)
    {
        if (papData.Length < 0x20 || papData[0] != 0x70 || papData[1] != 0x61 || papData[2] != 0x70)
        {
            return null;
        }

        if (BitConverter.ToUInt16(papData, 0x06) == 0)
        {
            return null;
        }

        var offset = BitConverter.ToUInt32(papData, 0x0E);
        if (offset == 0 || offset >= papData.Length)
        {
            offset = BitConverter.ToUInt32(papData, 0x10);
        }

        if (offset == 0 || offset >= papData.Length)
        {
            return null;
        }

        var start = (int)offset;
        var end = start;
        while (end < papData.Length && papData[end] != 0)
        {
            end++;
        }

        return end == start ? null : Encoding.ASCII.GetString(papData, start, end - start);
    }

    private static byte[]? RewriteAnimationName(byte[] data, string targetName, string carrierName)
    {
        var result = (byte[])data.Clone();
        var target = Encoding.ASCII.GetBytes(targetName);
        var carrier = Encoding.ASCII.GetBytes(carrierName);
        var replaced = false;

        for (var i = 0; i <= result.Length - target.Length; i++)
        {
            var match = true;
            for (var j = 0; j < target.Length; j++)
            {
                if (result[i + j] != target[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match || (i + target.Length < result.Length && result[i + target.Length] != 0))
            {
                continue;
            }

            var available = target.Length;
            while (i + available < result.Length && result[i + available] == 0)
            {
                available++;
            }

            if (carrier.Length + 1 > available)
            {
                continue;
            }

            Array.Copy(carrier, 0, result, i, carrier.Length);
            for (var j = carrier.Length; j < available; j++)
            {
                result[i + j] = 0;
            }

            replaced = true;
        }

        return replaced ? result : null;
    }

    private static void WriteMeta(string realModDir)
    {
        var metaPath = Path.Combine(realModDir, "meta.json");
        if (File.Exists(metaPath))
        {
            return;
        }

        var meta = new
        {
            FileVersion = 3,
            Name = SwapModName,
            Author = "Lillypad Toolkit",
            Description = "Auto-generated by Lillypad Toolkit for emote remapping.",
            Version = "1.0.0",
            Website = string.Empty,
            ModTags = Array.Empty<string>(),
        };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteDefaultMod(string realModDir, Dictionary<string, string> mappings)
    {
        var payload = new
        {
            Name = string.Empty,
            Priority = 0,
            Files = mappings.ToDictionary(kv => kv.Key.Replace("\\", "/", StringComparison.Ordinal), kv => kv.Value),
            FileSwaps = new Dictionary<string, string>(),
            Manipulations = Array.Empty<object>(),
        };
        File.WriteAllText(
            Path.Combine(realModDir, "default_mod.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteEmptyRealMod()
    {
        var root = this.penumbra.GetModDirectory();
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var dir = Path.Combine(root, SwapModName);
        if (!Directory.Exists(dir))
        {
            return;
        }

        WriteDefaultMod(dir, []);
        this.penumbra.ReloadMod(SwapModName);

        foreach (var file in Directory.EnumerateFiles(dir, "swap_*.*"))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    private readonly record struct TimelineEntry(int Slot, string Key, bool IsLoop, byte LoadType);

    private readonly record struct CarrierInfo(ushort Id, string Command, List<TimelineEntry> Entries);
}
