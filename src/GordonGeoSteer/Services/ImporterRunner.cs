using System.Diagnostics;
using System.IO;
using System.Text;

namespace GordonGeoSteer.Services;

public sealed record ImportResult(int ExitCode, string OutputDir, string ProjectJsonPath, string Log, string ImporterPath)
{
    public bool ProjectJsonWritten => File.Exists(ProjectJsonPath);

    public string ExitMeaning => ExitCode switch
    {
        0 => "OK",
        2 => "usage error / protected output path / source folder not found",
        3 => "import finished but one or more cross-checks FAILED",
        4 => "fatal error inside the importer",
        _ => "unexpected exit code",
    };
}

/// <summary>
/// Runs the external PzsImporter.exe (tools/PzsImporter, .NET Framework x86) as a separate process.
/// The app itself never loads PZS assemblies or uses BinaryFormatter - it only consumes the JSON output.
/// </summary>
public static class ImporterRunner
{
    public const string ImporterEnvVar = "GORDON_PZSIMPORTER";

    /// <summary>Finds PzsImporter.exe: env var, then next to the app, then the repo's tools build output.</summary>
    public static string? LocateImporter()
    {
        var env = Environment.GetEnvironmentVariable(ImporterEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);

        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "PzsImporter", "PzsImporter.exe");
        if (File.Exists(local)) return local;

        // Developer layout: walk up to the repo root and use tools\PzsImporter\bin\PzsImporter.exe
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            string cand = Path.Combine(dir.FullName, "tools", "PzsImporter", "bin", "PzsImporter.exe");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    public static string DefaultOutputDir(string pzsProjectFolder)
    {
        string name = new DirectoryInfo(pzsProjectFolder).Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 60) name = name[..60];
        return Path.Combine(Path.GetTempPath(), "GordonGeoSteer", "imports", $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}");
    }

    public static async Task<ImportResult> RunAsync(string pzsProjectFolder, string outputDir, Action<string>? onLine,
        CancellationToken ct = default)
    {
        string importer = LocateImporter() ?? throw new FileNotFoundException(
            "PzsImporter.exe not found. Build it with tools\\PzsImporter\\build.cmd (or set " + ImporterEnvVar + ").");

        var log = new StringBuilder();
        void Line(string? s)
        {
            if (s == null) return;
            lock (log) log.AppendLine(s);
            onLine?.Invoke(s);
        }

        var psi = new ProcessStartInfo
        {
            FileName = importer,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(importer)!,
        };
        psi.ArgumentList.Add(pzsProjectFolder);
        psi.ArgumentList.Add(outputDir);

        Line($"> \"{importer}\" \"{pzsProjectFolder}\" \"{outputDir}\"");
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Line("stderr: " + e.Data); };
        if (!p.Start()) throw new InvalidOperationException("Failed to start " + importer);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        p.WaitForExit(); // flush async output handlers
        Line($"PzsImporter exit code {p.ExitCode}");
        string log2; lock (log) log2 = log.ToString();
        return new ImportResult(p.ExitCode, outputDir, Path.Combine(outputDir, "project.json"), log2, importer);
    }
}
