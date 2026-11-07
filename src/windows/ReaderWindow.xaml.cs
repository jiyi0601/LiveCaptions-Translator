using System.Collections.ObjectModel;
using LiveCaptionsTranslator.speech;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.transcript;
using LiveCaptionsTranslator.utils;
using Microsoft.Win32;
namespace LiveCaptionsTranslator;

public partial class ReaderWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<ReaderEntry> Entries { get; } = new();
    private readonly Dictionary<long, ReaderEntry> byId = new();
    private readonly List<TranslationHistoryEntry> initialChanges = new();
    private readonly ReaderWindowState saved = Translator.Setting.ReaderWindow;
    private ScrollViewer? scroll;
    private bool initialized, loadingOlder, exhausted, programmatic, closed;
    private bool following = true;
    private long scrollIntent;
    private bool speakerControlsReady;
    public double OriginalFontSize => saved.OriginalFontSize;
    public double TranslatedFontSize => saved.TranslatedFontSize;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ReaderWindow()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Entries;
        TranscriptList.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(Transcript_ScrollBar));
        TranscriptList.RequestBringIntoView += (_, e) =>
        {
            if (!following && !programmatic) e.Handled = true;
        };
        ReloadSpeakerDevices();
        SpeakerToggle.IsChecked = saved.SpeakerEnabled;
        speakerControlsReady = true;
        Translator.SpeechPreviewChanged += SpeechPreviewChanged;
        Translator.SpeakerModeChanged += SpeakerModeChanged;
        SpeechPreviewChanged(Translator.SpeechPreview);
        ParagraphLengthBox.Items.Add(new ComboBoxItem { Content = "短（30–60 词）", Tag = ParagraphLength.Short });
        ParagraphLengthBox.Items.Add(new ComboBoxItem { Content = "适中（40–80 词）", Tag = ParagraphLength.Balanced });
        ParagraphLengthBox.Items.Add(new ComboBoxItem { Content = "长（60–120 词）", Tag = ParagraphLength.Long });
        ParagraphLengthBox.SelectedItem = ParagraphLengthBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (ParagraphLength)i.Tag == saved.ParagraphLength);
        if (ParagraphLengthBox.SelectedItem == null) ParagraphLengthBox.SelectedIndex = 1;
        foreach (int seconds in ParagraphSegmenter.PauseOptions)
            ParagraphPauseBox.Items.Add(new ComboBoxItem { Content = seconds == ParagraphSegmenter.DefaultPauseSeconds ? seconds + " 秒（默认）" : seconds + " 秒", Tag = seconds });
        ParagraphPauseBox.SelectedItem = ParagraphPauseBox.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == saved.ParagraphPauseSeconds);
        Width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var previous = new Rect(saved.Left, saved.Top, Width, Height);
        if (saved.Left != -1 && saved.Top != -1 && desktop.IntersectsWith(previous))
        {
            Left = Math.Clamp(saved.Left, desktop.Left, desktop.Right - Width);
            Top = Math.Clamp(saved.Top, desktop.Top, desktop.Bottom - Height);
        }
        else
        {
            Left = Math.Max(desktop.Left, SystemParameters.WorkArea.Right - Width - 24);
            Top = Math.Max(desktop.Top, SystemParameters.WorkArea.Top + 32);
        }
        Topmost = saved.Topmost;
        PinButton.Content = Topmost ? "已置顶" : "置顶";
        if (RuntimeOptions.Demo) Title += " · 演示";
        Translator.TranscriptChanged += EntryChanged;
        Translator.CaptureStatusChanged += CaptureStatusChanged;
        SQLiteHistoryLogger.HistoryCleared += HistoryCleared;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        scroll = FindChild<ScrollViewer>(TranscriptList);
        try
        {
            var rows = await SQLiteHistoryLogger.Store.ReadParagraphsBeforeAsync(null, 200);
            if (closed) return;
            foreach (var row in rows) Upsert(row);
            exhausted = rows.Select(r => r.GroupId).Distinct().Count() < 200;
            foreach (var row in initialChanges) Upsert(row);
            initialChanges.Clear();
            initialized = true;
            StatusText.Text = Translator.CaptureStatus;
            RefreshFooter();
            SchedulePosition(null);
        }
        catch (Exception ex) { StatusText.Text = "读取历史失败：" + ex.Message; }
    }
    private void EntryChanged(TranslationHistoryEntry entry)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (closed) return;
            if (!initialized) { initialChanges.Add(entry); return; }
            List<TranslationHistoryEntry>? missing = null;
            if (!byId.ContainsKey(entry.GroupId) && entry.Id != entry.GroupId)
            {
                try { missing = await SQLiteHistoryLogger.Store.ReadParagraphAsync(entry.GroupId); }
                catch (Exception ex) { StatusText.Text = "读取段落失败：" + ex.Message; }
            }
            if (closed) return;
            var anchor = following ? null : CaptureAnchor();
            if (missing != null) foreach (var part in missing) Upsert(part);
            Upsert(entry);
            RefreshFooter();
            SchedulePosition(anchor);
        });
    }
    private void ReloadSpeakerDevices()
    {
        bool wasReady = speakerControlsReady;
        speakerControlsReady = false;
        try
        {
            var devices = LoopbackSpeechSource.Devices().ToList();
            if (!devices.Any(d => d.Id == saved.SpeakerDeviceId))
                devices.Add(new(saved.SpeakerDeviceId, "已保存的播放设备（当前不可用）"));
            SpeakerDeviceBox.ItemsSource = devices;
            SpeakerDeviceBox.SelectedItem = devices.First(d => d.Id == saved.SpeakerDeviceId);
        }
        catch (Exception ex) { StatusText.Text = "无法列出播放设备：" + ex.Message; }
        finally { speakerControlsReady = wasReady; }
    }
    private void SpeakerDevice_Opened(object sender, EventArgs e) => ReloadSpeakerDevices();
    private void SpeakerToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (speakerControlsReady && !RuntimeOptions.Demo) Translator.ConfigureSpeaker(SpeakerToggle.IsChecked == true);
    }
    private void SpeakerDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (speakerControlsReady && SpeakerDeviceBox.SelectedItem is PlaybackDevice device)
            Translator.ConfigureSpeaker(SpeakerToggle.IsChecked == true, device.Id);
    }
    private void SpeakerReset_Click(object sender, RoutedEventArgs e)
    {
        if (SpeakerToggle.IsChecked == true && !RuntimeOptions.Demo) Translator.ConfigureSpeaker(true);
    }
    private void SpeakerModeChanged(bool enabled) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        speakerControlsReady = false;
        SpeakerToggle.IsChecked = enabled;
        speakerControlsReady = true;
    });
    private void SpeechPreviewChanged(string text) => Dispatcher.BeginInvoke(() =>
    {
        if (closed) return;
        var anchor = following ? null : CaptureAnchor();
        SpeechPreviewText.Text = text;
        SpeechPreviewPanel.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (initialized) SchedulePosition(anchor);
    });
    private void CaptureStatusChanged(string status) => Dispatcher.BeginInvoke(() =>
    {
        if (!closed) StatusText.Text = status;
    });
    private void HistoryCleared() => Dispatcher.BeginInvoke(() =>
    {
        Entries.Clear(); byId.Clear(); initialChanges.Clear(); exhausted = true;
        SetFollowing(true); RefreshFooter();
    });
    private void Upsert(TranslationHistoryEntry entry)
    {
        if (byId.TryGetValue(entry.GroupId, out var existing)) { existing.Update(entry); return; }
        var row = new ReaderEntry(entry);
        byId.Add(entry.GroupId, row);
        int low = 0, high = Entries.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (Entries[mid].Id < entry.GroupId) low = mid + 1; else high = mid;
        }
        Entries.Insert(low, row);
    }
    private sealed record Anchor(long Id, double Y);
    private Anchor? CaptureAnchor()
    {
        foreach (var row in Entries)
        {
            if (TranscriptList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            {
                double y = container.TransformToAncestor(TranscriptList).Transform(new Point()).Y;
                if (y + container.ActualHeight > 0 && y < TranscriptList.ActualHeight)
                    return new Anchor(row.Id, y);
            }
        }
        return null;
    }
    private void SchedulePosition(Anchor? anchor)
    {
        programmatic = true;
        long intent = scrollIntent;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (closed || intent != scrollIntent) { programmatic = false; return; }
            TranscriptList.UpdateLayout();
            if (following) scroll?.ScrollToEnd();
            else if (anchor != null && byId.TryGetValue(anchor.Id, out var item))
            {
                if (TranscriptList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement)
                {
                    TranscriptList.ScrollIntoView(item);
                    TranscriptList.UpdateLayout();
                }
                if (TranscriptList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement container && scroll != null)
                {
                    double y = container.TransformToAncestor(TranscriptList).Transform(new Point()).Y;
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + y - anchor.Y);
                }
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => programmatic = false);
        });
    }
    private void SetFollowing(bool enabled)
    {
        following = enabled;
        LatestButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        FollowText.Text = enabled ? "自动跟随最新" : "正在回看 · 新内容继续保存";
    }
    private bool AtBottom => scroll == null || scroll.ScrollableHeight - scroll.VerticalOffset < 24;
    private async void Transcript_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!initialized || closed || programmatic || loadingOlder) return;
        // Layout, focus and the provisional panel can all move the ScrollViewer.
        // Only explicit user input may re-enable following.
        if (scroll?.VerticalOffset < 80 && !following && !exhausted) await LoadOlderAsync();
    }
    private async Task LoadOlderAsync()
    {
        if (loadingOlder || exhausted || Entries.Count == 0) return;
        loadingOlder = true;
        var anchor = CaptureAnchor();
        try
        {
            var rows = await SQLiteHistoryLogger.Store.ReadParagraphsBeforeAsync(Entries[0].Id, 100);
            if (closed) return;
            foreach (var row in rows) Upsert(row);
            exhausted = rows.Select(r => r.GroupId).Distinct().Count() < 100;
            RefreshFooter();
            SchedulePosition(anchor);
        }
        catch (Exception ex) { StatusText.Text = "加载较早记录失败：" + ex.Message; }
        finally { loadingOlder = false; }
    }
    private void Transcript_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ChangeFont(e.Delta > 0 ? 1 : -1); e.Handled = true; return;
        }
        MarkUserScroll(upward: e.Delta > 0);
    }
    private void Transcript_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp or Key.Home) MarkUserScroll(upward: true);
        if (e.Key is Key.Down or Key.PageDown or Key.End) MarkUserScroll(upward: false);
        if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control)
        { SetFollowing(false); scroll?.ScrollToTop(); e.Handled = true; }
        if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control)
        { Latest_Click(sender, e); e.Handled = true; }
    }
    private void MarkUserScroll(bool upward)
    {
        long intent = ++scrollIntent;
        SetFollowing(false);
        if (!upward)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (!closed && scrollIntent == intent && AtBottom) SetFollowing(true);
            });
    }
    private void Transcript_ScrollBar(object sender, ScrollEventArgs e) =>
        MarkUserScroll(e.ScrollEventType is ScrollEventType.ThumbTrack or ScrollEventType.SmallDecrement
            or ScrollEventType.LargeDecrement or ScrollEventType.First);
    private void Latest_Click(object sender, RoutedEventArgs e)
    {
        scrollIntent++;
        SetFollowing(true); SchedulePosition(null);
    }
    private void ParagraphPause_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ParagraphPauseBox.SelectedItem is ComboBoxItem { Tag: int seconds })
            Translator.SetParagraphPauseSeconds(seconds);
    }
    private void ParagraphLength_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ParagraphLengthBox.SelectedItem is ComboBoxItem { Tag: ParagraphLength length })
            Translator.SetParagraphLength(length);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost; saved.Topmost = Topmost;
        PinButton.Content = Topmost ? "已置顶" : "置顶";
        Translator.Setting.Save();
    }
    private void Smaller_Click(object sender, RoutedEventArgs e) => ChangeFont(-1);
    private void Larger_Click(object sender, RoutedEventArgs e) => ChangeFont(1);
    private void ChangeFont(int step)
    {
        var anchor = following ? null : CaptureAnchor();
        saved.OriginalFontSize = Math.Clamp(saved.OriginalFontSize + step, 11, 32);
        saved.TranslatedFontSize = saved.OriginalFontSize + 3;
        PropertyChanged?.Invoke(this, new(nameof(OriginalFontSize)));
        PropertyChanged?.Invoke(this, new(nameof(TranslatedFontSize)));
        Translator.Setting.Save(); SchedulePosition(anchor);
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReaderEntry item })
        {
            try { Clipboard.SetText(item.CopyText); StatusText.Text = "已复制这一条双语记录"; }
            catch { StatusText.Text = "剪贴板暂不可用，请重试"; }
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV（含说话人） (*.csv)|*.csv|CSV（原五字段） (*.csv)|*.csv",
            FilterIndex = saved.SpeakerEnabled ? 1 : 2,
            FileName = "双语记录_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await SQLiteHistoryLogger.Store.ExportAsync(dialog.FileName, includeSpeakers: dialog.FilterIndex == 1);
            StatusText.Text = "全部历史已导出：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex) { StatusText.Text = "导出失败：" + ex.Message; }
    }
    private void RefreshFooter()
    {
        CountText.Text = "已载入 " + Entries.Count + " 条" + (exhausted ? " · 已到最早记录" : " · 上翻加载更多");
        EmptyText.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        Translator.TranscriptChanged -= EntryChanged;
        Translator.SpeechPreviewChanged -= SpeechPreviewChanged;
        Translator.SpeakerModeChanged -= SpeakerModeChanged;
        Translator.CaptureStatusChanged -= CaptureStatusChanged;
        SQLiteHistoryLogger.HistoryCleared -= HistoryCleared;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        saved.Left = bounds.Left; saved.Top = bounds.Top;
        saved.Width = bounds.Width; saved.Height = bounds.Height;
        saved.Topmost = Topmost;
        Translator.Setting.Save();
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var nested = FindChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
