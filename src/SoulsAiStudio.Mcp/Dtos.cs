namespace SoulsAiStudio.Mcp;

// All line numbers in these records are 1-based (the Core uses 0-based internally).

public sealed record ScriptInfo(string Key, string FileName, string Luabnd, string? NpcId, string Kind, string Character, string DisplayName);

public sealed record EntityInfo(string ChrId, string Name, IReadOnlyList<string> Scripts);

public sealed record EntityList(string Project, IReadOnlyList<EntityInfo> Entities, IReadOnlyList<string> UnassignedScripts);

public sealed record ScriptList(int Total, int Returned, bool Truncated, IReadOnlyList<ScriptInfo> Scripts);

public sealed record LuaText(string Script, bool WasBytecode, int TotalLines, int StartLine, int EndLine, bool Truncated, string Text,
    string? Note = null, int? NextStartLine = null);

public sealed record GoalInfo(string Name, string Parameters, int Line, int EndLine, string Group, int AddSubGoalCount, IReadOnlyList<int> AnimIds);

public sealed record GoalList(string Script, int Total, int Returned, bool Truncated, IReadOnlyList<GoalInfo> Goals);

public sealed record ComboNodeDto(string Type, int Line, int? AnimId = null, string? GoalType = null, IReadOnlyList<ComboArmDto>? Arms = null);

public sealed record ComboArmDto(string Condition, double? Probability, int Line, IReadOnlyList<ComboNodeDto> Body);

public sealed record HandlerInfo(int SpEffectId, int Line, int EndLine, string Condition, double? ChancePercent, bool ClearsQueue,
    bool Replans, bool DependsOnDistance, bool HasStateCondition, IReadOnlyList<string> ObservedOn, IReadOnlyList<int> AnimIds, IReadOnlyList<ComboNodeDto>? Body,
    string ClearMode = "n/a", int ArmsForThisId = 1, bool DuplicateId = false);

public sealed record ObservedInfo(int SpEffectId, IReadOnlyList<string> Targets, bool HasHandler);

public sealed record HandlerTable(string Script, int Total, bool Truncated, IReadOnlyList<HandlerInfo> Handlers, IReadOnlyList<ObservedInfo> Observed,
    int Offset = 0, int? NextOffset = null, IReadOnlyList<int>? DuplicateIds = null, string? Note = null);

public sealed record ComboSequenceResult(string Script, string Goal, int Line, int EndLine, IReadOnlyList<ComboNodeDto> Nodes, int StepCount, bool Truncated,
    int ReturnedNodes = 0, string? Note = null);

public sealed record Finding(string Severity, string Code, int Line, string Message);

public sealed record DiagnosticsResult(string Script, int Total, bool Truncated, IReadOnlyList<Finding> Findings, string Scope);
