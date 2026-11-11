using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PortIT.Core;

/// <summary>Runs PortIT's engine: the C# engine (EngineNet, PortIT.Engine.Worker) inside PortIT.exe - the same commands,
/// requests and answers as the Python engine's worker.py had. Each job keeps request.json / response.json / worker.log
/// under %LOCALAPPDATA%\PortIT\jobs. (Developers: PORTIT_PYTHON_ENGINE=1 runs the old Python engine instead, from an
/// Engine folder with worker.py beside PortIT.exe.)</summary>
public static class Engine
{
    public static readonly string WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortIT");

    /// <summary>(kept for the window's setting; the C# engine needs no Python)</summary>
    public static string DefaultPython
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "Engine", "python", "python.exe");
            return File.Exists(bundled) ? bundled : "python";
        }
    }

    public static async Task<JsonElement> Run(string python, Dictionary<string, object?> request, Action<string>? progress, CancellationToken cancel)
    {
        if (Environment.GetEnvironmentVariable("PORTIT_PYTHON_ENGINE") == "1") return await RunPython(python, request, progress, cancel);
        string job = Path.Combine(WorkRoot, "jobs", DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(job);
        string json = JsonSerializer.Serialize(request);
        await File.WriteAllTextAsync(Path.Combine(job, "request.json"), json, CancellationToken.None);
        var log = new StringBuilder();
        var result = await Task.Run(() =>
        {
            var worker = new PortIT.Engine.Worker(line =>
            {
                lock (log) log.AppendLine(line);
                progress?.Invoke(line);
            }, cancel)
            { Cake = CakeSource.Run };
            return worker.Run(JsonNode.Parse(json)!.AsObject());
        }, CancellationToken.None);
        string answer = result.ToJsonString();
        await File.WriteAllTextAsync(Path.Combine(job, "response.json"), answer, CancellationToken.None);
        lock (log) File.WriteAllText(Path.Combine(job, "worker.log"), log.ToString());
        cancel.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(answer);
        var root = document.RootElement.Clone();
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException((root.TryGetProperty("error", out var error) ? error.GetString() : "The engine failed.") + $"\n\nLog: {Path.Combine(job, "worker.log")}");
        return root;
    }

    /// <summary>the old Python engine (Engine/worker.py in a separate process) - for comparing, developers only</summary>
    static async Task<JsonElement> RunPython(string python, Dictionary<string, object?> request, Action<string>? progress, CancellationToken cancel)
    {
        string worker = Path.Combine(AppContext.BaseDirectory, "Engine", "worker.py");
        if (!File.Exists(worker)) throw new InvalidOperationException("PORTIT_PYTHON_ENGINE=1: Engine\\worker.py is not beside PortIT.exe.");
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
        start.Environment["PORTIT_EXE"] = Environment.ProcessPath ?? "";
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
        cancel.ThrowIfCancellationRequested();
        if (!File.Exists(output)) throw new InvalidOperationException($"The engine stopped before returning a result. Log: {Path.Combine(job, "worker.log")}");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output, cancel));
        var result = document.RootElement.Clone();
        if (!result.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException((result.TryGetProperty("error", out var error) ? error.GetString() : "The engine failed.") + $"\n\nLog: {Path.Combine(job, "worker.log")}");
        return result;
    }
}
