using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services
{
    // Linux/Vulkan playback benchmark.
    //
    // The Windows benchmark is a PowerShell harness that builds a TensorRT engine
    // per resolution before timing; none of that applies to the Vulkan (ncnn)
    // backend, which has no engine-build step. This is a self-contained,
    // dependency-light equivalent: it generates short synthetic clips with the
    // system ffmpeg, then plays each one through the bundled mpv with the aji
    // upscale filter active (--vo=null, offscreen) and measures throughput as
    // frames / wall-clock seconds.
    //
    // It writes benchmark.txt in the same markdown-table shape the
    // Submit-to-Catalog parser (BenchmarkSubmission.FromBenchmarkFile) expects,
    // so the existing submit flow keeps working unchanged.
    //
    // Linux-only: every caller is already gated behind
    // RuntimeInformation.IsOSPlatform(OSPlatform.Linux); the Windows path is
    // untouched.
    public static class LinuxBenchmark
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Source resolutions to benchmark, with the human label used as the
        // table column. Low source resolutions are the value case for AnimeJaNai
        // (the upscale benefit is greatest there), so the set stays in that range.
        public static readonly (int W, int H)[] Resolutions =
        {
            (480, 360),
            (640, 480),
            (720, 540),
            (960, 720),
            (1280, 720),
        };

        // Clip framerate and length. ~6 s at 24 fps = 144 frames: long enough
        // that the one-time model load / warm-up is amortised into a steady-state
        // figure, short enough to keep the whole run quick.
        private const int ClipFps = 24;
        private const int ClipSeconds = 6;
        private const int ClipFrames = ClipFps * ClipSeconds;

        public sealed class Result
        {
            public int SrcW { get; init; }
            public int SrcH { get; init; }
            // fps measured, or null if the cell couldn't be measured.
            public double? Fps { get; init; }
            public string? Error { get; init; }
            public string Label => $"{SrcW}x{SrcH}";
            public string DstLabel => $"{SrcW * 2}x{SrcH * 2}";
        }

        public sealed class Paths
        {
            public required string Mpv { get; init; }
            public required string ConfigDir { get; init; }
            public required string Conf { get; init; }
            public required string ModelDir { get; init; }
            public required string DataDir { get; init; }
        }

        // Resolve the package layout from the editor's own location. rootDir is
        // the install root (mpv + portable_config + animejanai/ live here);
        // dataDir is animejanai/ (where benchmark.txt is written).
        public static Paths ResolvePaths(string rootDir, string dataDir) => new()
        {
            Mpv = Path.Combine(rootDir, "mpv"),
            ConfigDir = Path.Combine(rootDir, "portable_config"),
            Conf = Path.Combine(dataDir, "animejanai.conf"),
            ModelDir = Path.Combine(dataDir, "onnx"),
            DataDir = dataDir,
        };

        // Pre-flight check: report the first missing requirement, or null if good.
        public static string? CheckPrerequisites(Paths p)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "The Vulkan benchmark only runs on Linux.";
            if (!File.Exists(p.Mpv))
                return $"The bundled mpv was not found at {p.Mpv}.";
            if (!File.Exists(p.Conf))
                return $"animejanai.conf was not found at {p.Conf}.";
            if (!Directory.Exists(p.ConfigDir))
                return $"portable_config was not found at {p.ConfigDir}.";
            if (FindFfmpeg() is null)
                return "ffmpeg was not found on PATH. Install ffmpeg to generate the benchmark test clips.";
            return null;
        }

        // Run the full benchmark. progress is invoked on the calling context's
        // thread pool with a short status string (caller marshals to the UI).
        public static async Task<List<Result>> RunAsync(
            Paths p,
            string backendLabel,
            Action<string>? progress = null,
            CancellationToken ct = default)
        {
            var results = new List<Result>();
            var tempFiles = new List<string>();
            try
            {
                for (var i = 0; i < Resolutions.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (w, h) = Resolutions[i];
                    progress?.Invoke($"Benchmarking {w}x{h} -> {w * 2}x{h * 2} ({i + 1}/{Resolutions.Length})...");

                    string clip;
                    try
                    {
                        clip = await GenerateClipAsync(w, h, ct);
                        tempFiles.Add(clip);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        results.Add(new Result { SrcW = w, SrcH = h, Error = $"clip generation failed: {ex.Message}" });
                        continue;
                    }

                    try
                    {
                        var (frames, seconds) = await PlayAndTimeAsync(p, clip, ct);
                        var fps = seconds > 0 ? Math.Round(frames / seconds, 1) : (double?)null;
                        results.Add(new Result { SrcW = w, SrcH = h, Fps = fps });
                        progress?.Invoke($"{w}x{h} -> {w * 2}x{h * 2}: {(fps.HasValue ? fps.Value.ToString("0.0", Inv) + " fps" : "no result")}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        results.Add(new Result { SrcW = w, SrcH = h, Error = ex.Message });
                    }
                }

                progress?.Invoke("Writing results...");
                WriteBenchmarkTxt(p.DataDir, backendLabel, results);
                return results;
            }
            finally
            {
                foreach (var f in tempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
                }
            }
        }

        // Generate a short synthetic clip (testsrc2, yuv420p h264) to a temp file.
        private static async Task<string> GenerateClipAsync(int w, int h, CancellationToken ct)
        {
            var ffmpeg = FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg not found on PATH.");
            var outPath = Path.Combine(Path.GetTempPath(), $"animejanai_bench_{w}x{h}_{Guid.NewGuid():N}.mp4");

            var args =
                $"-y -nostdin -f lavfi -i testsrc2=size={w}x{h}:rate={ClipFps}:duration={ClipSeconds} " +
                $"-pix_fmt yuv420p -c:v libx264 -preset ultrafast \"{outPath}\"";

            var (exit, _, stderr) = await RunProcessAsync(ffmpeg, args, workingDir: null, timeout: TimeSpan.FromSeconds(60), ct);
            if (exit != 0 || !File.Exists(outPath))
                throw new Exception($"ffmpeg exited {exit}: {Tail(stderr, 200)}");
            return outPath;
        }

        // Play one clip through mpv with the upscale filter and return
        // (frameCount, wallClockSeconds). The frame count is the known clip
        // length; wall-clock is measured around the whole process. --vo=null
        // keeps it offscreen and uncapped so the result is refresh-independent.
        private static async Task<(double frames, double seconds)> PlayAndTimeAsync(
            Paths p, string clip, CancellationToken ct)
        {
            var args =
                $"\"{clip}\" " +
                $"--config-dir=\"{p.ConfigDir}\" " +
                "--profile=upscale-on " +
                "--vo=null --untimed --no-audio --no-cache " +
                "--keep-open=no --idle=no --force-window=no " +
                "--msg-level=all=error";

            var sw = Stopwatch.StartNew();
            var (exit, _, stderr) = await RunProcessAsync(p.Mpv, args, workingDir: Path.GetDirectoryName(p.Mpv),
                timeout: TimeSpan.FromSeconds(120), ct);
            sw.Stop();

            if (exit != 0)
                throw new Exception($"mpv exited {exit}: {Tail(stderr, 200)}");

            return (ClipFrames, sw.Elapsed.TotalSeconds);
        }

        // Markdown table identical in shape to what benchmark.ps1 writes and
        // BenchmarkSubmission.FromBenchmarkFile parses. A single row "Vulkan"
        // (the current backend); cells are fps, or "-" when unmeasured.
        private static void WriteBenchmarkTxt(string dataDir, string backendLabel, List<Result> results)
        {
            var cols = results.Select(r => r.Label).ToArray();
            var sb = new StringBuilder();
            sb.AppendLine($"AnimeJaNai playback benchmark - backend: {backendLabel}");
            sb.AppendLine();
            sb.AppendLine("|fps|" + string.Join("|", cols) + "|");
            sb.AppendLine("|" + string.Join("|", Enumerable.Repeat("-", cols.Length + 1)) + "|");

            var row = results.Select(r =>
                r.Fps.HasValue ? r.Fps.Value.ToString("0.0", Inv) : "-");
            sb.AppendLine("|" + backendLabel + "|" + string.Join("|", row) + "|");

            File.WriteAllText(Path.Combine(dataDir, "benchmark.txt"), sb.ToString());
        }

        private static string? FindFfmpeg()
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, "ffmpeg");
                if (File.Exists(candidate)) return candidate;
            }
            // Common fixed locations as a fallback.
            foreach (var candidate in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" })
                if (File.Exists(candidate)) return candidate;
            return null;
        }

        private static async Task<(int exit, string stdout, string stderr)> RunProcessAsync(
            string fileName, string args, string? workingDir, TimeSpan timeout, CancellationToken ct)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = args;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!string.IsNullOrEmpty(workingDir))
                process.StartInfo.WorkingDirectory = workingDir;

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                if (ct.IsCancellationRequested) throw;   // user cancel propagates
                throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {timeout.TotalSeconds:0}s.");
            }

            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }

        private static string Tail(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : "..." + s.Substring(s.Length - max);
        }
    }
}
