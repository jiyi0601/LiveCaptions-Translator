using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    public double OriginalFontSize => saved.OriginalFontSize;
    public double TranslatedFontSize => saved.TranslatedFontSize;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ReaderWindow()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Entries;
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
            var rows = await SQLiteHistoryLogger.Store.ReadBeforeAsync(null, 200);
            if (closed) return;
            foreach (var row in rows) Upsert(row);
            exhausted = rows.Count < 200;
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
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            if (!initialized) { initialChanges.Add(entry); return; }
            var anchor = following ? null : CaptureAnchor();
            Upsert(entry);
            RefreshFooter();
            SchedulePosition(anchor);
        });
    }
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
        if (byId.TryGetValue(entry.Id, out var existing)) { existing.Update(entry); return; }
        var row = new ReaderEntry(entry);
        byId.Add(entry.Id, row);
        int low = 0, high = Entries.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (Entries[mid].Id < entry.Id) low = mid + 1; else high = mid;
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
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (closed) return;
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
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0) SetFollowing(AtBottom);
        if (scroll?.VerticalOffset < 80 && !following && !exhausted) await LoadOlderAsync();
    }
    private async Task LoadOlderAsync()
    {
        if (loadingOlder || exhausted || Entries.Count == 0) return;
        loadingOlder = true;
        var anchor = CaptureAnchor();
        try
        {
            var rows = await SQLiteHistoryLogger.Store.ReadBeforeAsync(Entries[0].Id, 100);
            if (closed) return;
            foreach (var row in rows) Upsert(row);
            exhausted = rows.Count < 100;
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
        if (e.Delta > 0) SetFollowing(false);
    }
    private void Transcript_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp or Key.Home) SetFollowing(false);
        if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control)
        { SetFollowing(false); scroll?.ScrollToTop(); e.Handled = true; }
        if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control)
        { Latest_Click(sender, e); e.Handled = true; }
    }
    private void Latest_Click(object sender, RoutedEventArgs e)
    {
        SetFollowing(true); SchedulePosition(null);
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
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "双语记录_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await SQLiteHistoryLogger.Store.ExportAsync(dialog.FileName);
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
