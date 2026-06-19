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
    // Aligned with the Windows PowerShell harness (animejanai/benchmarks/
    // benchmark.ps1) so the numbers are directly comparable:
    //   - Uses the built-in BENCHMARK slots 1010 (Balanced) and 1011
    //     (Performance), which run the HD model UNCONDITIONALLY at every
    //     resolution. (The regular slots 1001-1003 are resolution-conditional and
    //     fall to the SD model below 720p, which is NOT what the catalog measures.)
    //   - Same source resolutions as the Windows clip set.
    //   - Excludes warm-up: ncnn has no TensorRT-style engine build, but the first
    //     frames still pay pipeline creation + GPU clock ramp. A short probe run and
    //     a longer sample run are timed; the steady-state fps is the extra sample
    //     frames divided by the extra wall-clock (the shared init/warm-up cancels).
    //
    // Writes benchmark.txt in the markdown-table shape the Submit-to-Catalog parser
    // (BenchmarkSubmission.FromBenchmarkFile) expects: resolution columns, one row
    // per profile.
    //
    // Linux-only: every caller is gated behind RuntimeInformation.IsOSPlatform(
    // OSPlatform.Linux); the Windows path is untouched.
    public static class LinuxBenchmark
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Source resolutions, matching the Windows benchmark clip set (ascending by
        // pixel count). 2x upscale, so 480x360 -> 960x720, 1920x1080 -> 3840x2160.
        public static readonly (int W, int H)[] Resolutions =
        {
            (480, 360),
            (640, 480),
            (768, 576),
            (1280, 720),
            (1920, 1080),
        };

        // The benchmark profiles = the built-in benchmark slots (HD model at every
        // resolution), so a 360p cell measures the HD model, not the SD fallback.
        public static readonly (string Name, int Slot)[] Profiles =
        {
            ("Balanced", 1010),
            ("Performance", 1011),
        };

        private const int ClipFps = 24;
        // Long enough to supply probe + the largest sample (48 + 600) without
        // looping (looping interacts badly with --frames under --untimed).
        private const int ClipSeconds = 30;          // 720 frames @ 24 fps
        private const int ProbeFrames = 48;          // warm-up probe (subtracted out)
        private const int SampleSeconds = 5;         // target steady-state sample length
        private const int SampleFramesMin = 120;
        private const int SampleFramesMax = 600;

        public sealed class Result
        {
            public int SrcW { get; init; }
            public int SrcH { get; init; }
            public required string Profile { get; init; }
            public int Slot { get; init; }
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

        // Resolve the package layout from the editor's own location. rootDir is the
        // install root (mpv + portable_config + animejanai/ live here); dataDir is
        // animejanai/ (where benchmark.txt is written).
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

        // Run the full benchmark. progress is invoked with a short status string
        // (caller marshals to the UI). One clip per resolution, reused across the
        // two profiles; the resolution loop is outer so the table fills column by
        // column.
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
                int cellTotal = Resolutions.Length * Profiles.Length;
                int cellDone = 0;
                foreach (var (w, h) in Resolutions)
                {
                    ct.ThrowIfCancellationRequested();

                    string? clip = null;
                    string? clipErr = null;
                    try
                    {
                        progress?.Invoke($"Preparing {w}x{h} clip...");
                        clip = await GenerateClipAsync(w, h, ct);
                        tempFiles.Add(clip);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { clipErr = $"clip generation failed: {ex.Message}"; }

                    foreach (var (name, slot) in Profiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        cellDone++;
                        progress?.Invoke($"Benchmarking {name} {w}x{h} -> {w * 2}x{h * 2} ({cellDone}/{cellTotal})...");

                        if (clip is null)
                        {
                            results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot, Error = clipErr });
                            continue;
                        }
                        try
                        {
                            var fps = await MeasureFpsAsync(p, clip, slot, ct);
                            results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot, Fps = fps });
                            progress?.Invoke($"{name} {w}x{h}: {(fps.HasValue ? fps.Value.ToString("0.0", Inv) + " fps" : "no result")}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot, Error = ex.Message });
                        }
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

        // Generate a synthetic clip (testsrc2, yuv420p h264) long enough to supply
        // the probe + the largest sample without looping.
        private static async Task<string> GenerateClipAsync(int w, int h, CancellationToken ct)
        {
            var ffmpeg = FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg not found on PATH.");
            var outPath = Path.Combine(Path.GetTempPath(), $"animejanai_bench_{w}x{h}_{Guid.NewGuid():N}.mp4");

            var args =
                $"-y -nostdin -f lavfi -i testsrc2=size={w}x{h}:rate={ClipFps}:duration={ClipSeconds} " +
                $"-pix_fmt yuv420p -c:v libx264 -preset ultrafast \"{outPath}\"";

            var (exit, _, stderr) = await RunProcessAsync(ffmpeg, args, workingDir: null, timeout: TimeSpan.FromSeconds(120), ct);
            if (exit != 0 || !File.Exists(outPath))
                throw new Exception($"ffmpeg exited {exit}: {Tail(stderr, 200)}");
            return outPath;
        }

        // Steady-state fps for one slot. Runs a short probe (ProbeFrames) and a
        // longer sample (ProbeFrames + S); the shared init/warm-up cancels in the
        // difference, so fps = S / (t_sample - t_probe). S is sized from the probe
        // so the sample lands near SampleSeconds of work regardless of speed.
        private static async Task<double?> MeasureFpsAsync(Paths p, string clip, int slot, CancellationToken ct)
        {
            var vf = BuildVf(p, slot);

            double tProbe = await TimeFramesAsync(p, clip, vf, ProbeFrames, ct);
            double probeFps = tProbe > 0 ? ProbeFrames / tProbe : 0;

            int s = (int)Math.Round(probeFps * SampleSeconds);
            s = Math.Clamp(s, SampleFramesMin, SampleFramesMax);

            double tSample = await TimeFramesAsync(p, clip, vf, ProbeFrames + s, ct);

            double dt = tSample - tProbe;
            if (dt <= 0.0) return null;                 // sample shorter than probe -> unusable
            return Math.Round(s / dt, 1);
        }

        // Decode exactly `frames` frames through the upscale filter (offscreen,
        // uncapped) and return the wall-clock seconds.
        private static async Task<double> TimeFramesAsync(Paths p, string clip, string vf, int frames, CancellationToken ct)
        {
            var args =
                $"\"{clip}\" " +
                $"--config-dir=\"{p.ConfigDir}\" " +
                $"--vf=\"{vf}\" " +
                $"--frames={frames} " +
                "--vo=null --untimed --no-audio --no-cache " +
                "--keep-open=no --idle=no --force-window=no " +
                "--msg-level=all=error";

            var sw = Stopwatch.StartNew();
            var (exit, _, stderr) = await RunProcessAsync(p.Mpv, args, workingDir: Path.GetDirectoryName(p.Mpv),
                timeout: TimeSpan.FromSeconds(180), ct);
            sw.Stop();

            if (exit != 0)
                throw new Exception($"mpv exited {exit}: {Tail(stderr, 200)}");
            return sw.Elapsed.TotalSeconds;
        }

        // The aji filter string with the chosen slot, using absolute paths so it is
        // independent of the conf's default slot. Mirrors the vf in mpv-animejanai.conf.
        private static string BuildVf(Paths p, int slot)
        {
            string lib = Path.Combine(p.DataDir, "inference", "libaji.so");
            string rife = Path.Combine(p.DataDir, "rife");
            return $"@aji:animejanai:lib={lib}:conf={p.Conf}:model-dir={p.ModelDir}:rife-model-dir={rife}:slot={slot}";
        }

        // Markdown table identical in shape to what benchmark.ps1 writes and
        // BenchmarkSubmission.FromBenchmarkFile parses: resolution columns, one row
        // per profile.
        private static void WriteBenchmarkTxt(string dataDir, string backendLabel, List<Result> results)
        {
            var cols = Resolutions.Select(r => $"{r.W}x{r.H}").ToArray();
            var sb = new StringBuilder();
            sb.AppendLine($"AnimeJaNai playback benchmark - backend: {backendLabel}");
            sb.AppendLine();
            sb.AppendLine("|fps|" + string.Join("|", cols) + "|");
            sb.AppendLine("|" + string.Join("|", Enumerable.Repeat("-", cols.Length + 1)) + "|");

            foreach (var (name, _) in Profiles)
            {
                var cells = Resolutions.Select(res =>
                {
                    var r = results.FirstOrDefault(x => x.SrcW == res.W && x.SrcH == res.H && x.Profile == name);
                    return r?.Fps is { } f ? f.ToString("0.0", Inv) : "-";
                });
                sb.AppendLine("|" + name + "|" + string.Join("|", cells) + "|");
            }

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
