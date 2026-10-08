using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using heicon.Models;
using heicon.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Pickers = Microsoft.Windows.Storage.Pickers;

namespace heicon
{
    public sealed partial class MainWindow : Window
    {
        private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _cancellation;
        private bool _isBusy;
        private bool _isScanning;
        private bool _closeRequested;
        private bool _suspendListUpdates;

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            AppWindow.Title = "Heicon";

            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(960 * scale), (int)(720 * scale)));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)(720 * scale);
                presenter.PreferredMinimumHeight = (int)(560 * scale);
            }

            AppWindow.Closing += AppWindow_Closing;
            Items.CollectionChanged += (_, _) =>
            {
                if (!_suspendListUpdates)
                {
                    UpdateListState();
                }
            };
            UpdateListState();
        }

        public ObservableCollection<ConversionItem> Items { get; } = new();

        #region Status visuals (used by x:Bind in the item template)

        public static string GetStatusGlyph(ConversionStatus status) => status switch
        {
            ConversionStatus.Converting => "",
            ConversionStatus.Done => "",
            ConversionStatus.Skipped => "",
            ConversionStatus.Failed => "",
            _ => "",
        };

        public static Brush GetStatusBrush(ConversionStatus status)
        {
            var key = status switch
            {
                ConversionStatus.Converting => "AccentTextFillColorPrimaryBrush",
                ConversionStatus.Done => "SystemFillColorSuccessBrush",
                ConversionStatus.Skipped => "SystemFillColorCautionBrush",
                ConversionStatus.Failed => "SystemFillColorCriticalBrush",
                _ => "TextFillColorSecondaryBrush",
            };

            return (Brush)Application.Current.Resources[key];
        }

        #endregion

        #region Adding files

        private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Pickers.FileOpenPicker(AppWindow.Id)
                {
                    SuggestedStartLocation = Pickers.PickerLocationId.PicturesLibrary,
                    ViewMode = Pickers.PickerViewMode.Thumbnail,
                    CommitButtonText = "Add",
                };
                foreach (var extension in FileCollector.Extensions)
                {
                    picker.FileTypeFilter.Add(extension);
                }

                var results = await picker.PickMultipleFilesAsync();
                if (results is { Count: > 0 })
                {
                    await AddPathsAsync(results.Select(r => r.Path).ToList());
                }
            }
            catch (Exception ex)
            {
                ShowInfo("Couldn't open the file picker", ex.Message, InfoBarSeverity.Error);
            }
        }

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Pickers.FolderPicker(AppWindow.Id)
                {
                    SuggestedStartLocation = Pickers.PickerLocationId.PicturesLibrary,
                    CommitButtonText = "Add folder",
                };

                var results = await picker.PickMultipleFoldersAsync();
                if (results is { Count: > 0 })
                {
                    await AddPathsAsync(results.Select(r => r.Path).ToList());
                }
            }
            catch (Exception ex)
            {
                ShowInfo("Couldn't open the folder picker", ex.Message, InfoBarSeverity.Error);
            }
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            if (_isBusy || _isScanning || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Heicon";
            e.DragUIOverride.IsGlyphVisible = true;
            DropHighlight.Visibility = Visibility.Visible;
        }

        private void RootGrid_DragLeave(object sender, DragEventArgs e)
        {
            DropHighlight.Visibility = Visibility.Collapsed;
        }

        private async void RootGrid_Drop(object sender, DragEventArgs e)
        {
            DropHighlight.Visibility = Visibility.Collapsed;
            if (_isBusy || _isScanning || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var deferral = e.GetDeferral();
            List<string> paths;
            try
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                paths = storageItems
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .ToList();
            }
            catch (Exception ex)
            {
                ShowInfo("Couldn't read the dropped items", ex.Message, InfoBarSeverity.Error);
                return;
            }
            finally
            {
                deferral.Complete();
            }

            await AddPathsAsync(paths);
        }

        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_isBusy || _isScanning || paths.Count == 0)
            {
                return;
            }

            var recurse = SubfoldersToggle.IsOn;
            _isScanning = true;
            UpdateControlState();
            SummaryText.Text = "Looking for .heic files…";

            try
            {
                var found = await Task.Run(() => FileCollector.Collect(paths, recurse)
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList());

                var added = 0;
                _suspendListUpdates = true;
                try
                {
                    foreach (var path in found)
                    {
                        if (_knownPaths.Add(path))
                        {
                            Items.Add(new ConversionItem(path));
                            added++;
                        }
                    }
                }
                finally
                {
                    _suspendListUpdates = false;
                    UpdateListState();
                }

                var alreadyListed = found.Count - added;
                if (found.Count == 0)
                {
                    ShowInfo("No HEIC files found", "Only .heic and .heif files are added. Other files are ignored.", InfoBarSeverity.Warning);
                }
                else if (added == 0)
                {
                    ShowInfo("Nothing new to add", "Those files are already in the list.", InfoBarSeverity.Informational);
                }
                else
                {
                    var message = $"Added {Plural(added, "file")}.";
                    if (alreadyListed > 0)
                    {
                        message += $" {Plural(alreadyListed, "file was", "files were")} already listed.";
                    }

                    ShowInfo(null, message, InfoBarSeverity.Success);
                }
            }
            catch (Exception ex)
            {
                ShowInfo("Couldn't add files", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _isScanning = false;
                SummaryText.Text = string.Empty;
                UpdateControlState();
            }
        }

        #endregion

        #region Removing files

        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControlState();

        private void ClearFinishedButton_Click(object sender, RoutedEventArgs e) =>
            RemoveItems(Items.Where(item => item.IsFinished).ToList());

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveItems(Items.ToList());
            StatusInfoBar.IsOpen = false;
        }

        private void RemoveSelected() => RemoveItems(FileList.SelectedItems.Cast<ConversionItem>().ToList());

        private void RemoveItems(IReadOnlyCollection<ConversionItem> items)
        {
            if (_isBusy || items.Count == 0)
            {
                return;
            }

            _suspendListUpdates = true;
            try
            {
                foreach (var item in items)
                {
                    Items.Remove(item);
                    _knownPaths.Remove(item.SourcePath);
                }
            }
            finally
            {
                _suspendListUpdates = false;
                UpdateListState();
            }
        }

        #endregion

        #region Options

        private void FormatButtons_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControlState();

        private void QualitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (QualitySlider is not null)
            {
                QualitySlider.Header = $"JPEG quality: {(int)e.NewValue}";
            }
        }

        private void InPlaceCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (InPlaceHint is null)
            {
                return;
            }

            InPlaceHint.Foreground = (Brush)Application.Current.Resources[
                InPlaceCheckBox.IsChecked == true ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
        }

        private bool IsJpegSelected => FormatButtons.SelectedIndex != 1;

        private ConversionOptions ReadOptions() => new(
            IsJpegSelected ? OutputFormat.Jpeg : OutputFormat.Png,
            (int)QualitySlider.Value,
            InPlaceCheckBox.IsChecked == true);

        #endregion

        #region Converting

        private async void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _isScanning)
            {
                return;
            }

            var work = Items
                .Where(item => item.Status is ConversionStatus.Pending or ConversionStatus.Failed)
                .ToList();
            if (work.Count == 0)
            {
                ShowInfo(null, "Every file in the list has already been converted or skipped.", InfoBarSeverity.Informational);
                return;
            }

            var options = ReadOptions();
            if (options.DeleteSource && !await ConfirmDeleteAsync(work.Count))
            {
                return;
            }

            StatusInfoBar.IsOpen = false;
            _cancellation = new CancellationTokenSource();
            _isBusy = true;
            UpdateControlState();

            foreach (var item in work)
            {
                item.Status = ConversionStatus.Pending;
                item.Message = "Waiting";
            }

            var processed = 0;
            ConversionProgress.Value = 0;
            ConversionProgress.Maximum = work.Count;
            ConversionProgress.Visibility = Visibility.Visible;
            UpdateRunSummary(processed, work);

            var dispatcher = DispatcherQueue;
            var cancelled = false;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                CancellationToken = _cancellation.Token,
            };

            try
            {
                // Each conversion runs on a pool thread; results are applied on the UI thread
                // so the bound item properties are only ever touched there.
                await Parallel.ForEachAsync(work, parallelOptions, (item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    dispatcher.TryEnqueue(() =>
                    {
                        item.Status = ConversionStatus.Converting;
                        item.Message = "Converting…";
                    });

                    var result = HeicConverter.Convert(item.SourcePath, options);

                    dispatcher.TryEnqueue(() =>
                    {
                        item.Status = result.Status;
                        item.Message = result.Message;
                        item.OutputPath = result.OutputPath;
                        processed++;
                        ConversionProgress.Value = processed;
                        UpdateRunSummary(processed, work);
                    });

                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                ShowInfo("Conversion stopped", ex.Message, InfoBarSeverity.Error);
            }

            // Let any queued UI updates from the workers run before summarizing.
            var tcs = new TaskCompletionSource();
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => tcs.SetResult());
            await tcs.Task;

            foreach (var item in work.Where(item => item.Status == ConversionStatus.Pending))
            {
                item.Message = cancelled ? "Cancelled" : "Ready";
            }

            _cancellation.Dispose();
            _cancellation = null;
            _isBusy = false;
            ConversionProgress.Visibility = Visibility.Collapsed;
            SummaryText.Text = string.Empty;
            UpdateControlState();
            ShowRunResult(work, cancelled);

            if (_closeRequested)
            {
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellation is { IsCancellationRequested: false })
            {
                _cancellation.Cancel();
                SummaryText.Text = "Cancelling — finishing files already in progress…";
                CancelButton.IsEnabled = false;
            }
        }

        private async Task<bool> ConfirmDeleteAsync(int count)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Delete originals after converting?",
                Content = $"Convert in place is on. After each file is converted successfully, its original .heic will be permanently deleted ({Plural(count, "file")}). This can't be undone.",
                PrimaryButtonText = "Convert and delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private void UpdateRunSummary(int processed, IReadOnlyCollection<ConversionItem> work)
        {
            var done = work.Count(item => item.Status == ConversionStatus.Done);
            var skipped = work.Count(item => item.Status == ConversionStatus.Skipped);
            var failed = work.Count(item => item.Status == ConversionStatus.Failed);
            SummaryText.Text = $"{processed} of {work.Count} processed  ·  {done} converted  ·  {skipped} skipped  ·  {failed} failed";
        }

        private void ShowRunResult(IReadOnlyCollection<ConversionItem> work, bool cancelled)
        {
            var done = work.Count(item => item.Status == ConversionStatus.Done);
            var skipped = work.Count(item => item.Status == ConversionStatus.Skipped);
            var failed = work.Count(item => item.Status == ConversionStatus.Failed);
            var remaining = work.Count(item => item.Status == ConversionStatus.Pending);

            var parts = new List<string> { $"Converted {Plural(done, "file")}" };
            if (skipped > 0)
            {
                parts.Add($"{skipped} skipped (output already existed)");
            }

            if (failed > 0)
            {
                parts.Add($"{failed} failed");
            }

            if (remaining > 0)
            {
                parts.Add($"{remaining} not started");
            }

            var severity = failed > 0 ? InfoBarSeverity.Error
                : cancelled || skipped > 0 ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;

            ShowInfo(cancelled ? "Cancelled" : "Finished", string.Join(" · ", parts) + ".", severity);
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (!_isBusy)
            {
                return;
            }

            // Don't tear down mid-write: stop scheduling new files, let the current ones
            // finish, then close once the run winds down.
            args.Cancel = true;
            _closeRequested = true;
            CancelButton_Click(this, new RoutedEventArgs());
        }

        #endregion

        #region Helpers

        private void UpdateListState()
        {
            EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = Items.Count == 0 ? "No files" : Plural(Items.Count, "file");
            UpdateControlState();
        }

        private void UpdateControlState()
        {
            // Option controls raise change events while InitializeComponent is still wiring up names.
            if (ConvertButton is null)
            {
                return;
            }

            var idle = !_isBusy && !_isScanning;

            AddFilesButton.IsEnabled = idle;
            AddFolderButton.IsEnabled = idle;
            RemoveSelectedButton.IsEnabled = idle && FileList.SelectedItems.Count > 0;
            ClearFinishedButton.IsEnabled = idle && Items.Any(item => item.IsFinished);
            ClearAllButton.IsEnabled = idle && Items.Count > 0;

            FormatButtons.IsEnabled = idle;
            QualitySlider.IsEnabled = idle && IsJpegSelected;
            SubfoldersToggle.IsEnabled = idle;
            InPlaceCheckBox.IsEnabled = idle;

            ConvertButton.IsEnabled = idle && Items.Count > 0;
            CancelButton.Visibility = _isBusy ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = _isBusy;
        }

        private void ShowInfo(string? title, string message, InfoBarSeverity severity)
        {
            StatusInfoBar.Title = title ?? string.Empty;
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;
        }

        private static string Plural(int count, string singular, string? plural = null) =>
            $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        #endregion
    }
}
