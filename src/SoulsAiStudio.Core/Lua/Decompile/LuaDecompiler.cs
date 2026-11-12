using System.Diagnostics;
using SoulsAiStudio.Core.IO;

namespace SoulsAiStudio.Core.Lua.Decompile;

/// <summary>
/// Runs the external <c>DSLuaDecompiler.exe</c> (.NET 7 CLI) on HavokScript bytecode. Results are cached by
/// content hash so each file is decompiled at most once per project.
/// </summary>
public sealed class LuaDecompiler(string exePath, string cacheDir)
{
    /// <summary>Default guess (the user's modding tools folder); the real path comes from app settings.</summary>
    public static string DefaultExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads", "modding", "DSLuaDecompiler", "DSLuaDecompiler.exe");

    public bool IsAvailable => File.Exists(exePath);

    /// <summary>Max time per file; the process tree is killed after that.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<string> DecompileAsync(byte[] bytecode, string fileName, CancellationToken ct = default)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytecode))[..16];
        string cached = Path.Combine(cacheDir, "decompiled", $"{Path.GetFileNameWithoutExtension(fileName)}.{hash}.lua");
        if (File.Exists(cached)) return await File.ReadAllTextAsync(cached, ct);
        if (!IsAvailable) throw new FileNotFoundException("DSLuaDecompiler.exe not found. Set its path in settings.", exePath);

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
            };
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
