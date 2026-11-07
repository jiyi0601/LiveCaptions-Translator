using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using LiveCaptionsTranslator.models;
using Microsoft.Data.Sqlite;
namespace LiveCaptionsTranslator.transcript;

/// <summary>One connection per operation; WAL readers do not block caption writers.</summary>
public sealed class HistoryStore
{
    private readonly string connectionString;
    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS TranslationHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT, SourceText TEXT,
            TranslatedText TEXT, TargetLanguage TEXT, ApiUsed TEXT);";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(TranslationHistory)";
        bool hasVersion = false;
        using (var columns = command.ExecuteReader())
            while (columns.Read()) if (columns.GetString(1) == "Version") hasVersion = true;
        if (!hasVersion)
        {
            command.CommandText = "ALTER TABLE TranslationHistory ADD COLUMN Version INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    public async Task<TranslationHistoryEntry> CreateAsync(string source, string language, string api, string translation = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        command.CommandText = @"INSERT INTO TranslationHistory
            (Timestamp,SourceText,TranslatedText,TargetLanguage,ApiUsed,Version) VALUES ($time,$source,$translated,$language,$api,2);
            SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$time", time);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$translated", translation);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$api", api);
        long id = (long)(await command.ExecuteScalarAsync())!;
        var local = DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime;
        return new()
        {
            Id = id,
            Version = 2,
            Timestamp = local.ToString("yyyy-MM-dd HH:mm"),
            TimestampFull = local.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceText = source,
            TranslatedText = translation,
            TargetLanguage = language,
            ApiUsed = api
        };
    }
    public async Task UpdateAsync(TranslationHistoryEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE TranslationHistory SET SourceText=$source, TranslatedText=$translated,
            TargetLanguage=$language, ApiUsed=$api, Version=$version WHERE Id=$id AND Version <= $version";
        command.Parameters.AddWithValue("$id", entry.Id); command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$source", entry.SourceText);
        command.Parameters.AddWithValue("$translated", entry.TranslatedText);
        command.Parameters.AddWithValue("$language", entry.TargetLanguage);
        command.Parameters.AddWithValue("$api", entry.ApiUsed);
        await command.ExecuteNonQueryAsync();
    }
    public async Task<List<TranslationHistoryEntry>> ReadBeforeAsync(long? beforeId, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT * FROM TranslationHistory WHERE Id < $before ORDER BY Id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$before", beforeId ?? long.MaxValue);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var rows = await ReadRows(command);
        rows.Reverse();
        return rows;
    }
    public async Task<(List<TranslationHistoryEntry>, int)> ReadPageAsync(int page, int size, string search)
    {
        size = Math.Max(1, size);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TranslationHistory WHERE SourceText LIKE $q OR TranslatedText LIKE $q";
        command.Parameters.AddWithValue("$q", "%" + search + "%");
        long count = (long)(await command.ExecuteScalarAsync())!;
        command.CommandText = @"SELECT * FROM TranslationHistory WHERE SourceText LIKE $q OR TranslatedText LIKE $q
            ORDER BY Id DESC LIMIT $size OFFSET $offset";
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$offset", Math.Max(0, page - 1) * size);
        return (await ReadRows(command), Math.Max(1, (int)Math.Ceiling((double)count / size)));
    }
    public async Task ClearAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Never reuse ids: outstanding tasks must not overwrite a newly created row.
        command.CommandText = "DELETE FROM TranslationHistory";
        await command.ExecuteNonQueryAsync();
    }
    public async Task ExportAsync(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TranslationHistory ORDER BY Id ASC";
        using var reader = await command.ExecuteReaderAsync();
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteHeader<TranslationHistoryEntry>();
        await csv.NextRecordAsync();
        while (await reader.ReadAsync())
        {
            var entry = ReadRow(reader);
            csv.WriteRecord(entry with { Timestamp = entry.TimestampFull });
            await csv.NextRecordAsync();
        }
    }
    private static async Task<List<TranslationHistoryEntry>> ReadRows(SqliteCommand command)
    {
        var rows = new List<TranslationHistoryEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(ReadRow(reader));
        return rows;
    }
    private static TranslationHistoryEntry ReadRow(SqliteDataReader reader)
    {
        string time = reader["Timestamp"]?.ToString() ?? "";
        DateTime local = double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).LocalDateTime
            : DateTime.TryParse(time, out var legacy) ? legacy : DateTime.MinValue;
        return new()
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            Version = reader.GetInt64(reader.GetOrdinal("Version")),
            Timestamp = local.ToString("yyyy-MM-dd HH:mm"),
            TimestampFull = local.ToString("yyyy-MM-dd HH:mm:ss"),
            SourceText = reader["SourceText"]?.ToString() ?? "",
            TranslatedText = reader["TranslatedText"]?.ToString() ?? "",
            TargetLanguage = reader["TargetLanguage"]?.ToString() ?? "",
            ApiUsed = reader["ApiUsed"]?.ToString() ?? ""
        };
    }
}
