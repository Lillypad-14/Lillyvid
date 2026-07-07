using System.Numerics;
using Dalamud.Plugin.Services;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Watches the real player: accumulates in-world distance walked and, every so many yalms,
// rolls a zone-weighted wild encounter — Pokémon-Go-style. Runs on the game frame so it
// tracks even when the phone/app is closed.
internal sealed class EncounterService : IDisposable
{
    public const float StepDistance = 25f;
    private const float SpawnChance = 0.5f;
    private const float TeleportThreshold = 60f;

    private readonly LillypadGoState state;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly Random rng = new();
    private Vector3 lastPos;
    private bool hasLast;

    public EncounterService(LillypadGoState state, IClientState clientState, IObjectTable objectTable,
        IFramework framework)
    {
        this.state = state;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework _)
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
        {
            hasLast = false;
            return;
        }

        state.Territory = clientState.TerritoryType;
        state.CurrentBiome = ArrZones.Find(state.Territory)?.Biome ?? Biomes.ForTerritory(state.Territory);
        if (!state.BackgroundTrackingEnabled)
        {
            hasLast = false;
            return;
        }

        var pos = player.Position;
        if (!hasLast)
        {
            lastPos = pos;
            hasLast = true;
            return;
        }

        var dx = pos.X - lastPos.X;
        var dz = pos.Z - lastPos.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        lastPos = pos;

        if (distance is < 0.05f or > TeleportThreshold)
        {
            return; // idle, or a teleport / zone change
        }

        // No encounters while a wild is already pending, mid-battle, before getting a starter,
        // after a whiteout (revive at a Marketboard first), or inside a safe town.
        if (state.Pending is not null || state.InBattle || !state.HasAnyMonster ||
            state.AllMonstersFainted || Towns.IsTown(state.Territory))
        {
            return;
        }

        state.StepProgress += distance;
        if (state.StepProgress < StepDistance)
        {
            return;
        }

        state.StepProgress -= StepDistance;
        state.TotalSteps++;
        state.Save();
        if (rng.NextDouble() < SpawnChance)
        {
            Spawn();
        }
    }

    private void Spawn()
    {
        var table = ArrZones.Encounters(state.Territory, state.CurrentBiome);
        var total = 0;
        foreach (var entry in table)
        {
            total += entry.Weight;
        }

        if (total <= 0)
        {
            return;
        }

        var roll = rng.Next(total);
        foreach (var entry in table)
        {
            roll -= entry.Weight;
            if (roll >= 0)
            {
                continue;
            }

            var species = Dex.Find(entry.SpeciesId);
            if (species is null)
            {
                return;
            }

            var level = rng.Next(entry.MinLevel, entry.MaxLevel + 1);
            state.Pending = new MonsterInstance(species, level);
            state.Seen.Add(species.Id);
            state.Save();
            try
            {
                global::VideoSyncPrototype.Plugin.ChatGui.Print(
                    $"[Lillypad Go] A wild {species.Name} (Lv{level}) appeared nearby! Open the phone to engage.");
            }
            catch
            {
                // Chat unavailable (e.g. not logged in yet) — the app-icon badge still shows it.
            }

            return;
        }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
    }
}
