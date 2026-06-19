using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Material.Icons.Avalonia;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views
{
    public partial class MainWindow : FAAppWindow
    {
        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);
            Closing += MainWindow_Closing;
            Opened += MainWindow_Opened;
        }

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {

            }
        }

        private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {

            }
        }

        private async void ImportFullConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                // Start async operation to open the dialog.
                var storageProvider = topLevel.StorageProvider;

                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Profile Conf File",
                    AllowMultiple = false,
                    FileTypeFilter = new FilePickerFileType[] { new("AnimeJaNai Conf File") { Patterns = new[] { "*.conf" }, MimeTypes = new[] { "*/*" } }, FilePickerFileTypes.All },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (files.Count >= 1)
                {

                    var inPath = files[0].TryGetLocalPath();

                    if (inPath != null)
                    {
                        var td = new FATaskDialog
                        {
                            Title = "Confirm Full Conf Import",
                            ShowProgressBar = false,
                            Content = "The following full conf file will be imported. All configuration settings will be backed up and then all configuration settings for ALL PROFILES will be replaced with the imported conf file.\n\n" +
    inPath,
                            Buttons =
            {
                FATaskDialogButton.OKButton,
                FATaskDialogButton.CancelButton
            }
                        };


                        td.Closing += async (s, e) =>
                        {
                            if ((FATaskDialogStandardResult)e.Result == FATaskDialogStandardResult.OK)
                            {
                                var deferral = e.GetDeferral();

                                td.ShowProgressBar = true;
                                int value = 0;


                                await Task.Run(() =>
                                {
                                    vm.CheckAndDoBackup();
                                    // autoSave: true wires the imported slots/chains/models for
                                    // persistence; the explicit write commits the import itself to
                                    // animejanai.conf (assigning AnimeJaNaiConf does not trigger a save).
                                    vm.AnimeJaNaiConf = vm.ReadAnimeJaNaiConf(inPath, true);
                                    vm.WriteAnimeJaNaiConf();
                                });

                                deferral.Complete();
                            }
                        };

                        td.XamlRoot = this;
                        _ = await td.ShowAsync();
                    }

                }
            }
        }

        private async void ImportCurrentProfileConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                // Start async operation to open the dialog.
                var storageProvider = topLevel.StorageProvider;

                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Full Conf File",
                    AllowMultiple = false,
                    FileTypeFilter = new FilePickerFileType[] { new("AnimeJaNai Profile Conf File") { Patterns = new[] { "*.pconf" }, MimeTypes = new[] { "*/*" } }, FilePickerFileTypes.All },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (files.Count >= 1)
                {

                    var inPath = files[0].TryGetLocalPath();

                    if (inPath != null)
                    {
                        if (vm.CurrentSlot.Chains.Count == 0)
                        {
                            // blank slot, no need to prompt before importing and no need to do backup
                            vm.ReadAnimeJaNaiConfToCurrentSlot(inPath, true);
                        }
                        else
                        {
                            var td = new FATaskDialog
                            {
                                Title = "Confirm Profile Conf Import",
                                ShowProgressBar = false,
                                Content = $"The following profile conf file will be imported to the current profile {vm.CurrentSlot.ProfileName}. All configuration settings will be backed up and then all configuration settings for the current profile {vm.CurrentSlot.ProfileName} will be overwritten.\n\n" +
                                inPath,
                                Buttons =
                            {
                                FATaskDialogButton.OKButton,
                                FATaskDialogButton.CancelButton
                            }
                            };


                            td.Closing += async (s, e) =>
                            {
                                if ((FATaskDialogStandardResult)e.Result == FATaskDialogStandardResult.OK)
                                {
                                    var deferral = e.GetDeferral();

                                    td.ShowProgressBar = true;

                                    await Task.Run(() =>
                                    {
                                        vm.CheckAndDoBackup();
                                        vm.ReadAnimeJaNaiConfToCurrentSlot(inPath, true);
                                    });

                                    deferral.Complete();
                                }
                            };

                            td.XamlRoot = this;
                            _ = await td.ShowAsync();
                        }
                    }

                } 
            }
        }

        private async void CloneSelectedProfileToCurrentProfile(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (vm.SelectedProfileToClone != null)
                {
                    if (vm.CurrentSlot.Chains.Count == 0)
                    {
                        // Current slot is blank - no need to ask user for confirmation before cloning and no need to do backup
                        vm.ReadAnimeJaNaiConfToCurrentSlot(
                            vm.ParsedAnimeJaNaiProfileConf(vm.SelectedProfileToClone),
                            true);
                    }
                    else
                    {
                        var td = new FATaskDialog
                        {
                            Title = "Confirm Profile Conf Import",
                            ShowProgressBar = false,
                            Content = $"The profile {vm.SelectedProfileToClone.ProfileName} will be cloned to the current profile {vm.CurrentSlot.ProfileName}. All configuration settings will be backed up and then all configuration settings for the current profile {vm.CurrentSlot.ProfileName} will be overwritten.",
                            Buttons =
                        {
                            FATaskDialogButton.OKButton,
                            FATaskDialogButton.CancelButton
                        }
                        };


                        td.Closing += async (s, e) =>
                        {
                            if ((FATaskDialogStandardResult)e.Result == FATaskDialogStandardResult.OK)
                            {
                                var deferral = e.GetDeferral();

                                td.ShowProgressBar = true;
                                int value = 0;


                                await Task.Run(() =>
                                {
                                    vm.CheckAndDoBackup();
                                    vm.ReadAnimeJaNaiConfToCurrentSlot(
                                        vm.ParsedAnimeJaNaiProfileConf(vm.SelectedProfileToClone),
                                        true);
                                });

                                deferral.Complete();
                            }
                        };

                        td.XamlRoot = this;
                        _ = await td.ShowAsync();
                    }
                }
            }
        }

        private async void ExportFullConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                var storageProvider = topLevel.StorageProvider;

                // Start async operation to open the dialog.
                var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Full Conf File",
                    DefaultExtension = "conf",
                    FileTypeChoices = new FilePickerFileType[]
                    {
                    new("AnimeJaNai Conf File (*.conf)") { Patterns = new[] { "*.conf" } },
                    },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (file is not null)
                {

                    //vm.OutputFilePath = file.TryGetLocalPath() ?? "";

                    var outPath = file.TryGetLocalPath();

                    if (outPath != null)
                    {
                        vm.WriteAnimeJaNaiConf(outPath);
                    }

                }
            }
        }

        private async void ExportCurrentProfileConfButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                var storageProvider = topLevel.StorageProvider;

                // Start async operation to open the dialog.
                var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Current Profile Conf File",
                    DefaultExtension = "conf",
                    FileTypeChoices = new FilePickerFileType[]
                    {
                    new("AnimeJaNai Profile Conf File (*.pconf)") { Patterns = new[] { "*.pconf" } },
                    },
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(vm.BackupPath),
                });

                if (file is not null)
                {
                    var outPath = file.TryGetLocalPath();

                    if (outPath != null)
                    {
                        vm.WriteAnimeJaNaiCurrentProfileConf(outPath);
                    }
                }
            }
        }

        private async void RunBenchmarkButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            // The Vulkan backend (Linux) has no TensorRT engine-build step, so the
            // benchmark runs the bundled mpv directly and the wording / flow differ.
            if (MainWindowViewModel.IsNotWindows)
            {
                await RunLinuxBenchmarkAsync(vm);
                return;
            }

            const string runResult = "run";
            var td = new FATaskDialog
            {
                Title = "Run playback benchmark",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                    Text =
                        "This measures your real playback fps across several resolutions for the " +
                        "Balanced and Performance templates.\n\n" +
                        "mpv windows will open and close on their own while it runs. Do not close " +
                        "or click them, or the results will be invalid.\n\n" +
                        "The first run builds a TensorRT engine per resolution (about a minute " +
                        "each, cached afterward), so the whole benchmark can take 10+ minutes depending on your hardware.",
                },
                Buttons =
                {
                    new FATaskDialogButton("Start benchmark", runResult),
                    FATaskDialogButton.CancelButton,
                },
            };
            td.XamlRoot = this;
            if (Equals(await td.ShowAsync(), runResult))
                vm.LaunchBenchmark();
        }

        // Linux/Vulkan benchmark: confirm (no TensorRT wording), run offscreen mpv
        // across resolutions with a progress dialog, then show the fps results. The
        // run writes benchmark.txt so "Submit to Catalog" works exactly as on Windows.
        private async Task RunLinuxBenchmarkAsync(MainWindowViewModel vm)
        {
            if (vm.LinuxBenchmarkPrerequisiteError() is { } missing)
            {
                await ShowInfoDialog("Can't run the benchmark", missing);
                return;
            }

            const string runResult = "run";
            var confirm = new FATaskDialog
            {
                Title = "Run playback benchmark",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                    Text =
                        "This measures your real playback fps across several source resolutions " +
                        "using the ROCm backend and your current profile.\n\n" +
                        "It runs the bundled mpv offscreen (no windows open), generating short " +
                        "synthetic test clips with ffmpeg, so there is nothing to click. It " +
                        "usually takes about a minute.",
                },
                Buttons =
                {
                    new FATaskDialogButton("Start benchmark", runResult),
                    FATaskDialogButton.CancelButton,
                },
            };
            confirm.XamlRoot = this;
            if (!Equals(await confirm.ShowAsync(), runResult))
                return;

            var statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                Text = "Starting benchmark...",
            };
            var progressDialog = new FATaskDialog
            {
                Title = "Running playback benchmark",
                Content = statusText,
                ShowProgressBar = true,
                Buttons = { FATaskDialogButton.CancelButton },
            };
            progressDialog.SetProgressBarState(0, FATaskDialogProgressState.Indeterminate);
            progressDialog.XamlRoot = this;

            using var cts = new System.Threading.CancellationTokenSource();
            progressDialog.Closing += (s, ev) =>
            {
                // Any close that isn't us completing the run is a cancel request.
                if (!_benchmarkDone) cts.Cancel();
            };

            _benchmarkDone = false;
            System.Collections.Generic.List<AnimeJaNaiConfEditor.Services.LinuxBenchmark.Result>? results = null;
            Exception? failure = null;

            var runTask = Task.Run(async () =>
            {
                try
                {
                    void Report(string s) => Dispatcher.UIThread.Post(() => statusText.Text = s);
                    results = await vm.RunLinuxBenchmarkAsync(Report, cts.Token);
                }
                catch (OperationCanceledException) { /* user cancelled */ }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    _benchmarkDone = true;
                    Dispatcher.UIThread.Post(() => progressDialog.Hide());
                }
            });

            await progressDialog.ShowAsync();
            await runTask;

            if (cts.IsCancellationRequested && results == null)
                return;   // cancelled before completing
            if (failure != null)
            {
                await ShowInfoDialog("Benchmark failed", failure.Message);
                return;
            }
            if (results == null)
                return;

            await ShowLinuxBenchmarkResults(vm, results);
        }

        // Guards the progress dialog's Closing handler: true once the run finished
        // (so Hide() isn't misread as a cancel).
        private bool _benchmarkDone;

        private async Task ShowLinuxBenchmarkResults(
            MainWindowViewModel vm,
            System.Collections.Generic.List<AnimeJaNaiConfEditor.Services.LinuxBenchmark.Result> results)
        {
            var panel = new StackPanel { Width = 460 };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Playback fps on the {vm.LinuxBenchmarkBackendLabel} backend (2x upscale). " +
                       "Higher is better; 24+ fps generally means smooth real-time playback for that source.",
            });

            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            TextBlock Cell(string text, int row, int col, bool header = false) => new()
            {
                Text = text,
                FontWeight = header ? FontWeight.Bold : FontWeight.Normal,
                Margin = new Thickness(0, 2, 16, 2),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                [Grid.RowProperty] = row,
                [Grid.ColumnProperty] = col,
            };
            grid.Children.Add(Cell("Profile / Source -> 2x", 0, 0, header: true));
            grid.Children.Add(Cell("fps", 0, 1, header: true));

            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Children.Add(Cell($"{r.Profile}  {r.Label} -> {r.DstLabel}", i + 1, 0));
                var fpsText = r.Fps.HasValue
                    ? r.Fps.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    : (r.Error is { Length: > 0 } ? "error" : "-");
                grid.Children.Add(Cell(fpsText, i + 1, 1));
            }
            panel.Children.Add(grid);

            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                Opacity = .6,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Text = "Saved to benchmark.txt. Use \"Submit to Catalog\" to share these results.",
            });

            var td = new FATaskDialog
            {
                Title = "Benchmark results",
                Content = panel,
                Buttons = { FATaskDialogButton.OKButton },
            };
            td.XamlRoot = this;
            await td.ShowAsync();
        }

        private async void SubmitBenchmarkButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel) return;

            var benchmarkTxt = Path.Combine(MainWindowViewModel.DataDir, "benchmark.txt");
            if (!File.Exists(benchmarkTxt))
            {
                await ShowInfoDialog("No benchmark results yet",
                    "Run the benchmark first (\"Run Benchmarks\"), then come back here to submit the results.");
                return;
            }

            BenchmarkSubmission sub;
            try
            {
                sub = await Task.Run(() =>
                {
                    var s = BenchmarkSubmission.FromBenchmarkFile(benchmarkTxt);
                    s.FillSystemInfo(MainWindowViewModel.DataDir);
                    return s;
                });
            }
            catch (Exception ex)
            {
                await ShowInfoDialog("Couldn't read benchmark results", ex.Message);
                return;
            }

            if (!sub.HasResults)
            {
                await ShowInfoDialog("No benchmark results found",
                    "benchmark.txt didn't contain any results. Try running the benchmark again.");
                return;
            }

            // The dialog's state: name/note are two-way bound; the JSON preview is derived
            // reactively from them, so it always shows exactly what will be sent.
            var model = new SubmitDialogModel(sub);

            var preview = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MaxHeight = 260,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                FontSize = 11,
            };
            preview.Bind(TextBox.TextProperty, new Binding(nameof(SubmitDialogModel.Preview)) { Mode = BindingMode.OneWay });

            var submittedBy = new TextBox
            {
                Watermark = "Optional: a name or handle to credit you (blank = anonymous)",
                MaxLength = 60,
                Margin = new Thickness(0, 8, 0, 0),
            };
            submittedBy.Bind(TextBox.TextProperty, new Binding(nameof(SubmitDialogModel.SubmittedBy)) { Mode = BindingMode.TwoWay });

            var note = new TextBox
            {
                Watermark = "Optional note: anything notable not already captured above (e.g. undervolt, cooling, laptop on battery)",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxLength = 280,
                MinHeight = 60,
                MaxHeight = 90,
                Margin = new Thickness(0, 8, 0, 0),
            };
            note.Bind(TextBox.TextProperty, new Binding(nameof(SubmitDialogModel.Note)) { Mode = BindingMode.TwoWay });
            ScrollViewer.SetHorizontalScrollBarVisibility(note, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(note, ScrollBarVisibility.Auto);

            // Children inherit this DataContext, so the bindings above resolve against the model.
            var panel = new StackPanel { Width = 460, DataContext = model };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Below is the hardware data that will be sent to the community benchmark catalog. " +
                       "No account or login is required, and nothing else leaves your machine. " +
                       "You can optionally add your name and a note.",
            });
            panel.Children.Add(new HyperlinkButton
            {
                Content = "Browse the catalog first: " + BenchmarkSubmission.CatalogUrl,
                NavigateUri = new Uri(BenchmarkSubmission.CatalogUrl),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 12,
            });
            panel.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 2),
                Opacity = .6,
                FontSize = 11,
                Text = "Data to submit:",
            });
            panel.Children.Add(preview);
            panel.Children.Add(submittedBy);
            panel.Children.Add(note);

            const string submitResult = "submit";
            var td = new FATaskDialog
            {
                Title = "Submit benchmark to community catalog",
                Content = panel,
                ShowProgressBar = false,
                Buttons =
                {
                    new FATaskDialogButton("Submit", submitResult),
                    FATaskDialogButton.CancelButton,
                },
            };

            (bool ok, string message)? outcome = null;
            td.Closing += async (s, ev) =>
            {
                if (!Equals(ev.Result, submitResult)) return;
                var deferral = ev.GetDeferral();
                td.ShowProgressBar = true;
                outcome = await sub.SubmitAsync();   // sub's name/note are kept current by SubmitDialogModel
                deferral.Complete();
            };

            td.XamlRoot = this;
            await td.ShowAsync();

            if (outcome is { } result)
                await ShowInfoDialog(result.ok ? "Submitted" : "Submission failed", result.message);
        }

        // Backs the submit dialog: name/note are two-way bound; the JSON preview is an
        // ObservableAsPropertyHelper derived from them, so it stays in sync with what will be sent.
        private sealed class SubmitDialogModel : ReactiveObject
        {
            private string _submittedBy = "";
            private string _note = "";
            private readonly ObservableAsPropertyHelper<string> _preview;

            public SubmitDialogModel(BenchmarkSubmission sub)
            {
                _preview = this.WhenAnyValue(x => x.SubmittedBy, x => x.Note)
                    .Do(t =>
                    {
                        sub.SubmittedBy = (t.Item1 ?? "").Trim();
                        sub.Note = (t.Item2 ?? "").Trim();
                    })
                    .Select(_ => sub.ToPreviewJson())
                    .ToProperty(this, x => x.Preview);
            }

            public string SubmittedBy { get => _submittedBy; set => this.RaiseAndSetIfChanged(ref _submittedBy, value); }
            public string Note { get => _note; set => this.RaiseAndSetIfChanged(ref _note, value); }
            public string Preview => _preview.Value;
        }

        private async Task ShowInfoDialog(string title, string message)
        {
            var td = new FATaskDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
                Buttons = { FATaskDialogButton.OKButton },
            };
            td.XamlRoot = this;
            await td.ShowAsync();
        }
    }
}