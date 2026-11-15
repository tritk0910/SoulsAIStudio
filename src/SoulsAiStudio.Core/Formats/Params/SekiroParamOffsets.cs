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
}
