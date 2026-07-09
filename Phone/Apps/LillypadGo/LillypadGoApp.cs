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
        Market,
        Arena,
        Options,
        Detail,
        MoveRelearn,
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
        Region,
        National,
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
        public MoveAnimPlayback? Playback; // traced Showdown choreography, when available
    }

    // Drives the throw-and-catch animation: the ball arcs in, the wild is drawn inside, the ball drops
    // and shakes, then either clicks shut (success) or bursts open (break free).
    private sealed class CaptureFx
    {
        public enum Stage { Throw, Wait, Success, Break }

        public Stage Phase = Stage.Throw;
        public string BallId = "pokeball";
        public float Age;         // seconds since the throw began
        public float StageAge;    // seconds since the Success/Break stage began
        public float LastShake = -10f; // Age at which the most recent shake started
        public int Shakes;
    }

    private CaptureFx? captureFx;
    private string pendingCaptureBallId = "pokeball";

    private static readonly string[] StarterIds = { "bulbasaur", "charmander", "squirtle" };

    private readonly Random rng = new();
    private View view = View.Map;
    private Menu menu = Menu.Root;
    private Battle? battle;
    private int pendingGymIndex = -1;
    private bool confirmingRun;
    private float gymIntroTimer; // >0 while the pre-gym-battle leader intro is playing
    private GymDef? gymIntroGym;
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
    // Smoothly-eased mirrors of the displayed HP/XP so the bars glide instead of snapping.
    private float animatedPlayerHp;
    private float animatedWildHp;
    private float animatedPlayerXp;
    private MonsterSpecies? starterCandidate;
    private readonly List<BattlePopup> battlePopups = new();
    private MoveFx? moveFx;
    private MonsterInstance? detailMonster;
    private bool releaseConfirm;
    private int draggingMoveIndex = -1; // drag-and-drop move reordering in the Team detail screen
    private MonsterInstance? draggingRosterMon; // drag-and-drop party reordering on the Roster screen
    private Vector2 rosterDragOrigin;
    private bool rosterDragMoved;
    private View detailReturnView = View.Team;
    private string detailNameDraft = string.Empty;
    private Anim playerAnim;
    private Anim wildAnim;
    private string? message;
    private float messageTimer;
    private readonly List<BattleTextEntry> battleText = new();
    private bool awaitingResult;
    private float resultShownAt = -1f; // when the result screen first appeared, for its entrance ease
    // Guards the battle action/menu buttons for a moment after any message so the click that
    // advances battle text can't also press Fight/Bag/Team/Run or a move-learn choice.
    private float suppressBattleButtonsUntil;
    private int boxPage; // current storage-box page on the Roster/Box screen
    private int boxSort; // 0 Dex, 1 Level, 2 Type, 3 Name — cycled by the Sort button
    private string boxSearch = string.Empty; // filters the box grid by name
    private float time;
    private float navIndicator = -1f;
    private float dexSortIndicator = -1f;
    private bool effectScaleSliderActive;
    private DexSort dexSort = DexSort.Region;
    private readonly HashSet<uint> expandedDexZones = new();
    private readonly HashSet<string> expandedDexRegions = new(StringComparer.Ordinal);
    private float dexScroll;
    private float dexMaxScroll;
    private bool dexInitialized;
    private MonsterSpecies? dexEntrySpecies;
    private MonsterInstance? learnsetMonster; // when set, the Learnset tab lets you teach/replace moves
    private MoveDef? teachPendingMove; // a move awaiting a slot choice when the moveset is full
    private View dexEntryReturnView = View.Dex;
    private int dexEntryTab;
    private float dexEntryTabIndicator = -1f;
    private float dexEntryScroll;
    private int dexLearnFilter; // Learnset filter: 0 = All, 1 = Level-Up, 2 = TM
    private float dexLearnFilterIndicator = -1f;
    private int relearnTab; // Move Relearner: 0 = Level-Up, 1 = TMs
    private float relearnTabIndicator = -1f;
    private float relearnScroll;
    private MoveDef? draggingLearnMove; // a learnset move being dragged onto a move slot
    private Vector2 learnDragOrigin;
    private bool learnDragMoved;
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
        AssetTextures.Dispose();
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
            case View.Market:
                DrawMarket(content, theme);
                break;
            case View.Arena:
                DrawArena(content, theme);
                break;
            case View.Options:
                DrawOptions(content, theme);
                break;
            case View.Detail:
                DrawDetail(content, theme);
                break;
            case View.MoveRelearn:
                DrawMoveRelearn(content, theme);
                break;
        }
    }
}
