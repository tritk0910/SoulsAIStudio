using System.Diagnostics;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Lua.Decompile;

/// <summary>
/// Runs <c>DSLuaDecompiler.exe</c> (.NET 7 CLI, bundled with the app in <c>Tools\DSLuaDecompiler\</c>) on HavokScript
/// bytecode as a separate process. Results are cached by content hash so each file is decompiled at most once per project.
/// </summary>
public sealed class LuaDecompiler(string exePath, string cacheDir)
{
    /// <summary>Old default (a modding tools folder); only a fallback now that the decompiler ships with the app.</summary>
    public static string DefaultExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "modding", "DSLuaDecompiler", "DSLuaDecompiler.exe");

    /// <summary>The copy shipped next to the app: <c>&lt;app&gt;\Tools\DSLuaDecompiler\DSLuaDecompiler.exe</c>.</summary>
    public static string BundledExePath => Path.Combine(AppContext.BaseDirectory, "Tools", "DSLuaDecompiler", "DSLuaDecompiler.exe");

    /// <summary>
    /// The exe to use: an explicit per-project override if it exists, else the bundled copy, else the path from app
    /// settings (older setups). Never null; may not exist (then <see cref="IsAvailable"/> is false).
    /// </summary>
    public static string ResolveExePath(string? projectOverride, string? settingsPath)
    {
        if (!string.IsNullOrWhiteSpace(projectOverride) && File.Exists(projectOverride)) return projectOverride;
        if (File.Exists(BundledExePath)) return BundledExePath;
        if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath)) return settingsPath;
        return BundledExePath;
    }

    public bool IsAvailable => File.Exists(exePath);

    /// <summary>Max time per file; the process tree is killed after that.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<string> DecompileAsync(byte[] bytecode, string fileName, CancellationToken ct = default)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytecode))[..16];
        string cached = Path.Combine(cacheDir, "decompiled", $"{Path.GetFileNameWithoutExtension(fileName)}.{hash}.lua");
        if (File.Exists(cached)) return await File.ReadAllTextAsync(cached, ct);
        if (!IsAvailable)
            throw new FileNotFoundException($"DSLuaDecompiler.exe not found (it ships in the app's Tools\\DSLuaDecompiler folder): {exePath}", exePath);

        string work = Path.Combine(Path.GetTempPath(), "SoulsAiStudio", "decompile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string input = Path.Combine(work, fileName);
            await File.WriteAllBytesAsync(input, bytecode, ct);
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // It finds Resources\VariableNamePatterns.json next to itself; also run it from there.
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "",
            };
            // Built for .NET 7 (end of life). Major roll-forward: .NET 7 if installed, else the next installed major
            // (the .NET 9/10 runtime this app already needs), so nothing else has to be installed.
            psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
            psi.ArgumentList.Add(input);
            psi.ArgumentList.Add("-c");
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start DSLuaDecompiler.");
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);
            // Some inputs make the decompiler hang; never wait forever (review S4).
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(Timeout);
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    if (ct.IsCancellationRequested) throw;
                    throw new TimeoutException($"DSLuaDecompiler did not finish within {Timeout.TotalSeconds:0} s for '{fileName}'.");
                }
            }
            string text = await stdout;
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"DSLuaDecompiler failed (exit {proc.ExitCode}): {(await stderr).Trim()}");

            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            AtomicFile.WriteAllText(cached, text);
            return text;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }
}
