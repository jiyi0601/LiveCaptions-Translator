using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.transcript;
namespace LiveCaptionsTranslator.utils;

public static class SQLiteHistoryLogger
{
    public static HistoryStore Store { get; } = new(System.IO.Path.Combine(RuntimeOptions.DataDirectory, "translation_history.db"));
    public static event Action? HistoryCleared;
    public static Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(int page, int maxRow, string searchText,
        CancellationToken token = default) => Store.ReadPageAsync(page, maxRow, searchText);
    public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default) =>
        (await Store.ReadBeforeAsync(null, 1)).LastOrDefault();
    public static Task ExportToCSV(string path, CancellationToken token = default) => Store.ExportAsync(path);
    public static async Task ClearHistory(CancellationToken token = default)
    {
        await Store.ClearAsync();
        HistoryCleared?.Invoke();
    }
}
