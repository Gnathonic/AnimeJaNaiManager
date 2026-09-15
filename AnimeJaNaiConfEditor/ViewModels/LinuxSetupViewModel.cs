using AnimeJaNaiConfEditor.Services;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels
{
    // The Linux "Components" tab. Unlike the Windows pack-installer, this is a
    // ROCm readiness panel + a small downloader: it detects the ROCm stack (which
    // it can only guide the user to install, never install itself) and downloads
    // the RIFE models. Backed by LinuxSystemInfo for all detection/IO.
    public class LinuxSetupViewModel : ViewModelBase
    {
        // The published RIFE model archive (platform-independent fp16 .onnx set).
        const string RifeUrl =
            "https://github.com/the-database/animejanai-inference/releases/download/models-rife-fp16-1/rife-fp16-1.7z";
        const long RifeBytes = 322142263;

        static string EngineLib => Path.Combine(MainWindowViewModel.DataDir, "inference", MainWindowViewModel.NativeLib("aji_rocm"));
        static string RifeDir => Path.Combine(MainWindowViewModel.DataDir, "rife");

        // Fired after a refresh or a successful RIFE install so the rest of the UI
        // (Profiles-tab RIFE awareness) re-derives from disk.
        public event Action? Refreshed;

        public LinuxSetupViewModel()
        {
            RifeSizeText = $"{RifeBytes / 1048576:N0} MB";
        }

        // ---- readiness checks ----
        bool _rocmOk; public bool RocmOk { get => _rocmOk; set => this.RaiseAndSetIfChanged(ref _rocmOk, value); }
        string _rocmText = "Checking…"; public string RocmText { get => _rocmText; set => this.RaiseAndSetIfChanged(ref _rocmText, value); }

        bool _gpuOk; public bool GpuOk { get => _gpuOk; set => this.RaiseAndSetIfChanged(ref _gpuOk, value); }
        string _gpuText = "Checking…"; public string GpuText { get => _gpuText; set => this.RaiseAndSetIfChanged(ref _gpuText, value); }

        bool _engineOk; public bool EngineOk { get => _engineOk; set => this.RaiseAndSetIfChanged(ref _engineOk, value); }
        string _engineText = "Checking…"; public string EngineText { get => _engineText; set => this.RaiseAndSetIfChanged(ref _engineText, value); }

        bool _allReady; public bool AllReady { get => _allReady; set { this.RaiseAndSetIfChanged(ref _allReady, value); this.RaisePropertyChanged(nameof(ShowGuidance)); } }
        public bool ShowGuidance => _checked && !_allReady;

        // False until the first readiness check completes, so the rows don't flash a
        // red "X" next to "Checking…" on first paint.
        bool _checked; public bool Checked { get => _checked; set { this.RaiseAndSetIfChanged(ref _checked, value); this.RaisePropertyChanged(nameof(ShowGuidance)); } }

        string _installNote = ""; public string InstallNote { get => _installNote; set { this.RaiseAndSetIfChanged(ref _installNote, value); this.RaisePropertyChanged(nameof(HasInstallNote)); } }
        public bool HasInstallNote => _installNote.Length > 0;
        string _installCommand = ""; public string InstallCommand { get => _installCommand; set => this.RaiseAndSetIfChanged(ref _installCommand, value); }
        string _docsUrl = ""; public string DocsUrl { get => _docsUrl; set => this.RaiseAndSetIfChanged(ref _docsUrl, value); }

        // ---- RIFE download item ----
        public string RifeSizeText { get; }
        bool _rifeInstalled; public bool RifeInstalled { get => _rifeInstalled; set { this.RaiseAndSetIfChanged(ref _rifeInstalled, value); this.RaisePropertyChanged(nameof(RifeNotInstalled)); this.RaisePropertyChanged(nameof(CanDownload)); } }
        public bool RifeNotInstalled => !_rifeInstalled;
        // The Download button is live only when idle and not already installed.
        public bool CanDownload => !_isBusy && !_rifeInstalled;
        string _rifeStatus = ""; public string RifeStatus { get => _rifeStatus; set => this.RaiseAndSetIfChanged(ref _rifeStatus, value); }
        double _rifeProgress; public double RifeProgress { get => _rifeProgress; set => this.RaiseAndSetIfChanged(ref _rifeProgress, value); }
        bool _rifeDownloading; public bool RifeDownloading { get => _rifeDownloading; set => this.RaiseAndSetIfChanged(ref _rifeDownloading, value); }

        // ---- general ----
        bool _isBusy;
        public bool IsBusy { get => _isBusy; set { this.RaiseAndSetIfChanged(ref _isBusy, value); this.RaisePropertyChanged(nameof(NotBusy)); this.RaisePropertyChanged(nameof(CanDownload)); } }
        public bool NotBusy => !_isBusy;
        string _statusLine = ""; public string StatusLine { get => _statusLine; set => this.RaiseAndSetIfChanged(ref _statusLine, value); }

        public async void Refresh()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var (rocmVer, mxVer, gpu, gfx, engine) = await Task.Run(() =>
                    (LinuxSystemInfo.RocmVersion(), LinuxSystemInfo.MigraphxVersion(),
                     LinuxSystemInfo.GpuName(), LinuxSystemInfo.GfxTarget(),
                     LinuxSystemInfo.EngineLoads(EngineLib)));

                RocmOk = rocmVer.Length > 0;
                RocmText = RocmOk
                    ? $"ROCm {rocmVer}" + (mxVer.Length > 0 ? $"  ·  MIGraphX {mxVer}" : "")
                    : "ROCm runtime not found under /opt/rocm";

                GpuOk = gpu.Length > 0;
                GpuText = GpuOk
                    ? gpu + (gfx.Length > 0 ? $"  ({gfx})" : "")
                    : "No AMD GPU detected by rocminfo";

                EngineOk = engine;
                EngineText = engine
                    ? "The ROCm upscaling engine loads — you're ready to upscale."
                    : "The ROCm engine can't load — its runtime dependencies are missing.";

                AllReady = RocmOk && GpuOk && EngineOk;

                if (!AllReady)
                {
                    var (command, note, docs) = LinuxSystemInfo.RocmInstallHint();
                    InstallCommand = command;
                    InstallNote = note.Length > 0 ? $"Detected: {note}" : "";
                    DocsUrl = docs;
                }

                RifeInstalled = MainWindowViewModel.RifeOnDisk();
                RifeStatus = RifeInstalled
                    ? $"Installed ({RifeOnnxCount()} models)."
                    : "Not downloaded.";
            }
            catch (Exception e)
            {
                // async void: keep a thrown detection error from taking down the app
                StatusLine = "Readiness check failed: " + e.Message;
            }
            finally
            {
                Checked = true;
                IsBusy = false;
                Refreshed?.Invoke();
            }
        }

        public async void DownloadRife()
        {
            if (IsBusy) return;
            IsBusy = true;
            RifeDownloading = true;
            RifeProgress = 0;
            var tmp = Path.Combine(Path.GetTempPath(), $"rife-fp16-1-{Guid.NewGuid():N}.7z");
            try
            {
                RifeStatus = "Downloading…";
                var progress = new Progress<double>(p => RifeProgress = p);
                await LinuxSystemInfo.DownloadAsync(RifeUrl, tmp, progress);

                RifeStatus = "Extracting…";
                var (ok, err) = await Task.Run(() =>
                {
                    var success = LinuxSystemInfo.Extract7z(tmp, RifeDir, out var e);
                    return (success, e);
                });

                if (!ok)
                {
                    RifeStatus = "Extract failed: " + err;
                    return;
                }

                RifeInstalled = MainWindowViewModel.RifeOnDisk();
                RifeStatus = RifeInstalled
                    ? $"Installed ({RifeOnnxCount()} models). Interpolation activates in a future update."
                    : "Download finished but no models were found.";
            }
            catch (Exception e)
            {
                RifeStatus = "Download failed: " + e.Message;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                RifeDownloading = false;
                IsBusy = false;
                Refreshed?.Invoke();
            }
        }

        public void OpenDocs()
        {
            if (DocsUrl.Length == 0) return;
            try { Process.Start(new ProcessStartInfo { FileName = DocsUrl, UseShellExecute = true }); }
            catch { /* no browser handler */ }
        }

        static int RifeOnnxCount()
        {
            try { return Directory.Exists(RifeDir) ? Directory.GetFiles(RifeDir, "*.onnx").Length : 0; }
            catch { return 0; }
        }
    }
}
