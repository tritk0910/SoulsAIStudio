namespace SoulsAiStudio.Editor.Completion;

/// <summary>What a suggestion is. Order = priority when the same label comes from several sources (lower wins).</summary>
public enum CompletionKind : byte
{
    Keyword,
    Builtin,
    Local,
    Constant,
    Function,
    Method,
    Field,
    Variable,
}

/// <summary>One suggestion. Immutable; shared between snapshots and threads.</summary>
public sealed class CompletionItem
{
    public CompletionItem(string label, CompletionKind kind, string? detail = null)
    {
        Label = label;
        Kind = kind;
        Detail = detail;
    }

    public string Label { get; }
    public CompletionKind Kind { get; }

    /// <summary>Optional extra text (e.g. a signature); not used for filtering.</summary>
    public string? Detail { get; }

    /// <summary>Short, dimmed kind label shown at the right of the popup row.</summary>
    public string KindLabel => KindLabels[(int)Kind];

    static readonly string[] KindLabels = { "keyword", "builtin", "local", "constant", "function", "method", "field", "variable" };

    public override string ToString() => $"{Label} ({KindLabel})";
}

/// <summary>
/// Immutable set of suggestions split by context: <see cref="Globals"/> (plain identifiers), <see cref="Methods"/>
/// (after <c>:</c>) and <see cref="Fields"/> (after <c>.</c>). Labels are unique within each array, which is
/// kept in <see cref="CompletionFilter.LabelOrder"/> so filtering needs no sort.
/// Built off the UI thread; swapping a reference is the only cross-thread operation.
/// </summary>
public sealed class CompletionSet
{
    public static readonly CompletionSet Empty = new(Array.Empty<CompletionItem>(), Array.Empty<CompletionItem>(), Array.Empty<CompletionItem>());

    /// <summary>Takes ownership of the arrays and sorts them in <see cref="CompletionFilter.LabelOrder"/>.</summary>
    public CompletionSet(CompletionItem[] globals, CompletionItem[] methods, CompletionItem[] fields)
    {
        Array.Sort(globals, CompletionFilter.LabelOrder);
        Array.Sort(methods, CompletionFilter.LabelOrder);
        Array.Sort(fields, CompletionFilter.LabelOrder);
        Globals = globals;
        Methods = methods;
        Fields = fields;
    }

    public CompletionItem[] Globals { get; }
    public CompletionItem[] Methods { get; }
    public CompletionItem[] Fields { get; }

    public int Count => Globals.Length + Methods.Length + Fields.Length;

    /// <summary>
    /// Union of several sets, de-duplicated by label (the lower <see cref="CompletionKind"/> wins).
    /// <paramref name="keep"/> filters items (e.g. drop other files' locals).
    /// </summary>
    public static CompletionSet Merge(IEnumerable<CompletionSet> sets, Func<CompletionItem, bool>? keep = null)
    {
        var g = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        var m = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        var f = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        foreach (var s in sets)
        {
            Add(g, s.Globals, keep);
            Add(m, s.Methods, keep);
            Add(f, s.Fields, keep);
        }
        return new CompletionSet(g.Values.ToArray(), m.Values.ToArray(), f.Values.ToArray());
    }

    static void Add(Dictionary<string, CompletionItem> d, CompletionItem[] items, Func<CompletionItem, bool>? keep)
    {
        foreach (var it in items)
        {
            if (keep != null && !keep(it))
                continue;
            if (!d.TryGetValue(it.Label, out var old) || it.Kind < old.Kind)
                d[it.Label] = it;
        }
    }

    /// <summary>Builder used by scanners/seeds: collects items, keeps the best kind per label.</summary>
    public sealed class Builder
    {
        readonly Dictionary<string, CompletionItem> _g = new(StringComparer.Ordinal);
        readonly Dictionary<string, CompletionItem> _m = new(StringComparer.Ordinal);
        readonly Dictionary<string, CompletionItem> _f = new(StringComparer.Ordinal);

        public Builder Global(string label, CompletionKind kind, string? detail = null) => Put(_g, label, kind, detail);
        public Builder Method(string label, string? detail = null) => Put(_m, label, CompletionKind.Method, detail);
        public Builder Field(string label, string? detail = null) => Put(_f, label, CompletionKind.Field, detail);

        Builder Put(Dictionary<string, CompletionItem> d, string label, CompletionKind kind, string? detail)
        {
            if (!d.TryGetValue(label, out var old) || kind < old.Kind)
                d[label] = new CompletionItem(label, kind, detail ?? old?.Detail);
            return this;
        }

        public CompletionSet Build() => new(_g.Values.ToArray(), _m.Values.ToArray(), _f.Values.ToArray());
    }
}

/// <summary>
/// Extra suggestions supplied by the host (other open scripts, game API seeds). The editor adds Lua
/// keywords/builtins and the identifiers of its own buffer itself.
/// </summary>
public interface ICompletionProvider
{
    /// <summary>Current snapshot. Called on the UI thread when the popup opens or refilters: must be cheap (no scanning).</summary>
    CompletionSet Current { get; }
}

/// <summary>
/// Thread-safe provider merging a seed set with one set per open document. Documents may be updated from worker
/// threads; the merged snapshot is rebuilt lazily (at most once per change) when <see cref="Current"/> is read.
/// Locals/plain variables of documents are dropped: they only make sense in their own file (the editor adds
/// those from its own buffer).
/// </summary>
public sealed class WorkspaceCompletionProvider : ICompletionProvider
{
    readonly object _lock = new();
    readonly Dictionary<string, CompletionSet> _docs = new(StringComparer.Ordinal);
    readonly CompletionSet _seed;
    CompletionSet? _merged;

    public WorkspaceCompletionProvider(CompletionSet? seed = null) => _seed = seed ?? SekiroAiSeeds.Set;

    public void SetDocument(string key, CompletionSet set)
    {
        lock (_lock)
        {
            _docs[key] = set;
            _merged = null;
        }
    }

    public void RemoveDocument(string key)
    {
        lock (_lock)
        {
            if (_docs.Remove(key))
                _merged = null;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _docs.Clear();
            _merged = null;
        }
    }

    public CompletionSet Current
    {
        get
        {
            lock (_lock)
            {
                return _merged ??= CompletionSet.Merge(_docs.Values.Prepend(_seed),
                    it => it.Kind != CompletionKind.Local && it.Kind != CompletionKind.Variable);
            }
        }
    }
}
