using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services
{
    // Linux/AMD system detection + setup helpers, shared by the benchmark
    // submission (hardware fields) and the Linux Components/setup tab. Every
    // probe is best-effort: a missing file or absent tool yields "" / false
    // rather than throwing.
    public static class LinuxSystemInfo
    {
        // ---- ROCm / GPU detection --------------------------------------------------

        // First GPU agent's Marketing Name from rocminfo (exact SKU, e.g.
        // "AMD Radeon RX 9070 XT"); falls back to lspci's model string.
        public static string GpuName()
        {
            var name = RocminfoField("Marketing Name", gpuOnly: true);
            if (name.Length > 0) return name;
            return LspciGpuName();
        }

        // The GPU's gfx target (e.g. "gfx1201") from rocminfo's GPU-agent Name line.
        public static string GfxTarget()
        {
            foreach (var raw in RunTool("rocminfo", "").Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^Name:\s*(gfx[0-9a-fA-F]+)\b");
                if (m.Success) return m.Groups[1].Value;
            }
            return "";
        }

        // ROCm runtime version from /opt/rocm/.info/version (or a versioned install).
        public static string RocmVersion()
        {
            var v = SafeReadText("/opt/rocm/.info/version").Trim();
            if (v.Length == 0)
                foreach (var d in SafeDirs("/opt", "rocm-*"))
                {
                    v = SafeReadText(Path.Combine(d, ".info", "version")).Trim();
                    if (v.Length > 0) break;
                }
            return v;
        }

        // MIGraphX version decoded from its library soname, e.g.
        // libmigraphx.so.2015000 -> "2.15.0" (major*1e6 + minor*1e3 + patch).
        public static string MigraphxVersion()
        {
            try
            {
                var root = Directory.Exists("/opt/rocm/lib") ? "/opt/rocm/lib"
                         : Directory.Exists("/opt/rocm") ? "/opt/rocm" : "";
                if (root.Length == 0) return "";
                foreach (var f in Directory.GetFiles(root, "libmigraphx.so.*", SearchOption.AllDirectories))
                {
                    var m = Regex.Match(Path.GetFileName(f), @"^libmigraphx\.so\.(\d+)$");
                    if (!m.Success || !long.TryParse(m.Groups[1].Value, out var n) || n < 1000) continue;
                    return $"{n / 1000000}.{(n / 1000) % 1000}.{n % 1000}";
                }
            }
            catch { /* leave blank */ }
            return "";
        }

        // The AMD compute driver stack that governs inference perf, e.g.
        // "ROCm 7.2.4 / MIGraphX 2.15.0". Empty when ROCm isn't found.
        public static string DriverStack()
        {
            var parts = new System.Collections.Generic.List<string>();
            var rocm = RocmVersion();
            if (rocm.Length > 0) parts.Add($"ROCm {rocm}");
            var mx = MigraphxVersion();
            if (mx.Length > 0) parts.Add($"MIGraphX {mx}");
            return string.Join(" / ", parts);
        }

        // The strongest readiness check: actually dlopen the ROCm engine. This
        // resolves libaji_rocm.so's DT_NEEDED deps (libmigraphx, libamdhip64,
        // hsa-runtime, ...), so it fails when the ROCm stack is missing or partial
        // in a way that file/version checks alone wouldn't catch. Loaded only to
        // probe, then freed.
        public static bool EngineLoads(string libPath)
        {
            if (!File.Exists(libPath)) return false;
            try
            {
                if (NativeLibrary.TryLoad(libPath, out var handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }
            }
            catch { /* unresolved deps -> not ready */ }
            return false;
        }

        // Best-effort per-distro ROCm install guidance. Package names vary, so
        // the docs link is always the authoritative path.
        public static (string command, string note, string docsUrl) RocmInstallHint()
        {
            const string docs = "https://rocm.docs.amd.com/projects/install-on-linux/en/latest/";
            var osr = SafeReadText("/etc/os-release");
            string id = Field(osr, "ID").ToLowerInvariant();
            string idLike = Field(osr, "ID_LIKE").ToLowerInvariant();
            bool Is(params string[] names) => names.Contains(id) || names.Any(n => idLike.Contains(n));

            if (Is("arch", "endeavouros", "manjaro", "cachyos"))
                return ("sudo pacman -S rocm-hip-sdk migraphx rocminfo", "Arch-based", docs);
            if (Is("ubuntu", "debian", "pop", "linuxmint"))
                return ("Install AMD's amdgpu-install, then:  sudo amdgpu-install --usecase=rocm", "Debian/Ubuntu", docs);
            if (Is("fedora", "rhel", "centos"))
                return ("sudo dnf install rocm-hip migraphx rocminfo", "Fedora/RHEL", docs);
            return ("Install the ROCm runtime + MIGraphX for your distribution", "", docs);
        }

        // ---- download + extract ----------------------------------------------------

        // Streamed download with 0-100 progress. Throws on HTTP/IO error so the
        // caller can surface it.
        public static async Task DownloadAsync(string url, string destFile, IProgress<double>? progress, CancellationToken ct = default)
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeJaNaiManager");
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1L;
            Directory.CreateDirectory(Path.GetDirectoryName(destFile) ?? ".");
            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(destFile);
                var buffer = new byte[81920];
                long readTotal = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    readTotal += n;
                    if (total > 0) progress?.Report(readTotal * 100.0 / total);
                }
            }
            catch
            {
                // don't leave a half-written file behind on cancel/IO error
                try { if (File.Exists(destFile)) File.Delete(destFile); } catch { }
                throw;
            }
        }

        // Extract a .7z via a system 7-Zip (7zz/7z/7za). Returns false + a message
        // when no extractor is on PATH (the one external dependency of the download).
        public static bool Extract7z(string archive, string outDir, out string error)
        {
            error = "";
            string tool = new[] { "7zz", "7z", "7za" }.FirstOrDefault(t => Which(t) != null) ?? "";
            if (tool.Length == 0)
            {
                error = "No 7-Zip extractor found on PATH. Install p7zip (e.g. 'sudo pacman -S p7zip' / 'sudo apt install p7zip-full').";
                return false;
            }
            try
            {
                Directory.CreateDirectory(outDir);
                var psi = new ProcessStartInfo
                {
                    FileName = tool,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("x");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-o" + outDir);
                psi.ArgumentList.Add(archive);
                using var p = Process.Start(psi);
                if (p == null) { error = "Failed to start " + tool; return false; }
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) { error = err.Length > 0 ? err.Trim() : $"{tool} exited {p.ExitCode}"; return false; }
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        // ---- shared low-level helpers ----------------------------------------------

        public static string SafeReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        public static string[] SafeDirs(string root, string pattern)
        {
            try { return Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        public static string RunTool(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p == null) return "";
                // Drain stdout concurrently (so a chatty tool can't deadlock on a
                // full pipe) while enforcing a real timeout: kill a hung tool so a
                // wedged rocminfo/lspci can't freeze the readiness check forever.
                var stdout = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(true); } catch { /* already gone */ }
                    return "";
                }
                return stdout.GetAwaiter().GetResult();
            }
            catch { return ""; }
        }

        // First GPU-agent value of a rocminfo field. rocminfo lists the CPU agent
        // first; within an agent the field precedes "Device Type", so we emit the
        // most recent value once a GPU device type is seen.
        static string RocminfoField(string field, bool gpuOnly)
        {
            var outp = RunTool("rocminfo", "");
            if (outp.Length == 0) return "";
            string last = "";
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                var m = Regex.Match(line, $@"^{Regex.Escape(field)}:\s*(.+)$");
                if (m.Success) { last = m.Groups[1].Value.Trim(); if (!gpuOnly) return last; continue; }
                if (gpuOnly && Regex.IsMatch(line, @"^Device Type:\s*GPU") && last.Length > 0) return last;
            }
            return "";
        }

        static string LspciGpuName()
        {
            foreach (var line in RunTool("lspci", "-nn").Split('\n'))
            {
                if (!Regex.IsMatch(line, @"VGA compatible controller|Display controller|3D controller", RegexOptions.IgnoreCase))
                    continue;
                var brackets = Regex.Matches(line, @"\[([^\]]+)\]");
                for (int i = brackets.Count - 1; i >= 0; i--)
                {
                    var v = brackets[i].Groups[1].Value.Trim();
                    if (!Regex.IsMatch(v, @"^[0-9a-fA-F]{4}:[0-9a-fA-F]{4}$") && v.Length > 0)
                        return v;
                }
            }
            return "";
        }

        static string Field(string osRelease, string key)
        {
            var m = Regex.Match(osRelease, $"^{Regex.Escape(key)}=\"?(.*?)\"?$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : "";
        }

        static string? Which(string exe)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            {
                if (dir.Length == 0) continue;
                try { var p = Path.Combine(dir, exe); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }
    }
}
