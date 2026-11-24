using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SoulsAiStudio.Mcp;

/// <summary>MCP tool surface. Read-only: no tool writes anything. Lines are 1-based.</summary>
[McpServerToolType]
public sealed class SoulsAiTools(AiProjectService svc)
{
    [McpServerTool(Name = "open_project", ReadOnly = true, Destructive = false), Description("Open a Souls AI Studio project (.sasproj) read-only. Needed only if the server was not started with --project.")]
    public string OpenProject([Description("Absolute path to the .sasproj file")] string path) => Run(() => svc.OpenProject(path));

    [McpServerTool(Name = "list_entities", ReadOnly = true, Destructive = false), Description("List the project's characters (c####) with their AI scripts.")]
    public EntityList ListEntities() => Run(svc.ListEntities);

    [McpServerTool(Name = "list_scripts", ReadOnly = true, Destructive = false), Description("List the project's Lua AI scripts (key, file, luabnd, npc id, kind). Max 200 returned.")]
    public ScriptList ListScripts([Description("Optional substring filter on file/name/character/luabnd")] string? filter = null) => Run(() => svc.ListScripts(filter));

    [McpServerTool(Name = "read_lua", ReadOnly = true, Destructive = false), Description("Read Lua source (1-based, inclusive line range; at most 400 lines / 60k chars per call; startLine beyond EOF or after endLine is an error, endLine past EOF is clamped; see nextStartLine). Bytecode is decompiled on the fly.")]
    public Task<LuaText> ReadLua([Description("Script key, file name (e.g. 506300_battle.lua) or unique substring")] string script,
        [Description("First line (integer), default 1")] JsonElement? startLine = null,
        [Description("Last line (integer), default startLine+399")] JsonElement? endLine = null,
        CancellationToken ct = default) => RunAsync(() => svc.ReadLuaAsync(script, Int(startLine, "startLine"), Int(endLine, "endLine"), ct));

    [McpServerTool(Name = "list_goals", ReadOnly = true, Destructive = false), Description("Goal outline of a script: every Goal.X function with line range, group, AddSubGoal count and literal anim ids.")]
    public Task<GoalList> ListGoals([Description("Script key or file name")] string script, CancellationToken ct = default) => RunAsync(() => svc.ListGoalsAsync(script, ct));

    [McpServerTool(Name = "get_spEffect_handlers", ReadOnly = true, Destructive = false), Description("SpEffect interrupt handlers of Goal.Interrupt (every == SpEffectId arm, duplicates listed separately and flagged; arms with their combo bodies, chance, conditions) and the observed SpEffects. Default output is a summary (anim ids); pass spEffectId or includeBodies for the full bodies.")]
    public Task<HandlerTable> GetSpEffectHandlers([Description("Script key or file name")] string script,
        [Description("Only the arms for this SpEffect id (integer; full bodies are then included)")] JsonElement? spEffectId = null,
        [Description("Include every handler's full combo body (large); default is a summary with anim ids")] bool includeBodies = false,
        [Description("Arm index (integer) to start from when the output was truncated (see nextOffset), default 0")] JsonElement? offset = null,
        [Description("Max serialized characters returned (integer), default 20000, min 1000")] JsonElement? maxChars = null,
        CancellationToken ct = default) => RunAsync(() => svc.GetSpEffectHandlersAsync(script, Int(spEffectId, "spEffectId"), includeBodies, ct, Int(offset, "offset"), Int(maxChars, "maxChars")));

    [McpServerTool(Name = "get_combo_sequence", ReadOnly = true, Destructive = false), Description("Combo tree of one goal function: AddSubGoal steps (anim id), ClearSubGoal, and random/condition branches with probabilities. Output is capped (maxNodes); for a big goal like Goal.Interrupt pass spEffectId.")]
    public Task<ComboSequenceResult> GetComboSequence([Description("Script key or file name")] string script,
        [Description("Goal name, e.g. Kengeki03 or Goal.Act05")] string goal,
        [Description("Only the arm(s) of this SpEffect id (integer) inside the goal; use for Goal.Interrupt")] JsonElement? spEffectId = null,
        [Description("Max nodes returned (integer, depth-first), default 100")] JsonElement? maxNodes = null,
        CancellationToken ct = default) => RunAsync(() => svc.GetComboSequenceAsync(script, goal, ct, Int(spEffectId, "spEffectId"), Int(maxNodes, "maxNodes")));

    [McpServerTool(Name = "diagnose_combo", ReadOnly = true, Destructive = false), Description("Static combo hints for a script: handlers not observed, observed SpEffects without handler, random threshold gaps/overlaps, handlers that do not clear the queue.")]
    public Task<DiagnosticsResult> DiagnoseCombo([Description("Script key or file name")] string script, CancellationToken ct = default) => RunAsync(() => svc.DiagnoseComboAsync(script, ct));

    /// <summary>
    /// Integer arguments arrive as JsonElement so a wrong type (1.5, "abc") gives a readable McpException: the SDK reports
    /// binding failures of typed parameters (int?) only as a generic 'An error occurred invoking ...'.
    /// </summary>
    public static int? Int(JsonElement? e, string name)
    {
        if (e is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        throw new McpException($"{name} must be an integer (got {v.GetRawText()}).");
    }

    // The SDK hides exception text unless it is an McpException; the caller needs to see why a call failed.
    private static T Run<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException or NotSupportedException or TimeoutException || ex.GetType().Name == "NoOodleFoundException")
        { throw new McpException(ex.GetType().Name == "NoOodleFoundException" ? "oo2core_6_win64.dll not found in the project's game folder (needed for DCX_KRAK files such as m25)." : ex.Message, ex); }
    }

    private static async Task<T> RunAsync<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException or NotSupportedException or TimeoutException || ex.GetType().Name == "NoOodleFoundException")
        { throw new McpException(ex.GetType().Name == "NoOodleFoundException" ? "oo2core_6_win64.dll not found in the project's game folder (needed for DCX_KRAK files such as m25)." : ex.Message, ex); }
    }
}
