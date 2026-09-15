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
    //   - MIGraphX compiles a per-(model, resolution) engine on first use. The compile
    //     is async (the filter defers it and plays passthrough meanwhile) and can take a
    //     few minutes at 4K, so EACH cell is first warmed up: the clip is played until
    //     the stats log shows the engine active (compile done + cached), with progress
    //     reported so the UI isn't frozen. Only THEN is fps timed, on the cached engine.
    //   - The timed measurement excludes warm-up: a short probe run and a longer sample
    //     run are timed; the steady-state fps is the extra sample frames divided by the
    //     extra wall-clock (the shared pipeline-init/GPU-clock-ramp cost cancels).
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
        // Max wait for a first-play engine compile before giving up on a cell. The MIGraphX
        // compile is ~tens of seconds at low res and a few minutes at 4K (with MLIR on); 900s
        // leaves generous headroom for slower GPUs without hanging the benchmark forever.
        private const int CompileWaitSeconds = 900;
        // Skip a cell (and every larger resolution for that profile) once it can't sustain this
        // many fps — matching benchmark.ps1. Well below the ~24 fps real-time bar, so the exact
        // value doesn't matter; it avoids compiling a multi-minute 4K engine for a profile that
        // is already hopeless at a smaller resolution.
        private const double FpsFloor = 6.0;

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
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "The offscreen benchmark runs on Linux and macOS only (Windows uses benchmark.ps1).";
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
            // Shared stats-log the filter writes its compile/active status to (reused per
            // cell; the warm-up truncates it before each engine compile).
            var statsPath = Path.Combine(Path.GetTempPath(), $"animejanai_bench_stats_{Guid.NewGuid():N}.log");
            tempFiles.Add(statsPath);
            try
            {
                int cellTotal = Resolutions.Length * Profiles.Length;
                int cellDone = 0;
                // Profiles that already fell below the fps floor at a smaller resolution; every
                // larger resolution for them is skipped without compiling/running (ascending res).
                var tooSlow = new HashSet<string>();
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

                        // A smaller resolution for this profile already fell below the floor; a
                        // larger one can only be slower, so skip it without compiling or running.
                        if (tooSlow.Contains(name))
                        {
                            results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot });
                            progress?.Invoke($"{name} {w}x{h}: skipped (a smaller resolution was already too slow)");
                            continue;
                        }

                        progress?.Invoke($"Benchmarking {name} {w}x{h} -> {w * 2}x{h * 2} ({cellDone}/{cellTotal})...");

                        if (clip is null)
                        {
                            results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot, Error = clipErr });
                            continue;
                        }
                        try
                        {
                            var fps = await MeasureFpsAsync(p, clip, slot, statsPath, $"{name} {w}x{h}", progress, ct);
                            if (fps.HasValue && fps.Value < FpsFloor)
                            {
                                // too slow to be usable: record "-" and skip every larger resolution
                                tooSlow.Add(name);
                                results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot });
                                progress?.Invoke($"{name} {w}x{h}: {fps.Value.ToString("0.0", Inv)} fps — skipped (under {FpsFloor:0} fps)");
                            }
                            else
                            {
                                results.Add(new Result { SrcW = w, SrcH = h, Profile = name, Slot = slot, Fps = fps });
                                progress?.Invoke($"{name} {w}x{h}: {(fps.HasValue ? fps.Value.ToString("0.0", Inv) + " fps" : "no result")}");
                            }
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
        private static async Task<double?> MeasureFpsAsync(Paths p, string clip, int slot,
            string statsPath, string label, Action<string>? progress, CancellationToken ct)
        {
            var vf = BuildVf(p, slot, statsPath);

            // Compile + cache the engine first (async, minutes at 4K), so the timed runs
            // below measure the model on the cached engine, not passthrough during compile.
            await EnsureCompiledAsync(p, clip, vf, statsPath, label, progress, ct);

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
                // --load-scripts=no (matching the Windows benchmark): the player's lua scripts
                // re-apply default_slot on file-loaded (and the engine-monitor pauses playback),
                // which would override the benchmark's slot=N and silently measure the default
                // slot instead. Keep the config for mpv.conf parity but drop the scripts;
                // --hwdec=no explicitly since backend.lua (which sets it) no longer runs.
                $"--config-dir=\"{p.ConfigDir}\" --load-scripts=no --hwdec=no " +
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

        // Ensure the MIGraphX engine for this vf (slot + resolution) is compiled and cached
        // before timing, so the timed runs measure the model rather than passthrough during
        // the compile. The compile is async: on first play the filter defers it (playing
        // passthrough) and writes "Building MIGraphX engine ..." to the stats log, then the
        // active "... -> ..." chain once it finishes (~tens of seconds at low res, a few
        // minutes at 4K). A cached engine activates immediately. Plays the clip looped +
        // offscreen and polls the stats log, reporting progress; throws on build failure or
        // after CompileWaitSeconds.
        private static async Task EnsureCompiledAsync(Paths p, string clip, string vf, string statsPath,
            string label, Action<string>? progress, CancellationToken ct)
        {
            try { File.WriteAllText(statsPath, string.Empty); } catch { /* best effort */ }

            var args =
                $"\"{clip}\" " +
                // --load-scripts=no (matching the Windows benchmark): same reason as the timed
                // runs — the player scripts would re-apply default_slot and override slot=N, and
                // the engine-monitor would pause playback. We watch the stats log directly.
                $"--config-dir=\"{p.ConfigDir}\" --load-scripts=no --hwdec=no " +
                $"--vf=\"{vf}\" " +
                "--vo=null --no-audio --no-cache --loop-file=inf " +
                "--msg-level=all=error";

            using var proc = StartProcess(p.Mpv, args, Path.GetDirectoryName(p.Mpv));
            try
            {
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (proc.HasExited)
                        throw new Exception($"mpv exited ({proc.ExitCode}) before the engine was ready.");

                    string stats = SafeRead(statsPath);
                    if (stats.Contains("->")) return;                 // active chain => engine ready
                    if (stats.Contains("FAILED"))
                        throw new Exception("MIGraphX engine build failed.");
                    if (stats.Contains("Building"))
                        progress?.Invoke($"Compiling {label} engine (first run, up to a few minutes)... {sw.Elapsed.TotalSeconds:0}s");

                    if (sw.Elapsed.TotalSeconds > CompileWaitSeconds)
                        throw new TimeoutException($"engine compile for {label} exceeded {CompileWaitSeconds}s.");

                    await Task.Delay(1000, ct);
                }
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }

        // Start a process without waiting for exit; stdout/stderr are drained + discarded
        // (we watch the stats file instead). Caller owns disposal/kill.
        private static Process StartProcess(string fileName, string args, string? workingDir)
        {
            var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = args;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!string.IsNullOrEmpty(workingDir)) process.StartInfo.WorkingDirectory = workingDir;
            process.OutputDataReceived += (_, __) => { };
            process.ErrorDataReceived += (_, __) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        // Read a file another process is concurrently writing (shared read+write).
        private static string SafeRead(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch { return string.Empty; }
        }

        // The aji filter string with the chosen slot, using absolute paths so it is
        // independent of the conf's default slot. Mirrors the vf in mpv-animejanai.conf.
        // stats= makes the filter write its status (the "Building MIGraphX engine ..."
        // marker while compiling, the active "... -> ..." chain once ready) so the warm-up
        // can detect when the engine is compiled and cached.
        private static string BuildVf(Paths p, int slot, string statsPath)
        {
            string lib = Path.Combine(p.DataDir, "inference", AnimeJaNaiConfEditor.ViewModels.MainWindowViewModel.NativeLib("aji"));
            string rife = Path.Combine(p.DataDir, "rife");
            return $"@aji:animejanai:lib={lib}:conf={p.Conf}:model-dir={p.ModelDir}:rife-model-dir={rife}:stats={statsPath}:slot={slot}";
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
            // GUI apps on macOS do not inherit the shell's PATH, so probe the Homebrew /
            // MacPorts prefixes explicitly after PATH.
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            var dirs = pathEnv.Split(Path.PathSeparator).Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" });
            foreach (var dir in dirs)
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
