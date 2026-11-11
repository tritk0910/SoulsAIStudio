using SoulsAiStudio.Core.Formats;
using SoulsAiStudio.Core.Formats.Luabnd;
using SoulsAiStudio.Core.Lua.Analysis;
using SoulsAiStudio.Core.Project;
using SoulsAiStudio.Core.Tests.TestSupport;

namespace SoulsAiStudio.Core.Tests.Project;

public class ProjectTests
{
    /// <summary>Builds a fake game+mod layout in temp: game/script has packed m10, mod/script has the unpacked copy.</summary>
    private static (TempWorkDir Work, string Game, string Mod) MakeLayout()
    {
        var work = TempWorkDir.Create("project");
        string game = Path.Combine(work.Path, "game"), mod = Path.Combine(work.Path, "mod");
        Directory.CreateDirectory(Path.Combine(game, "script"));
        Directory.CreateDirectory(Path.Combine(mod, "script"));
        File.Copy(Samples.LuabndPath("m10_00_00_00"), Path.Combine(game, "script", "m10_00_00_00.luabnd.dcx"));
        File.Copy(Samples.LuabndPath("aicommon"), Path.Combine(game, "script", "aicommon.luabnd.dcx"));
        CopyDir(Samples.UnpackedDir("m10_00_00_00"), Path.Combine(mod, "script", "m10_00_00_00-luabnd-dcx"));
        File.Copy(Samples.LuabndPath("m10_00_00_00"), Path.Combine(mod, "script", "m10_00_00_00.luabnd.dcx"));
        foreach (var f in Directory.EnumerateFiles(work.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        return (work, game, mod);
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
    }

    [RequiresTestDataFact]
    public void Project_SaveLoad_RoundTrip()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        string path = Path.Combine(work.Path, "proj", "Owl.sasproj");
        var p = SasProject.Create(path, "Owl", game, mod,
            [new ProjectScript { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_battle.lua", DisplayName = "Inner Owl" }]);
        p.Ui.OpenScriptKey = p.Scripts[0].Key;
        p.Save();

        var loaded = SasProject.Load(path);
        Assert.Equal("Owl", loaded.Name);
        Assert.Equal(mod, loaded.ModDir);
        Assert.Single(loaded.Scripts);
        Assert.Equal(@"out\bin\506300_battle.lua", loaded.Scripts[0].EntryPath);
        Assert.Equal(p.Scripts[0].Key, loaded.Ui.OpenScriptKey);
        Assert.True(Directory.Exists(loaded.BackupDir));
    }

    [RequiresTestDataFact]
    public void Catalog_PrefersModUnpacked_AndScansAll()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        var cat = new ScriptCatalog(game, mod);
        Assert.Equal(LuabndSource.ModUnpacked, cat.Locate("m10_00_00_00"));
        Assert.Equal(LuabndSource.Game, cat.Locate("aicommon"));
        Assert.Null(cat.Locate("m99_00_00_00"));

        var (scripts, errors) = cat.Scan();
        Assert.Empty(errors);
        var owl = Assert.Single(scripts, s => s.FileName == "506300_battle.lua");
        Assert.False(owl.IsBytecode);
        Assert.Equal("506300", owl.NpcId);
        Assert.Equal("battle", owl.Kind);
        Assert.Contains(scripts, s => s.Luabnd == "aicommon" && s.IsBytecode);
    }

    [RequiresTestDataFact]
    public async Task Session_OpenEditSave_WritesModOnly_WithBackup()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        string gameHashBefore = Hash(Path.Combine(game, "script", "m10_00_00_00.luabnd.dcx"));
        var p = SasProject.Create(Path.Combine(work.Path, "proj", "Owl.sasproj"), "Owl", game, mod,
            [new ProjectScript { Luabnd = "m10_00_00_00", EntryPath = @"out\bin\506300_battle.lua" }]);
        var session = new ProjectSession(p, decompilerExe: "");

        var doc = await session.OpenAsync(p.Scripts[0]);
        Assert.False(doc.WasBytecode);
        Assert.Contains("Goal.Interrupt", doc.SavedText);

        string edited = doc.SavedText + "-- edited\n";
        var written = await session.SaveAsync(doc, edited);

        Assert.Equal(2, written.Count); // unpacked .lua + repacked .dcx
        Assert.All(written, w => Assert.StartsWith(mod, w));
        Assert.Equal(edited, File.ReadAllText(Path.Combine(mod, "script", "m10_00_00_00-luabnd-dcx", "out", "bin", "506300_battle.lua")));
        Assert.Equal(edited, LuabndArchive.LoadPacked(Path.Combine(mod, "script", "m10_00_00_00.luabnd.dcx"))
            .Find("506300_battle.lua")!.GetText());
        Assert.Equal(gameHashBefore, Hash(Path.Combine(game, "script", "m10_00_00_00.luabnd.dcx")));

        // Backups: original + one snapshot, both holding the pre-edit text.
        string origLua = Path.Combine(session.Backups.OriginalDir, "script", "m10_00_00_00-luabnd-dcx", "out", "bin", "506300_battle.lua");
        Assert.Equal(doc.SavedText.Replace("-- edited\n", ""), File.ReadAllText(origLua));
        Assert.Single(session.Backups.ListSnapshots());
    }

    [RequiresTestDataFact]
    public async Task Session_FirstSaveOfGameOnlyLuabnd_CreatesModCopy()
    {
        var (work, game, mod) = MakeLayout();
        using var _ = work;
        File.Delete(Path.Combine(game, "script", "aicommon.luabnd.dcx"));
        File.Copy(Samples.LuabndPath("m10_00_00_00"), Path.Combine(game, "script", "m77_00_00_00.luabnd.dcx"));
        var p = SasProject.Create(Path.Combine(work.Path, "proj", "x.sasproj"), "x", game, mod,
            [new ProjectScript { Luabnd = "m77_00_00_00", EntryPath = @"out\bin\506300_battle.lua" }]);
        var session = new ProjectSession(p, "");
        var doc = await session.OpenAsync(p.Scripts[0]);
        Assert.Equal(LuabndSource.Game, doc.LoadedFrom);

        await session.SaveAsync(doc, doc.SavedText + "--x\n");
        Assert.True(File.Exists(Path.Combine(mod, "script", "m77_00_00_00.luabnd.dcx")));
        Assert.Equal(LuabndSource.ModPacked, doc.LoadedFrom);
    }

    [Fact]
    public void RecentProjects_NewestFirst_Deduped_Capped()
    {
        using var work = TempWorkDir.Create("recent");
        string store = work.Combine("recent.json");
        var r = new RecentProjects(store);
        for (int i = 0; i < 12; i++) r.Add(work.Combine($"p{i}.sasproj"));
        r.Add(work.Combine("p5.sasproj"));
        var again = new RecentProjects(store);
        Assert.Equal(RecentProjects.MaxEntries, again.Paths.Count);
        Assert.EndsWith("p5.sasproj", again.Paths[0]);
        Assert.Single(again.Paths, p => p.EndsWith("p5.sasproj"));
    }

    [RequiresTestDataFact]
    public void GoalOutline_Owl_ListsExpectedGoals()
    {
        var goals = GoalOutline.Build(File.ReadAllText(Samples.OwlBattle));
        var names = goals.Select(g => g.Name).ToList();
        Assert.Contains("Activate", names);
        Assert.Contains("Interrupt", names);
        Assert.Contains("Kengeki03", names);
        Assert.Contains("Act50", names);
        Assert.Equal(names.Count, names.Distinct().Count());

        var interrupt = goals.Single(g => g.Name == "Interrupt");
        Assert.Equal("arg0, arg1, arg2", interrupt.Parameters);
        Assert.Equal(GoalGroup.Interrupt, interrupt.Group);
        Assert.Equal(1589, interrupt.Line); // 0-based (line 1590 in the editor)
        Assert.True(interrupt.EndLine > interrupt.Line);
        Assert.True(interrupt.AddSubGoalCount > 10);
        Assert.Contains(3012, interrupt.AnimIds);

        Assert.Equal(GoalGroup.Act, goals.Single(g => g.Name == "Act01").Group);
        Assert.Equal(GoalGroup.Kengeki, goals.Single(g => g.Name == "Kengeki_Activate").Group);
        Assert.Equal(GoalGroup.Lifecycle, goals.Single(g => g.Name == "Update").Group);
        // Functions don't overlap.
        for (int i = 1; i < goals.Count; i++) Assert.True(goals[i].Line > goals[i - 1].EndLine, goals[i].Name);
    }

    [RequiresTestDataTheory]
    [MemberData(nameof(LuaCorpus.KeyBattleScripts), MemberType = typeof(LuaCorpus))]
    public void Lexer_CoversWholeFile_WithoutGaps(string relPath)
    {
        string src = File.ReadAllText(Path.Combine(TestPaths.RequireScriptDir(), relPath));
        var tokens = Core.Lua.Syntax.LuaLexer.Tokenize(src);
        int pos = 0;
        foreach (var t in tokens)
        {
            Assert.True(t.Start >= pos);
            Assert.True(string.IsNullOrWhiteSpace(src[pos..t.Start]), $"gap at {pos}");
            pos = t.Start + t.Length;
        }
        Assert.Equal(src.Length, pos);
    }

    private static string Hash(string f) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
}
