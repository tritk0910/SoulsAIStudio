namespace SoulsAiStudio.Core.Formats.Params;

/// <summary>
/// Row offsets of the few Sekiro param fields the app reads straight from row bytes (inside DSAS's own param load).
/// Derived from the Paramdex SDT paramdef with <see cref="ParamLayout"/>; ParamLayoutTests checks they still match and
/// that they decode the game's real SpEffectParam exactly like SoulsFormats' ApplyParamdef.
/// </summary>
public static class SekiroParamOffsets
{
    /// <summary>SpEffectParam <c>cycleOccurrenceSpEffectId</c> (s32): SpEffect applied every <c>motionInterval</c> while this one is active.</summary>
    public const int SpEffectCycleOccurrenceSpEffectId = 0x130;

    /// <summary>SpEffectParam <c>motionInterval</c> (f32, seconds): activation interval.</summary>
    public const int SpEffectMotionInterval = 0x0C;

    /// <summary>SpEffectParam row size.</summary>
    public const int SpEffectRowSize = 0x440;

    /// <summary>AtkParam (Npc/Pc) <c>spEffectId0</c> (s32); <c>spEffectId1..4</c> follow every 4 bytes. Applied to the target hit.</summary>
    public const int AtkSpEffectId0 = 0x20;

    /// <summary>Number of <c>spEffectIdN</c> fields in AtkParam / BulletParam.</summary>
    public const int SpEffectIdCount = 5;

    /// <summary>AtkParam row size.</summary>
    public const int AtkRowSize = 0x1D0;

    /// <summary>BulletParam <c>atkId_Bullet</c> (s32): AtkParam of the bullet's hit.</summary>
    public const int BulletAtkId = 0x0;

    /// <summary>BulletParam <c>spEffectIDForShooter</c> (s32): SpEffect applied to the shooter when the bullet is fired.</summary>
    public const int BulletSpEffectIdForShooter = 0x60;

    /// <summary>BulletParam <c>spEffectId0</c> (s32); <c>spEffectId1..4</c> follow every 4 bytes. Applied to the target hit.</summary>
    public const int BulletSpEffectId0 = 0x6C;

    /// <summary>BulletParam row size.</summary>
    public const int BulletRowSize = 0x100;
}
