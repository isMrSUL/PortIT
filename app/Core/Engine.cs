using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortIT.Core;

/// <summary>Runs PortIT's Python engine (Engine/worker.py) in a separate, cancellable process: request.json in,
/// response.json out, progress lines on stdout (the same protocol as AnimIT's porter).</summary>
public static class Engine
{
    public static readonly string WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortIT");

    public static string DefaultPython
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "Engine", "python", "python.exe");
            if (File.Exists(bundled)) return bundled;
            string cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "python", "python.exe");
            return File.Exists(cached) ? cached : "python";
        }
    }

    public static async Task<JsonElement> Run(string python, Dictionary<string, object?> request, Action<string>? progress, CancellationToken cancel)
    {
        string worker = Path.Combine(AppContext.BaseDirectory, "Engine", "worker.py");
        if (!File.Exists(worker)) throw new InvalidOperationException("The Engine folder is missing beside PortIT.exe. Copy the complete application folder.");
        string job = Path.Combine(WorkRoot, "jobs", DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(job);
        string input = Path.Combine(job, "request.json"), output = Path.Combine(job, "response.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(request), cancel);
        var start = new ProcessStartInfo(python.Trim().Trim('"'))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(worker)!, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "-I", "-B", worker, input, output }) start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PORTIT_EXE"] = Environment.ProcessPath ?? "";   // (the engine runs PortIT.exe --cake for 2K26 archives)
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Python runtime.");
        using var registration = cancel.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var log = new StringBuilder();
        var stderr = process.StandardError.ReadToEndAsync();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) != null)
        {
            log.AppendLine(line);
            progress?.Invoke(line);
        }
        await process.WaitForExitAsync(cancel);
        log.Append(await stderr);
        await File.WriteAllTextAsync(Path.Combine(job, "worker.log"), log.ToString(), CancellationToken.None);
        cancel.ThrowIfCancellationRequested();   // (stopped on purpose: not 'the engine stopped')
        if (!File.Exists(output)) throw new InvalidOperationException($"The engine stopped before returning a result (NumPy and Pillow are required). Log: {Path.Combine(job, "worker.log")}");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output, cancel));
        var result = document.RootElement.Clone();
        if (!result.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException((result.TryGetProperty("error", out var error) ? error.GetString() : "The engine failed.") + $"\n\nLog: {Path.Combine(job, "worker.log")}");
        return result;
    }
}
