using System.Globalization;
using AndroidManager.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace AndroidManager.Transfer.Services;

/// <summary>
/// Converts Android msgstore.db into iOS WhatsApp ChatStorage.sqlite (CoreData layout).
/// Supports both legacy (key_remote_jid) and modern (chat_row_id / jid) schemas.
/// </summary>
public sealed class WhatsAppSchemaConverter
{
    private const double CoreDataEpochOffset = 978_307_200; // 2001-01-01 vs Unix epoch

    private readonly ILogger _logger;

    public WhatsAppSchemaConverter(ILogger? logger = null) =>
        _logger = logger ?? Log.ForContext<WhatsAppSchemaConverter>();

    public async Task<WhatsAppConversionResult> ConvertAsync(
        string msgStorePath,
        string outputDirectory,
        IProgress<WhatsAppTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(msgStorePath))
            throw new FileNotFoundException("Android msgstore.db bulunamadı.", msgStorePath);

        Directory.CreateDirectory(outputDirectory);
        var chatStoragePath = Path.Combine(outputDirectory, "ChatStorage.sqlite");
        if (File.Exists(chatStoragePath))
            File.Delete(chatStoragePath);

        Report(progress, WhatsAppTransferPhase.ConvertingDatabase, 10, "iOS şablonu oluşturuluyor…");
        await CreateIosSchemaAsync(chatStoragePath, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        var skipped = 0;

        await using var android = new SqliteConnection($"Data Source={msgStorePath};Mode=ReadOnly");
        await android.OpenAsync(cancellationToken).ConfigureAwait(false);

        var schema = await DetectAndroidSchemaAsync(android, cancellationToken).ConfigureAwait(false);
        _logger.Information("Android msgstore schema: {Schema}", schema);

        await using var ios = new SqliteConnection($"Data Source={chatStoragePath}");
        await ios.OpenAsync(cancellationToken).ConfigureAwait(false);

        var jidMap = await LoadJidMapAsync(android, cancellationToken).ConfigureAwait(false);
        var chats = await LoadAndroidChatsAsync(android, jidMap, schema, cancellationToken).ConfigureAwait(false);

        Report(progress, WhatsAppTransferPhase.ConvertingDatabase, 30,
            $"{chats.Count} sohbet dönüştürülüyor… ({schema})");

        await using var tx = await ios.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var chatPk = 1;
        var messagePk = 1;
        var mediaPk = 1;
        var chatSessionByJid = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var chat in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await InsertChatSessionAsync(ios, chatPk, chat, cancellationToken).ConfigureAwait(false);
            chatSessionByJid[chat.Jid] = chatPk;
            chatPk++;
        }

        Report(progress, WhatsAppTransferPhase.ConvertingDatabase, 50, "Mesajlar aktarılıyor…");

        var messages = await LoadAndroidMessagesAsync(android, jidMap, schema, cancellationToken)
            .ConfigureAwait(false);

        var convertedMessages = 0;
        foreach (var msg in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(msg.RemoteJid) ||
                !chatSessionByJid.TryGetValue(msg.RemoteJid, out var sessionPk))
            {
                skipped++;
                continue;
            }

            long? mediaItemPk = null;
            if (msg.MediaType > 0 && !string.IsNullOrWhiteSpace(msg.MediaName))
            {
                mediaItemPk = mediaPk;
                await InsertMediaItemAsync(
                        ios,
                        mediaPk,
                        msg.MediaName,
                        MapMediaType(msg.MediaType),
                        cancellationToken)
                    .ConfigureAwait(false);
                mediaPk++;
            }

            var messageDate = ToCoreDataDate(msg.TimestampMs);
            var messageType = MapMessageType(msg.MediaType, msg.Text);

            await InsertMessageAsync(
                    ios,
                    messagePk,
                    sessionPk,
                    msg.FromMe,
                    messageType,
                    msg.Text,
                    messageDate,
                    mediaItemPk,
                    cancellationToken)
                .ConfigureAwait(false);

