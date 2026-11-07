using System.Globalization;
using LiveCaptionsTranslator.speech;
using System.IO;
using System.Text;
using CsvHelper;
using LiveCaptionsTranslator.models;
using Microsoft.Data.Sqlite;

namespace LiveCaptionsTranslator.transcript;

/// <summary>Versioned fragments in SQLite; paragraph ids are assigned atomically with their first text.</summary>
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
        var columns = new HashSet<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("Version"))
        {
            command.CommandText = "ALTER TABLE TranslationHistory ADD COLUMN Version INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("ParagraphId"))
        {
            command.CommandText = "ALTER TABLE TranslationHistory ADD COLUMN ParagraphId INTEGER";
            command.ExecuteNonQuery();
        }
        foreach (var column in new[] { "SessionId TEXT", "SourceKey TEXT", "SpeakerId TEXT", "SpeakerState INTEGER", "AudioStart REAL", "AudioEnd REAL" })
        {
            if (columns.Contains(column.Split(' ')[0])) continue;
            command.CommandText = "ALTER TABLE TranslationHistory ADD COLUMN " + column;
            command.ExecuteNonQuery();
        }
        command.CommandText = @"CREATE UNIQUE INDEX IF NOT EXISTS IX_History_SpeechKey
            ON TranslationHistory(SessionId,SourceKey) WHERE SessionId IS NOT NULL;
            CREATE TABLE IF NOT EXISTS SpeakerDrafts (SessionId TEXT PRIMARY KEY, Text TEXT NOT NULL,
            Language TEXT NOT NULL, Api TEXT NOT NULL, AudioStart REAL, AudioEnd REAL);";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE INDEX IF NOT EXISTS IX_TranslationHistory_Paragraph ON TranslationHistory(COALESCE(ParagraphId,Id),Id)";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    public async Task<TranslationHistoryEntry> CreateAsync(string source, string language, string api,
        string translation = "", long? paragraphId = null, SpeechInfo? speech = null, SpeechDraft? remainingDraft = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (speech != null)
        {
            command.CommandText = "SELECT * FROM TranslationHistory WHERE SessionId=$session AND SourceKey=$key";
            command.Parameters.AddWithValue("$session", speech.SessionId);
            command.Parameters.AddWithValue("$key", speech.SourceKey);
            using (var existing = await command.ExecuteReaderAsync())
                if (await existing.ReadAsync()) return ReadRow(existing);
            command.Parameters.Clear();
        }
        if (paragraphId.HasValue)
        {
            command.CommandText = "SELECT COUNT(*) FROM TranslationHistory WHERE Id=$parent";
            command.Parameters.AddWithValue("$parent", paragraphId.Value);
            if ((long)(await command.ExecuteScalarAsync())! == 0) paragraphId = null;
            command.Parameters.Clear();
        }
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long version = string.IsNullOrWhiteSpace(translation) ? 2 : 3;
        command.CommandText = @"INSERT INTO TranslationHistory
            (Timestamp,SourceText,TranslatedText,TargetLanguage,ApiUsed,Version,ParagraphId)
            VALUES ($time,$source,$translated,$language,$api,$version,$parent);
            SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$time", time);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$translated", translation);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$api", api);
        command.Parameters.AddWithValue("$parent", (object?)paragraphId ?? DBNull.Value);
        long id = (long)(await command.ExecuteScalarAsync())!;
        if (!paragraphId.HasValue)
        {
            paragraphId = id;
            command.Parameters.Clear();
            command.CommandText = "UPDATE TranslationHistory SET ParagraphId=Id WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }
        if (speech != null)
        {
            command.Parameters.Clear();
            command.CommandText = @"UPDATE TranslationHistory SET SessionId=$session, SourceKey=$key,
                SpeakerId=$speaker, SpeakerState=$state, AudioStart=$start, AudioEnd=$end WHERE Id=$id";
            command.Parameters.AddWithValue("$session", speech.SessionId);
            command.Parameters.AddWithValue("$key", speech.SourceKey);
            command.Parameters.AddWithValue("$speaker", (object?)speech.SpeakerId ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", (int)speech.State);
            command.Parameters.AddWithValue("$start", speech.Start);
            command.Parameters.AddWithValue("$end", speech.End);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }
        if (remainingDraft != null) await SaveDraft(connection, transaction, remainingDraft);
        transaction.Commit();
        var local = DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime;
        return new()
        {
            Id = id,
            Speech = speech,
            ParagraphId = paragraphId.Value,
            Version = version,
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
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$version", entry.Version);
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
        command.CommandText = "SELECT * FROM TranslationHistory WHERE Id < $before ORDER BY Id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$before", beforeId ?? long.MaxValue);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var rows = await ReadRows(command);
        rows.Reverse();
        return rows;
    }
    // A page contains whole paragraphs, even when a paragraph has many fragments.
    public async Task<List<TranslationHistoryEntry>> ReadParagraphsBeforeAsync(long? beforeId, int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"WITH Selected AS (
            SELECT DISTINCT COALESCE(ParagraphId,Id) AS GroupId FROM TranslationHistory
            WHERE COALESCE(ParagraphId,Id) < $before ORDER BY GroupId DESC LIMIT $limit)
            SELECT h.* FROM TranslationHistory h JOIN Selected s ON COALESCE(h.ParagraphId,h.Id)=s.GroupId
            ORDER BY s.GroupId,h.Id";
        command.Parameters.AddWithValue("$before", beforeId ?? long.MaxValue);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        return await ReadRows(command);
    }
    public async Task<List<TranslationHistoryEntry>> ReadParagraphAsync(long paragraphId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TranslationHistory WHERE COALESCE(ParagraphId,Id)=$id ORDER BY Id";
        command.Parameters.AddWithValue("$id", paragraphId);
        return await ReadRows(command);
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
        command.CommandText = "DELETE FROM TranslationHistory; DELETE FROM SpeakerDrafts;";
        await command.ExecuteNonQueryAsync();
    }
    public async Task ExportAsync(string path, bool includeSpeakers = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TranslationHistory ORDER BY COALESCE(ParagraphId,Id),Id";
        using var reader = await command.ExecuteReaderAsync();
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteHeader<TranslationHistoryEntry>();
        if (includeSpeakers) { csv.WriteField("SessionId"); csv.WriteField("Speaker"); }
        void WriteParagraph(ReaderEntry value)
        {
            csv.WriteRecord(value.ExportEntry());
            if (includeSpeakers)
            {
                csv.WriteField(value.ExportEntry().Speech?.SessionId ?? "");
                csv.WriteField(value.Speaker);
            }
        }
        await csv.NextRecordAsync();
        ReaderEntry? paragraph = null;
        while (await reader.ReadAsync())
        {
            var entry = ReadRow(reader);
            if (paragraph != null && paragraph.Id != entry.GroupId)
            {
                WriteParagraph(paragraph);
                await csv.NextRecordAsync();
                paragraph = null;
            }
            if (paragraph == null) paragraph = new ReaderEntry(entry);
            else paragraph.Update(entry);
        }
        if (paragraph != null)
        {
            WriteParagraph(paragraph);
            await csv.NextRecordAsync();
        }
    }
    public async Task SaveDraftAsync(SpeechDraft draft)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        await SaveDraft(connection, transaction, draft);
        transaction.Commit();
    }
    private static async Task SaveDraft(SqliteConnection connection, SqliteTransaction transaction, SpeechDraft draft)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", draft.SessionId);
        if (string.IsNullOrWhiteSpace(draft.Text))
            command.CommandText = "DELETE FROM SpeakerDrafts WHERE SessionId=$session";
        else
        {
            command.CommandText = @"INSERT INTO SpeakerDrafts VALUES($session,$text,$language,$api,$start,$end)
                ON CONFLICT(SessionId) DO UPDATE SET Text=$text,Language=$language,Api=$api,AudioStart=$start,AudioEnd=$end";
            command.Parameters.AddWithValue("$text", draft.Text);
            command.Parameters.AddWithValue("$language", draft.Language);
            command.Parameters.AddWithValue("$api", draft.Api);
            command.Parameters.AddWithValue("$start", draft.Start);
            command.Parameters.AddWithValue("$end", draft.End);
        }
        await command.ExecuteNonQueryAsync();
    }
    public async Task<List<SpeechDraft>> ReadDraftsAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM SpeakerDrafts ORDER BY rowid";
        using var reader = await command.ExecuteReaderAsync();
        var result = new List<SpeechDraft>();
        while (await reader.ReadAsync())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetDouble(5)));
        return result;
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
            Speech = reader["SessionId"] is string session ? new SpeechInfo(session,
                reader["SourceKey"]?.ToString() ?? "", reader["SpeakerId"] is string speaker ? speaker : null,
                reader["SpeakerState"] is long state ? (SpeakerState)state : SpeakerState.Unknown,
                reader["AudioStart"] is double start ? start : 0, reader["AudioEnd"] is double end ? end : 0) : null,
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ParagraphId = reader["ParagraphId"] is long id ? id : reader.GetInt64(reader.GetOrdinal("Id")),
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
