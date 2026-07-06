using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using VideoSyncPrototype.Phone.Apps.Games.Framework;
using VideoSyncPrototype.Phone.Core;
using VideoSyncPrototype.Phone.Core.Animation;
using VideoSyncPrototype.Phone.Core.Apps;
using VideoSyncPrototype.Phone.Core.Theme;
using VideoSyncPrototype.Phone.Windows.Components;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// Lillypad Go — an original creature-collector app. Walk Eorzea to find wild monsters, then
// battle/capture them in a turn-based fight. Creatures and procedural art are original.
// The screens are split across LillypadGoApp.*.cs partial files (Starter/Map/Battle/Team/Detail/
// Dex/Bag/Shared) so each screen can be edited in isolation. This file holds shared state and the
// per-frame Draw dispatch.
internal sealed partial class LillypadGoApp : IPhoneApp
{
    private enum View
    {
        Starter,
        Map,
        Battle,
        Team,
        Dex,
        DexEntry,
        Bag,
        Options,
        Detail,
    }

    private enum Menu
    {
        Root,
        Fight,
        Item,
        Switch,
    }

    private enum DexSort
    {
        Progression,
        Region,
        Missing,
    }

    private struct Anim
    {
        public float Lunge;
        public float Hurt;
        public float Alpha;
        public float AlphaTarget;

        public void Reset()
        {
            Lunge = 0f;
            Hurt = 0f;
            Alpha = 1f;
            AlphaTarget = 1f;
        }

        public void Update(float dt)
        {
            Lunge = MathF.Max(0f, Lunge - dt * 3.2f);
            Hurt = MathF.Max(0f, Hurt - dt * 3.2f);
            Alpha += (AlphaTarget - Alpha) * MathF.Min(1f, dt * 4f);
        }
    }

    private sealed class BattlePopup
    {
        public bool OnWild;
        public string Value = string.Empty;
        public string Label = string.Empty;
        public Vector4 Color;
        public float Age;
        public float HorizontalOffset;
    }

    private sealed class MoveFx
    {
        public MoveDef Move = null!;
        public bool FromPlayer;
        public float Age;
        public float Duration = 0.9f;
    }

    private static readonly string[] StarterIds = { "bulbasaur", "charmander", "squirtle" };

    private readonly Random rng = new();
    private View view = View.Map;
    private Menu menu = Menu.Root;
    private Battle? battle;
    private MonsterInstance? displayedPlayer;
    private int displayedPlayerHp;
    private int displayedWildHp;
    private Status displayedPlayerStatus;
    private Status displayedWildStatus;
    private int displayedPlayerAtkStage;
    private int displayedPlayerDefStage;
    private int displayedPlayerSpAtkStage;
    private int displayedPlayerSpDefStage;
    private int displayedPlayerSpdStage;
    private int displayedWildAtkStage;
    private int displayedWildDefStage;
    private int displayedWildSpAtkStage;
    private int displayedWildSpDefStage;
    private int displayedWildSpdStage;
    private int displayedPlayerLevel;
    private int displayedWildLevel;
    private float displayedPlayerXpFraction;
    private MonsterSpecies? starterCandidate;
    private readonly List<BattlePopup> battlePopups = new();
    private MoveFx? moveFx;
    private MonsterInstance? detailMonster;
    private View detailReturnView = View.Team;
    private string detailNameDraft = string.Empty;
    private Anim playerAnim;
    private Anim wildAnim;
    private string? message;
    private float messageTimer;
    private readonly List<BattleTextEntry> battleText = new();
    private bool awaitingResult;
    private bool teamShowingStorage;
    private int teamPage;
    private float time;
    private float navIndicator = -1f;
    private float teamTabIndicator = -1f;
    private float dexSortIndicator = -1f;
    private bool effectScaleSliderActive;
    private DexSort dexSort = DexSort.Region;
    private readonly HashSet<uint> expandedDexZones = new();
    private readonly HashSet<string> expandedDexRegions = new(StringComparer.Ordinal);
    private float dexScroll;
    private float dexMaxScroll;
    private bool dexInitialized;
    private MonsterSpecies? dexEntrySpecies;
    private int dexEntryTab;
    private float dexEntryTabIndicator = -1f;
    private float dexEntryScroll;
    private View lastDrawnView;
    private float viewAnim = 1f;
    private const float ViewTransitionSeconds = 0.18f;

    private static LillypadGoState State => Plugin.LillypadGo;

    public string Id => "lillypadgo";
    public string DisplayName => "Lillypad Go";
    public string Glyph => "L";
    public Vector4 Accent => AppAccents.For(Id);
    public int BadgeCount => State.Pending is not null && !State.InBattle ? 1 : 0;

    public void OnOpened()
    {
        view = !State.HasAnyMonster ? View.Starter : State.InBattle && battle is not null ? View.Battle : View.Map;
        menu = Menu.Root;
        // Don't run our own entrance while the phone shell is already sliding the app in.
        lastDrawnView = view;
        viewAnim = 1f;
    }

    public void OnClosed()
    {
    }

    public void Dispose()
    {
        PokemonSprites.Dispose();
        MoveFxSprites.Dispose();
        BiomeBgTextures.Dispose();
    }

    public void Draw(in PhoneContext context)
    {
        time += ImGui.GetIO().DeltaTime;
        var dt = MathF.Min(ImGui.GetIO().DeltaTime, 0.1f);
        var content = context.Content;
        var theme = context.Theme;
        var drawList = ImGui.GetWindowDrawList();
        GameScene.Ambient(drawList, content, Accent);

        if (view != lastDrawnView)
        {
            viewAnim = 0f;
            lastDrawnView = view;
        }

        viewAnim = MathF.Min(1f, viewAnim + dt / ViewTransitionSeconds);
        if (viewAnim < 1f)
        {
            // Clip the incoming screen and suppress input briefly, but keep the content anchored.
            // Vertical motion felt like a jarring shake when pressing navigation/action buttons.
            var target = content;
            LgUi.Interactive = false;
            SceneCompositor.DrawClipped(content, target, 0f, painted => PaintView(painted, theme, dt));
            LgUi.Interactive = true;
        }
        else
        {
            PaintView(content, theme, dt);
        }
    }

    private void PaintView(Rect content, PhoneTheme theme, float dt)
    {
        switch (view)
        {
            case View.Starter:
                DrawStarter(content, theme);
                break;
            case View.Map:
                DrawMap(content, theme);
                break;
            case View.Battle:
                DrawBattle(content, theme, dt);
                break;
            case View.Team:
                DrawTeam(content, theme);
                break;
            case View.Dex:
                DrawDex(content, theme);
                break;
            case View.DexEntry:
                DrawDexEntry(content, theme);
                break;
            case View.Bag:
                DrawBag(content, theme);
                break;
            case View.Options:
                DrawOptions(content, theme);
                break;
            case View.Detail:
                DrawDetail(content, theme);
                break;
        }
    }
}
