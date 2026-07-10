using System.Numerics;

namespace VideoSyncPrototype.Phone.Apps.LillypadGo;

// The hand-off between the phone battle screen and the in-world battle stage. While a battle
// is on the phone, DrawBattle publishes one snapshot per frame describing what the arena
// currently shows (battlers, their transient anim state, the active traced move animation,
// the capture/send-out ball, damage popups). The world renderer mirrors that snapshot as
// billboards in front of the player, IN TANDEM with the phone — the phone stays the source
// of truth and the place the battle is played. Snapshots go stale quickly, so closing the
// phone (or leaving the battle screen) hides the world stage on its own.
internal static class WorldBattleStage
{
    public const float StaleSeconds = 0.35f;

    public readonly record struct PopupSnap(bool OnWild, string Value, string Label, Vector4 Color, float Age);

    public sealed class Snapshot
    {
        public string PlayerSpeciesId = string.Empty;
        public string WildSpeciesId = string.Empty;

        // Transient multipliers exactly as the phone folds them (excluding the traced anim's
        // own pose alpha, which the world evaluates itself from the playback below).
        public float PlayerAlpha = 1f;
        public float PlayerHurt;
        public float PlayerLunge;
        public bool PlayerFainted;
        public float WildAlpha = 1f;
        public float WildHurt;
        public float WildLunge;
        public bool WildFainted;
        public float WildBaseScale = 1f; // Alpha bosses loom larger
        public float WildScale = 1f;     // capture shrink * send-out grow
        public float WildPullT;          // 0 = at its spot, 1 = pulled into the ball

        // The traced Showdown move animation currently playing, re-evaluated by the world
        // renderer against its own 3D scene map.
        public MoveAnimPlayback? FxPlayback;
        public float FxAgeMs;
        public float FxEffectScale = 1f;

        // Capture / send-out ball. ArcT: 0..1 = flying from the player's mon to the wild,
        // exactly 1 = at the wild; Grounded = resting at the wild's feet.
        public bool BallVisible;
        public float BallArcT;
        public bool BallGrounded;
        public float BallAngle;
        public float BallFlash;
        public string BallSpriteId = string.Empty;

        public PopupSnap[] Popups = [];
    }

    private static readonly object Gate = new();
    private static Snapshot? current;
    private static long writtenAtMs;

    public static void Publish(Snapshot snapshot)
    {
        lock (Gate)
        {
            current = snapshot;
            writtenAtMs = Environment.TickCount64;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            current = null;
        }
    }

    public static bool TryGet(out Snapshot snapshot)
    {
        lock (Gate)
        {
            if (current is not null &&
                Environment.TickCount64 - writtenAtMs <= (long)(StaleSeconds * 1000f))
            {
                snapshot = current;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }
}