            messagePk++;
            convertedMessages++;
        }

        await UpdatePrimaryKeysAsync(ios, chatPk - 1, messagePk - 1, mediaPk - 1, cancellationToken)
            .ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        Report(progress, WhatsAppTransferPhase.ConvertingDatabase, 95, "Dönüşüm tamamlandı.");

        if (skipped > 0)
            warnings.Add($"{skipped} mesaj eşleşen sohbet bulunamadığı için atlandı.");

        if (convertedMessages == 0 && messages.Count > 0)
            warnings.Add("Mesajlar okundu ama sohbet JID eşleşmesi yapılamadı.");

        _logger.Information(
            "WhatsApp conversion done: schema={Schema} chats={Chats} messages={Messages} skipped={Skipped}",
            schema, chats.Count, convertedMessages, skipped);

        return new WhatsAppConversionResult
        {
            ChatStoragePath = chatStoragePath,
            ConvertedChats = chats.Count,
            ConvertedMessages = convertedMessages,
            SkippedMessages = skipped,
            Warnings = warnings
        };
    }

    private enum AndroidMsgSchema
    {
        /// <summary>message + chat_row_id + text_data (2.19+ / 2.21+)</summary>
        Modern,

        /// <summary>message + key_remote_jid (geçiş dönemi)</summary>
        Hybrid,

        /// <summary>messages (çoğul) + key_remote_jid (eski)</summary>
        Legacy
    }

    private static async Task<AndroidMsgSchema> DetectAndroidSchemaAsync(
        SqliteConnection android,
        CancellationToken cancellationToken)
    {
        var hasMessage = await TableExistsAsync(android, "message", cancellationToken).ConfigureAwait(false);
        var hasMessages = await TableExistsAsync(android, "messages", cancellationToken).ConfigureAwait(false);

        if (hasMessage)
        {
            var cols = await GetColumnsAsync(android, "message", cancellationToken).ConfigureAwait(false);
            if (cols.Contains("chat_row_id"))
                return AndroidMsgSchema.Modern;
            if (cols.Contains("key_remote_jid"))
                return AndroidMsgSchema.Hybrid;
        }

        if (hasMessages)
        {
            var cols = await GetColumnsAsync(android, "messages", cancellationToken).ConfigureAwait(false);
            if (cols.Contains("key_remote_jid"))
                return AndroidMsgSchema.Legacy;
        }

        throw new InvalidOperationException(
            "msgstore.db şeması tanınamadı (message/messages + chat_row_id veya key_remote_jid yok).");
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", table);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        // PRAGMA table_info cannot use parameters for table name safely; table comes from our detection only.
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
                set.Add(reader.GetString(1));
        }

        return set;
    }

    private static async Task CreateIosSchemaAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var ddl = """
            CREATE TABLE IF NOT EXISTS Z_PRIMARYKEY (
                Z_ENT INTEGER PRIMARY KEY,
                Z_NAME VARCHAR,
                Z_SUPER INTEGER,
                Z_MAX INTEGER
            );

            CREATE TABLE IF NOT EXISTS ZWACHATSESSION (
                Z_PK INTEGER PRIMARY KEY,
                Z_ENT INTEGER,
                Z_OPT INTEGER,
                ZARCHIVED INTEGER,
                ZUNREADCOUNT INTEGER,
                ZCONTACTJID VARCHAR,
                ZPARTNERNAME VARCHAR,
                ZLASTMESSAGEDATE REAL,
                ZSESSIONTYPE INTEGER
            );

            CREATE TABLE IF NOT EXISTS ZWAMESSAGE (
                Z_PK INTEGER PRIMARY KEY,
                Z_ENT INTEGER,
                Z_OPT INTEGER,
                ZISFROMME INTEGER,
                ZMESSAGETYPE INTEGER,
                ZCHATSESSION INTEGER,
                ZMEDIAITEM INTEGER,
                ZTEXT VARCHAR,
                ZMESSAGEDATE REAL,
                ZSORT REAL
            );

            CREATE TABLE IF NOT EXISTS ZWAMEDIAITEM (
                Z_PK INTEGER PRIMARY KEY,
                Z_ENT INTEGER,
                Z_OPT INTEGER,
                ZMEDIALOCALPATH VARCHAR,
                ZMEDIAURL VARCHAR,
                ZMESSAGETYPE INTEGER
            );

            INSERT OR REPLACE INTO Z_PRIMARYKEY (Z_ENT, Z_NAME, Z_SUPER, Z_MAX) VALUES
                (1, 'WAChatSession', 0, 0),
                (2, 'WAMessage', 0, 0),
                (3, 'WAMediaItem', 0, 0);
            """;

        foreach (var statement in ddl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(statement))
                continue;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<long, string>> LoadJidMapAsync(
        SqliteConnection android,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, string>();
        if (!await TableExistsAsync(android, "jid", cancellationToken).ConfigureAwait(false))
            return map;

        await using var cmd = android.CreateCommand();
        cmd.CommandText = "SELECT _id, raw_string FROM jid";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var raw = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(raw))
                map[id] = raw;
        }

        return map;
    }

    private static async Task<List<AndroidChat>> LoadAndroidChatsAsync(
        SqliteConnection android,
        IReadOnlyDictionary<long, string> jidMap,
        AndroidMsgSchema schema,
        CancellationToken cancellationToken)
    {
        var chats = new List<AndroidChat>();

        if (schema is AndroidMsgSchema.Modern or AndroidMsgSchema.Hybrid &&
            await TableExistsAsync(android, "chat", cancellationToken).ConfigureAwait(false))
        {
            var chatCols = await GetColumnsAsync(android, "chat", cancellationToken).ConfigureAwait(false);
            if (chatCols.Contains("jid_row_id"))
            {
                await using var cmd = android.CreateCommand();
                cmd.CommandText = """
                    SELECT c._id, c.jid_row_id, c.subject, c.sort_timestamp
                    FROM chat c
                    ORDER BY c.sort_timestamp DESC
                    """;

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var jidRowId = reader.GetInt64(1);
                    if (!jidMap.TryGetValue(jidRowId, out var jid))
                        continue;

                    chats.Add(new AndroidChat(
                        Jid: jid,
                        Subject: reader.IsDBNull(2) ? null : reader.GetString(2),
                        SortTimestampMs: reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
                }

                if (chats.Count > 0)
                    return chats;
            }
        }

        // Legacy / fallback: distinct JIDs from messages
        await using (var cmd = android.CreateCommand())
        {
            cmd.CommandText = schema switch
            {
                AndroidMsgSchema.Legacy => """
                    SELECT key_remote_jid, MAX(timestamp) AS ts
                    FROM messages
                    WHERE key_remote_jid IS NOT NULL AND key_remote_jid != ''
                    GROUP BY key_remote_jid
                    ORDER BY ts DESC
                    """,
                AndroidMsgSchema.Hybrid => """
                    SELECT key_remote_jid, MAX(timestamp) AS ts
                    FROM message
                    WHERE key_remote_jid IS NOT NULL AND key_remote_jid != ''
                    GROUP BY key_remote_jid
                    ORDER BY ts DESC
                    """,
                _ => """
                    SELECT j.raw_string, MAX(m.timestamp) AS ts
                    FROM message m
                    JOIN chat c ON c._id = m.chat_row_id
                    JOIN jid j ON j._id = c.jid_row_id
                    WHERE j.raw_string IS NOT NULL AND j.raw_string != ''
                    GROUP BY j.raw_string
                    ORDER BY ts DESC
                    """
            };

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var jid = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(jid))
                    continue;

                chats.Add(new AndroidChat(
                    Jid: jid,
                    Subject: null,
                    SortTimestampMs: reader.IsDBNull(1) ? 0 : reader.GetInt64(1)));
            }
        }

        return chats;
    }

    private static async Task<List<AndroidMessage>> LoadAndroidMessagesAsync(
        SqliteConnection android,
        IReadOnlyDictionary<long, string> jidMap,
        AndroidMsgSchema schema,
        CancellationToken cancellationToken)
    {
        return schema switch
        {
            AndroidMsgSchema.Modern => await LoadModernMessagesAsync(android, jidMap, cancellationToken)
                .ConfigureAwait(false),
            AndroidMsgSchema.Hybrid => await LoadHybridMessagesAsync(android, cancellationToken)
                .ConfigureAwait(false),
            _ => await LoadLegacyMessagesAsync(android, cancellationToken).ConfigureAwait(false)
        };
    }

    private static async Task<List<AndroidMessage>> LoadModernMessagesAsync(
        SqliteConnection android,
        IReadOnlyDictionary<long, string> jidMap,
        CancellationToken cancellationToken)
    {
        var list = new List<AndroidMessage>();
        var msgCols = await GetColumnsAsync(android, "message", cancellationToken).ConfigureAwait(false);
        var hasTextData = msgCols.Contains("text_data");
        var hasData = msgCols.Contains("data");
        var hasFromMe = msgCols.Contains("from_me");
        var hasKeyFromMe = msgCols.Contains("key_from_me");
        var hasMessageType = msgCols.Contains("message_type");
        var hasMediaType = msgCols.Contains("media_wa_type");
        var hasMediaName = msgCols.Contains("media_name");

        var textExpr = hasTextData ? "m.text_data" : hasData ? "m.data" : "NULL";
        var fromMeExpr = hasFromMe ? "m.from_me" : hasKeyFromMe ? "m.key_from_me" : "0";
        var mediaTypeExpr = hasMediaType
            ? "m.media_wa_type"
            : hasMessageType ? "m.message_type" : "0";
        var mediaNameExpr = hasMediaName ? "m.media_name" : "NULL";

        // message_media şeması sürüme göre değişir — sadece var olan sütunları kullan.
        var mediaJoin = "";
        var mediaTypeSelect = mediaTypeExpr;
        var mediaNameSelect = mediaNameExpr;
        var mediaWhereExtra = mediaTypeExpr;

        if (await TableExistsAsync(android, "message_media", cancellationToken).ConfigureAwait(false))
        {
            var mmCols = await GetColumnsAsync(android, "message_media", cancellationToken).ConfigureAwait(false);
            var joinKey = mmCols.Contains("message_row_id") ? "message_row_id"
                : mmCols.Contains("message_id") ? "message_id" : null;

            if (joinKey is not null)
            {
                mediaJoin = $"LEFT JOIN message_media mm ON mm.{joinKey} = m._id";

                var mmTypeCol = FirstExisting(mmCols, "message_type", "media_wa_type", "type");
                if (mmTypeCol is not null)
                {
                    mediaTypeSelect = $"COALESCE(mm.{mmTypeCol}, {mediaTypeExpr})";
                    mediaWhereExtra = mediaTypeSelect;
                }

                var mmPathCol = FirstExisting(mmCols, "file_path", "media_name", "file_name", "original_file_hash");
                if (mmPathCol is not null)
                    mediaNameSelect = $"COALESCE(mm.{mmPathCol}, {mediaNameExpr})";
            }
        }

        var sql = $"""
            SELECT m._id,
                   j.raw_string,
                   {fromMeExpr},
                   {textExpr},
                   m.timestamp,
                   {mediaTypeSelect} AS media_type,
                   {mediaNameSelect} AS media_name
            FROM message m
            JOIN chat c ON c._id = m.chat_row_id
            JOIN jid j ON j._id = c.jid_row_id
            {mediaJoin}
            WHERE ({textExpr}) IS NOT NULL OR ({mediaWhereExtra}) > 0
            ORDER BY m.timestamp ASC
            """;

        try
        {
            await using var cmd = android.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadMessageRow(reader));
        }
        catch (SqliteException ex)
        {
            // Sütun uyuşmazlığında medya join'siz sade sorguya düş.
            Log.Warning(ex, "Modern message query failed, retrying without message_media join");
            list.Clear();
            await using var cmd = android.CreateCommand();
            cmd.CommandText = $"""
                SELECT m._id,
                       j.raw_string,
                       {fromMeExpr},
                       {textExpr},
                       m.timestamp,
                       {mediaTypeExpr} AS media_type,
                       {mediaNameExpr} AS media_name
                FROM message m
                JOIN chat c ON c._id = m.chat_row_id
                JOIN jid j ON j._id = c.jid_row_id
                WHERE ({textExpr}) IS NOT NULL OR ({mediaTypeExpr}) > 0
                ORDER BY m.timestamp ASC
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadMessageRow(reader));
        }

        // If join returned nothing but we have messages, try chat_row_id map fallback
        if (list.Count == 0 && jidMap.Count > 0)
        {
            await using var fallback = android.CreateCommand();
            fallback.CommandText = $"""
                SELECT m._id, c.jid_row_id, {fromMeExpr}, {textExpr}, m.timestamp,
                       {mediaTypeExpr}, {mediaNameExpr}
                FROM message m
                JOIN chat c ON c._id = m.chat_row_id
                ORDER BY m.timestamp ASC
                """;
            await using var fr = await fallback.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await fr.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var jidRowId = fr.IsDBNull(1) ? -1L : fr.GetInt64(1);
                if (!jidMap.TryGetValue(jidRowId, out var jid))
                    continue;

                list.Add(new AndroidMessage(
                    RemoteJid: jid,
                    FromMe: !fr.IsDBNull(2) && Convert.ToInt32(fr.GetValue(2), CultureInfo.InvariantCulture) == 1,
                    Text: fr.IsDBNull(3) ? string.Empty : fr.GetString(3),
                    TimestampMs: fr.IsDBNull(4) ? 0L : fr.GetInt64(4),
                    MediaType: fr.IsDBNull(5) ? 0 : Convert.ToInt32(fr.GetValue(5), CultureInfo.InvariantCulture),
                    MediaName: fr.IsDBNull(6) ? null : fr.GetString(6)));
            }
        }

        return list;
    }

    private static string? FirstExisting(HashSet<string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            if (columns.Contains(name))
                return name;
        }

        return null;
    }

    private static async Task<List<AndroidMessage>> LoadHybridMessagesAsync(
        SqliteConnection android,
        CancellationToken cancellationToken)
    {
        var list = new List<AndroidMessage>();
        var cols = await GetColumnsAsync(android, "message", cancellationToken).ConfigureAwait(false);
        var textCol = cols.Contains("text_data") ? "text_data" : "data";
        var fromMeCol = cols.Contains("from_me") ? "from_me" : "key_from_me";
        var mediaTypeCol = cols.Contains("media_wa_type") ? "media_wa_type" :
            cols.Contains("message_type") ? "message_type" : "0";
        var mediaNameCol = cols.Contains("media_name") ? "media_name" : "NULL";

        await using var cmd = android.CreateCommand();
        cmd.CommandText = $"""
            SELECT m._id, m.key_remote_jid, m.{fromMeCol}, m.{textCol}, m.timestamp,
                   {(mediaTypeCol == "0" ? "0" : "m." + mediaTypeCol)},
                   {(mediaNameCol == "NULL" ? "NULL" : "m." + mediaNameCol)}
            FROM message m
            WHERE m.key_remote_jid IS NOT NULL
              AND (m.{textCol} IS NOT NULL OR {(mediaTypeCol == "0" ? "0" : "m." + mediaTypeCol)} > 0)
            ORDER BY m.timestamp ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(ReadMessageRow(reader));

        return list;
    }

    private static async Task<List<AndroidMessage>> LoadLegacyMessagesAsync(
        SqliteConnection android,
        CancellationToken cancellationToken)
    {
        var list = new List<AndroidMessage>();
        await using var cmd = android.CreateCommand();
        cmd.CommandText = """
            SELECT m._id, m.key_remote_jid, m.key_from_me, m.data, m.timestamp,
                   m.media_wa_type, m.media_name
            FROM messages m
            WHERE m.key_remote_jid IS NOT NULL
              AND (m.data IS NOT NULL OR m.media_wa_type > 0)
            ORDER BY m.timestamp ASC
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(ReadMessageRow(reader));

        return list;
    }

    private static AndroidMessage ReadMessageRow(SqliteDataReader reader)
    {
        return new AndroidMessage(
            RemoteJid: reader.IsDBNull(1) ? null : reader.GetString(1),
            FromMe: !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1,
            Text: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            TimestampMs: reader.IsDBNull(4) ? 0L : reader.GetInt64(4),
            MediaType: reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
            MediaName: reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task InsertChatSessionAsync(
        SqliteConnection ios,
        long pk,
        AndroidChat chat,
        CancellationToken cancellationToken)
    {
        await using var cmd = ios.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ZWACHATSESSION
                (Z_PK, Z_ENT, Z_OPT, ZARCHIVED, ZUNREADCOUNT, ZCONTACTJID, ZPARTNERNAME, ZLASTMESSAGEDATE, ZSESSIONTYPE)
            VALUES
                ($pk, 1, 1, 0, 0, $jid, $name, $lastDate, $sessionType)
            """;
        cmd.Parameters.AddWithValue("$pk", pk);
        cmd.Parameters.AddWithValue("$jid", chat.Jid);
        cmd.Parameters.AddWithValue("$name", chat.DisplayName);
        cmd.Parameters.AddWithValue("$lastDate", ToCoreDataDate(chat.SortTimestampMs));
        cmd.Parameters.AddWithValue("$sessionType", chat.Jid.Contains("@g.us", StringComparison.Ordinal) ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMessageAsync(
        SqliteConnection ios,
        long pk,
        long chatSessionPk,
        bool fromMe,
        int messageType,
        string text,
        double messageDate,
        long? mediaItemPk,
        CancellationToken cancellationToken)
    {
        await using var cmd = ios.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ZWAMESSAGE
                (Z_PK, Z_ENT, Z_OPT, ZISFROMME, ZMESSAGETYPE, ZCHATSESSION, ZMEDIAITEM, ZTEXT, ZMESSAGEDATE, ZSORT)
            VALUES
                ($pk, 2, 1, $fromMe, $type, $session, $media, $text, $date, $sort)
            """;
        cmd.Parameters.AddWithValue("$pk", pk);
        cmd.Parameters.AddWithValue("$fromMe", fromMe ? 1 : 0);
        cmd.Parameters.AddWithValue("$type", messageType);
        cmd.Parameters.AddWithValue("$session", chatSessionPk);
        cmd.Parameters.AddWithValue("$media", (object?)mediaItemPk ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$date", messageDate);
        cmd.Parameters.AddWithValue("$sort", messageDate);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMediaItemAsync(
        SqliteConnection ios,
        long pk,
        string mediaName,
        int mediaType,
        CancellationToken cancellationToken)
    {
        var iosPath = MapMediaPath(mediaName, mediaType);
        await using var cmd = ios.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ZWAMEDIAITEM
                (Z_PK, Z_ENT, Z_OPT, ZMEDIALOCALPATH, ZMEDIAURL, ZMESSAGETYPE)
            VALUES
                ($pk, 3, 1, $local, $url, $type)
            """;
        cmd.Parameters.AddWithValue("$pk", pk);
        cmd.Parameters.AddWithValue("$local", iosPath);
        cmd.Parameters.AddWithValue("$url", mediaName);
        cmd.Parameters.AddWithValue("$type", mediaType);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdatePrimaryKeysAsync(
        SqliteConnection ios,
        long maxChat,
        long maxMessage,
        long maxMedia,
        CancellationToken cancellationToken)
    {
        await using var cmd = ios.CreateCommand();
        cmd.CommandText = """
            UPDATE Z_PRIMARYKEY SET Z_MAX = $max WHERE Z_ENT = 1;
            UPDATE Z_PRIMARYKEY SET Z_MAX = $maxMsg WHERE Z_ENT = 2;
            UPDATE Z_PRIMARYKEY SET Z_MAX = $maxMedia WHERE Z_ENT = 3;
            """;
        cmd.Parameters.AddWithValue("$max", maxChat);
        cmd.Parameters.AddWithValue("$maxMsg", maxMessage);
        cmd.Parameters.AddWithValue("$maxMedia", maxMedia);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static double ToCoreDataDate(long timestampMs)
    {
        if (timestampMs <= 0)
            return 0;
        var unixSeconds = timestampMs > 9999999999L
            ? timestampMs / 1000.0
            : timestampMs;
        return unixSeconds - CoreDataEpochOffset;
    }

    private static int MapMessageType(int androidMediaType, string text)
    {
        if (androidMediaType <= 0)
            return 0;
        return MapMediaType(androidMediaType);
    }

    private static int MapMediaType(int androidMediaType) => androidMediaType switch
    {
        1 => 1,  // image
        2 => 2,  // audio
        3 => 3,  // video
        4 => 4,  // contact
        5 => 5,  // location
        9 => 9,  // document
        _ => 0
    };

    private static string MapMediaPath(string mediaName, int mediaType)
    {
        var folder = mediaType switch
        {
            1 => "Images",
            2 => "Audio",
            3 => "Video",
            9 => "Documents",
            _ => "Media"
        };
        return $"Media/{folder}/{Path.GetFileName(mediaName)}";
    }

    private static void Report(
        IProgress<WhatsAppTransferProgress>? progress,
        WhatsAppTransferPhase phase,
        int percent,
        string message) =>
        progress?.Report(new WhatsAppTransferProgress { Phase = phase, Percent = percent, Message = message });

    private sealed record AndroidChat(string Jid, string? Subject, long SortTimestampMs)
    {
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Subject)
                ? Jid.Split('@')[0]
                : Subject;
    }

    private sealed record AndroidMessage(
        string? RemoteJid,
        bool FromMe,
        string Text,
        long TimestampMs,
        int MediaType,
        string? MediaName);
}
