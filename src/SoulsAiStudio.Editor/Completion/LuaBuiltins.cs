namespace SoulsAiStudio.Editor.Completion;

/// <summary>Lua 5.1 keywords, base functions and standard library members (always offered by the editor).</summary>
public static class LuaBuiltins
{
    public static readonly string[] Keywords =
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    public static readonly string[] Functions =
    {
        "assert", "error", "ipairs", "next", "pairs", "pcall", "print", "rawequal", "rawget", "rawset",
        "select", "setmetatable", "getmetatable", "tonumber", "tostring", "type", "unpack", "xpcall",
    };

    /// <summary>Library tables and their members, offered after <c>lib.</c>.</summary>
    public static readonly IReadOnlyDictionary<string, CompletionItem[]> LibraryMembers = new Dictionary<string, CompletionItem[]>(StringComparer.Ordinal)
    {
        ["math"] = Items("abs", "ceil", "cos", "floor", "fmod", "huge", "max", "min", "modf", "pi", "pow", "random", "randomseed", "sin", "sqrt", "tan"),
        ["string"] = Items("byte", "char", "find", "format", "gmatch", "gsub", "len", "lower", "match", "rep", "reverse", "sub", "upper"),
        ["table"] = Items("concat", "insert", "remove", "sort", "getn"),
    };

    static CompletionItem[] Items(params string[] names) =>
        names.Select(n => new CompletionItem(n, CompletionKind.Builtin)).Order(CompletionFilter.LabelOrder).ToArray();

    /// <summary>Keywords + base functions + library table names.</summary>
    public static readonly CompletionSet Set = BuildSet();

    static CompletionSet BuildSet()
    {
        var b = new CompletionSet.Builder();
        foreach (var k in Keywords)
            b.Global(k, CompletionKind.Keyword);
        foreach (var f in Functions)
            b.Global(f, CompletionKind.Builtin);
        foreach (var lib in LibraryMembers.Keys)
            b.Global(lib, CompletionKind.Builtin);
        return b.Build();
    }
}

/// <summary>
/// Seed list of common Sekiro AI API names (methods on <c>ai</c>/<c>goal</c> objects and aicommon constants),
/// so suggestions work in a fresh file before any other script is open. Names actually seen in open scripts are
/// added on top by the host; this list only has to cover the everyday ones.
/// </summary>
public static class SekiroAiSeeds
{
    /// <summary>Methods called with <c>:</c> on the ai (arg1) or goal (arg2) object.</summary>
    public static readonly string[] Methods =
    {
        // goal (arg2)
        "AddSubGoal", "ClearSubGoal", "AddFinalAnimCancel", "GetSubGoalNum", "GetLife", "SetLifeEndSuccess",
        "SetNumber", "GetNumber", "SetTargetRange", "SetFailedEndOption", "TimingSetNumber", "TimingSetTimer",
        "GetParam", "SetTimer",
        // ai (arg1)
        "Replanning", "GetRandam_Int", "GetRandam_Float", "GetDist", "GetDistY", "GetHpRate", "GetHp",
        "GetToTargetAngle", "IsInsideTarget", "IsInsideTargetEx", "IsTargetGuard", "IsLookToTarget",
        "HasSpecialEffectId", "HasSpecialEffectAttribute", "IsInterupt", "GetSpecialEffectActivateInterruptType",
        "GetSpecialEffectInactivateInterruptType", "AddObserveSpecialEffectAttribute", "DeleteObserveSpecialEffectAttribute",
        "AddObserveArea", "AddObserveRegion", "IsFinishTimer", "GetTimer", "SetStringIndexedNumber",
        "GetStringIndexedNumber", "GetNinsatsuNum", "GetEventRequest", "GetTeamOrder", "IsExistMeshOnLine",
        "GetAttackPassedTime", "GetExcelParam", "IsBothHandMode", "GetMapHitRadius", "IsActiveGoal",
        "GetNpcThinkParamID", "SetEventMoveTarget", "IsSearchTarget", "IsVisibleTarget", "GetPrevTargetState",
    };

    /// <summary>Global functions defined by the game/aicommon.</summary>
    public static readonly string[] Functions =
    {
        "RegisterTableGoal", "REGISTER_GOAL_NO_UPDATE", "REGISTER_GOAL", "REGISTER_GOAL_UPDATE_TIME",
        "REGISTER_GOAL_NO_INTERUPT", "REGISTER_GOAL_NO_SUB_GOAL",
    };

    public static readonly string[] Constants =
    {
        "GOAL_COMMON_Attack", "GOAL_COMMON_AttackTunableSpin", "GOAL_COMMON_AttackImmediateAction",
        "GOAL_COMMON_ComboAttack", "GOAL_COMMON_ComboAttackTunableSpin", "GOAL_COMMON_ComboRepeat",
        "GOAL_COMMON_ComboFinal", "GOAL_COMMON_ComboTunable_SuccessAngle180", "GOAL_COMMON_Wait",
        "GOAL_COMMON_WaitWithAnime", "GOAL_COMMON_ApproachTarget", "GOAL_COMMON_LeaveTarget",
        "GOAL_COMMON_SidewayMove", "GOAL_COMMON_SpinStep", "GOAL_COMMON_StepSafety", "GOAL_COMMON_Turn",
        "GOAL_COMMON_Guard", "GOAL_COMMON_EndureAttack", "GOAL_COMMON_MoveToSomewhere", "GOAL_COMMON_KeepDist",
        "TARGET_SELF", "TARGET_ENE_0", "TARGET_FRI_0", "TARGET_EVENT", "TARGET_NONE", "TARGET_LOCALPLAYER",
        "INTERUPT_ActivateSpecialEffect", "INTERUPT_InactivateSpecialEffect", "INTERUPT_Damaged",
        "INTERUPT_FindEnemy", "INTERUPT_FindAttack", "INTERUPT_GuardBreak", "INTERUPT_GuardFinish",
        "INTERUPT_Shoot", "INTERUPT_ShootImpact", "INTERUPT_ParryTiming", "INTERUPT_SuccessParry",
        "INTERUPT_SuccessGuard", "INTERUPT_UseItem", "INTERUPT_Inside_ObserveArea", "INTERUPT_MissSwing",
        "INTERUPT_LoseSightTarget", "INTERUPT_TargetOutOfRange", "INTERUPT_CANNOT_MOVE",
        "INTERUPT_MovedEnd_OnFailedPath", "INTERUPT_BackstabRisk", "INTERUPT_ForgetTarget",
        "AI_DIR_TYPE_F", "AI_DIR_TYPE_B", "AI_DIR_TYPE_L", "AI_DIR_TYPE_R", "AI_DIR_TYPE_CENTER",
        "AI_TIMING_SET__ACTIVATE", "AI_TIMING_SET__UPDATE_SUCCESS", "AI_TIMING_SET__UPDATE_FAILED",
        "DIST_Near", "DIST_Middle", "DIST_Far", "DIST_Out", "DIST_None",
    };

    public static readonly CompletionSet Set = BuildSet();

    static CompletionSet BuildSet()
    {
        var b = new CompletionSet.Builder();
        foreach (var m in Methods)
            b.Method(m);
        foreach (var f in Functions)
            b.Global(f, CompletionKind.Function);
        foreach (var c in Constants)
            b.Global(c, CompletionKind.Constant);
        return b.Build();
    }
}
