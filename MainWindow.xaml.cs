using FFMpegCore;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace LosslessCutter
{
    public partial class MainWindow : Window
    {
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".webm",
            ".m4v", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg",
            ".3gp", ".ogv", ".asf", ".divx", ".vob", ".rm", ".rmvb"
        };

        private static readonly string SupportedFilter =
            "すべてのサポート済み動画|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv;*.webm;" +
            "*.m4v;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.ogv;*.asf;*.divx;*.vob;*.rm;*.rmvb|" +
            "すべてのファイル|*.*";

        private string? _videoPath;
        private TimeSpan _videoDuration;
        private readonly List<TimeSpan> _slicePoints = new();
        private readonly HashSet<(TimeSpan Start, TimeSpan End)> _cutRanges = new();
        private bool _isSliderDragging;
        private bool _wasPlayingBeforeDrag;
        private bool _isPlaying;
        private readonly DispatcherTimer _timer;

        public MainWindow()
        {
            InitializeComponent();
            sldTimeline.AddHandler(Thumb.DragStartedEvent,
                new DragStartedEventHandler(sldTimeline_DragStarted));
            sldTimeline.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler(sldTimeline_DragCompleted));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += Timer_Tick;
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            MovieImage.Close();
            base.OnClosed(e);
        }

        // ─────────────────────────────── Drag & Drop ────────────────────────────────

        private void MovieImage_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files?.Length > 0 &&
                    SupportedExtensions.Contains(IOPath.GetExtension(files[0])))
                {
                    e.Effects = DragDropEffects.Copy;
                }
            }
            e.Handled = true;
        }

        private async void MovieImage_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files?.Length > 0)
                    await LoadVideoAsync(files[0]);
            }
        }

        // ──────────────────────────────── Video Loading ──────────────────────────────

        private async Task LoadVideoAsync(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                SetStatus("動画を読み込み中...");
                SetControlsEnabled(false);
                StopPlayback();

                _videoPath = path;
                _slicePoints.Clear();
                _cutRanges.Clear();

                var info = await FFProbe.AnalyseAsync(path);
                _videoDuration = info.Duration;

                var fileInfo = new FileInfo(path);
                txtFileInfo.Text =
                    $"{IOPath.GetFileName(path)}  |  " +
                    $"Duration: {_videoDuration:hh\\:mm\\:ss}  |  " +
                    $"Size: {fileInfo.Length / 1024.0 / 1024.0:F1} MB";

                sldTimeline.Maximum = _videoDuration.TotalSeconds;
                sldTimeline.Value = 0;
                txtDropHint.Visibility = Visibility.Collapsed;
                UpdateSliceInfo();

                // Open in MediaElement — Play then Pause to render the first frame
                MovieImage.Source = new Uri(path);
                MovieImage.Play();
                MovieImage.Pause();

                SetControlsEnabled(true);
                DrawSliceCanvas();
                SetStatus("クリックで再生");
            }
            catch (Exception ex)
            {
                SetStatus($"エラー: {ex.Message}");
                MessageBox.Show($"動画の読み込みに失敗しました:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ─────────────────────────── MediaElement Events ─────────────────────────────

        private void MovieImage_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (MovieImage.NaturalDuration.HasTimeSpan)
                sldTimeline.Maximum = MovieImage.NaturalDuration.TimeSpan.TotalSeconds;
        }

        private void MovieImage_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            _timer.Stop();
            MovieImage.Position = TimeSpan.Zero;
            sldTimeline.Value = 0;
            DrawSliceCanvas();
            SetStatus("再生完了 — クリックで再再生");
        }

        private void MovieImage_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            SetStatus($"メディアエラー: {e.ErrorException.Message}");
        }

        // ─────────────────────────── Playback Control ────────────────────────────────

        private void MovieArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_videoPath == null) return;
            TogglePlayPause();
            e.Handled = true;
        }

        private void TogglePlayPause()
        {
            if (_isPlaying) PausePlayback();
            else ResumePlayback();
        }

        private void ResumePlayback()
        {
            MovieImage.Play();
            _isPlaying = true;
            _timer.Start();
            SetStatus("再生中...");
        }

        private void PausePlayback()
        {
            MovieImage.Pause();
            _isPlaying = false;
            _timer.Stop();
            SetStatus($"一時停止: {FormatTime(MovieImage.Position)}");
        }

        private void StopPlayback()
        {
            if (MovieImage.Source != null)
                MovieImage.Stop();
            _isPlaying = false;
            _timer.Stop();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_videoPath == null || _isSliderDragging) return;
            sldTimeline.Value = MovieImage.Position.TotalSeconds;
            SetStatus($"再生中: {FormatTime(MovieImage.Position)}");
        }

        private void MovieArea_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_videoPath == null) return;

            // 5 seconds per standard wheel notch (delta = ±120)
            double newSec = Math.Clamp(
                MovieImage.Position.TotalSeconds - (e.Delta / 120.0) * 5.0,
                0, _videoDuration.TotalSeconds);

            MovieImage.Position = TimeSpan.FromSeconds(newSec);
            sldTimeline.Value   = newSec;
            e.Handled = true;
        }

        // ──────────────────────────────── Slice Canvas ───────────────────────────────

        private List<TimeSpan> GetBoundaries()
        {
            var b = new List<TimeSpan> { TimeSpan.Zero };
            b.AddRange(_slicePoints.OrderBy(t => t));
            b.Add(_videoDuration);
            return b;
        }

        private void DrawSliceCanvas()
        {
            SliceCanvas.Children.Clear();
            if (_videoDuration.TotalSeconds <= 0) return;

            double w = SliceCanvas.ActualWidth;
            double h = SliceCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            var boundaries = GetBoundaries();

            // Cut region fills (light clay)
            for (int i = 0; i < boundaries.Count - 1; i++)
            {
                if (!_cutRanges.Contains((boundaries[i], boundaries[i + 1]))) continue;
                double x1 = (boundaries[i].TotalSeconds / _videoDuration.TotalSeconds) * w;
                double x2 = (boundaries[i + 1].TotalSeconds / _videoDuration.TotalSeconds) * w;
                var rect = new Rectangle
                {
                    Width = Math.Max(0, x2 - x1),
                    Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(210, 185, 158, 110))
                };
                Canvas.SetLeft(rect, x1);
                Canvas.SetTop(rect, 0);
                SliceCanvas.Children.Add(rect);
            }

            // Slice point markers (orange)
            foreach (var pt in _slicePoints)
            {
                double x = (pt.TotalSeconds / _videoDuration.TotalSeconds) * w;
                SliceCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = h,
                    Stroke = Brushes.Orange, StrokeThickness = 2
                });
            }

            // Current position marker (red)
            double posX = (sldTimeline.Value / _videoDuration.TotalSeconds) * w;
            SliceCanvas.Children.Add(new Line
            {
                X1 = posX, Y1 = 0, X2 = posX, Y2 = h,
                Stroke = Brushes.Red, StrokeThickness = 2
            });
        }

        private void SliceCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSliceCanvas();

        private void SliceCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            if (_videoPath == null || _videoDuration.TotalSeconds <= 0) return;

            double w = SliceCanvas.ActualWidth;
            if (w <= 0) return;

            var clickTime = TimeSpan.FromSeconds(
                e.GetPosition(SliceCanvas).X / w * _videoDuration.TotalSeconds);

            var boundaries = GetBoundaries();
            for (int i = 0; i < boundaries.Count - 1; i++)
            {
                if (clickTime < boundaries[i] || clickTime >= boundaries[i + 1]) continue;

                var range = (boundaries[i], boundaries[i + 1]);
                bool nowCut;
                if (_cutRanges.Contains(range)) { _cutRanges.Remove(range); nowCut = false; }
                else                            { _cutRanges.Add(range);    nowCut = true;  }

                DrawSliceCanvas();
                UpdateSliceInfo();
                SetStatus(nowCut
                    ? $"カット領域を設定: {FormatTime(range.Item1)} → {FormatTime(range.Item2)}"
                    : $"カット領域を解除: {FormatTime(range.Item1)} → {FormatTime(range.Item2)}");
                e.Handled = true;
                break;
            }
        }

        // ────────────────────────────── Timeline Slider ──────────────────────────────

        private void sldTimeline_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isSliderDragging = true;
            _wasPlayingBeforeDrag = _isPlaying;
            if (_isPlaying) PausePlayback();
        }

        private void sldTimeline_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isSliderDragging = false;
            MovieImage.Position = TimeSpan.FromSeconds(sldTimeline.Value);
            DrawSliceCanvas();
            if (_wasPlayingBeforeDrag) ResumePlayback();
            else SetStatus($"位置: {FormatTime(TimeSpan.FromSeconds(sldTimeline.Value))}");
        }

        private void sldTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSliderDragging && _videoPath != null)
                MovieImage.Position = TimeSpan.FromSeconds(sldTimeline.Value);
            DrawSliceCanvas();
        }

        // ──────────────────────────── Slice / Reset ──────────────────────────────────

        private void btnSlice_Click(object sender, RoutedEventArgs e)
        {
            if (_videoPath == null) return;
            var pos = TimeSpan.FromSeconds(sldTimeline.Value);

            if (_slicePoints.Any(p => (p - pos).Duration() < TimeSpan.FromMilliseconds(200)))
                return;

            // If pos is inside a cut region → deselect that region and insert the point
            var inCut = _cutRanges.Where(r => pos > r.Start && pos < r.End).ToList();
            if (inCut.Count > 0)
            {
                foreach (var r in inCut) _cutRanges.Remove(r);
                _slicePoints.Add(pos);
                UpdateSliceInfo();
                DrawSliceCanvas();
                SetStatus($"カット解除 + スライス追加: {FormatTime(pos)}");
            }
            else
            {
                // Normal path: add point (no cut ranges to split since pos is not inside one)
                _slicePoints.Add(pos);
                UpdateSliceInfo();
                DrawSliceCanvas();
                SetStatus($"スライス追加: {FormatTime(pos)}");
            }
        }

        private void btnReset_Click(object sender, RoutedEventArgs e)
        {
            _slicePoints.Clear();
            _cutRanges.Clear();
            UpdateSliceInfo();
            DrawSliceCanvas();
            SetStatus("スライスポイントと選択領域をリセットしました");
        }

        private void UpdateSliceInfo()
        {
            if (_slicePoints.Count == 0 && _cutRanges.Count == 0)
            {
                txtSliceInfo.Text = "スライスポイント: なし";
                return;
            }
            int total = _slicePoints.Count + 1;
            int keep  = total - _cutRanges.Count;
            var times = _slicePoints.OrderBy(t => t).Select(FormatTime);
            txtSliceInfo.Text =
                $"スライスポイント: {_slicePoints.Count} 件 / 削除: {_cutRanges.Count} 領域 / 保持: {keep} セグメント" +
                (_slicePoints.Count > 0 ? $"  [ {string.Join(" | ", times)} ]" : "");
        }

        // ───────────────────────────── Lossless Cut ──────────────────────────────────

        private async void btnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_videoPath == null) return;

            if (_cutRanges.Count == 0)
            {
                MessageBox.Show("削除する領域をダブルクリックで選択してください。",
                    "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Keep segments = all regions NOT in _cutRanges
            var boundaries = GetBoundaries();
            var keepSegments = new List<(TimeSpan Start, TimeSpan End)>();
            for (int i = 0; i < boundaries.Count - 1; i++)
            {
                if (!_cutRanges.Contains((boundaries[i], boundaries[i + 1])))
                    keepSegments.Add((boundaries[i], boundaries[i + 1]));
            }

            if (keepSegments.Count == 0)
            {
                MessageBox.Show("保持する領域がありません。",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var inputExt = IOPath.GetExtension(_videoPath!);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = SupportedFilter,
                FileName = IOPath.GetFileNameWithoutExtension(_videoPath) + "_cut" + inputExt,
                InitialDirectory = IOPath.GetDirectoryName(_videoPath)
            };
            if (dialog.ShowDialog() != true) return;
            var outputPath = dialog.FileName;

            PausePlayback();
            SetControlsEnabled(false);

            try
            {
                if (keepSegments.Count == 1)
                {
                    // Single keep segment — simple seek-and-cut
                    var (start, end) = keepSegments[0];
                    SetStatus($"処理中: {FormatTime(start)} → {FormatTime(end)}");
                    await Task.Run(() =>
                    {
                        FFMpegArguments
                            .FromFileInput(_videoPath, true, opt => opt.Seek(start))
                            .OutputToFile(outputPath, true, opt => opt
                                .WithDuration(end - start)
                                .CopyChannel()
                                .WithCustomArgument("-avoid_negative_ts make_zero"))
                            .ProcessSynchronously();
                    });
                }
                else
                {
                    // Multiple keep segments — cut each to temp, then concat
                    var tempFiles = new List<string>();
                    string? concatFile = null;
                    try
                    {
                        var tempDir = IOPath.GetTempPath();
                        for (int i = 0; i < keepSegments.Count; i++)
                        {
                            var (start, end) = keepSegments[i];
                            var tmp = IOPath.Combine(tempDir, $"lc_{Guid.NewGuid():N}.mp4");
                            tempFiles.Add(tmp);
                            SetStatus($"処理中 {i + 1}/{keepSegments.Count}: {FormatTime(start)} → {FormatTime(end)}");
                            await Task.Run(() =>
                            {
                                FFMpegArguments
                                    .FromFileInput(_videoPath, true, opt => opt.Seek(start))
                                    .OutputToFile(tmp, true, opt => opt
                                        .WithDuration(end - start)
                                        .CopyChannel()
                                        .WithCustomArgument("-avoid_negative_ts make_zero"))
                                    .ProcessSynchronously();
                            });
                        }

                        concatFile = IOPath.Combine(IOPath.GetTempPath(), $"lc_concat_{Guid.NewGuid():N}.txt");
                        await File.WriteAllLinesAsync(concatFile,
                            tempFiles.Select(f => $"file '{f.Replace('\\', '/')}'"));

                        SetStatus("セグメントを結合中...");
                        await Task.Run(() =>
                        {
                            FFMpegArguments
                                .FromFileInput(concatFile, false, opt => opt
                                    .WithCustomArgument("-f concat -safe 0"))
                                .OutputToFile(outputPath, true, opt => opt.CopyChannel())
                                .ProcessSynchronously();
                        });
                    }
                    finally
                    {
                        if (concatFile != null && File.Exists(concatFile)) File.Delete(concatFile);
                        foreach (var f in tempFiles.Where(File.Exists)) File.Delete(f);
                    }
                }

                SetStatus($"完了: {outputPath}");
                MessageBox.Show($"ロスレスカット完了!\n{outputPath}",
                    "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SetStatus($"エラー: {ex.Message}");
                MessageBox.Show($"変換エラー:\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetControlsEnabled(true);
            }
        }

        // ─────────────────────────────── Helpers ─────────────────────────────────────

        private static string FormatTime(TimeSpan t)
            => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

        private void SetStatus(string message) => txtStatus.Text = message;

        private void SetControlsEnabled(bool enabled)
        {
            sldTimeline.IsEnabled = enabled;
            btnConvert.IsEnabled = enabled;
            btnSlice.IsEnabled = enabled;
            btnReset.IsEnabled = enabled;
        }
    }
}
